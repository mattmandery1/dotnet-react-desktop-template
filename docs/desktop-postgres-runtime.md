# Desktop PostgreSQL runtime

`Dotnet10Template.Desktop` owns a private PostgreSQL runtime in desktop mode. Docker and a separately installed PostgreSQL service are not required for the desktop app.

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

The desktop project copies `Runtime/Postgres/**` to the build, publish, and packaged output when the folder is present. In packaged MSIX runs, the app copies that private runtime to the per-user app data directory and executes PostgreSQL from there.

## Data directory

Desktop data is stored outside the app install/output folder:

```text
<per-user app data>/Dotnet10Template/PostgresData
```

The desktop app initializes this directory with `initdb` on first launch and reuses it on later launches.

In packaged runs, `<per-user app data>` is the package LocalState path returned by Windows App SDK. In ordinary unpackaged development runs, the fallback is:

```text
%LOCALAPPDATA%/Dotnet10Template
```

The writable packaged PostgreSQL runtime copy is stored beside the data folder:

```text
<per-user app data>/Dotnet10Template/PostgresRuntime
```

## Local binding and port

Desktop PostgreSQL binds only to:

```text
127.0.0.1
```

The default desktop PostgreSQL port is:

```text
55432
```

Override it for local development with:

```powershell
$env:DOTNET10TEMPLATE_DESKTOP_POSTGRES_PORT = "55433"
```

If the selected port is already occupied, the desktop app fails with a visible startup error. It does not stop or kill unrelated PostgreSQL processes.

## Shutdown behavior

The desktop app owns only the API and PostgreSQL processes it starts. On normal window close, it stops the API first, then stops PostgreSQL with:

```text
pg_ctl stop -D <owned data directory> -m fast -w -t <timeout>
```

If a child process does not exit within the bounded shutdown timeout, the desktop app force-kills only that owned child process as a final fallback.

During development, a hard debugger stop can terminate the desktop process without running normal WinUI shutdown events. That is a development-time limitation; production shutdown should use normal app/window close paths.
