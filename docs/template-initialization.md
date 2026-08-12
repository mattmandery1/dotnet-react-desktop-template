# Template initialization

Use `scripts/initialize-template.ps1` after cloning this repository to turn the template into a new product. Run it once from the repository root.

## Prerequisites

- PowerShell 7 or Windows PowerShell 5.1
- .NET SDKs/workloads required by the repository
- Node.js and npm
- Docker Desktop for Docker validation and integration tests
- Windows packaging tooling when validating desktop packaging later

## Example

```powershell
.\scripts\initialize-template.ps1 `
  -ProductShortName "AcmeContacts" `
  -ProductDisplayName "Acme Contacts" `
  -ProductRootNamespace "AcmeContacts" `
  -ProductPublisher "CN=AcmeContactsDevelopment" `
  -ProductPublisherDisplayName "Acme Contacts Development"
```

## Required inputs

- `ProductShortName`: short product token used for derived names, for example `AcmeContacts`.
- `ProductDisplayName`: user-visible product name, for example `Acme Contacts`.
- `ProductRootNamespace`: .NET root namespace, for example `AcmeContacts`.
- `ProductPublisher`: MSIX signing publisher subject, for example `CN=AcmeContactsDevelopment`.
- `ProductPublisherDisplayName`: user-visible publisher name.

## Derived values

These can be overridden when needed:

- `ProductDataFolderName`: defaults to `ProductShortName`.
- `ProductEnvPrefix`: defaults to an uppercase environment token derived from `ProductShortName`, for example `ACMECONTACTS`.
- `ProductVersion`: defaults to `1.0.0`.
- `ProductPackageVersion`: defaults to `ProductVersion` plus `.0`, for example `1.0.0.0`.
- `ProductPackageIdentityName`: defaults to `<ProductRootNamespace>.Desktop`.
- `DesktopExecutableName`: defaults to `<ProductRootNamespace>.Desktop`.
- `ApiExecutableName`: defaults to `<ProductRootNamespace>.Api`.
- `PostgresDatabaseName`: defaults to a lowercase database token derived from `ProductShortName`, for example `acme_contacts`.
- `DockerNamePrefix`: defaults to a lowercase Docker token derived from `ProductShortName`, for example `acme-contacts`.
- `WebPackageName`: defaults to `<docker-token>.web`, for example `acme-contacts.web`.

## Generated values

Every initialization generates fresh values for:

- `ProductPhoneProductId`
- `ProductPhonePublisherId`
- API `UserSecretsId`

Known generated local signing material and package outputs are removed from `.certificates`, `artifacts`, `Dotnet10Template.Desktop/AppPackages`, `Dotnet10Template.Desktop/artifacts`, and `Dotnet10Template.Desktop/BundleArtifacts`. Other certificate files elsewhere in the repository are preserved. Do not reuse template `.pfx` files or private keys. The initialized product should generate its own development certificate when `scripts/package-desktop.ps1` is run with `-GenerateDevelopmentCertificate`.

## Renamed structure

The initializer structurally renames:

- `Dotnet10Template.slnx`
- `Dotnet10Template.Desktop`
- `src/Dotnet10Template.Api`
- `src/Dotnet10Template.Application`
- `src/Dotnet10Template.Domain`
- `src/Dotnet10Template.Infrastructure`
- `tests/Dotnet10Template.UnitTests`
- `tests/Dotnet10Template.IntegrationTests`
- `src/dotnet10template.web`

It also renames matching `.csproj` and `.http` files, updates solution entries, project references, Docker paths, package paths, launch profile names, C# namespaces/usings, XAML `x:Class` values, EF migration namespaces, EF model snapshot namespaces, and structural references in scripts and docs.

## Intentionally unchanged

The initializer does not replace the sample `Person`/`Hello` domain. The initialized product should still return:

```text
Hello World, the names are Matt, Tony, Bob
```

The following implementation constants remain unchanged:

- `app.local`
- `/api`
- `/health`
- PostgreSQL executable and tool names
- dynamic API/PostgreSQL port allocation behavior
- desktop runtime folder layout

## Safety

The script validates names before modifying files. It refuses to run against a repository that no longer looks like a fresh `Dotnet10Template` clone. To initialize a product, use a fresh clone or restore the checkout before running the script.

Replacement is limited to source/config/documentation file allowlists and skips generated or vendor folders such as `.git`, `.vs`, `bin`, `obj`, `artifacts`, `node_modules`, `dist`, `.certificates`, `AppPackages`, and the bundled PostgreSQL runtime binaries.

After rewriting, the script scans for remaining `Dotnet10Template`, `dotnet10template`, and `DOTNET10TEMPLATE` references. Remaining references must be categorized as intentional template-initializer references or generated/vendor artifacts; missed structural references fail the run.

## Validation

By default the script runs:

- `dotnet restore`
- `dotnet build -c Debug --no-restore`
- `dotnet build -c Release --no-restore`
- unit tests
- `npm ci`
- `npm run build`
- `docker compose config --quiet`
- package metadata/path validation for `scripts/package-desktop.ps1`

If Docker is running, it also runs:

- `docker compose build`
- integration tests

Use `-SkipValidation` only when the local machine is missing required tooling and validation will be performed elsewhere.

## Next steps

After initialization, review `Directory.Product.props`, commit the rename, then perform a clean-clone install and packaging test for the new product. When packaging the desktop app, create a new development signing certificate for the initialized publisher.
