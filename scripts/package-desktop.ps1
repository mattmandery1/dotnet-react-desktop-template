[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$CertificatePath,
    [securestring]$CertificatePassword,
    [switch]$GenerateDevelopmentCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [string]$WorkingDirectory = (Get-Location).Path
    )

    Write-Host ">> $FilePath $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Xml,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $node = $Xml.Project.PropertyGroup.ChildNodes |
        Where-Object { $_.NodeType -eq "Element" -and $_.Name -eq $Name } |
        Select-Object -First 1

    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Directory.Product.props is missing required property '$Name'."
    }

    return $node.InnerText.Trim()
}

function ConvertTo-PlainText {
    param([securestring]$Value)

    if ($null -eq $Value) {
        return $null
    }

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

function Read-SigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Password
    )

    $flags =
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet -bor
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet

    try {
        if ([string]::IsNullOrEmpty($Password)) {
            return [Security.Cryptography.X509Certificates.X509Certificate2]::new($Path, $null, $flags)
        }

        return [Security.Cryptography.X509Certificates.X509Certificate2]::new($Path, $Password, $flags)
    }
    catch {
        throw "Unable to read signing certificate '$Path'. If this is a generated development certificate, delete the matching .pfx/.cer files and rerun with -GenerateDevelopmentCertificate. $($_.Exception.Message)"
    }
}

function Assert-SigningCertificateBase {
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSubject
    )

    if ($Certificate.Subject -ne $ExpectedSubject) {
        throw "Signing certificate subject '$($Certificate.Subject)' does not match package publisher '$ExpectedSubject'."
    }

    if (-not $Certificate.HasPrivateKey) {
        throw "Signing certificate must include a private key: $Path"
    }
}

function Assert-DevelopmentSigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSubject
    )

    Assert-SigningCertificateBase `
        -Certificate $Certificate `
        -Path $Path `
        -ExpectedSubject $ExpectedSubject

    $basicConstraints = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.19" } |
        Select-Object -First 1

    if ($null -eq $basicConstraints) {
        throw "Existing development signing certificate '$Path' is missing the Basic Constraints extension required by the generated Add-AppDevPackage.ps1 flow. Delete '$Path' and its .cer file, then rerun with -GenerateDevelopmentCertificate."
    }

    $basicConstraints = [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basicConstraints
    if ($basicConstraints.CertificateAuthority) {
        throw "Existing development signing certificate '$Path' has Basic Constraints CA=true. Delete '$Path' and its .cer file, then rerun with -GenerateDevelopmentCertificate."
    }

    $keyUsage = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.15" } |
        Select-Object -First 1

    if ($null -eq $keyUsage) {
        throw "Existing development signing certificate '$Path' is missing the Digital Signature key usage required for MSIX signing. Delete '$Path' and its .cer file, then rerun with -GenerateDevelopmentCertificate."
    }

    $keyUsage = [Security.Cryptography.X509Certificates.X509KeyUsageExtension]$keyUsage
    if (($keyUsage.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -eq 0) {
        throw "Existing development signing certificate '$Path' does not allow Digital Signature key usage. Delete '$Path' and its .cer file, then rerun with -GenerateDevelopmentCertificate."
    }

    $enhancedKeyUsage = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
        Select-Object -First 1

    if ($null -eq $enhancedKeyUsage) {
        throw "Existing development signing certificate '$Path' is missing the Code Signing enhanced key usage required for MSIX signing. Delete '$Path' and its .cer file, then rerun with -GenerateDevelopmentCertificate."
    }

    $enhancedKeyUsage = [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$enhancedKeyUsage
    $hasCodeSigning = $enhancedKeyUsage.EnhancedKeyUsages |
        Where-Object { $_.Value -eq "1.3.6.1.5.5.7.3.3" } |
        Select-Object -First 1

    if ($null -eq $hasCodeSigning) {
        throw "Existing development signing certificate '$Path' is missing the Code Signing enhanced key usage required for MSIX signing. Delete '$Path' and its .cer file, then rerun with -GenerateDevelopmentCertificate."
    }
}

function Import-SigningCertificate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [string]$Password,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSubject
    )

    $certificate = Read-SigningCertificate -Path $Path -Password $Password
    Assert-SigningCertificateBase `
        -Certificate $certificate `
        -Path $Path `
        -ExpectedSubject $ExpectedSubject

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::My,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)

    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $store.Add($certificate)
    }
    finally {
        $store.Close()
    }

    return $certificate
}

$repoRoot = (Get-Location).Path
if (-not (Test-Path (Join-Path $repoRoot "Dotnet10Template.slnx"))) {
    throw "Run this script from the repository root."
}

$productPropsPath = Join-Path $repoRoot "Directory.Product.props"
if (-not (Test-Path $productPropsPath)) {
    throw "Directory.Product.props was not found."
}

[xml]$productProps = Get-Content -Raw $productPropsPath
$productVersion = Get-RequiredProperty $productProps "ProductVersion"
$packageVersion = Get-RequiredProperty $productProps "ProductPackageVersion"
$packageIdentity = Get-RequiredProperty $productProps "ProductPackageIdentityName"
$publisher = Get-RequiredProperty $productProps "ProductPublisher"
$displayName = Get-RequiredProperty $productProps "ProductDisplayName"

$manifestPath = Join-Path $repoRoot "Dotnet10Template.Desktop\Package.appxmanifest"
[xml]$manifest = Get-Content -Raw $manifestPath
$identity = $manifest.Package.Identity
if ($identity.Name -ne $packageIdentity -or $identity.Publisher -ne $publisher -or $identity.Version -ne $packageVersion) {
    throw "Package.appxmanifest identity metadata must match Directory.Product.props. Expected '$packageIdentity', '$publisher', '$packageVersion'."
}

$artifactRoot = Join-Path $repoRoot "artifacts\desktop\$productVersion"
$apiPublishDir = Join-Path $artifactRoot "api-publish-$RuntimeIdentifier"
$desktopProject = Join-Path $repoRoot "Dotnet10Template.Desktop\Dotnet10Template.Desktop.csproj"
$apiProject = Join-Path $repoRoot "src\Dotnet10Template.Api\Dotnet10Template.Api.csproj"
$webProjectDir = Join-Path $repoRoot "src\dotnet10template.web"

$platform = switch ($RuntimeIdentifier) {
    "win-x64" { "x64" }
    "win-x86" { "x86" }
    "win-arm64" { "ARM64" }
    default { throw "Unsupported desktop runtime identifier '$RuntimeIdentifier'." }
}

Remove-Item -LiteralPath $artifactRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

if ($GenerateDevelopmentCertificate -and [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $repoRoot ".certificates\desktop-dev-signing.pfx"
}

if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and -not [IO.Path]::IsPathRooted($CertificatePath)) {
    $CertificatePath = Join-Path $repoRoot $CertificatePath
}

$certificatePasswordText = ConvertTo-PlainText $CertificatePassword
if ($GenerateDevelopmentCertificate) {
    if ([string]::IsNullOrWhiteSpace($certificatePasswordText)) {
        throw "Use -CertificatePassword when generating a development signing certificate."
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $CertificatePath) | Out-Null
    if (-not (Test-Path $CertificatePath)) {
        Write-Host "Creating development signing certificate: $CertificatePath"
        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $publisher `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -KeyExportPolicy Exportable `
            -KeySpec Signature `
            -HashAlgorithm SHA256 `
            -KeyUsage DigitalSignature `
            -FriendlyName "$displayName Development Signing" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -NotAfter (Get-Date).AddYears(3) `
            -TextExtension @(
                "2.5.29.19={critical}{text}CA=false",
                "2.5.29.37={text}1.3.6.1.5.5.7.3.3"
            )

        Export-PfxCertificate `
            -Cert $cert `
            -FilePath $CertificatePath `
            -Password $CertificatePassword | Out-Null

        Export-Certificate `
            -Cert $cert `
            -FilePath ([IO.Path]::ChangeExtension($CertificatePath, ".cer")) | Out-Null
    }

    $developmentCertificate = Read-SigningCertificate `
        -Path $CertificatePath `
        -Password $certificatePasswordText

    Assert-DevelopmentSigningCertificate `
        -Certificate $developmentCertificate `
        -Path $CertificatePath `
        -ExpectedSubject $publisher
}

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    throw "Provide -CertificatePath for MSIX signing, or use -GenerateDevelopmentCertificate with -CertificatePassword."
}

if (-not (Test-Path $CertificatePath)) {
    throw "Signing certificate was not found: $CertificatePath"
}

$signingCertificate = Import-SigningCertificate `
    -Path $CertificatePath `
    -Password $certificatePasswordText `
    -ExpectedSubject $publisher

$publicCertificatePath = Join-Path $artifactRoot "desktop-signing.cer"
Export-Certificate -Cert $signingCertificate -FilePath $publicCertificatePath -Force | Out-Null

Write-Host "Packaging $displayName $productVersion for $RuntimeIdentifier."

Invoke-Checked -FilePath "npm" -Arguments @("ci") -WorkingDirectory $webProjectDir
Invoke-Checked -FilePath "npm" -Arguments @("run", "build") -WorkingDirectory $webProjectDir

Invoke-Checked -FilePath "dotnet" -Arguments @(
    "publish",
    $apiProject,
    "--nologo",
    "--configuration", $Configuration,
    "--runtime", $RuntimeIdentifier,
    "--self-contained", "true",
    "--output", $apiPublishDir,
    "-p:UseAppHost=true",
    "-p:PublishSingleFile=false"
) -WorkingDirectory $repoRoot

$packageArgs = @(
    "publish",
    $desktopProject,
    "--nologo",
    "--configuration", $Configuration,
    "--runtime", $RuntimeIdentifier,
    "--self-contained", "true",
    "-p:Platform=$platform",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:AppxBundle=Never",
    "-p:UapAppxPackageBuildMode=SideloadOnly",
    "-p:AppxSymbolPackageEnabled=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:AppxPackageSigningEnabled=true",
    "-p:PackageCertificateThumbprint=$($signingCertificate.Thumbprint)",
    "-p:AppxPackageDir=$(($artifactRoot -replace '\\', '/') + '/')",
    "-p:PackageVersion=$packageVersion"
)

Invoke-Checked -FilePath "dotnet" -Arguments $packageArgs -WorkingDirectory $repoRoot

$projectScopedArtifactRoot = Join-Path $repoRoot "Dotnet10Template.Desktop\artifacts\desktop\$productVersion"
if (Test-Path $projectScopedArtifactRoot) {
    Get-ChildItem -LiteralPath $projectScopedArtifactRoot -Recurse -File | ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath($projectScopedArtifactRoot, $_.FullName)
        $destination = Join-Path $artifactRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}

$producedArtifacts = @(Get-ChildItem -LiteralPath $artifactRoot -Recurse -File |
    Where-Object {
        $_.Extension -in @(".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".cer") -or
        $_.Name -match "Add-AppDevPackage\.ps1$"
    } |
    Sort-Object FullName)

if ($producedArtifacts.Count -eq 0) {
    throw "Desktop package build completed but no installable artifacts were found under $artifactRoot."
}

Write-Host ""
Write-Host "Desktop package artifacts:"
foreach ($artifact in $producedArtifacts) {
    Write-Host $artifact.FullName
}
