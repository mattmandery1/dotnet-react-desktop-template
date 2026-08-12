[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductShortName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductDisplayName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductRootNamespace,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductPublisher,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ProductPublisherDisplayName,

    [string]$ProductDataFolderName,
    [string]$ProductEnvPrefix,
    [string]$ProductVersion = "1.0.0",
    [string]$ProductPackageVersion,
    [string]$ProductPackageIdentityName,
    [string]$DesktopExecutableName,
    [string]$ApiExecutableName,
    [string]$PostgresDatabaseName,
    [string]$DockerNamePrefix,
    [string]$WebPackageName,

    [switch]$SkipValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$TemplatePascalName = "Dotnet10Template"
$TemplateLowerName = "dotnet10template"
$TemplateUpperName = "DOTNET10TEMPLATE"

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "==> $Message"
}

function Test-ValidDotNetIdentifierPart {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value -match '^[A-Za-z_][A-Za-z0-9_]*$'
}

function Assert-DotNetNamespace {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is required."
    }

    foreach ($part in $Value.Split(".")) {
        if (-not (Test-ValidDotNetIdentifierPart $part)) {
            throw "$Name must be a valid .NET namespace. Invalid segment: '$part'."
        }
    }
}

function Assert-SimpleToken {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if ($Value -notmatch '^[A-Za-z][A-Za-z0-9._-]*$') {
        throw "$Name must start with a letter and contain only letters, digits, dot, underscore, or hyphen."
    }
}

function Assert-Publisher {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '^CN=[^,=]+$') {
        throw "ProductPublisher must be a simple certificate subject such as 'CN=AcmeContactsDevelopment'."
    }
}

function Assert-DisplayText {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is required."
    }
    if ($Value -match '[\r\n`"]') {
        throw "$Name cannot contain newlines, backticks, or double quotes."
    }
}

function ConvertTo-KebabOrLower {
    param([Parameter(Mandatory = $true)][string]$Value)

    $normalized = $Value -creplace '([a-z0-9])([A-Z])', '$1-$2'
    $normalized = $normalized -creplace '[^A-Za-z0-9]+', '-'
    return $normalized.Trim("-").ToLowerInvariant()
}

function ConvertTo-UpperToken {
    param([Parameter(Mandatory = $true)][string]$Value)
    return ($Value -replace '[^A-Za-z0-9]+', '_').Trim("_").ToUpperInvariant()
}

function ConvertTo-DatabaseName {
    param([Parameter(Mandatory = $true)][string]$Value)
    return ($Value -creplace '([a-z0-9])([A-Z])', '$1_$2' -creplace '[^A-Za-z0-9]+', '_' ).Trim("_").ToLowerInvariant()
}

function Get-RepoRoot {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw "Unable to determine script path."
    }

    return [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $scriptPath) ".."))
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPath = [IO.Path]::GetFullPath($Root)
    if (-not $rootPath.EndsWith([IO.Path]::DirectorySeparatorChar)) {
        $rootPath = "$rootPath$([IO.Path]::DirectorySeparatorChar)"
    }

    $targetPath = [IO.Path]::GetFullPath($Path)
    $rootUri = [Uri]::new($rootPath)
    $targetUri = [Uri]::new($targetPath)
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($targetUri).ToString()).Replace("/", [IO.Path]::DirectorySeparatorChar)
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

function Set-ProductProperty {
    param(
        [Parameter(Mandatory = $true)][xml]$Xml,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $propertyGroup = $Xml.Project.PropertyGroup | Select-Object -First 1
    $node = $propertyGroup.ChildNodes |
        Where-Object { $_.NodeType -eq "Element" -and $_.Name -eq $Name } |
        Select-Object -First 1

    if ($null -eq $node) {
        $node = $Xml.CreateElement($Name)
        [void]$propertyGroup.AppendChild($node)
    }

    $node.InnerText = $Value
}

function Set-ProjectProperty {
    param(
        [Parameter(Mandatory = $true)][xml]$Xml,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $propertyGroup = $Xml.Project.PropertyGroup |
        Where-Object {
            $_.ChildNodes |
                Where-Object { $_.NodeType -eq "Element" -and $_.Name -eq $Name } |
                Select-Object -First 1
        } |
        Select-Object -First 1

    if ($null -eq $propertyGroup) {
        $propertyGroup = $Xml.Project.PropertyGroup | Select-Object -First 1
    }
    if ($null -eq $propertyGroup) {
        $propertyGroup = $Xml.CreateElement("PropertyGroup")
        [void]$Xml.Project.PrependChild($propertyGroup)
    }

    $node = $propertyGroup.ChildNodes |
        Where-Object { $_.NodeType -eq "Element" -and $_.Name -eq $Name } |
        Select-Object -First 1

    if ($null -eq $node) {
        $node = $Xml.CreateElement($Name)
        [void]$propertyGroup.AppendChild($node)
    }

    $node.InnerText = $Value
}

function Save-XmlDocument {
    param(
        [Parameter(Mandatory = $true)][xml]$Xml,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $true
    $settings.NewLineChars = "`r`n"
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try {
        $Xml.Save($writer)
    }
    finally {
        $writer.Close()
    }
}

function Get-AllowedSourceFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $excludedSegments = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    @(
        ".git",
        ".vs",
        "bin",
        "obj",
        "artifacts",
        "node_modules",
        "dist",
        ".certificates",
        "AppPackages",
        "BundleArtifacts"
    ) | ForEach-Object { [void]$excludedSegments.Add($_) }

    $excludedRelativePrefixes = @(
        "Dotnet10Template.Desktop/Runtime/Postgres/",
        "$ProductRootNamespace.Desktop/Runtime/Postgres/"
    )

    $allowedExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    @(
        ".appxmanifest",
        ".config",
        ".cs",
        ".csproj",
        ".css",
        ".html",
        ".http",
        ".js",
        ".json",
        ".manifest",
        ".md",
        ".mjs",
        ".props",
        ".ps1",
        ".slnx",
        ".targets",
        ".ts",
        ".tsx",
        ".xaml",
        ".xml",
        ".yaml",
        ".yml"
    ) | ForEach-Object { [void]$allowedExtensions.Add($_) }

    Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        Where-Object {
            $relativePath = (Get-RelativePath -Root $Root -Path $_.FullName).Replace("\", "/")
            foreach ($prefix in $excludedRelativePrefixes) {
                if ($relativePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }

            foreach ($part in $relativePath.Split("/")) {
                if ($excludedSegments.Contains($part)) {
                    return $false
                }
            }

            if ($_.Name -in @("Dockerfile", "api.Dockerfile", "web.Dockerfile", ".dockerignore", ".env", ".env.example")) {
                return $true
            }

            return $allowedExtensions.Contains($_.Extension)
        }
}

function Update-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$ReplacementMap,
        [Parameter(Mandatory = $true)][Collections.Generic.List[string]]$ModifiedFiles
    )

    $original = [IO.File]::ReadAllText($Path)
    $updated = $original

    foreach ($replacement in $ReplacementMap) {
        $updated = $updated.Replace([string]$replacement.From, [string]$replacement.To)
    }

    if ($updated -ne $original) {
        [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
        [void]$ModifiedFiles.Add($Path)
        Write-Host "Modified file: $(Get-RelativePath -Root $repoRoot -Path $Path)"
    }
}

function Rename-PathIfExists {
    param(
        [Parameter(Mandatory = $true)][string]$From,
        [Parameter(Mandatory = $true)][string]$To,
        [Collections.Generic.List[string]]$RenamedPaths
    )

    if (-not (Test-Path -LiteralPath $From)) {
        return
    }

    if (Test-Path -LiteralPath $To) {
        throw "Cannot rename '$From' to '$To' because the destination already exists."
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $To) | Out-Null
    Move-Item -LiteralPath $From -Destination $To
    $message = "$(Get-RelativePath -Root $repoRoot -Path $From) -> $(Get-RelativePath -Root $repoRoot -Path $To)"
    [void]$RenamedPaths.Add($message)
    Write-Host "Renamed path: $message"
}

function Remove-GeneratedSigningArtifacts {
    param([Parameter(Mandatory = $true)][string]$Root)

    $paths = @(
        ".certificates",
        "artifacts",
        "Dotnet10Template.Desktop/AppPackages",
        "Dotnet10Template.Desktop/artifacts",
        "Dotnet10Template.Desktop/BundleArtifacts"
    )

    foreach ($relativePath in $paths) {
        $path = Join-Path $Root $relativePath
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
            Write-Host "Removed generated artifact: $relativePath"
        }
    }

}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$WorkingDirectory = $repoRoot
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

function Test-DockerRunning {
    try {
        & docker info *> $null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Test-PackageDesktopMetadata {
    param([Parameter(Mandatory = $true)][string]$Root)

    $scriptPath = Join-Path $Root "scripts/package-desktop.ps1"
    $scriptText = [IO.File]::ReadAllText($scriptPath)

    foreach ($expected in @(
        "$ProductRootNamespace.slnx",
        "$ProductRootNamespace.Desktop\Package.appxmanifest",
        "$ProductRootNamespace.Desktop\$ProductRootNamespace.Desktop.csproj",
        "src\$ProductRootNamespace.Api\$ProductRootNamespace.Api.csproj",
        "src\$WebPackageName"
    )) {
        if (-not $scriptText.Contains($expected)) {
            throw "package-desktop.ps1 does not contain expected renamed path '$expected'."
        }
    }

    [xml]$productProps = Get-Content -Raw (Join-Path $Root "Directory.Product.props")
    [void](Get-RequiredProperty $productProps "ProductPackageIdentityName")
    [void](Get-RequiredProperty $productProps "ProductPublisher")
    [void](Get-RequiredProperty $productProps "ProductPackageVersion")
    [void](Get-RequiredProperty $productProps "ProductPhoneProductId")
    [void](Get-RequiredProperty $productProps "ProductPhonePublisherId")
}

function Get-RemainingReferenceScan {
    param([Parameter(Mandatory = $true)][string]$Root)

    $references = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-AllowedSourceFiles -Root $Root) {
        $relativePath = (Get-RelativePath -Root $Root -Path $file.FullName).Replace("\", "/")
        $text = [IO.File]::ReadAllText($file.FullName)

        foreach ($token in @($TemplatePascalName, $TemplateLowerName, $TemplateUpperName)) {
            if ($text.Contains($token)) {
                $category = "missed structural rename"
                if ($relativePath -in @("scripts/initialize-template.ps1", "docs/template-initialization.md")) {
                    $category = "intentional implementation/example reference"
                }

                [void]$references.Add([pscustomobject]@{
                    Token = $token
                    Path = $relativePath
                    Category = $category
                })
            }
        }
    }

    return $references
}

$repoRoot = Get-RepoRoot
Push-Location $repoRoot
try {
    Write-Step "Validating inputs"
    Assert-DotNetNamespace -Name "ProductRootNamespace" -Value $ProductRootNamespace
    Assert-SimpleToken -Name "ProductShortName" -Value $ProductShortName
    Assert-Publisher -Value $ProductPublisher
    Assert-DisplayText -Name "ProductDisplayName" -Value $ProductDisplayName
    Assert-DisplayText -Name "ProductPublisherDisplayName" -Value $ProductPublisherDisplayName

    if ([string]::IsNullOrWhiteSpace($ProductDataFolderName)) {
        $ProductDataFolderName = $ProductShortName
    }
    if ([string]::IsNullOrWhiteSpace($ProductEnvPrefix)) {
        $ProductEnvPrefix = ConvertTo-UpperToken $ProductShortName
    }
    if ([string]::IsNullOrWhiteSpace($ProductPackageVersion)) {
        $ProductPackageVersion = "$(($ProductVersion -split '[-+]')[0]).0"
    }
    if ([string]::IsNullOrWhiteSpace($ProductPackageIdentityName)) {
        $ProductPackageIdentityName = "$ProductRootNamespace.Desktop"
    }
    if ([string]::IsNullOrWhiteSpace($DesktopExecutableName)) {
        $DesktopExecutableName = "$ProductRootNamespace.Desktop"
    }
    if ([string]::IsNullOrWhiteSpace($ApiExecutableName)) {
        $ApiExecutableName = "$ProductRootNamespace.Api"
    }
    if ([string]::IsNullOrWhiteSpace($PostgresDatabaseName)) {
        $PostgresDatabaseName = ConvertTo-DatabaseName $ProductShortName
    }
    if ([string]::IsNullOrWhiteSpace($DockerNamePrefix)) {
        $DockerNamePrefix = ConvertTo-KebabOrLower $ProductShortName
    }
    if ([string]::IsNullOrWhiteSpace($WebPackageName)) {
        $WebPackageName = "$(ConvertTo-KebabOrLower $ProductShortName).web"
    }

    foreach ($pair in @{
        ProductDataFolderName = $ProductDataFolderName
        ProductEnvPrefix = $ProductEnvPrefix
        ProductPackageIdentityName = $ProductPackageIdentityName
        DesktopExecutableName = $DesktopExecutableName
        ApiExecutableName = $ApiExecutableName
        DockerNamePrefix = $DockerNamePrefix
        WebPackageName = $WebPackageName
    }.GetEnumerator()) {
        Assert-SimpleToken -Name $pair.Key -Value ([string]$pair.Value)
    }

    if ($PostgresDatabaseName -notmatch '^[a-z][a-z0-9_]*$') {
        throw "PostgresDatabaseName must start with a lowercase letter and contain only lowercase letters, digits, and underscores."
    }
    if ($ProductVersion -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
        throw "ProductVersion must look like a semantic version, for example '1.0.0'."
    }
    if ($ProductPackageVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "ProductPackageVersion must use four numeric parts, for example '1.0.0.0'."
    }

    $productPropsPath = Join-Path $repoRoot "Directory.Product.props"
    $solutionPath = Join-Path $repoRoot "$TemplatePascalName.slnx"
    $apiProjectPath = Join-Path $repoRoot "src/$TemplatePascalName.Api/$TemplatePascalName.Api.csproj"
    if (-not (Test-Path -LiteralPath $productPropsPath)) {
        throw "Directory.Product.props was not found. Run this script from a clone of the template repository."
    }

    [xml]$existingProductProps = Get-Content -Raw $productPropsPath
    $existingShortName = Get-RequiredProperty $existingProductProps "ProductShortName"
    if (($existingShortName -ne $TemplatePascalName) -or
        (-not (Test-Path -LiteralPath $solutionPath)) -or
        (-not (Test-Path -LiteralPath $apiProjectPath))) {
        throw "This repository does not look like a fresh Dotnet10Template clone. Use a fresh clone or restore your checkout before running template initialization."
    }

    Write-Step "Updating centralized product identity"
    $phoneProductId = [guid]::NewGuid().ToString()
    $phonePublisherId = [guid]::NewGuid().ToString()
    $apiUserSecretsId = [guid]::NewGuid().ToString()

    foreach ($entry in @{
        ProductShortName = $ProductShortName
        ProductDisplayName = $ProductDisplayName
        ProductRootNamespace = $ProductRootNamespace
        ProductDataFolderName = $ProductDataFolderName
        ProductEnvPrefix = $ProductEnvPrefix
        ProductVersion = $ProductVersion
        ProductPackageVersion = $ProductPackageVersion
        ProductPackageIdentityName = $ProductPackageIdentityName
        ProductPublisher = $ProductPublisher
        ProductPublisherDisplayName = $ProductPublisherDisplayName
        ProductPhoneProductId = $phoneProductId
        ProductPhonePublisherId = $phonePublisherId
        DesktopExecutableName = $DesktopExecutableName
        ApiExecutableName = $ApiExecutableName
        PostgresDatabaseName = $PostgresDatabaseName
        DockerNamePrefix = $DockerNamePrefix
        WebPackageName = $WebPackageName
    }.GetEnumerator()) {
        Set-ProductProperty -Xml $existingProductProps -Name $entry.Key -Value ([string]$entry.Value)
    }
    Save-XmlDocument -Xml $existingProductProps -Path $productPropsPath

    $modifiedFiles = [Collections.Generic.List[string]]::new()
    [void]$modifiedFiles.Add($productPropsPath)
    Write-Host "Modified file: Directory.Product.props"

    [xml]$apiProject = Get-Content -Raw $apiProjectPath
    Set-ProjectProperty -Xml $apiProject -Name "UserSecretsId" -Value $apiUserSecretsId
    Save-XmlDocument -Xml $apiProject -Path $apiProjectPath
    [void]$modifiedFiles.Add($apiProjectPath)
    Write-Host "Modified file: $(Get-RelativePath -Root $repoRoot -Path $apiProjectPath)"

    Write-Step "Rewriting allowlisted source files"
    $replacementMap = @(
        [pscustomobject]@{ From = "$TemplatePascalName.Api_HostAddress"; To = "$ProductRootNamespace.Api_HostAddress" }
        [pscustomobject]@{ From = "$TemplatePascalName.IntegrationTests"; To = "$ProductRootNamespace.IntegrationTests" }
        [pscustomobject]@{ From = "$TemplatePascalName.UnitTests"; To = "$ProductRootNamespace.UnitTests" }
        [pscustomobject]@{ From = "$TemplatePascalName.Infrastructure"; To = "$ProductRootNamespace.Infrastructure" }
        [pscustomobject]@{ From = "$TemplatePascalName.Application"; To = "$ProductRootNamespace.Application" }
        [pscustomobject]@{ From = "$TemplatePascalName.Desktop"; To = "$ProductRootNamespace.Desktop" }
        [pscustomobject]@{ From = "$TemplatePascalName.Domain"; To = "$ProductRootNamespace.Domain" }
        [pscustomobject]@{ From = $TemplatePascalName; To = $ProductRootNamespace }
        [pscustomobject]@{ From = $TemplateLowerName; To = $WebPackageName.Replace(".web", "") }
        [pscustomobject]@{ From = $TemplateUpperName; To = $ProductEnvPrefix }
    )

    foreach ($file in Get-AllowedSourceFiles -Root $repoRoot) {
        $relativePath = (Get-RelativePath -Root $repoRoot -Path $file.FullName).Replace("\", "/")
        if ($relativePath -eq "scripts/initialize-template.ps1") {
            continue
        }

        Update-TextFile -Path $file.FullName -ReplacementMap $replacementMap -ModifiedFiles $modifiedFiles
    }

    Write-Step "Removing generated signing and package artifacts"
    Remove-GeneratedSigningArtifacts -Root $repoRoot

    Write-Step "Renaming project files and directories"
    $renamedPaths = [Collections.Generic.List[string]]::new()

    $directoryRenames = @(
        @("src/$TemplatePascalName.Api", "src/$ProductRootNamespace.Api"),
        @("src/$TemplatePascalName.Application", "src/$ProductRootNamespace.Application"),
        @("src/$TemplatePascalName.Domain", "src/$ProductRootNamespace.Domain"),
        @("src/$TemplatePascalName.Infrastructure", "src/$ProductRootNamespace.Infrastructure"),
        @("tests/$TemplatePascalName.UnitTests", "tests/$ProductRootNamespace.UnitTests"),
        @("tests/$TemplatePascalName.IntegrationTests", "tests/$ProductRootNamespace.IntegrationTests"),
        @("src/$TemplateLowerName.web", "src/$WebPackageName"),
        @("$TemplatePascalName.Desktop", "$ProductRootNamespace.Desktop")
    )

    foreach ($rename in $directoryRenames) {
        Rename-PathIfExists -From (Join-Path $repoRoot $rename[0]) -To (Join-Path $repoRoot $rename[1]) -RenamedPaths $renamedPaths
    }

    $fileRenames = @(
        @("src/$ProductRootNamespace.Api/$TemplatePascalName.Api.csproj", "src/$ProductRootNamespace.Api/$ProductRootNamespace.Api.csproj"),
        @("src/$ProductRootNamespace.Api/$TemplatePascalName.Api.http", "src/$ProductRootNamespace.Api/$ProductRootNamespace.Api.http"),
        @("src/$ProductRootNamespace.Application/$TemplatePascalName.Application.csproj", "src/$ProductRootNamespace.Application/$ProductRootNamespace.Application.csproj"),
        @("src/$ProductRootNamespace.Domain/$TemplatePascalName.Domain.csproj", "src/$ProductRootNamespace.Domain/$ProductRootNamespace.Domain.csproj"),
        @("src/$ProductRootNamespace.Infrastructure/$TemplatePascalName.Infrastructure.csproj", "src/$ProductRootNamespace.Infrastructure/$ProductRootNamespace.Infrastructure.csproj"),
        @("tests/$ProductRootNamespace.UnitTests/$TemplatePascalName.UnitTests.csproj", "tests/$ProductRootNamespace.UnitTests/$ProductRootNamespace.UnitTests.csproj"),
        @("tests/$ProductRootNamespace.IntegrationTests/$TemplatePascalName.IntegrationTests.csproj", "tests/$ProductRootNamespace.IntegrationTests/$ProductRootNamespace.IntegrationTests.csproj"),
        @("$ProductRootNamespace.Desktop/$TemplatePascalName.Desktop.csproj", "$ProductRootNamespace.Desktop/$ProductRootNamespace.Desktop.csproj"),
        @("$TemplatePascalName.slnx", "$ProductRootNamespace.slnx")
    )

    foreach ($rename in $fileRenames) {
        Rename-PathIfExists -From (Join-Path $repoRoot $rename[0]) -To (Join-Path $repoRoot $rename[1]) -RenamedPaths $renamedPaths
    }

    Write-Step "Scanning for remaining template references"
    $remainingReferences = @(Get-RemainingReferenceScan -Root $repoRoot)
    foreach ($reference in $remainingReferences) {
        Write-Host "Remaining $($reference.Category): $($reference.Token) in $($reference.Path)"
    }

    $missedReferences = @($remainingReferences | Where-Object { $_.Category -eq "missed structural rename" })
    if ($missedReferences.Count -gt 0) {
        throw "Template initialization left $($missedReferences.Count) missed structural reference(s). See scan output above."
    }

    if ($SkipValidation) {
        Write-Step "Validation skipped"
    }
    else {
        Write-Step "Validating initialized product"
        Invoke-Checked -FilePath "dotnet" -Arguments @("restore")
        Invoke-Checked -FilePath "dotnet" -Arguments @("build", "-c", "Debug", "--no-restore", "-warnaserror")
        Invoke-Checked -FilePath "dotnet" -Arguments @("build", "-c", "Release", "--no-restore", "-warnaserror")
        Invoke-Checked -FilePath "dotnet" -Arguments @("test", "tests/$ProductRootNamespace.UnitTests/$ProductRootNamespace.UnitTests.csproj", "--no-build", "-c", "Debug")
        Invoke-Checked -FilePath "npm" -Arguments @("ci") -WorkingDirectory (Join-Path $repoRoot "src/$WebPackageName")
        Invoke-Checked -FilePath "npm" -Arguments @("run", "build") -WorkingDirectory (Join-Path $repoRoot "src/$WebPackageName")
        Invoke-Checked -FilePath "docker" -Arguments @("compose", "config", "--quiet")
        Test-PackageDesktopMetadata -Root $repoRoot
        Write-Host "package-desktop.ps1 metadata/path validation passed."

        if (Test-DockerRunning) {
            Invoke-Checked -FilePath "docker" -Arguments @("compose", "build")
            Invoke-Checked -FilePath "dotnet" -Arguments @("test", "tests/$ProductRootNamespace.IntegrationTests/$ProductRootNamespace.IntegrationTests.csproj", "--no-build", "-c", "Debug")
        }
        else {
            Write-Host "Docker is not running; skipped docker compose build and integration tests."
        }
    }

    Write-Host ""
    Write-Host "Template initialization complete."
    Write-Host "Generated ProductPhoneProductId: $phoneProductId"
    Write-Host "Generated ProductPhonePublisherId: $phonePublisherId"
    Write-Host "Generated API UserSecretsId: $apiUserSecretsId"
    Write-Host ""
    Write-Host "Renamed paths:"
    $renamedPaths | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "Modified files:"
    $modifiedFiles |
        Sort-Object -Unique |
        ForEach-Object { Write-Host "  $(Get-RelativePath -Root $repoRoot -Path $_)" }
}
finally {
    Pop-Location
}
