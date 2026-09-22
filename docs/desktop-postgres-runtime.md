# Desktop PostgreSQL runtime

`Dotnet10Template.Desktop` owns the user experience. It launches `Dotnet10Template.RuntimeHost`, and RuntimeHost owns the private PostgreSQL and API processes in desktop mode. Docker and a separately installed PostgreSQL service are not required for the desktop app.

## Runtime folder

For local development, place the Windows PostgreSQL runtime under:

```text
Dotnet10Template.Desktop\Runtime\Postgres
```

The expected PostgreSQL server executable path is:

```text
Dotnet10Template.Desktop\Runtime\Postgres\bin\postgres.exe
```

Populate this folder from a complete PostgreSQL Windows binary distribution, such as the ZIP archive from EnterpriseDB's PostgreSQL Windows builds or the installed PostgreSQL program directory copied from a matching local installation. Do not copy only `postgres.exe`, `initdb.exe`, `pg_ctl.exe`, and `pg_isready.exe`; PostgreSQL also needs its supporting DLLs, libraries, extensions, timezone files, locale data, and other runtime assets.

Preserve the distribution's required directory layout, including `bin`, `lib`, `share`, and any other directories included by the PostgreSQL distribution. At minimum, the desktop startup code expects these tools to exist:

```text
Dotnet10Template.Desktop\Runtime\Postgres\bin\postgres.exe
Dotnet10Template.Desktop\Runtime\Postgres\bin\initdb.exe
Dotnet10Template.Desktop\Runtime\Postgres\bin\pg_ctl.exe
Dotnet10Template.Desktop\Runtime\Postgres\bin\pg_isready.exe
Dotnet10Template.Desktop\Runtime\Postgres\bin\psql.exe
Dotnet10Template.Desktop\Runtime\Postgres\bin\createdb.exe
```

The desktop project copies `Runtime/Postgres/**` to the build, publish, and packaged output when the folder is present. RuntimeHost copies that private runtime to the per-user app data directory and executes PostgreSQL from there.

## Data directory

Desktop data is stored outside the app install/output folder:

```text
<per-user app data>/<ProductDataFolderName>/PostgresData
```

The desktop app initializes this directory with `initdb` on first launch and reuses it on later launches.

In packaged runs, `<per-user app data>` is the package LocalState path returned by Windows App SDK. In ordinary unpackaged development runs, the fallback is:

```text
%LOCALAPPDATA%/<ProductDataFolderName>
```

The writable packaged PostgreSQL runtime copy is stored beside the data folder:

```text
<per-user app data>/<ProductDataFolderName>/PostgresRuntime
```

## Data lifecycle policy

The template keeps `PostgresData` under the MSIX LocalState-backed app data path. It does not add Persistent Identity, does not move database files outside LocalState, and does not include backup/restore infrastructure. Persistent data across uninstall is intentionally left to the cloned product's domain requirements.

| Scenario | PostgreSQL data behavior |
| --- | --- |
| Normal restart | Preserved. RuntimeHost reuses existing `PostgresData` when `PG_VERSION` exists. |
| MSIX in-place update | Preserved. Updating the same package identity does not uninstall first, so LocalState remains available. |
| Runtime crash/recovery | Preserved. Existing `PostgresData` remains in place; PostgreSQL performs normal WAL crash recovery on the next launch when needed. |
| Full uninstall | Removed. Uninstalling removes this application's LocalState, including `PostgresData` and the local PostgreSQL database. |
| Reinstall after uninstall | Fresh database. The next launch initializes a new `PostgresData` with `initdb` and normal migrations recreate the sample data. |

## Local binding and port

Desktop PostgreSQL binds only to:

```text
127.0.0.1
```

The PostgreSQL port is dynamically selected on each RuntimeHost launch.

Override it for local development with:

```powershell
$env:<ProductEnvPrefix>_DESKTOP_POSTGRES_PORT = "55433"
```

If a dynamically selected port becomes occupied during startup, RuntimeHost retries with a new dynamic loopback port. It does not stop or kill unrelated PostgreSQL processes.

## Process topology

Runtime processes are supervised as:

```text
Dotnet10Template.Desktop
  -> Dotnet10Template.RuntimeHost
       -> postgres.exe
       -> Dotnet10Template.Api
```

Desktop and RuntimeHost use a per-launch named pipe for control messages. Desktop waits for RuntimeHost to report the selected API endpoint before loading WebView2. RuntimeHost monitors the exact Desktop PID it was launched for, not a process name.

## Shutdown behavior

RuntimeHost owns only the API and PostgreSQL processes it starts. On normal window close, Desktop sends a shutdown request over the named pipe. RuntimeHost gracefully stops the API first, then stops PostgreSQL with:

```text
pg_ctl stop -D <owned data directory> -m fast -w -t <timeout>
```

If Desktop is ended from Task Manager, RuntimeHost detects that the supervised Desktop PID exited, then runs the same API-first/PostgreSQL-second cleanup and exits. If RuntimeHost itself is force-killed, its Windows Job Object closes with `KILL_ON_JOB_CLOSE`, and Windows terminates only the API/PostgreSQL child processes that RuntimeHost assigned to that job. PostgreSQL then relies on normal WAL crash recovery on the next launch.

RuntimeHost also redirects the desktop-owned API stdin and sends a `shutdown` command before falling back to process termination. That lets ASP.NET Core run its normal host shutdown path without opening a control port.
