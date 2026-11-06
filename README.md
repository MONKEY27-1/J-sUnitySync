# J'sUnitySync

A multiplayer Unity development and collaboration platform focused on shared Scenes, collaborative scripting, asset synchronization, developer presence, version control, and project management. Local Unity project files remain under the developer's control.

## Product scope

The roadmap is limited to Unity development and project collaboration. Built-in messaging, voice/video or screen-sharing features and AI coding assistants are out of scope in every phase and are not planned.

## Components

| Component | Project | Current state |
|---|---|---|
| Desktop application | `src/JsUnitySync.Desktop` | WPF shell with auth, teams, projects, sessions, shared-script editor and file history |
| Desktop presentation logic | `src/JsUnitySync.Desktop.Core` | WPF-independent MVVM, settings and HTTP services |
| Unity Editor extension | `src/JsUnitySync.Unity/UnityPackage` | Unity 6 UPM Editor package: authenticated sessions, stable scene IDs, supported Scene/Hierarchy/Inspector sync |
| Backend API | `src/JsUnitySync.Api` | ASP.NET Core auth/profile/team/project/session APIs plus authenticated WebSocket transport |
| Server domain/persistence | `src/JsUnitySync.Server` | Provider-selectable EF Core SQLite/PostgreSQL; accounts, teams, projects, sessions and durable event log |
| Real-time collaboration transport | `src/JsUnitySync.Server/Realtime` | Durable ordered events, WebSocket rooms, reconnect replay, conflict rejection and optional Redis backplane |
| Asset synchronization API | `src/JsUnitySync.Server/Assets` | In progress: content-addressed server blobs, bounded uploads/downloads and revision metadata; Unity Editor transfer is not yet integrated |
| Shared domain & protocol | `src/JsUnitySync.Domain`, `src/JsUnitySync.Contracts` | Phase 1 foundation libraries |

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full system design,
[DEVELOPMENT.md](DEVELOPMENT.md) for conventions, and
[PHASES.md](PHASES.md) for progress tracking.

## Prerequisites

- Windows 10/11 with .NET SDK **10.0** — https://dotnet.microsoft.com/download
  (the desktop app and desktop tests require Windows/WPF; SDK 8/9 cannot build this solution)
- Optional PostgreSQL 15+ and Redis 7+ for live deployment validation (local/tests use SQLite and a single-node in-process backplane)
- Unity 6 Editor 6000.0+ (UPM package use and EditMode tests)

## One-file Windows app

The self-contained Windows x64 executable bundles the .NET runtime and the API host.
On launch it starts (or reuses) the loopback API at `http://localhost:5080`, applies
SQLite migrations, then opens the desktop UI. No separate .NET installation, PostgreSQL
or Redis service is required for this local setup. The default account database and
asset store live under `%LOCALAPPDATA%\\JsUnitySync\\server`.

Build the same single-file executable with:

```powershell
dotnet publish src/JsUnitySync.Desktop/JsUnitySync.Desktop.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=embedded
```

The output is `src/JsUnitySync.Desktop/bin/Release/net10.0-windows/win-x64/publish/JsUnitySync.exe`.
Unity Editor itself is a separate licensed application and is not bundled or silently
installed; add the repository's UPM package explicitly in a Unity project when needed.

## Setup

```bash
# restore and build everything
dotnet build JsUnitySync.slnx

# run all tests
dotnet test JsUnitySync.slnx

# run the backend API (health, accounts, teams, projects, sessions and WebSockets)
dotnet run --project src/JsUnitySync.Api

# in a second terminal, launch the desktop application
dotnet run --project src/JsUnitySync.Desktop
```

The API listens on `http://localhost:5080` using the default HTTP launch profile
(see `src/JsUnitySync.Api/Properties/launchSettings.json`).
`GET /health/live` and `GET /health/ready` report process/database/backplane readiness.
API data defaults to `%LOCALAPPDATA%\\JsUnitySync\\server\\collaboration.db` (SQLite);
`Database:Provider` defaults to `Sqlite`. PostgreSQL setup:

```powershell
dotnet tool restore
$env:Database__Provider = 'Postgres'
dotnet ef migrations script --idempotent --project src/JsUnitySync.Server `
  --startup-project src/JsUnitySync.Api --configuration Release `
  --output "$env:TEMP\\jsunitysync-postgres-migrations.sql"
Remove-Item Env:Database__Provider
```

Review/apply the generated script through your PostgreSQL deployment process. Run the
API with secret-backed `Database__ConnectionString`, `Database__Provider=Postgres`,
and `Database__ApplyMigrationsOnStartup=false`; hosts refuse startup when migrations
are pending. Never commit connection strings. Production PostgreSQL connections to
remote hosts require `SSL Mode=VerifyFull`. Redis is optional via
`Redis__ConnectionString`; production requires TLS (`ssl=true`). Without Redis, the
service uses a tested in-process single-node backplane. Uploaded assets default to
`%LOCALAPPDATA%\\JsUnitySync\\server\\assets`; deployments should set
`AssetSync__RootPath` to a protected durable volume and include it in backup/restore
operations. Server-side script content and asset blobs are not application-encrypted at
rest; use protected storage and establish key management before deployment. A live
PostgreSQL/Redis server, durable object-store lifecycle and backup/restore drill are
required before claiming production deployment verification.

## Desktop (Phases 2–16)

- Use the sidebar or overview cards to navigate. Tab/Shift+Tab moves focus;
  arrow keys select sidebar pages. **F5** checks the API; **Ctrl+,** opens Settings.
- Settings accepts a server origin, e.g. `https://sync.example.com` or
  `http://localhost:5080`. Remote servers require HTTPS; credentials, paths, queries
  and fragments are rejected. **Save preferences** before checking a changed address.
- **Accounts:** Settings includes Register, Sign in/out and editable profile actions.
  Passwords are transient and not persisted. Remembered opaque server sessions are
  protected by Windows CurrentUser DPAPI, endpoint-bound, expire within 12 hours,
  and are validated on startup. No password reset/email verification/MFA is shipped.
- **Teams & friends:** create teams, invite using the recipient's shareable account ID,
  assign Lead/Member roles, and accept/decline recipient-owned invitations. Friend
  requests require recipient acceptance. Member views do not reveal email. Share your
  Account ID from Settings with people who need to invite you. Invitations are in-app
  only; no email notification service exists.
- **Projects:** metadata belongs to a team; only Owners/Leads create, rename or archive.
  Explicitly select an existing Unity folder to link it on this device. Its absolute
  path is local-only in `%LOCALAPPDATA%\\JsUnitySync\\project-links.json`; unlink only
  removes the association. Archived records can be restored. The link operation itself
  does not scan or write project files. When the Unity Editor package joins a session,
  its supported typed Scene/Inspector events are sent to the server; this Unity package
  does not upload, clone or overwrite local files. The Projects panel edits shared
  server-side C# documents (up to 1 MiB) with revision conflicts and explicit refresh;
  it does not edit the linked Unity folder. Authenticated asset APIs stream at most
  32 MiB per file into server-local content-addressed storage, with 2,000-file and
  512 MiB active-project quotas. Unity upload/download, dependency closure and
  automatic local materialization are not implemented.
- **Sessions / real-time transport:** create or list sessions for the selected active
  project; Connect opens an authenticated WebSocket. Events receive server-assigned
  per-session sequences and are committed to the database before publication. Stale
  property writes are rejected with the accepted revision/value. On the desktop,
  incoming events are encrypted with CurrentUser DPAPI and atomically stored before
  acknowledgement. Reconnect resumes from this durable cursor. The Unity 6 Editor package consumes the ordered session stream and applies supported
  Scene/Hierarchy/Inspector changes. Shared script documents and content-addressed asset synchronization are being expanded in Phases 11–12.
- SQLite is the tested/default store. PostgreSQL support and provider-neutral idempotent
  migration SQL are implemented and SQL-generation tested, but a live PostgreSQL
  server was unavailable here. Redis fan-out is implemented as an optional TLS-capable
  multi-node backplane; no live Redis server was available for verification. The
  default in-process backplane is single-node. Deployment, backups, email recovery,
  MFA, secret rotation and production proxy/TLS hardening remain incomplete.
- The app checks once on startup; further checks are manual. Each check is bounded
  to three attempts (3-second timeout per attempt), with exponential backoff and
  jitter. Cancel stops the current check. API readiness is a point-in-time result,
  **not authentication, a session connection or continuous server monitoring**.
- **Reduce motion** previews immediately and persists on Save. Panel fades also
  respect Windows animation preferences. Native window controls support resizing,
  maximizing and Windows snapping; minimum window size is 1020 × 680 logical pixels.
- Settings: `%LOCALAPPDATA%\JsUnitySync\desktop-settings.json`; protected account:
  `session.dpapi`; local folder links: `project-links.json`; encrypted real-time inbox:
  `realtime-inbox.dpapi`. Files are separately protected/stored; corrupt data is
  reported and not overwritten.
- Logs: `%LOCALAPPDATA%\JsUnitySync\logs\desktop-*.log` (rolling, seven-file limit).
  No project content, credentials or server response bodies are logged.
- History provides server-side script and asset version recovery as a new revision; it never overwrites a developer's local Unity files. It is not Git integration. Developer presence and real-time multi-user code editing remain incomplete. Unity project installation and Editor usage details are in [`UnityPackage/README.md`](src/JsUnitySync.Unity/UnityPackage/README.md).

The .NET solution currently has **123 automated tests** (22 foundation, 37 server integration,
64 Windows desktop), including real Kestrel/SQLite/two-peer WebSocket replay and
conflict tests, PostgreSQL migration SQL generation, DPAPI event-inbox storage, API
authorization, folder privacy and STA WPF rendering/navigation. The Unity 6 package
has **10 additional EditMode tests** (stable IDs, Scene changes, Inspector/components,
DPAPI inbox, and a Unity Editor↔live API/WebSocket integration test); the live test uses
`JSUNITYSYNC_TEST_SERVER` pointing at a running local API. `dotnet test` does not run
Unity tests.
Run `dotnet test JsUnitySync.slnx -c Release` to verify the release configuration.

## Solution layout

```
JsUnitySync.slnx
src/
  JsUnitySync.Domain/     Core domain model (no infrastructure dependencies)
  JsUnitySync.Contracts/  Wire protocol shared by client & server
  JsUnitySync.Api/        Backend HTTP API host
  JsUnitySync.Server/     Accounts, authorization and persistence
  JsUnitySync.Server/Realtime/ Authenticated WebSocket rooms and durable event store
  JsUnitySync.Server/Assets/ Content-addressed project asset storage APIs (Phase 12)
  JsUnitySync.Desktop/    WPF desktop application
  JsUnitySync.Desktop.Core/ MVVM and desktop services (no WPF dependency)
  JsUnitySync.Unity/UnityPackage/ Unity 6 UPM Editor extension (EditMode tests included)
tests/
  JsUnitySync.Domain.Tests/
  JsUnitySync.Desktop.Tests/  Windows/WPF, account/session and backend tests
  JsUnitySync.Server.Tests/   ASP.NET Core, provider SQL and WebSocket integration tests
```
