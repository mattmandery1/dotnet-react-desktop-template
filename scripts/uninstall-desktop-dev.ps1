[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

trap {
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
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
        throw "Directory.Product.props is missing required property '$Name'."
    }

    return $node.InnerText.Trim()
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
[xml]$props = Get-Content -Raw (Join-Path $repoRoot "Directory.Product.props")
$displayName = Get-RequiredProperty $props "ProductDisplayName"
$packageIdentity = Get-RequiredProperty $props "ProductPackageIdentityName"

$package = Get-AppxPackage -Name $packageIdentity -ErrorAction SilentlyContinue |
    Sort-Object Version -Descending |
    Select-Object -First 1

if ($null -eq $package) {
    Write-Host "Not installed: $displayName ($packageIdentity)"
    exit 0
}

Write-Host "Package selected for removal:"
Write-Host "  $displayName"
Write-Host "  Version: $($package.Version)"
Write-Host "  Package: $($package.PackageFullName)"
Write-Host ""
Write-Host "Warning: current MSIX uninstall semantics may remove this app's LocalState, including PostgresData."

if (-not $Force) {
    $answer = Read-Host "Type REMOVE to uninstall this package"
    if ($answer -ne "REMOVE") {
        Write-Host "Uninstall cancelled."
        exit 1
    }
}

if ($PSCmdlet.ShouldProcess($package.PackageFullName, "Remove-AppxPackage")) {
    Remove-AppxPackage -Package $package.PackageFullName
    Write-Host "Removed: $($package.PackageFullName)"
}
