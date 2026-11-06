# J'sUnitySync — Architecture

J'sUnitySync is a real-time multiplayer Unity development platform. It consists of a
standalone Windows desktop application, a native Unity Editor extension, a backend
collaboration server, and supporting infrastructure.

## Implemented scope

Phases 1–10 provide the collaboration foundation and supported Unity Scene/Hierarchy/Inspector synchronization. Phase 11 now provides bounded server-side shared C# documents, an explicit WPF editor and optimistic revision conflict checks; collaborators refresh to see accepted edits, so live operation-level co-editing is not complete. Phase 12 stores content-addressed project asset blobs outside local Unity folders. Phase 14 adds bounded (100 revisions per file) script/asset history and non-destructive server-side restore. These additions use SQLite/Kestrel integration tests; live PostgreSQL/Redis remain unverified. Unity Editor upload/download and automatic local file application, dependency-closure packaging, Scene snapshots/compaction, developer presence, deployment backups, and release/security sign-off remain incomplete. Local Unity files are never automatically overwritten.

## Topology

```
+---------------------+        +---------------------+
|  Unity Editor 6     |        |  Windows Desktop    |
|  (Editor extension) |        |  App (WPF)          |
+----------+----------+        +----------+----------+
           |   WebSocket (RealTime) + HTTPS (Api)    |
           +-------------------+---------------------+
                               |
                  +------------+------------+
                  |   Backend services      |
                  |  - Api (ASP.NET Core)   |
                  |  - RealTime hub        |
                  |  - Asset storage       |
                  +------------+------------+
                               |
        +-----------+---------+---------+-----------+
        |           |                   |           |
   PostgreSQL     Redis          Local content
   (source of     (event         addressed blob
    truth)         pub/sub)      store (assets)
```

## Systems

1. **Desktop application** (`src/JsUnitySync.Desktop`, WPF) — sessions dashboard, team
   and project management, shared server-side C# document editing with explicit refresh
   and optimistic revisions, and script/asset history recovery controls. Premium dark
   theme: charcoal backgrounds, electric purple accents, electric blue highlights.

2. **Unity Editor extension** (`src/JsUnitySync.Unity/UnityPackage`, Unity 6 UPM) —
   explicit scene identity setup, authenticated session WebSocket, hierarchy/transform
   capture and application, supported Inspector fields, safe component allow-list,
   DPAPI event inbox and conflict notices. It does not auto-save, transfer assets or
   execute arbitrary remote scripts.

3. **Backend API** (`src/JsUnitySync.Api`, ASP.NET Core) — authentication, teams,
   projects, invitations, sessions, database/backplane readiness and authenticated
   WebSocket events. Horizontal SQL concurrency is designed; live multi-node PostgreSQL/
   Redis operation is still unverified.

4. **Real-time collaboration service** (`src/JsUnitySync.Server/Realtime`) — authenticated
   WebSocket rooms, ordered durable session events, client acknowledgments, replay and
   optimistic property conflict rejection. Redis pub/sub is the optional low-latency
   multi-instance backplane; per-connection database polling repairs dropped pub/sub
   notifications. Developer presence, snapshots and event compaction remain incomplete.

5. **Database and caching infrastructure** — EF Core has an explicit SQLite local/test
   provider and a PostgreSQL provider with provider-neutral, idempotent migrations.
   SQLite is the only database runtime exercised in this environment. Redis holds
   optional realtime pub/sub; current presence/session durability is not Redis-based.

6. **Asset synchronization (Phase 12, in progress)** — authenticated, team-authorized
   upload/download of bounded Unity asset files using SHA-256 content-addressed storage
   on the server host. The current implementation does not push bytes into a local Unity
   folder or transfer dependency closures; production object storage is not configured.

The product scope remains multiplayer Unity development: shared Scenes, collaborative
scripts, asset synchronization, developer presence, version control, and project
management. Built-in communication features and AI assistants are explicitly excluded
from every roadmap phase.

## Phases 3–5 accounts, teams and projects

### Ownership boundaries

- `JsUnitySync.Server` contains EF Core entities, versioned migrations, SQLite
  development persistence, password/account/session services, bearer authentication,
  team/social services and team-scoped project metadata services. It references
  Domain and Contracts; Desktop Core remains independent of server infrastructure.
- `JsUnitySync.Api` composes the server services and migrations, enforces the
  authentication/HTTPS/rate-limit middleware, and maps versioned HTTP endpoints.
  API exception mapping emits bounded structured errors and does not echo SQL,
  passwords, tokens or database details.
- `JsUnitySync.Contracts` contains the JSON request/response DTOs shared by the
  desktop HTTP client and backend. Device-specific paths and bearer-session storage
  are intentionally absent.
- Desktop Core uses the endpoint-bound `CollaborationClient`; only it attaches the
  in-memory bearer token to versioned relative API routes. It allow-lists the one
  project query option, does not follow redirects, never automatically retries
  mutating requests, and tells the user when a transport error may have occurred
  after a write.

### Authentication & profiles (Phase 3)

`POST /api/v1/auth/register` and `/login` validate email, display name and password.
Email is case-insensitively unique. Passwords use the ASP.NET Core `PasswordHasher`
PBKDF2 implementation configured for 210,000 iterations; no password is returned or
logged. Login uses the same public error for unknown addresses and bad passwords and
a dummy hash verification for unknown accounts. Auth routes are rate-limited to 20
requests per minute per observed source IP.

Successful registration/login issues a cryptographically random 256-bit opaque token,
valid for 12 hours. Only its SHA-256 digest and expiry are stored server-side; the
bearer token is revocable by deleting that specific session at logout. Protected
requests require the bearer; expired/revoked tokens fail closed. `/api/v1/me` reads
profile data and `PUT` requires the current profile `ExpectedRevision`, so stale
accepted profile writes cannot replace a newer revision. The API marks authenticated
responses `no-store`, caps request bodies at 64 KiB and rejects non-HTTPS API access
outside loopback development.

“Remember session” is opt-in and uses Windows DPAPI `CurrentUser` to encrypt the
opaque token in a separate atomic local file. The stored record binds token to the
normalized server origin and expiration; on startup the desktop validates it with
`/api/v1/me`. Passwords are never persisted. Changing servers clears the in-memory
session and profile. Logout revokes remotely when reachable and always clears the
in-memory token; if remote revocation or protected local-file removal fails, the UI
warns rather than claiming that the server session was revoked. No refresh, password
reset, email verification or MFA exists yet.

### Teams, invitations and friends (Phase 4)

Teams have durable membership keyed by `(TeamId, UserId)` and roles `Owner=2`,
`Lead=1`, `Member=0`. The creator becomes the Owner. The Owner can assign Member/Lead
roles using an expected membership revision; ownership cannot be removed or silently
transferred by that operation. Owners/Leads can invite by recipient account ID;
invites are seven-day, in-app, versioned, pending records. Only the recipient can
accept/decline; accepting inserts Member membership atomically. Duplicate active
invites are rejected and expired/declined invites can be reissued. Nonmembers and
unknown team identifiers deliberately share a not-found response.

Friend requests are pair-normalized so opposite-direction duplicate requests cannot
race into two rows. The recipient explicitly accepts/declines; self-requests,
pending/accepted duplicates are rejected, while a declined request can be reissued.
Public member/friend summaries contain stable user ID and display name only—never
email. No email delivery, user directory search, or unsolicited notifications exists;
users share their displayed account ID out of band.

### Team projects and local copies (Phase 5)

Projects are server records scoped to a team. Team Members may list/read; Owners and
Leads may create, rename and archive. Names are unique case-insensitively within a
team, using a normalized indexed key. Rename/archive operations require the expected
project revision. Archive hides a project by default without deleting its record or
any local project folder.

A developer must explicitly choose an existing folder in the native Windows folder
picker. `UnityProjectFolderValidator` checks only that the selected folder is a
non-reparse directory with `Assets/`, `ProjectSettings/` and a bounded
`ProjectSettings/ProjectVersion.txt`. It does not read the manifest contents or
inspect assets/scenes/scripts. The absolute folder path is stored in an atomic,
account/server-scoped LocalAppData JSON link file, never included in API contracts,
server logs or project metadata. Only the link can be removed; unlink/archive never
deletes files. The metadata registration and local folder link are intentionally
separate operations, so link-storage failure cannot imply remote rollback.

### Storage and deferred deployment

The request for phases 3–5 needs durable records before Phase 6. Therefore the
smallest real persistence foundation was pulled forward: EF Core 10 with a real
SQLite file for local development and integration tests, with checked-in migrations
(`Accounts`, `TeamsAndSocial`, `Projects`, `TeamRolesAndProjectNames`). The API
applies migrations at startup; `/health/ready` verifies live database connectivity.
Local database default:
`%LOCALAPPDATA%\\JsUnitySync\\server\\collaboration.db`, override with
`Database__ConnectionString`. Provider-neutral SQLite/PostgreSQL migrations and SQL generation are implemented;
actual PostgreSQL execution, backups, migration rollout/locking, distributed rate limits
and production key/secret management remain unverified Phase 6/16 work. There is no
in-memory fake domain store; the in-process realtime backplane is an explicit single-node
transport option. CI feature tests use isolated real SQLite files.

### Verification for Phases 3–5

Server Kestrel integration tests create isolated SQLite files and cover registration,
password hash/token digest storage, duplicate and invalid auth, profile revisions,
expiry/logout, team/user isolation, invitation recipient access, role revision,
friend request acceptance/privacy, team project permissions/name conflicts/revisions/
archive. Windows desktop tests cover DPAPI, route authorization/cancellation, folder
validation, local-link scoping/preservation and WPF rendering. An end-to-end desktop
workflow registers and authenticates, creates a team/project via the real API, links
an explicitly prepared Unity folder locally, verifies no local path in the real server
response, and confirms project files survive link/unlink unchanged.

## Phases 6–7 database and realtime architecture

### Database configuration and operations (Phase 6)

`Database:Provider` is explicitly `Sqlite` (default) or `Postgres`; unknown providers
fail startup. SQLite defaults to
`%LOCALAPPDATA%\\JsUnitySync\\server\\collaboration.db` for local development and
isolated integration tests. PostgreSQL requires `Database:ConnectionString`; no
connection string/password belongs in checked-in JSON. Use environment/secret
configuration. The Npgsql provider enables bounded connection/command timeouts,
connection pooling and transient retries. Production remote PostgreSQL requires
`SSL Mode=VerifyFull`. API readiness reports 503 unless the selected database can
connect and the configured realtime backplane is healthy.

Checked-in EF migrations are provider-neutral (no SQLite `TEXT`/`INTEGER` column
annotations; CLR long/bool map to PostgreSQL `BIGINT`/`BOOLEAN`). An automated
provider-schema test uses Npgsql's migration SQL generator without requiring a live
PostgreSQL server, and checks PostgreSQL types/index predicates and absence of PRAGMA/
AUTOINCREMENT. It is **not** proof that the generated script applies to a live server.
Generate a reviewed deployment script with `Database__Provider=Postgres dotnet ef
migrations script --idempotent`; set `Database__ApplyMigrationsOnStartup=false` on
PostgreSQL hosts. Startup checks for pending migrations and refuses to serve until the
operator applies them. SQLite development/test hosts apply migrations automatically.
No live PostgreSQL service, migration execution, backup/restore drill or performance
plan was available/verified in this phase.

### Durable sessions and event protocol (Phase 7)

`POST /api/v1/projects/{projectId}/sessions` creates a project-scoped session for an
authorized team member. `GET` lists active session metadata. The client upgrades
`GET /realtime/v1/sessions/{sessionId}` with an `Authorization: Bearer` header (tokens
are never placed in the URL, frame or logs). The server verifies bearer validity and
team membership before accepting the WebSocket. Remote HTTP API and WebSocket paths
require TLS; new WebSocket handshakes are rate-limited.

The first JSON text frame is `Hello` with protocol version, authenticated user id,
stable installation/client id and the client's durably applied sequence. The server
returns `Welcome`, including the authoritative last sequence. Frames are capped at
64 KiB and non-JSON/binary frames are rejected. Client event frames may not assign
server sequence numbers. Event ids are idempotent StableIds; the server determines the
author from authentication, validates the typed scene mutation, atomically increments
the session's concurrency-checked sequence row and inserts the event in the same DB
transaction. Events are retained in the durable ordered log. Publication happens only
after commit.

`SessionEvents` is keyed by `(SessionId, Sequence)` with a second unique
`(SessionId, EventId)` index. `SessionClientCursors` stores acknowledgments per
(session, user, installation); a client cannot acknowledge future work or move its
cursor backwards. On reconnect, the server replays every event after the client's
last durably applied sequence. Live connections also poll the durable log to repair
missed/reordered pub/sub notifications. The desktop persists received events and the
updated cursor together by an atomic CurrentUser-DPAPI encrypted file replacement,
then sends `EventAck`. A local-storage failure prevents acknowledgment, so a later
reconnect replays the event. The inbox is bounded (20,000 events/128 MiB); users must
keep enough free disk and later Unity integration will consume/compact this inbox.
Unity Scene snapshots and event-log compaction remain Phase 14 work; script and asset file-history retention is implemented separately.

For `PropertyChanged`, the server maintains accepted serialized values/revisions by
(session, object, component, property). The writer must provide the current
`BaseRevision`. A stale or future revision returns `Conflict` containing accepted and
rejected values/revisions and **does not append an event or advance the sequence**.
Concurrent first writes retry against the durable state; event idempotency and the
session row's optimistic concurrency protect accepted sequence allocation. Other scene
event types are append-only and centrally ordered; actual Unity object mutation and
merge semantics are implemented in Phases 8–10, not here.

### Realtime fan-out and limitations

With no `Redis:ConnectionString`, one node uses bounded in-process room channels; slow
peers are disconnected rather than silently losing events. Configure the secret-backed
Redis connection for multi-node low-latency pub/sub (TLS required in Production).
Redis is a hint, not the source of truth: each connected node queries the durable event
log to fill sequence gaps. The in-memory mode is single-process. Redis unavailability,
network partitions, multi-node failover/latency and failover recovery were not
integration-tested because Redis was not installed. A live PostgreSQL+Redis deployment
is needed before claiming multi-node production operation.

`RealtimeSessionClient` automatically reconnects with bounded jittered backoff, sends
the persisted cursor, writes incoming events to the protected inbox before ack, and
stores explicit server property conflicts. The WPF Sessions panel can create/list
sessions and connect/receive the ordered event stream. It labels events as queued—not
applied—until the Unity Editor adapter consumes them (Phases 8–10). Developer presence
is not yet published by the transport.

### Phase 6–7 verification

Kestrel/SQLite tests use real WebSocket upgrades and two peers; they cover handshake
identity/access, central ordering, fan-out, persistent property conflicts, ack,
disconnect-before-ack/reconnect replay and bounded single-node backpressure. A Windows
desktop client test connects over WebSocket, verifies encrypted inbox persistence,
then checks the server cursor advanced only after local persistence and restarts the
Kestrel server to verify the desktop client reconnects from its stored cursor without
duplicating the event. PostgreSQL migration SQL generation/type tests run without a
PostgreSQL connection. Redis and
live PostgreSQL integration remain unverified; tests must run in a deployment CI with
those services before Phase 6/7 are marked fully production-validated.

## Phases 8–10 Unity Editor and shared Scene integration

### Unity package and boundaries

`src/JsUnitySync.Unity/UnityPackage` is an embedded Unity Package Manager package
(`com.jsunitysync.collaboration`) targeting Unity 6. It has separate Runtime and
Editor asmdefs and is excluded from the .NET class-library build; Unity's own compiler
is the source of truth for this package. Add the local package from its `package.json`
with Unity Package Manager or a `file:` dependency. The Editor window signs into the
same API using the ordinary account endpoint; the password is transient and its
revocable bearer token stays in memory only. It joins an existing project session via
a WSS Authorization header—not a URL/query parameter.

### Explicit stable identity and safe baseline

`JsUnitySyncObjectId` is a serialized MonoBehaviour tag, so IDs survive rename,
reparent and reimport. The package **never adds tags silently**: the user explicitly
chooses “Enable / repair stable IDs”; changes are Undo-recorded and mark the Scene
unsaved. The user must review, save and share/commit that baseline before collaborators
join. No Scene/project is auto-saved.

The first Editor peer binds the session to the Unity scene asset GUID and a SHA-256
fingerprint of the saved scene file plus sorted stable-object IDs. Only the digest is
sent; no scene bytes are uploaded. The same installation retains its initial fingerprint
in its protected local inbox. A server-side Scene/baseline mismatch is rejected before
joining; local validation after replay also checks expected object-ID membership. The
session intentionally represents one active saved Scene, not multi-scene collaboration.

### Editor event production and application

`UnitySceneSyncCoordinator` listens for Hierarchy changes, polls tracked transforms at
10 Hz, and hooks `Undo.postprocessModifications` for Inspector edits. It emits typed
GameObject create/destroy/rename, Transform/reparent, supported component-structure and
property changes using the canonical scene-kind numeric values and stable object IDs.
Incoming frames first enter a bounded CurrentUser-DPAPI encrypted inbox/outbox under
LocalAppData, then get acknowledged. Unity applies/replays them on the Editor main
thread via Undo/SerializedObject; saves remain the user's decision. The transport
preserves local pending event IDs across reconnect so resubmission is idempotent.

Same-property stale writes (including rename and transform) are rejected at the server
with accepted/rejected values and the identities of both writers; a conflict is stored
for reconnect delivery. In the Editor, a pending local divergent property is not
replaced by the other peer's accepted value: it remains visible for manual
reconciliation. Conflicts are DPAPI-spooled, and a subsequent intentional write uses
the latest accepted revision.

Component structure is deliberately allow-listed to selected built-in Unity components;
remote arbitrary script components never instantiate and run code. Inspector value
serialization supports primitive numeric/bool/string/enum values, color/vector/rect/
bounds and stable references to in-Scene GameObjects/components. Arrays, arbitrary
managed references and asset/object references that lack a shared stable asset
identity remain local and are reported as unsupported. Unity Editor asset upload/download is still incomplete in Phase 12.

### Verification and explicit scope

The package compiles in a real Unity 6000.5.0f1 Editor and passes 10 EditMode tests,
including stable-ID generation/repair, saved Scene hashing, encrypted inbox,
remote Scene create/rename/transform/destroy, property serialization and allow-listed
component state. A live Unity Editor test connects to the running Kestrel/SQLite API
with two accounts, exercises remote and locally generated Scene/Hierarchy/component/
Inspector events and verifies cross-user stale-property conflict does not overwrite
accepted Editor state. The same protocol has two-peer/reconnect server tests.

`dotnet test` does not run Unity tests. See the package README for UPM installation and
the live-test `JSUNITYSYNC_TEST_SERVER` setup. This Unity validation used SQLite and a
single-node in-process backplane. Live PostgreSQL migrations and Redis multi-node fan-out
remain unverified due unavailable services; Phases 6–7 remain flagged in progress for
those deployment acceptance checks. Script document editing, content-addressed asset
storage and file history are API-backed but are not yet integrated into the Unity Editor.

## Phases 11, 12 and 14 — shared files and recovery (in progress)

### Shared script documents (Phase 11)

The server stores collaborative C# document content separately from local Unity folders.
`GET /api/v1/projects/{projectId}/scripts` lists paths and revisions;
`GET/PUT /api/v1/projects/{projectId}/scripts/document?path=Assets/...cs` reads or
saves one document. Paths are relative, traversal-checked and restricted to C# files
under `Assets/`. Documents are limited to 1 MiB and each write supplies the revision
it read; stale concurrent writes return a conflict rather than replacing accepted work.
The WPF Projects panel offers explicit load/refresh, text editing and save. Refresh is
manual; there is no operation-level merge, live editor session, Unity Editor integration,
or local-file write. This is a versioned shared-document foundation, not a full live
collaborative IDE.

### Content-addressed asset API (Phase 12)

`GET /api/v1/projects/{projectId}/assets` returns the project's asset catalog.
Authenticated team members can upload/download relative files below `Assets/`, except
`.cs` scripts which use the script-document API. Uploads stream through an app-owned
temporary file, are bounded to 32 MiB per item and hashed with SHA-256; final blobs are
stored outside Unity project folders under a hash-derived path. Each path has an
optimistic revision. The active catalog is limited to 2,000 items and 512 MiB per
project. Current storage is host-local; Unity clients do not yet upload/download,
materialize local files, or resolve/transmit asset dependency closures. Blob garbage
collection, distributed object storage and live deployment validation remain open.

### Version history and recovery (Phase 14)

Every accepted script or asset update records an immutable server-side version, capped
at the latest 100 per project file. `GET /api/v1/projects/{projectId}/history` lists
metadata; `POST .../history/restore` requires the selected source revision and the
current revision. Restore copies prior script content or switches an asset's content
hash, but increments the current revision and records a new history entry. It does not
modify local Unity files. This is not Git integration. Unity Scene snapshots,
event-log compaction, scene recovery, full audit/retention policy, database
backup/restore and operator recovery drills are not implemented.

## Phase 16 — security, testing, optimization and release (in progress)

New project-file writes use authenticated membership checks, constrained `Assets/`
paths, bounded document/upload sizes, per-IP script/upload rate limits, content hashes,
and revision guards. Asset uploads stream to temporary files and never use a client path
as a filesystem path. Tests cover real Kestrel+SQLite uploads/downloads, script access,
revision conflicts, path traversal rejection and non-destructive file history restore.
The present test suite is not a substitute for load/fuzz testing, high-DPI/screen-reader
review, storage garbage collection, encryption-at-rest/key management for server-side
source/assets, TLS/proxy/deployment review, PostgreSQL/Redis live tests or backup/restore
drills. These remain release blockers; no production readiness claim is made.

## Phase 2 desktop architecture

### Boundaries and dependencies

- `JsUnitySync.Desktop` targets `net10.0-windows` and contains WPF views, theme
  resources and the composition root (`App.xaml.cs`). The composition root references
  the API to start a local loopback host when none is ready. The WPF-independent
  `Desktop.Core` remains free of API/server infrastructure references.
- `JsUnitySync.Desktop.Core` targets `net10.0`, with no WPF reference. It holds
  navigation models, observable state, commands, settings storage and HTTP health
  probes. It references Contracts (reuses its retry policy) and Logging.Abstractions.
- Microsoft DI constructs the singleton shell and services. No service locator is
  used in views/view models. The application owns and disposes the provider,
  HTTP client and log sink; window closure cancels active network work.

### Presentation

`MainWindow` binds to `ShellViewModel`. Navigation selection updates observable
page state, with commands also exposed to overview cards and keyboard shortcuts.
The Overview and Settings panels are functional, as are the session, team, project,
shared-script and file-history workflows added in later phases. No mock activity or
fake collaboration data is presented. Local Unity paths stay on-device, and the shared
script editor operates on separate server-side documents.

`Themes/Dark.xaml` centralizes brushes, spacing through control templates, focus
rings, navigation selection, button/input states and scrolling. Black/charcoal
surfaces use purple accents and blue diagnostic highlights. Native chrome is
retained (including Windows snap/system controls); supported Windows versions get
an immersive dark title bar. Content scrolls at the minimum window size. 160-ms
panel fades honor both the local Reduce motion setting and Windows animations.
Controls provide accessible names, keyboard focus feedback and access keys.

### Local settings and file ownership

`ISettingsStore` / `JsonSettingsStore` stores only endpoint and reduced-motion
preferences in `%LOCALAPPDATA%\\JsUnitySync\\desktop-settings.json`. An adjacent,
uniquely named temporary file is atomically moved over the destination; write
failure leaves previously active settings intact. Invalid persisted settings are
reported, not overwritten automatically. Save is explicit. Logs are separate,
size/day-rolled files in the same app-owned LocalAppData directory. Neither service
reads or writes Unity project files or stores tokens.

### API readiness and failure states

`IServerHealthClient` probes the existing `GET /health/ready` route. Success requires
both `status: ready` and `service: JsUnitySync.Api`; HTTP success alone is not enough.
Checks use the **saved** origin, not partially edited form text. Endpoints require
HTTPS except HTTP loopback development; userinfo, query, fragment and paths are
rejected. Redirects are not followed and responses are capped at 64 KiB by the host.
Transient failures retry at most three times with jittered exponential backoff
and a 3-second timeout per attempt. Non-transient HTTP errors and malformed JSON
are surfaced without retry. User cancellation and window shutdown stop the check.
Commands are single-flight and restore their availability in `finally`.

The self-contained app starts or reuses its local SQLite API before showing the UI;
closing the app stops only the API host instance it started. The UI then moves through
Checking to API ready, Unavailable, Check cancelled or Check failed. Every completed check has a local
timestamp. A saved endpoint change invalidates old readiness. A successful probe
**never** implies an authenticated user, a live session or continuous monitoring.
There is one startup check and subsequent manual checks, not a background poller.

### Verification

Desktop tests cover navigation/notifications, command execution and cancellation,
endpoint validation, atomic settings replacement and corruption, HTTP response
validation and retry limits. An STA smoke test loads the actual theme/XAML,
navigates and renders all pages at 1320 × 880 and 1020 × 680, verifies form bindings,
and fails on WPF binding errors. A real Kestrel API start/stop/restart test verifies
desktop compatibility and offline recovery. This is not a substitute for future
screen-reader, high-DPI and broader usability testing in Phase 16.

## Collaboration model (transport Phase 7; supported Unity mutations Phases 8–10)

- **Stable object identifiers**: every synchronized scene object has a
  `StableId` (ULID) that survives renames, reparenting, and Unity GUID changes.
  It is the join key for all events and snapshots.
- **Scene synchronization events**: stable event IDs and server-assigned per-session
  monotonic sequence numbers. Phase 7 persists/orders typed event frames and cursors;
  Phase 8–10 Unity Editor hooks produce/apply supported Scene/Hierarchy/Inspector events.
- **Reliable delivery**: authenticated WebSocket event log, per-installation durable
  acknowledgments, ordered replay and CurrentUser-DPAPI inbox/outbox are implemented.
  Desktop observer inboxes are encrypted and store events; the Unity Editor package
  consumes/replays those events on the Editor thread.
- **Conflict safety**: PropertyChanged, TransformChanged and GameObjectRenamed use
  accepted-value revision checks; stale/future writes are durably recorded and broadcast
  to session participants without consuming a scene sequence. Unity preserves local
  rejected values and surfaces accepted/rejected authors/values. Component-structure
  writes are restricted to an allow-list; arbitrary script components are not created.
- **Snapshot recovery**: script and asset files have bounded immutable history (100
  versions per file); restore creates a new revision under an expected-current-revision
  guard. These actions affect server-side copies/catalog entries only. Periodic Unity
  Scene snapshots, event-log compaction, and applying a restore into a local project are
  not implemented. The session inbox is bounded at 20,000 events/128 MiB.
- **Asset dependency tracking**: the server tracks content hashes per project-relative
  file, but Unity GUID dependency graphs and complete dependency closures are not yet
  synchronized.

## Architectural rules

- **Dependency direction**: `Domain` and `Contracts` depend on nothing.
  Infrastructure (API and Server services, including Realtime and Assets) depends on Domain and Contracts.
  Desktop depends on Desktop.Core and Contracts, not server infrastructure.
  The existing Unity event factory also references the netstandard2.1 Domain library;
  its Editor integration boundary is reviewed in Phase 8.
- **Dependency injection everywhere**: composition roots are the app hosts
  (`Program.cs` in Api/RealTime, `App.xaml.cs` in Desktop). Libraries register their services via
  `IServiceCollection` extension methods.
- **Structured logging**: `Microsoft.Extensions.Logging` with Serilog sinks
  (console + file). Every log carries a `SessionId`/`CorrelationId` scope where
  applicable.
- **Network failure handling**: all client–server calls have retry with exponential
  backoff and jitter; the WebSocket client auto-reconnects with event replay.
- **Security**: revocable opaque bearer sessions for API and WebSockets; authorization
  enforced per-team/project/session; TLS outside loopback; user project files are never
  modified without explicit user action; secrets never enter source control.
- **No fake implementations**: every phase ships real, tested functionality.
  Planned features are clearly labeled with their scheduled phase in docs and UI;
  no dummy data or no-op workflow is presented as a completed feature.

## Testing strategy

- Domain and protocol logic: xUnit unit tests (`tests/JsUnitySync.Domain.Tests`,
  `tests/JsUnitySync.Contracts.Tests`, ...).
- API: real Kestrel + isolated SQLite integration tests (provider script generation
  tested without claiming a live PostgreSQL deployment).
- Real-time: real Kestrel WebSocket clients, SQLite event persistence/replay, plus
  unit coverage for bounded in-process backplane fan-out. Live Redis is unverified.
- Desktop: WPF UI logic kept in testable view models (Phase 2 onward).
- Unity: protocol-level tests run in the .NET test projects; the Editor package
  additionally ships a Unity Test Runner suite (Phase 8).
