[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$AllowDowngrade,
    [switch]$ElevatedRelaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

trap {
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$script:TrustStorePath = "Cert:\LocalMachine\Root"
$script:TrustStoreDescription = "LocalMachine\Root"
$script:CodeSigningEku = "1.3.6.1.5.5.7.3.3"

function Write-Info {
    param([AllowEmptyString()][string]$Message)
    Write-Host $Message
}

function Stop-Install {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw $Message
}

function Get-RepoRoot {
    $root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    if (-not (Test-Path (Join-Path $root "Directory.Product.props"))) {
        Stop-Install "Unable to resolve repository root from script location: $PSScriptRoot"
    }

    return $root
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)][xml]$Xml,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $node = $Xml.Project.PropertyGroup.ChildNodes |
        Where-Object { $_.NodeType -eq "Element" -and $_.Name -eq $Name } |
        Select-Object -First 1

    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        Stop-Install "Directory.Product.props is missing required property '$Name'."
    }

    return $node.InnerText.Trim()
}

function Get-ProductInfo {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)

    [xml]$props = Get-Content -Raw (Join-Path $RepoRoot "Directory.Product.props")

    return [pscustomobject]@{
        ShortName = Get-RequiredProperty $props "ProductShortName"
        DisplayName = Get-RequiredProperty $props "ProductDisplayName"
        ProductVersion = Get-RequiredProperty $props "ProductVersion"
        PackageVersion = Get-RequiredProperty $props "ProductPackageVersion"
        PackageIdentity = Get-RequiredProperty $props "ProductPackageIdentityName"
        Publisher = Get-RequiredProperty $props "ProductPublisher"
    }
}

function Convert-RuntimeIdentifierToArchitecture {
    param([Parameter(Mandatory = $true)][string]$Value)

    switch ($Value) {
        "win-x64" { return "x64" }
        "win-x86" { return "x86" }
        "win-arm64" { return "arm64" }
        default { Stop-Install "Unsupported runtime identifier '$Value'. Expected win-x64, win-x86, or win-arm64." }
    }
}

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Read-PackageManifest {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry("AppxManifest.xml")
        if ($null -eq $entry) {
            Stop-Install "Package '$PackagePath' does not contain AppxManifest.xml."
        }

        $stream = $entry.Open()
        try {
            $reader = [IO.StreamReader]::new($stream)
            try {
                [xml]$manifest = $reader.ReadToEnd()
                return $manifest
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-PackageIdentity {
    param([Parameter(Mandatory = $true)][xml]$Manifest)

    $namespaceManager = [Xml.XmlNamespaceManager]::new($Manifest.NameTable)
    $namespaceManager.AddNamespace("pkg", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $identity = $Manifest.SelectSingleNode("/pkg:Package/pkg:Identity", $namespaceManager)

    if ($null -eq $identity) {
        Stop-Install "Package manifest is missing /Package/Identity."
    }

    return [pscustomobject]@{
        Name = $identity.GetAttribute("Name")
        Publisher = $identity.GetAttribute("Publisher")
        Version = $identity.GetAttribute("Version")
        Architecture = $identity.GetAttribute("ProcessorArchitecture")
    }
}

function Find-DevelopmentPackage {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)]$Product,
        [Parameter(Mandatory = $true)][string]$Architecture
    )

    $artifactRoot = Join-Path $RepoRoot "artifacts\desktop\$($Product.ProductVersion)"
    if (-not (Test-Path $artifactRoot)) {
        Stop-Install "Package not built yet. Expected artifacts under '$artifactRoot'. Run .\scripts\package-desktop.ps1 first."
    }

    $packages = @(Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
        Where-Object {
            $_.Extension -in @(".msix", ".appx", ".msixbundle", ".appxbundle") -and
            $_.FullName -notmatch "\\Dependencies\\"
        })

    if ($packages.Count -eq 0) {
        Stop-Install "Package not built yet. No MSIX/AppX package was found under '$artifactRoot'. Run .\scripts\package-desktop.ps1 first."
    }

    $matches = @()
    foreach ($package in $packages) {
        try {
            $manifest = Read-PackageManifest -PackagePath $package.FullName
            $identity = Get-PackageIdentity -Manifest $manifest
        }
        catch {
            Write-Verbose "Skipping '$($package.FullName)': $($_.Exception.Message)"
            continue
        }

        if (
            $identity.Name -eq $Product.PackageIdentity -and
            $identity.Publisher -eq $Product.Publisher -and
            $identity.Version -eq $Product.PackageVersion -and
            $identity.Architecture -eq $Architecture
        ) {
            $matches += [pscustomobject]@{
                File = $package
                Identity = $identity
                Score = $(if ($package.FullName -match "_Test\\[^\\]+$") { 2 } else { 1 })
            }
        }
    }

    if ($matches.Count -eq 0) {
        Stop-Install "No matching package was found. Expected identity '$($Product.PackageIdentity)', publisher '$($Product.Publisher)', version '$($Product.PackageVersion)', architecture '$Architecture' under '$artifactRoot'."
    }

    $selected = $matches | Sort-Object Score, { $_.File.LastWriteTimeUtc } -Descending | Select-Object -First 1
    return $selected
}

function Find-PublicCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)]$Product,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$SignerThumbprint
    )

    $artifactRoot = Join-Path $RepoRoot "artifacts\desktop\$($Product.ProductVersion)"
    $candidatePaths = @(
        [IO.Path]::ChangeExtension($PackagePath, ".cer"),
        (Join-Path (Split-Path -Parent $PackagePath) "desktop-signing.cer"),
        (Join-Path $artifactRoot "desktop-signing.cer")
    ) | Select-Object -Unique

    foreach ($path in $candidatePaths) {
        if (-not (Test-Path $path)) {
            continue
        }

        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path $path).Path)
        if ($cert.Subject -eq $Product.Publisher -and $cert.Thumbprint -eq $SignerThumbprint) {
            return [pscustomobject]@{
                Path = (Resolve-Path $path).Path
                Certificate = $cert
            }
        }
    }

    Stop-Install "Missing certificate. Expected a public .cer matching publisher '$($Product.Publisher)' and package signer '$SignerThumbprint' beside the package or at '$artifactRoot\desktop-signing.cer'. Rebuild with .\scripts\package-desktop.ps1 -GenerateDevelopmentCertificate."
}

function Assert-PublicCertificate {
    param(
        [Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Product
    )

    if ($Certificate.HasPrivateKey) {
        Stop-Install "Refusing to trust private key material. Expected a public .cer, but '$Path' contains a private key."
    }

    if ($Certificate.Subject -ne $Product.Publisher) {
        Stop-Install "Publisher mismatch. Certificate subject '$($Certificate.Subject)' does not match ProductPublisher '$($Product.Publisher)'."
    }

    if ($Certificate.NotAfter -le (Get-Date)) {
        Stop-Install "Certificate is expired. '$Path' expired at $($Certificate.NotAfter). Rebuild with a fresh development certificate."
    }

    $eku = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
        Select-Object -First 1

    if ($null -eq $eku) {
        Stop-Install "Certificate '$Path' is missing the Code Signing enhanced key usage."
    }

    $eku = [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$eku
    $hasCodeSigning = $eku.EnhancedKeyUsages |
        Where-Object { $_.Value -eq $script:CodeSigningEku } |
        Select-Object -First 1

    if ($null -eq $hasCodeSigning) {
        Stop-Install "Certificate '$Path' does not contain the Code Signing enhanced key usage."
    }
}

function Assert-PackageSignature {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $signature = Get-AuthenticodeSignature -FilePath $PackagePath
    if ($null -eq $signature -or $null -eq $signature.SignerCertificate) {
        Stop-Install "Invalid signature. Windows could not read a signer certificate from '$PackagePath'."
    }

    if ($signature.SignerCertificate.Thumbprint -ne $Certificate.Thumbprint) {
        Stop-Install "Invalid signature. Package signer thumbprint '$($signature.SignerCertificate.Thumbprint)' does not match public certificate '$($Certificate.Thumbprint)'."
    }

    if ($signature.Status -notin @("Valid", "UnknownError")) {
        Stop-Install "Invalid signature. Authenticode status for '$PackagePath' is '$($signature.Status)': $($signature.StatusMessage)"
    }
}

function Test-CertificateTrusted {
    param([Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $trusted = Get-ChildItem $script:TrustStorePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint } |
        Select-Object -First 1

    return $null -ne $trusted
}

function Import-DevelopmentCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$CertificatePath,
        [Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if (Test-CertificateTrusted -Certificate $Certificate) {
        return $false
    }

    Import-Certificate -FilePath $CertificatePath -CertStoreLocation $script:TrustStorePath | Out-Null
    if (-not (Test-CertificateTrusted -Certificate $Certificate)) {
        Stop-Install "Certificate trust failure. Windows did not find '$($Certificate.Thumbprint)' in $script:TrustStoreDescription after import."
    }

    return $true
}

function Invoke-ElevatedSelf {
    param(
        [Parameter(Mandatory = $true)][string]$RuntimeIdentifier,
        [Parameter(Mandatory = $true)][bool]$AllowDowngrade
    )

    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-RuntimeIdentifier", $RuntimeIdentifier,
        "-ElevatedRelaunch"
    )

    if ($AllowDowngrade) {
        $arguments += "-AllowDowngrade"
    }

    try {
        $process = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    }
    catch {
        Stop-Install "Elevation declined or failed. Re-run this command from an elevated PowerShell window: .\scripts\install-desktop-dev.ps1 -RuntimeIdentifier $RuntimeIdentifier"
    }

    if ($process.ExitCode -ne 0) {
        exit $process.ExitCode
    }

    exit 0
}

function Get-InstalledPackage {
    param([Parameter(Mandatory = $true)][string]$PackageIdentity)

    $packages = @(Get-AppxPackage -Name $PackageIdentity -ErrorAction SilentlyContinue)
    if ($packages.Count -eq 0) {
        return $null
    }

    return $packages | Sort-Object Version -Descending | Select-Object -First 1
}

function Test-IsLooseRegistration {
    param([Parameter(Mandatory = $true)]$Package)

    if ($Package.SignatureKind -eq "None") {
        return $true
    }

    if ($Package.InstallLocation -match "\\bin\\.+\\AppX$") {
        return $true
    }

    return $false
}

function Assert-InstallAllowed {
    param(
        [AllowNull()]$InstalledPackage,
        [Parameter(Mandatory = $true)][string]$PackageVersion,
        [Parameter(Mandatory = $true)][bool]$AllowDowngrade
    )

    if ($null -eq $InstalledPackage) {
        return "Install"
    }

    if (Test-IsLooseRegistration -Package $InstalledPackage) {
        Stop-Install @"
Visual Studio debug deployment conflict.

Windows has a loose/debug registration for '$($InstalledPackage.Name)' at:
$($InstalledPackage.InstallLocation)

Visual Studio's debug deployment is conflicting with the packaged MSIX app. Close the app and remove the debug deployment from Visual Studio or Windows Settings before installing the packaged development build. This script will not remove it automatically because uninstall semantics may remove LocalState data.
"@
    }

    $installedVersion = [version]$InstalledPackage.Version
    $candidateVersion = [version]$PackageVersion

    if ($installedVersion -eq $candidateVersion) {
        return "AlreadyInstalled"
    }

    if ($installedVersion -gt $candidateVersion -and -not $AllowDowngrade) {
        Stop-Install "Downgrade refused. Installed version is $installedVersion, package version is $candidateVersion. Re-run with -AllowDowngrade only if you intentionally want Windows to attempt a downgrade."
    }

    return "Update"
}

function Invoke-PackageInstall {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][string]$Architecture
    )

    if ($Action -eq "AlreadyInstalled") {
        return
    }

    try {
        $dependencyRoot = Join-Path (Split-Path -Parent $PackagePath) "Dependencies"
        $dependencyPaths = @()
        if (Test-Path $dependencyRoot) {
            $dependencyPaths = @(Get-ChildItem -LiteralPath $dependencyRoot -Recurse -File |
                Where-Object {
                    $_.Extension -in @(".msix", ".appx", ".msixbundle", ".appxbundle") -and
                    ($_.Directory.Name -eq $Architecture -or $_.Directory.Name -eq "neutral")
                } |
                Select-Object -ExpandProperty FullName)
        }

        if ($dependencyPaths.Count -gt 0) {
            Add-AppxPackage -Path $PackagePath -DependencyPath $dependencyPaths -ErrorAction Stop
        }
        else {
            Add-AppxPackage -Path $PackagePath -ErrorAction Stop
        }
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match "0x80073CFB|higher version|newer version") {
            Stop-Install "Add-AppxPackage refused a downgrade or same-version package conflict: $message"
        }

        if ($message -match "0x80073D06|already installed|same package") {
            Stop-Install "Same-version package conflict. The package appears to already be installed. Run this script again to inspect current state. Raw AppX error: $message"
        }

        if ($message -match "0x80073CF3|dependency|framework") {
            Stop-Install "Add-AppxPackage deployment failure due to a dependency/framework issue: $message"
        }

        if ($message -match "0x800B0109|certificate|signature|trust") {
            Stop-Install "Add-AppxPackage deployment failure due to certificate trust or signature validation: $message"
        }

        Stop-Install "Add-AppxPackage deployment failure: $message"
    }
}

$repoRoot = Get-RepoRoot
$product = Get-ProductInfo -RepoRoot $repoRoot
$architecture = Convert-RuntimeIdentifierToArchitecture -Value $RuntimeIdentifier

$package = Find-DevelopmentPackage -RepoRoot $repoRoot -Product $product -Architecture $architecture

$signature = Get-AuthenticodeSignature -FilePath $package.File.FullName
if ($null -eq $signature -or $null -eq $signature.SignerCertificate) {
    Stop-Install "Invalid signature. Windows could not read a signer certificate from '$($package.File.FullName)'."
}

$certificateInfo = Find-PublicCertificate -RepoRoot $repoRoot -Product $product -PackagePath $package.File.FullName -SignerThumbprint $signature.SignerCertificate.Thumbprint

Assert-PublicCertificate -Certificate $certificateInfo.Certificate -Path $certificateInfo.Path -Product $product
Assert-PackageSignature -PackagePath $package.File.FullName -Certificate $certificateInfo.Certificate

if ($signature.SignerCertificate.Subject -ne $product.Publisher) {
    Stop-Install "Package publisher mismatch. Package signer subject '$($signature.SignerCertificate.Subject)' does not match ProductPublisher '$($product.Publisher)'."
}

$installed = Get-InstalledPackage -PackageIdentity $product.PackageIdentity
$action = Assert-InstallAllowed -InstalledPackage $installed -PackageVersion $product.PackageVersion -AllowDowngrade:$AllowDowngrade.IsPresent

$trustedBefore = Test-CertificateTrusted -Certificate $certificateInfo.Certificate
if (-not $trustedBefore -and -not (Test-IsElevated)) {
    Write-Info "This package uses a FREE self-signed DEVELOPMENT certificate."
    Write-Info "Windows must trust the public certificate locally before it can install this development MSIX."
    Write-Info "The script will import only the public .cer into $script:TrustStoreDescription and then run the install."
    Invoke-ElevatedSelf -RuntimeIdentifier $RuntimeIdentifier -AllowDowngrade:$AllowDowngrade.IsPresent
}

if (-not $trustedBefore) {
    Write-Info "Trusting development certificate in ${script:TrustStoreDescription}: $($certificateInfo.Certificate.Subject)"
    [void](Import-DevelopmentCertificate -CertificatePath $certificateInfo.Path -Certificate $certificateInfo.Certificate)
}

$trustedAfter = Test-CertificateTrusted -Certificate $certificateInfo.Certificate
if (-not $trustedAfter) {
    Stop-Install "Certificate trust failure. '$($certificateInfo.Certificate.Subject)' is not trusted in $script:TrustStoreDescription."
}

if ($action -eq "AlreadyInstalled") {
    Write-Info "Already installed:"
}
else {
    Invoke-PackageInstall -PackagePath $package.File.FullName -Action $action -Architecture $architecture
    $installed = Get-InstalledPackage -PackageIdentity $product.PackageIdentity
    if ($null -eq $installed) {
        Stop-Install "Add-AppxPackage completed, but Windows does not report '$($product.PackageIdentity)' as installed."
    }

    if ([version]$installed.Version -ne [version]$product.PackageVersion) {
        Stop-Install "Add-AppxPackage completed, but Windows reports version '$($installed.Version)' instead of expected version '$($product.PackageVersion)'."
    }

    Write-Info "Installed:"
}

Write-Info "  $($product.DisplayName)"
Write-Info "  Version: $($product.PackageVersion)"
Write-Info "  Package: $($installed.PackageFullName)"
Write-Info "  Publisher: $($product.Publisher)"
Write-Info ""
Write-Info "Development certificate:"
Write-Info "  trusted locally ($script:TrustStoreDescription)"
Write-Info ""
Write-Info "Launch from:"
Write-Info "  Windows Start Menu -> $($product.DisplayName)"
