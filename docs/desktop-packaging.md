# Desktop packaging

`scripts/package-desktop.ps1` builds a local MSIX-style desktop installer for `Dotnet10Template.Desktop`.

The installed app is intended to run without Visual Studio, the .NET SDK, Node, npm, Docker, WSL, or a separately installed PostgreSQL server. The desktop package includes the WinUI app, the React production assets, the self-contained local API publish output, and the private PostgreSQL runtime already stored under `Dotnet10Template.Desktop/Runtime/Postgres`.

## Prerequisites

Install these tools on the machine that builds the installer:

- .NET SDK with the workloads/packages required by this solution.
- Node.js and npm, because the packaging script builds the React production assets.
- PowerShell 7 or Windows PowerShell.
- A Windows machine capable of building and installing MSIX packages.

The generated installer does not require these tools on the target machine.

## Product metadata

Generic product metadata is centralized in:

```text
Directory.Product.props
```

The desktop package script reads this file for:

- display name
- package identity
- publisher
- package version
- desktop executable name
- API executable name

`Dotnet10Template.Desktop/Package.appxmanifest` is stamped from those values during build and by `scripts/package-desktop.ps1` before packaging.

Metadata relationships:

- `ProductDisplayName` is the human-readable Start Menu/package display name.
- `DesktopExecutableName` controls the desktop app assembly and executable name. It is explicit and is not inferred from the project filename.
- `ApiExecutableName` controls the API assembly and executable name produced by desktop packaging.
- `ProductPackageIdentityName` is the stable Windows package identity. Keeping it stable is what allows upgrades to target the same installed app.
- `ProductPublisher` is the package signing publisher subject and must match the signing certificate subject.
- `ProductPublisherDisplayName` is the human-readable publisher name shown by Windows.
- `ProductVersion` is the generic product version used for artifact folders.
- `ProductPackageVersion` is the four-part Windows package version used in the manifest/package.

`Dotnet10Template.Desktop` embeds `ApiExecutableName` as assembly metadata during build. The desktop runtime host reads that metadata when locating the bundled API, so changing `ApiExecutableName` in `Directory.Product.props` does not require manually editing host path constants in source files.

## Signing

Windows requires locally installed MSIX packages to be signed by a trusted certificate.

For development, generate a local certificate with:

```powershell
$password = Read-Host -AsSecureString "PFX password"
.\scripts\package-desktop.ps1 -Configuration Release -RuntimeIdentifier win-x64 -GenerateDevelopmentCertificate -CertificatePassword $password
```

This creates a local PFX under `.certificates/`, which is ignored by git. Do not commit PFX files or private keys.

To use an existing certificate:

```powershell
$password = Read-Host -AsSecureString "PFX password"
.\scripts\package-desktop.ps1 -Configuration Release -RuntimeIdentifier win-x64 -CertificatePath C:\path\to\signing.pfx -CertificatePassword $password
```

The certificate subject must match `ProductPublisher` in `Directory.Product.props`. A future publisher certificate can replace the development certificate by passing its PFX path and password to the same script.

## Usage

Run from the repository root:

```powershell
.\scripts\package-desktop.ps1 -Configuration Release -RuntimeIdentifier win-x64 -CertificatePath C:\path\to\signing.pfx
```

The script:

- restores and builds the React app with `npm ci` and `npm run build`
- publishes the API project self-contained for the selected runtime identifier
- publishes/packages `Dotnet10Template.Desktop`
- signs the generated package
- places final artifacts under `artifacts/desktop/<version>/`
- prints every generated installer/signing artifact path

The default values are:

```powershell
-Configuration Release
-RuntimeIdentifier win-x64
```

## Artifacts

Final artifacts are written under:

```text
artifacts/desktop/<ProductVersion>/
```

Expected files include the generated `.msix` or `.appx` package, package install helper files produced by MSBuild, and any exported `.cer` file when a development certificate is generated.

The script also writes an intermediate API publish folder under the same versioned artifact directory so the exact API payload used for the package can be inspected.

## Manual install

If the signing certificate is not already trusted, install the public `.cer` into:

```text
Local Machine > Trusted Root Certification Authorities
```

Installing to the machine trusted root store requires approval/elevation. On the validated Windows machine, `Current User > Trusted People` was not sufficient for `Add-AppxPackage`; AppX deployment rejected the package until the self-signed development certificate was trusted in `LocalMachine\Root`.

Then install the package with one of these options:

```powershell
Add-AppxPackage .\artifacts\desktop\<version>\<package-file>.msix
```

or run the generated `Add-AppDevPackage.ps1` helper if MSBuild produced one.

After installation, launch `Dotnet10Template Desktop` from the Windows Start Menu.

## Manual uninstall

Uninstall from Windows Settings:

```text
Settings > Apps > Installed apps > Dotnet10Template Desktop > Uninstall
```

or use PowerShell:

```powershell
Get-AppxPackage Dotnet10Template.Desktop | Remove-AppxPackage
```

## PostgreSQL data

The desktop app stores PostgreSQL data outside the app install folder. At runtime it asks Windows App SDK for the packaged per-user local app data path and appends:

```text
<ProductDataFolderName>\PostgresData
```

The exact resolved path is written to the desktop host log on startup. In packaged runs this is under the app package's per-user local data area. The ordinary unpackaged fallback is:

```text
%LOCALAPPDATA%\<ProductDataFolderName>\PostgresData
```

The PostgreSQL log is stored beside the data folder as:

```text
<ProductDataFolderName>\postgres.log
```

The packaged app also copies the bundled private PostgreSQL runtime to:

```text
<ProductDataFolderName>\PostgresRuntime
```

PostgreSQL is executed from that per-user runtime copy. The package still remains the source for the private PostgreSQL binaries; `postgres.exe` is not renamed and no external PostgreSQL installation is used.

## Data behavior

First launch initializes `PostgresData` with `initdb`, creates the application database, and applies normal application initialization. Later launches reuse the existing data directory when `PG_VERSION` is present.

Package upgrades with the same package identity are expected to preserve the per-user local app data area, so the PostgreSQL data directory is reused across upgrades.

Ordinary uninstall follows Windows/MSIX package behavior for per-user app data. This project does not add custom uninstall code and does not intentionally delete PostgreSQL data.

Observed local validation on Windows on August 11, 2026: `Remove-AppxPackage` removed the package LocalState data for this app, so reinstalling the same package caused PostgreSQL to initialize a fresh `PostgresData` directory. Closing and relaunching without uninstall preserved and reused the existing database.
