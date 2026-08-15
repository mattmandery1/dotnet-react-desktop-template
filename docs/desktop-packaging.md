# Desktop packaging

`scripts/package-desktop.ps1` builds a local MSIX-style desktop installer for `Dotnet10Template.Desktop`.

The installed app is intended to run without Visual Studio, the .NET SDK, Node, npm, Docker, WSL, or a separately installed PostgreSQL server. The desktop package includes the WinUI app, the RuntimeHost supervisor, the React production assets, the self-contained local API publish output, and the private PostgreSQL runtime already stored under `Dotnet10Template.Desktop/Runtime/Postgres`.

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
- RuntimeHost executable name
- API executable name

`Dotnet10Template.Desktop/Package.appxmanifest` is stamped from those values during build and by `scripts/package-desktop.ps1` before packaging.

Metadata relationships:

- `ProductDisplayName` is the human-readable Start Menu/package display name.
- `DesktopExecutableName` controls the desktop app assembly and executable name. It is explicit and is not inferred from the project filename.
- `RuntimeHostExecutableName` controls the RuntimeHost supervisor assembly and executable name.
- `ApiExecutableName` controls the API assembly and executable name produced by desktop packaging.
- `ProductPackageIdentityName` is the stable Windows package identity. Keeping it stable is what allows upgrades to target the same installed app.
- `ProductPublisher` is the package signing publisher subject and must match the signing certificate subject.
- `ProductPublisherDisplayName` is the human-readable publisher name shown by Windows.
- `ProductVersion` is the generic product version used for artifact folders.
- `ProductPackageVersion` is the four-part Windows package version used in the manifest/package.

`Dotnet10Template.Desktop` and `Dotnet10Template.RuntimeHost` embed executable names as assembly metadata during build. Desktop uses that metadata to locate RuntimeHost, and RuntimeHost uses it to locate the bundled API, so changing executable names in `Directory.Product.props` does not require manually editing host path constants in source files.

## Canonical free development flow

The template's development installer path is free. It uses a local self-signed development certificate and does not require Azure Artifact Signing, a paid code-signing certificate, Microsoft Store distribution, or any commercial signing provider.

Run from the repository root:

```powershell
.\scripts\package-desktop.ps1 -Development
.\scripts\install-desktop-dev.ps1
```

On the first install, accept the normal Windows UAC prompt if local certificate trust is required. After install, launch the app from:

```text
Windows Start Menu > Dotnet10Template Desktop
```

For updates, rerun the same two commands after changing `ProductPackageVersion` in `Directory.Product.props`. The installer updates in place with `Add-AppxPackage`; it does not uninstall first.

## Development signing

Windows requires locally installed MSIX packages to be signed by a trusted certificate.

`.\scripts\package-desktop.ps1 -Development` generates or reuses a local development signing certificate under `.certificates/`, which is ignored by git. The PFX contains the private key and must never be committed. The packaging script exports only the public certificate into the artifact folder for installation trust.

If the generated development PFX already exists but you no longer know its password, regenerate only the template-owned development certificate material:

```powershell
.\scripts\package-desktop.ps1 -Development -RegenerateDevelopmentCertificate
```

This option is intentionally limited to the default `.certificates\desktop-dev-signing.pfx` development path. It does not delete external or production signing material passed with `-CertificatePath`.

The installer helper:

- locates the expected MSIX/AppX package from `Directory.Product.props`
- verifies package identity, publisher, version, architecture, certificate subject, certificate expiration, Code Signing EKU, and signer thumbprint
- imports only the public `.cer` if trust is missing
- never imports the `.pfx` or exposes private-key material

The selected development trust store is:

```text
LocalMachine\Root
```

Reason: the generated development signing certificate is self-signed. For AppX/MSIX deployment, Windows must be able to build trust to a local trust anchor. On the validated Windows development machine, `CurrentUser\TrustedPeople` was not sufficient for `Add-AppxPackage`, while trusting the public self-signed certificate in `LocalMachine\Root` allowed development package install/update. The helper uses only this one store and only for the exact public certificate matching the package signer.

If trust is missing and the shell is not elevated, `install-desktop-dev.ps1` explains the self-signed development certificate and relaunches itself with a normal UAC prompt. If UAC is declined, rerun from an elevated PowerShell window:

```powershell
.\scripts\install-desktop-dev.ps1
```

Self-signed development signing is not a recommendation for public end-user distribution. Publicly trusted signing, Store distribution, enterprise deployment, or any other production signing choice belongs to the cloned product/domain, not the open-source template.

To use an existing certificate for packaging:

```powershell
$password = Read-Host -AsSecureString "PFX password"
.\scripts\package-desktop.ps1 -Configuration Release -RuntimeIdentifier win-x64 -CertificatePath C:\path\to\signing.pfx -CertificatePassword $password
```

The certificate subject must match `ProductPublisher` in `Directory.Product.props`.

## Usage

Run from the repository root:

```powershell
.\scripts\package-desktop.ps1 -Development
```

The script:

- restores and builds the React app with `npm ci` and `npm run build`
- publishes the API project self-contained for the selected runtime identifier
- publishes the RuntimeHost project self-contained for the selected runtime identifier
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

The script also writes intermediate API and RuntimeHost publish folders under the same versioned artifact directory so the exact payloads used for the package can be inspected.

## Install/update

```powershell
.\scripts\install-desktop-dev.ps1
```

The helper handles not-installed, same-version, older-version, and newer-version cases:

- not installed: installs the package
- same version already installed: reports that it is current
- older version installed: updates in place without uninstalling
- newer version installed: refuses downgrade unless `-AllowDowngrade` is supplied intentionally

The developer should not need to locate `.cer`, `.msix`, generated `_Test` folders, `Add-AppDevPackage.ps1`, `PackageFullName`, certificate stores, or package versions manually.

## Visual Studio debug deployment conflict

Visual Studio can register a loose/debug app package from the project `bin` folder while debugging. That registration has the same package identity as the packaged app but is not the installed MSIX package.

If Windows reports that this loose registration conflicts with the packaged app, `install-desktop-dev.ps1` stops with a targeted message. It does not automatically remove the debug deployment because removal/uninstall semantics may affect LocalState data. Close the app and remove the debug deployment from Visual Studio or Windows Settings before installing the packaged development build.

## Manual uninstall

Uninstall from Windows Settings:

```text
Settings > Apps > Installed apps > Dotnet10Template Desktop > Uninstall
```

or use PowerShell:

```powershell
.\scripts\uninstall-desktop-dev.ps1
```

The uninstall helper identifies only the package identity from `Directory.Product.props`, shows the package/version being removed, warns about LocalState/PostgresData, and requires typing `REMOVE` unless `-Force` is supplied. It does not remove certificates.

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

RuntimeHost is packaged under:

```text
RuntimeHost\
```

The API is packaged under:

```text
Api\
```

## Data behavior

First launch initializes `PostgresData` with `initdb`, creates the application database, and applies normal application initialization. Later launches reuse the existing data directory when `PG_VERSION` is present.

Package upgrades with the same package identity are expected to preserve the per-user local app data area, so the PostgreSQL data directory is reused across upgrades.

Ordinary uninstall follows Windows/MSIX package behavior for per-user app data. This project does not add custom uninstall code and does not intentionally delete PostgreSQL data.

Observed local validation on Windows on August 11, 2026: `Remove-AppxPackage` removed the package LocalState data for this app, so reinstalling the same package caused PostgreSQL to initialize a fresh `PostgresData` directory. Closing and relaunching without uninstall preserved and reused the existing database.
