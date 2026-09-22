# AGENTS.md

Guidance for AI coding agents and automated contributors working in this repository. Use the implementation as source of truth. Do not repeat README workflow prose here; keep this file focused on invariants, ownership boundaries, validation, and forbidden shortcuts.

## Core Architecture

This repository has one shared application and two delivery/runtime modes.

Shared application:
- React UI: `src/dotnet10template.web`
- API: `src/Dotnet10Template.Api`
- Application: `src/Dotnet10Template.Application`
- Domain: `src/Dotnet10Template.Domain`
- Infrastructure: `src/Dotnet10Template.Infrastructure`
- EF Core/PostgreSQL model and migrations in Infrastructure

Mode-specific hosting:
- Web/Docker: `compose.yaml`, `docker/api.Dockerfile`, `docker/web.Dockerfile`, `docker/nginx.conf`, PostgreSQL container, nginx, browser delivery.
- Windows Desktop: `Dotnet10Template.Desktop`, WinUI 3/WebView2, `src/Dotnet10Template.RuntimeHost`, named-pipe IPC, bundled API, bundled PostgreSQL runtime, dynamic loopback ports, MSIX, LocalState.

Preserve this split. Business/domain behavior belongs in the shared application unless there is a compelling platform-specific reason. Do not put normal business logic in Desktop or RuntimeHost.

## Project References And Layering

Current project-reference boundaries:
- Domain has no project references.
- Application references Domain.
- Infrastructure references Application and Domain.
- API references Application and Infrastructure.
- UnitTests reference Application and Domain.
- IntegrationTests reference API and Infrastructure.
- Desktop packages React/API/RuntimeHost/PostgreSQL payloads through MSBuild targets; it does not own business logic.
- RuntimeHost is a standalone desktop infrastructure executable.

Layer rules:
- Domain: core business entities/value concepts only. No dependency on Application, Infrastructure, API, Desktop, RuntimeHost, or React.
- Application: use cases, abstractions, orchestration. Depends on Domain.
- Infrastructure: EF Core, PostgreSQL persistence, implementation of Application abstractions, external infrastructure integrations. Depends on Application and Domain.
- API: HTTP transport and composition root. Depends on Application and Infrastructure.
- React: UI/client. Consumes API. Do not embed backend persistence assumptions.
- Desktop: Windows UI host, WebView2 host, RuntimeHost client, lifecycle UI. No domain logic.
- RuntimeHost: desktop-only process supervisor, API/PostgreSQL lifecycle, named-pipe control, dynamic ports, Windows Job Object. No domain logic.
- Tests: unit tests should target Domain/Application behavior; integration tests may use API/Infrastructure/PostgreSQL.

Web-only downstream products must be possible: shared projects and React must not depend on Desktop or RuntimeHost.

## Desktop Process Ownership

Desktop runtime topology:

```text
Dotnet10Template.Desktop.exe
  -> Dotnet10Template.RuntimeHost.exe
      -> Dotnet10Template.Api.exe
      -> postgres.exe
```

RuntimeHost owns the exact backend `Process` instances it starts. Preserve these behaviors:
- Normal Desktop close: Desktop sends shutdown over the named pipe; RuntimeHost gracefully stops API, then PostgreSQL, releases ports, sends shutdown-complete, and exits.
- Desktop killed: RuntimeHost detects Desktop PID exit, cleans up backend processes, and exits.
- RuntimeHost killed: Windows Job Object closes and removes owned API/PostgreSQL children.
- API dies unexpectedly: RuntimeHost reports fatal state, stops PostgreSQL, and exits.
- PostgreSQL dies unexpectedly: RuntimeHost reports fatal state, stops API, and exits.
- Desktop reports fatal runtime state when RuntimeHost exits unexpectedly or sends a fatal message.

Do not:
- Kill by generic process name.
- Scan for unrelated `postgres.exe` instances.
- Introduce machine-specific watchdogs.
- Use fixed control ports.
- Silently restart backend children without an explicit architecture decision.

## Dynamic Ports

Desktop API and PostgreSQL ports are selected at runtime on `127.0.0.1`.

Rules:
- Treat them as ephemeral.
- Do not persist them.
- Do not use them as product identity.
- Expect them to change between launches.
- Do not hardcode desktop API/PostgreSQL ports as default architecture.
- Optional environment overrides exist only for debugging: `<ProductEnvPrefix>_DESKTOP_API_PORT` and `<ProductEnvPrefix>_DESKTOP_POSTGRES_PORT`.
- Docker/web networking is separate and may use configured ports from `.env`.

## Desktop Data Lifecycle

Current desktop data policy:
- Normal close: preserve data.
- Relaunch: preserve data.
- Runtime crash/recovery: preserve existing data; PostgreSQL performs normal recovery on next launch.
- MSIX in-place update: preserve data through stable package identity.
- Full uninstall: delete package LocalState, including PostgreSQL data.
- Reinstall after uninstall: fresh `initdb` and database.

Desktop data lives under package LocalState at `<LocalState>/<ProductDataFolderName>/`, with `PostgresData`, `PostgresRuntime`, `ApiRuntime`, and logs below it. Do not move desktop data outside LocalState without an explicit product-level architecture change.

## Product Identity

`Directory.Product.props` is the central product identity source. Use existing metadata instead of scattering hardcoded identity strings.

Current properties:
- `ProductShortName`: `Dotnet10Template`
- `ProductDisplayName`: `Dotnet10Template Desktop`
- `ProductRootNamespace`: `Dotnet10Template`
- `ProductDataFolderName`: `Dotnet10Template`
- `ProductEnvPrefix`: `DOTNET10TEMPLATE`
- `ProductVersion`: `1.0.1`
- `ProductPackageVersion`: `1.0.1.0`
- `ProductPackageIdentityName`: `Dotnet10Template.Desktop`
- `ProductPublisher`: `CN=Dotnet10TemplateDevelopment`
- `ProductPublisherDisplayName`: `Dotnet10Template Development`
- `ProductPhoneProductId`: `90de26b1-99bf-40a0-a4f6-7460aa9166d8`
- `ProductPhonePublisherId`: `4c9cbdf7-4581-4e2c-bf15-740a149556ef`
- `DesktopExecutableName`: `Dotnet10Template.Desktop`
- `RuntimeHostExecutableName`: `Dotnet10Template.RuntimeHost`
- `ApiExecutableName`: `Dotnet10Template.Api`
- `PostgresDatabaseName`: `dotnet10template`
- `DockerNamePrefix`: `dotnet10template`
- `WebPackageName`: `dotnet10template.web`

Important:
- Desktop ports are not product identity.
- `app.local` is an implementation host name, not product metadata.
- `ProductPackageIdentityName` and `ProductPublisher` affect MSIX update identity.
- Executable names derive from centralized metadata where implemented.
- Do not introduce new hardcoded `Dotnet10Template` identity strings into runtime/config code unless structurally required by template initialization.

## Template Initializer

`scripts/initialize-template.ps1` is intentionally one-shot for fresh template clones. Do not add a casual `-Force` or reinitialize mode.

It currently handles solution/project/folder renames, namespaces/usings, XAML classes, EF migration namespaces, project references, RuntimeHost paths, Docker paths, web package naming, product metadata, generated IDs, API `UserSecretsId`, token scans, generated artifact cleanup, and validation.

If adding a product-specific path/token/project:
- Update initializer behavior.
- Update remaining-token scan expectations.
- Validate against a disposable renamed product.
- Do not manually repair the disposable clone; template bugs belong in the template.

## Desktop Manifest And Packaging

`Dotnet10Template.Desktop/Package.appxmanifest` is a stable source template. Build targets copy it to configuration/RID-specific `obj` output and stamp product metadata there. Do not mutate the shared source manifest during normal builds. Preserve concurrent Debug/Release build safety.

Free development packaging workflow:

```powershell
.\scripts\package-desktop.ps1 -Development
.\scripts\install-desktop-dev.ps1
```

Development certificate policy:
- Self-signed development certificate is allowed.
- Public `.cer` may be trusted locally.
- PFX contains private key and must not be committed/shared.
- Install helper imports only the public `.cer`.
- Development signing is not public production trust.

Do not make paid signing/services mandatory for the template. Production signing/distribution is a downstream product concern.

Never commit PFX files, private keys, package artifacts, machine-specific certificate paths, or certificate passwords.

## Docker Rules

Web/Docker mode is independent of Desktop. Docker is not required for installed desktop runtime.

Docker build context must stay small. Preserve required root metadata:
- `Directory.Build.props`
- `Directory.Product.props`
- `Directory.Packages.props`

Do not accidentally include generated or desktop-only payloads in Docker context:
- `bin/`, `obj/`, `artifacts/`, `node_modules/`, `dist/`
- `.certificates/`
- `AppPackages/`, `BundleArtifacts/`
- bundled desktop PostgreSQL runtime
- package outputs

Integration tests use Docker/Testcontainers and require Docker engine availability.

## Sample Domain

People/Hello is a minimal vertical slice proving:

```text
React -> API -> Application -> Infrastructure/EF -> PostgreSQL
```

Do not treat it as meaningful business functionality. When replacing it in a product, preserve the architecture and intentionally update tests, migrations, seed behavior, API endpoints, and React calls.

## Validation Matrix

Use exact current commands from the repo root unless a change clearly narrows the required set:

```powershell
dotnet restore
dotnet build -c Debug --no-restore -warnaserror
dotnet build -c Release --no-restore -warnaserror
dotnet test tests\Dotnet10Template.UnitTests\Dotnet10Template.UnitTests.csproj --no-build -c Debug
dotnet test tests\Dotnet10Template.IntegrationTests\Dotnet10Template.IntegrationTests.csproj --no-build -c Debug
```

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

Desktop package:

```powershell
.\scripts\package-desktop.ps1 -Development
.\scripts\install-desktop-dev.ps1
```

Initializer disposable rename validation:

```powershell
.\scripts\initialize-template.ps1 `
  -ProductShortName "AcmeContacts" `
  -ProductDisplayName "Acme Contacts" `
  -ProductRootNamespace "AcmeContacts" `
  -ProductPublisher "CN=AcmeContactsDevelopment" `
  -ProductPublisherDisplayName "Acme Contacts Development"
```

Do not claim success if a relevant validation path was skipped. If Docker is unavailable, report Docker/integration validation as environmentally blocked, not green.

## Change-Specific Validation

Choose validation based on blast radius:
- Shared backend/domain: Debug/Release builds, unit tests, integration tests, React/API smoke path if relevant.
- React: `npm ci`, `npm run build`, Docker web validation, and desktop bundled UI/package validation if production assets are affected.
- Desktop/RuntimeHost: package/build, normal close, relevant process-failure scenario, and verify no orphan backend processes.
- Product identity/packaging: disposable renamed initialization, package renamed product, verify identity/publisher/version, preserve update semantics.
- Docker: `docker compose config --quiet`, `docker compose build`, `docker compose up --build`, `/api/hello`, integration tests.
- Initializer: run on a fresh disposable copy, no manual repair, scan remaining template tokens, build/test renamed result.

## Do Not Casually Redesign

These decisions require deliberate discussion before changing:
- Dual-mode shared application architecture.
- PostgreSQL as the database.
- RuntimeHost process-supervision model.
- Named-pipe Desktop/RuntimeHost IPC.
- Windows Job Object fallback.
- Dynamic desktop ports.
- LocalState desktop data location and lifecycle policy.
- MSIX packaging/update identity.
- Centralized product metadata.
- One-shot initializer.
- Docker/web separation.
- Shared React UI.

Implementation details may be improved, but do not replace these foundations as incidental refactors.

## Generated, Vendor, And Local Files

Avoid editing generated/vendor/local output directly:
- `.git/`, `.vs/`
- `bin/`, `obj/`, `Debug/`, `Release/`, `x64/`, `x86/`, `ARM64/`
- `artifacts/`
- `node_modules/`
- `src/dotnet10template.web/dist/`
- `.certificates/`
- `AppPackages/`, `BundleArtifacts/`
- `*.appx`, `*.appxbundle`, `*.msix`, `*.msixbundle`, `*.appxupload`
- `*.pfx`, private keys, local certificate material
- generated intermediate manifests under `obj/`
- bundled PostgreSQL binaries under `Dotnet10Template.Desktop/Runtime/Postgres/**` unless explicitly updating the desktop runtime payload

The repository keeps `.gitkeep` placeholders for PostgreSQL runtime folders. Preserve ignore behavior when changing runtime payload handling.

## Security And Secrets

Do not:
- Commit secrets, PFX files, private keys, bearer tokens, package signing passwords, or machine-specific certificate paths.
- Print PFX passwords or log bearer tokens/secrets.
- Weaken certificate/signature validation to make packaging pass.
- Disable vulnerability auditing globally to get builds green.
- Suppress dependency vulnerability warnings instead of resolving them where practical.

## Documentation Expectations

If behavior changes:
- Update README when public developer workflow changes.
- Update deeper docs under `docs/` when implementation, lifecycle, packaging, or initializer behavior changes.
- Update `AGENTS.md` only when architectural/contributor rules change.

Do not let documentation drift from implementation.

## Agent Work Style

1. Inspect before modifying.
2. Prefer narrow changes.
3. Preserve current architecture unless the task explicitly requests redesign.
4. Report root cause before workaround.
5. Do not add machine-specific paths, usernames, ports, or package-family IDs.
6. Do not quietly patch disposable test clones.
7. Do not report unexecuted tests as passed.
8. If the environment blocks validation, state that explicitly.
9. Keep provider-specific CI/CD out unless specifically requested.
10. Avoid adding infrastructure merely because it might be useful someday.
