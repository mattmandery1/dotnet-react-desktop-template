# Dotnet10Template

Dotnet10Template is a reusable application template, not a finished business product. It gives a new application a working React frontend, layered .NET backend, PostgreSQL persistence, Docker web hosting, Windows desktop hosting, MSIX packaging, tests, and one-shot product initialization.

The included `People` / `Hello` feature is deliberately small. It proves the end-to-end path:

```text
React -> ASP.NET Core API -> Application -> Infrastructure / EF Core -> PostgreSQL
```

Replace that sample with your real domain model and use cases.

## Two Runtime Modes

The repository builds two delivery/runtime modes from one shared application architecture:

```text
                         SHARED PRODUCT
                               |
                    +----------+----------+
                    |                     |
               React Web UI          .NET API
                    |                     |
                    +----------+----------+
                               |
                   Application / Domain
                        Infrastructure
                               |
                         EF Core model
                         PostgreSQL
                               |
                +--------------+--------------+
                |                             |
          WEB / DOCKER                  WINDOWS DESKTOP
                |                             |
          nginx / browser              WinUI 3 + WebView2
          Docker Compose                    |
          PostgreSQL container       RuntimeHost supervision
                                            |
                              bundled API + PostgreSQL runtime
                              local dynamic loopback ports
                              MSIX package + LocalState data
```

Shared application code:

- `src/dotnet10template.web`: React UI source.
- `src/Dotnet10Template.Api`: ASP.NET Core API.
- `src/Dotnet10Template.Application`: use cases, interfaces, orchestration.
- `src/Dotnet10Template.Domain`: core business concepts.
- `src/Dotnet10Template.Infrastructure`: EF Core, PostgreSQL persistence, external infrastructure implementations.
- EF Core migrations and the PostgreSQL data model.

Web/Docker-specific hosting:

- `compose.yaml`
- `docker/api.Dockerfile`
- `docker/web.Dockerfile`
- `docker/nginx.conf`
- PostgreSQL container, container networking, nginx browser delivery.

Windows-desktop-specific hosting:

- `Dotnet10Template.Desktop`: WinUI 3 shell and WebView2 host.
- `src/Dotnet10Template.RuntimeHost`: desktop-only supervisor for the local API and PostgreSQL processes.
- Named-pipe Desktop/RuntimeHost control IPC.
- Windows Job Object fallback cleanup.
- Bundled PostgreSQL runtime under `Dotnet10Template.Desktop/Runtime/Postgres`.
- Local API executable, dynamic localhost API/PostgreSQL ports, MSIX packaging, LocalState persistence.

Desktop and RuntimeHost exist only for the Windows desktop flavor. They are not required by the core application or Docker/web flavor.

## Quick Start

### Web / Docker

Prerequisites: Docker Desktop with the engine running.

```powershell
docker compose up --build
```

Open:

- `http://localhost:3000`
- `http://localhost:3000/api/hello`

The web container serves the Vite build through nginx. nginx proxies `/api/` to the API container, and the API uses the PostgreSQL 17 container.

Shut down:

```powershell
docker compose down
```

Docker is not required for the installed Windows desktop application.

### Windows Desktop

Prerequisites on the packaging machine: Windows, .NET SDK/workloads for this solution, Node.js/npm, PowerShell, and MSIX-capable Windows tooling.

Populate `Dotnet10Template.Desktop/Runtime/Postgres` with a complete Windows PostgreSQL runtime before packaging. See [Desktop PostgreSQL runtime](docs/desktop-postgres-runtime.md).

Build and install a development MSIX:

```powershell
.\scripts\package-desktop.ps1 -Development
.\scripts\install-desktop-dev.ps1
```

Launch from:

```text
Windows Start Menu -> Dotnet10Template Desktop
```

The installed desktop app includes the WinUI shell, React production assets, RuntimeHost, a self-contained API publish, and the private PostgreSQL runtime. It does not require Visual Studio, the .NET SDK, Node, npm, Docker, WSL, or a separately installed PostgreSQL server on the target machine.

### Create A New Product

Use the initializer once from a fresh clone:

```powershell
.\scripts\initialize-template.ps1 `
  -ProductShortName "AcmeContacts" `
  -ProductDisplayName "Acme Contacts" `
  -ProductRootNamespace "AcmeContacts" `
  -ProductPublisher "CN=AcmeContactsDevelopment" `
  -ProductPublisherDisplayName "Acme Contacts Development"
```

The initializer renames projects/folders, rewrites namespaces and product tokens, updates product metadata, generates fresh package IDs and user secrets IDs, and validates the initialized product. See [Template initialization](docs/template-initialization.md).

## Tech Stack

Current implementation:

| Area | Technology |
| --- | --- |
| Shared backend | .NET `net10.0`, C#, ASP.NET Core, OpenAPI |
| Persistence | EF Core `10.0.10`, Npgsql EF provider `10.0.3`, PostgreSQL 17 |
| Frontend | React `19.2.x`, TypeScript `~6.0.2`, Vite `8.2.x`, Node `24` in Docker web build |
| Web hosting | Docker Compose, ASP.NET `10.0` container image, nginx Alpine |
| Desktop host | WinUI 3, WebView2, Windows App SDK `2.3.1`, MSIX |
| Desktop support processes | `net8.0-windows` RuntimeHost and `net8.0-windows10.0.19041.0` Desktop project |
| Tests | xUnit v3 `3.2.2`, Moq, Testcontainers `4.14.0`, PostgreSQL 17 test container |

Package versions are centralized in `Directory.Packages.props`.

## Project Structure

```text
.
├─ Dotnet10Template.Desktop/
├─ src/
│  ├─ Dotnet10Template.Api/
│  ├─ Dotnet10Template.Application/
│  ├─ Dotnet10Template.Domain/
│  ├─ Dotnet10Template.Infrastructure/
│  ├─ Dotnet10Template.RuntimeHost/
│  └─ dotnet10template.web/
├─ tests/
│  ├─ Dotnet10Template.UnitTests/
│  └─ Dotnet10Template.IntegrationTests/
├─ docker/
├─ scripts/
└─ docs/
```

`Dotnet10Template.Desktop` is at the repository root in the filesystem. `Dotnet10Template.slnx` groups it under the `/src/` solution folder for Visual Studio organization.

Layering:

```text
Domain
  ↑
Application
  ↑       ↑
Infrastructure
  ↑
API

React UI -> API
Desktop -> RuntimeHost -> API + PostgreSQL
Docker web -> nginx -> API + PostgreSQL container
```

Project references in the current solution:

- `Domain` has no project references.
- `Application` references `Domain`.
- `Infrastructure` references `Application` and `Domain`.
- `API` references `Application` and `Infrastructure`.
- `UnitTests` reference `Application` and `Domain`.
- `IntegrationTests` reference `API` and `Infrastructure`.
- `Desktop` packages the React build, API publish output, RuntimeHost publish output, and PostgreSQL runtime through MSBuild targets rather than normal project references.
- `RuntimeHost` is a standalone desktop infrastructure executable. Do not put product business logic there.

## Product Metadata

`Directory.Product.props` is the central source for product identity and cross-project naming.

Current properties:

| Property | Used for |
| --- | --- |
| `ProductShortName` | Short product token, desktop title, web display metadata. |
| `ProductDisplayName` | Windows package display name and product display metadata. |
| `ProductRootNamespace` | Template initialization and renamed .NET root namespace. |
| `ProductDataFolderName` | Desktop LocalState subfolder. |
| `ProductEnvPrefix` | Desktop environment variable prefix, for example `<PREFIX>_DESKTOP_API_PORT`. |
| `ProductVersion` | General product version and desktop artifact folder. |
| `ProductPackageVersion` | Four-part MSIX package version. |
| `ProductPackageIdentityName` | Stable MSIX package identity. |
| `ProductPublisher` | MSIX signing certificate subject. |
| `ProductPublisherDisplayName` | Windows package publisher display name. |
| `ProductPhoneProductId` | Manifest `mp:PhoneIdentity` product ID. |
| `ProductPhonePublisherId` | Manifest `mp:PhoneIdentity` publisher ID. |
| `DesktopExecutableName` | Desktop assembly/executable name. |
| `RuntimeHostExecutableName` | RuntimeHost assembly/executable name. |
| `ApiExecutableName` | API assembly/executable name and Docker runtime entrypoint lookup. |
| `PostgresDatabaseName` | Default application database name. |
| `DockerNamePrefix` | Compose container name prefix through `.env`. |
| `WebPackageName` | React `package.json` / lockfile package name. |

`ProductVersion` can be semantic-version shaped, for example `1.0.1`. `ProductPackageVersion` must be four numeric parts, for example `1.0.1.0`, because MSIX package versions require that shape.

## Web / Docker Mode

`compose.yaml` defines three services:

- `postgres`: `postgres:17`, persistent named volume `postgres-data`, health check with `pg_isready`.
- `api`: builds `docker/api.Dockerfile`, listens on container port `8080`, applies EF migrations on startup.
- `web`: builds `docker/web.Dockerfile`, serves Vite output through nginx on container port `80`.

Default host ports come from `.env.example` and `.env`:

| Service | Default host URL |
| --- | --- |
| Web/nginx | `http://localhost:3000` |
| API through nginx | `http://localhost:3000/api/hello` |
| API direct port | `http://localhost:8080` |
| PostgreSQL | `localhost:5432` |

The API container receives its connection string from Compose:

```text
Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
```

## Windows Desktop Architecture

Desktop runtime topology:

```text
Dotnet10Template.Desktop.exe
        |
        v
Dotnet10Template.RuntimeHost.exe
        |
        +---- Dotnet10Template.Api.exe
        |
        +---- postgres.exe
```

Desktop owns the UI. It loads the React production assets in WebView2 using `https://app.local`, forwards `/api/` requests to the local API endpoint reported by RuntimeHost, displays startup/runtime failures, and asks RuntimeHost to shut down on normal close.

RuntimeHost owns the backend process lifecycle for desktop mode. It starts the private PostgreSQL server, starts the bundled API with a connection string pointing at that PostgreSQL instance, waits for health checks, monitors Desktop/API/PostgreSQL exits, coordinates graceful shutdown, and uses a Windows Job Object with `KILL_ON_JOB_CLOSE` as backend cleanup fallback.

Desktop and RuntimeHost communicate over a named pipe generated per Desktop launch. Messages include startup ready, startup error, shutdown request, shutdown complete, and fatal runtime failure.

The desktop API payload is copied to a writable per-user `ApiRuntime` directory before execution. The bundled PostgreSQL runtime is copied to a writable per-user `PostgresRuntime` directory before execution. PostgreSQL data is stored separately in `PostgresData`.

API and PostgreSQL ports are dynamically selected on `127.0.0.1` for each RuntimeHost launch. This avoids fixed-port conflicts and allows independently initialized products to run side by side. Development overrides exist through:

```powershell
$env:<ProductEnvPrefix>_DESKTOP_API_PORT = "5005"
$env:<ProductEnvPrefix>_DESKTOP_POSTGRES_PORT = "55433"
```

## Desktop Data Lifecycle

This is the template contract for desktop data:

| Scenario | Data behavior |
| --- | --- |
| Normal close | Preserve data. |
| Relaunch | Preserve data and reuse `PostgresData` when `PG_VERSION` exists. |
| Runtime crash / recovery | Preserve existing data; PostgreSQL uses normal WAL crash recovery on next launch when needed. |
| MSIX in-place update | Preserve data because the package identity is stable and update does not uninstall first. |
| Full uninstall | Delete local data. Uninstall removes this application's LocalState, including the PostgreSQL database. |
| Reinstall after uninstall | Create a fresh database; first launch runs `initdb` again and migrations recreate sample data. |

The PostgreSQL data directory currently lives under package LocalState:

```text
<package LocalState>/<ProductDataFolderName>/PostgresData
```

The uninstall helper warns:

```text
Warning: Uninstalling removes this application's local data, including its PostgreSQL database.
```

Persistent data across uninstall is not implemented by this template. A downstream product may choose a different policy, but that requires product/domain-specific design.

## Desktop Packaging

Canonical free development workflow:

```powershell
.\scripts\package-desktop.ps1 -Development
.\scripts\install-desktop-dev.ps1
```

Development packaging uses a self-signed development certificate. No paid certificate is required for this workflow.

Important behavior:

- The generated PFX is private signing material under `.certificates/` and is ignored by git.
- The package script exports only a public `.cer` into `artifacts/desktop/<ProductVersion>/`.
- The install helper validates package identity, publisher, version, architecture, signer certificate, Code Signing EKU, and signer thumbprint.
- If trust is missing, the install helper imports only the public `.cer` into `LocalMachine\Root`; this may require UAC/admin elevation.
- The helper handles fresh install, same-version already installed, in-place update from older version, and downgrade refusal unless `-AllowDowngrade` is supplied.

Regenerate the template-owned development certificate when the generated local PFX exists but its password is lost or mismatched:

```powershell
.\scripts\package-desktop.ps1 -Development -RegenerateDevelopmentCertificate
```

That option is intentionally limited to `.certificates\desktop-dev-signing.pfx` with `-Development`. It must not be generalized to delete external or production signing material.

Production signing and distribution are deliberately product concerns. Possible strategies include Microsoft Store distribution, a trusted code-signing certificate/service, enterprise deployment/trust, or another product-specific mechanism. The template does not prescribe one.

Implemented MSIX update behavior:

- Versioned MSIX packages.
- Stable package family identity through `ProductPackageIdentityName`.
- In-place upgrade through `Add-AppxPackage`.
- LocalState/Postgres preservation during in-place update.
- Downgrade guard in the development installer.

Not implemented:

- Automatic internet update discovery/download.
- Update server/feed.
- Store publishing workflow.

`Dotnet10Template.Desktop/Package.appxmanifest` is a stable source template. The Desktop project copies it to configuration/RID-specific `obj` output and stamps product metadata into the generated manifest during build. Normal builds should not mutate the shared source manifest.

More detail: [Desktop packaging](docs/desktop-packaging.md).

## Template Initialization

`scripts/initialize-template.ps1` is a one-shot initializer for fresh clones. It refuses to run against a repository that no longer looks like a fresh `Dotnet10Template` checkout.

Required inputs:

- `ProductShortName`
- `ProductDisplayName`
- `ProductRootNamespace`
- `ProductPublisher`
- `ProductPublisherDisplayName`

Optional inputs with defaults:

- `ProductDataFolderName`
- `ProductEnvPrefix`
- `ProductVersion`
- `ProductPackageVersion`
- `ProductPackageIdentityName`
- `DesktopExecutableName`
- `RuntimeHostExecutableName`
- `ApiExecutableName`
- `PostgresDatabaseName`
- `DockerNamePrefix`
- `WebPackageName`

The initializer:

- Updates `Directory.Product.props`.
- Generates fresh `ProductPhoneProductId`, `ProductPhonePublisherId`, and API `UserSecretsId`.
- Rewrites allowlisted source/config/docs files.
- Renames solution, project directories, project files, test directories, Desktop directory, and web package directory.
- Updates namespaces, usings, XAML class names, EF migration namespaces, solution entries, project references, Docker paths, launch profile names, package paths, and script/docs references.
- Removes generated local signing/package artifacts from template-owned locations.
- Scans for missed `Dotnet10Template`, `dotnet10template`, and `DOTNET10TEMPLATE` references.

Default validation runs restore, Debug/Release builds with warnings as errors, unit tests, `npm ci`, `npm run build`, `docker compose config --quiet`, and package metadata/path validation. If Docker is running, it also runs `docker compose build` and integration tests. If Docker is not running, Docker build and integration tests are skipped with a message.

Initialized repositories cannot simply rerun the initializer because the script requires fresh template structure. Use a fresh clone for a new product.

More detail: [Template initialization](docs/template-initialization.md).

## Testing And Validation Commands

From the repository root:

```powershell
dotnet restore
dotnet build -c Debug --no-restore -warnaserror
dotnet build -c Release --no-restore -warnaserror
dotnet test tests\Dotnet10Template.UnitTests\Dotnet10Template.UnitTests.csproj --no-build -c Debug
dotnet test tests\Dotnet10Template.IntegrationTests\Dotnet10Template.IntegrationTests.csproj --no-build -c Debug
```

Integration tests use Testcontainers with `postgres:17`; Docker must be running.

Frontend:

```powershell
cd src\dotnet10template.web
npm ci
npm run build
cd ..\..
```

Docker:

```powershell
docker compose config --quiet
docker compose build
docker compose up --build
docker compose down
```

Desktop packaging:

```powershell
.\scripts\package-desktop.ps1 -Development
.\scripts\install-desktop-dev.ps1
```

## Proven Template Behavior

The template has been validated for:

- Fresh template initialization.
- Renamed product initialization.
- Docker build/run through `http://localhost:3000` and `/api/hello`.
- Windows desktop package/install.
- Normal desktop shutdown.
- Desktop process kill cleanup.
- RuntimeHost kill cleanup.
- API kill detection/cleanup.
- PostgreSQL kill detection/cleanup.
- Relaunch after forced backend failure.
- Two independently initialized products installed/running simultaneously.
- Separate package families, LocalState folders, PostgreSQL data directories, dynamic API/PostgreSQL ports, and failure isolation between products.
- In-place cloned-product MSIX update with database preservation.
- Full data removal during uninstall.
- Fresh database creation after reinstall.
- Concurrent Debug/Release build safety for generated manifests.

## Web-Only Flavor

If you only want React + .NET + PostgreSQL + Docker, you can remove the desktop flavor. Do not do this in the template checkout unless you intentionally want to maintain a stripped fork.

Safe cleanup path for the current repository:

1. Remove the Desktop project directory:

   ```text
   Dotnet10Template.Desktop/
   ```

2. Remove the desktop supervisor project:

   ```text
   src/Dotnet10Template.RuntimeHost/
   ```

3. Remove these project entries from `Dotnet10Template.slnx`:

   ```text
   Dotnet10Template.Desktop/Dotnet10Template.Desktop.csproj
   src/Dotnet10Template.RuntimeHost/Dotnet10Template.RuntimeHost.csproj
   ```

4. Remove desktop-only scripts if you no longer package MSIX:

   ```text
   scripts/package-desktop.ps1
   scripts/install-desktop-dev.ps1
   scripts/uninstall-desktop-dev.ps1
   ```

5. Remove desktop-only docs/artifacts:

   ```text
   docs/desktop-packaging.md
   docs/desktop-postgres-runtime.md
   artifacts/desktop/
   .certificates/
   ```

6. Clean desktop-only ignore rules if desired:

   - `.gitignore` entries for `Dotnet10Template.Desktop/Runtime/Postgres`.
   - `.dockerignore` entry excluding `Dotnet10Template.Desktop`.

7. Keep `Directory.Product.props` initially. The web/API/Docker path still uses `ProductShortName`, `ProductDisplayName`, `ProductRootNamespace`, `ProductVersion`, `ApiExecutableName`, `PostgresDatabaseName`, `DockerNamePrefix`, and `WebPackageName`. Desktop-only properties may remain unused safely.

8. If you maintain a stripped reusable template, update `scripts/initialize-template.ps1` before using it again. The current initializer validates and renames Desktop and RuntimeHost paths, and `Test-PackageDesktopMetadata` expects desktop packaging references.

After this cleanup, the Docker/web path is still:

```powershell
docker compose up --build
```

RuntimeHost should be treated as desktop infrastructure when desktop mode is kept. Business and domain behavior belongs in Domain/Application/Infrastructure/API/React, not in RuntimeHost.

## What This Template Does Not Provide

- Finished business/domain functionality.
- Production authentication strategy.
- Authorization or role model.
- Production cloud infrastructure.
- Public production signing/distribution workflow.
- Automatic internet update discovery/download.
- Cross-uninstall desktop data persistence.
- Backup/restore strategy.
- Production monitoring/observability strategy.
- Product-specific security/compliance policy.

## Troubleshooting

Docker engine unavailable:

- Symptoms include Docker pipe/engine errors, Compose failures, or Testcontainers failures.
- Start Docker Desktop and wait for the engine to be ready.
- Integration tests require Docker; unit tests do not.

Stale development PFX password:

- If `package-desktop.ps1 -Development` cannot read the generated local PFX, regenerate only the template-owned development certificate:

  ```powershell
  .\scripts\package-desktop.ps1 -Development -RegenerateDevelopmentCertificate
  ```

Visual Studio loose AppX registration conflict:

- Visual Studio can register a loose/debug package with the same package identity.
- `install-desktop-dev.ps1` stops with a targeted message instead of removing it automatically because removal may affect LocalState.
- Close the app and remove the debug deployment from Visual Studio or Windows Settings before installing the packaged development build.

Downgrade refused:

- The development installer refuses to install an older `ProductPackageVersion` over a newer installed package.
- Use `-AllowDowngrade` only when you intentionally want Windows to attempt a downgrade.

Node/npm missing:

- The React build and desktop package script run `npm ci` and `npm run build`.
- Install Node.js/npm on development and packaging machines.

Desktop launch/runtime failures:

- Check desktop logs under the product LocalState folder.
- RuntimeHost logs selected API/PostgreSQL ports, process starts/stops, initdb, readiness, and shutdown behavior.

## Security And Certificate Hygiene

- `.pfx` files contain private signing material. Do not commit or share them.
- `.certificates/`, `artifacts/`, `*.pfx`, package outputs, `node_modules`, `bin`, and `obj` are ignored by git.
- The public `.cer` can be distributed for development trust, but it is not a public production trust mechanism.
- `install-desktop-dev.ps1` imports only the public `.cer` and refuses private-key material.
- Self-signed development signing is for development/testing/self-hosting workflows. Public production signing and distribution are downstream product decisions.

## Deeper Documentation

- [Desktop PostgreSQL runtime](docs/desktop-postgres-runtime.md)
- [Desktop packaging](docs/desktop-packaging.md)
- [Template initialization](docs/template-initialization.md)
