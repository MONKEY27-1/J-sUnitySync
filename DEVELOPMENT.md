# Development Conventions — J'sUnitySync

## Language & frameworks
- C# 12+, nullable reference types enabled everywhere.
- .NET 10 for hosts and test projects; **netstandard2.1** for anything the Unity
  Editor must load (`src/JsUnitySync.Unity` and any library it references).
- ASP.NET Core minimal hosting; WPF with MVVM for the desktop app.

## Project structure
- One concern per project (see ARCHITECTURE.md topology).
- `Domain` and `Contracts` have zero package dependencies beyond the BCL.
- Infrastructure projects may depend on infrastructure packages; client projects
  may not depend on server infrastructure.

## Naming
- Public types: `PascalCase`. Private fields: `_camelCase`. Constants: `PascalCase`.
- File-scoped namespaces. One primary type per file.
- Test method names: `MethodName_Scenario_ExpectedResult`.

## Dependency injection
- All services are registered via DI; no service locators, no static mutable state
  in application logic.
- Each library exposes `AddXxx(this IServiceCollection)` extension methods in a
  `DependencyInjection.cs` file.

## Logging
- Inject `ILogger<T>`. Structured message templates only:
  `Log.LogInformation("Scene event applied {EventId} for {SessionId}", eventId, sessionId)`.
- Never log secrets, tokens, or user project file contents.

## Errors & resilience
- Public APIs validate arguments with `ArgumentException` helpers.
- Client networking code retries with exponential backoff + jitter and reconnects
  with replay; never loops without a cancellation token.

## Desktop UI (Phase 2 onward)
- Keep view models and desktop service contracts in `JsUnitySync.Desktop.Core`.
  It must remain WPF-independent; do not reference API or Unity infrastructure.
- Compose dependencies once in `App.xaml.cs`. Code-behind handles only view/OS
  concerns; application state and commands belong in testable view models.
- Use `ObservableObject`, `RelayCommand` and cancellable `AsyncCommand`. Async-void
  is permitted only at the WPF/ICommand boundary with exceptions observed.
- Centralize dark theme resources in `Themes/Dark.xaml`. Preserve visible keyboard
  focus, native window behavior, scrolling and system/local reduced-motion support.
- Never populate shipping screens with fake participants/projects/messages. Show
  clear availability notices for future phases instead of no-op action buttons.
- Desktop preferences/logs belong in `%LOCALAPPDATA%\\JsUnitySync`, never beside
  the executable or in a Unity project. Write preferences atomically; never store
  credentials there. Test storage with a unique temporary directory and clean it up.
- UI smoke tests require Windows and a dedicated STA thread with dispatcher pumping.
  They must load real XAML and fail on binding errors. Do not create Application
  singletons from individual parallel tests.
- Integration tests use ephemeral loopback ports. Close only processes/windows
  created by the test; never terminate all `dotnet.exe` processes on the machine.

## Accounts, teams and projects (Phases 3–5)
- Server services live in `JsUnitySync.Server`; API routes/hosting in `JsUnitySync.Api`;
  shared wire/request types in `JsUnitySync.Contracts`. Desktop Core may call the API
  only through `CollaborationClient`, never by referencing server implementation.
- Use EF versioned migrations; never `EnsureCreated` in production/test flows. Tests
  use isolated real SQLite files and apply migrations through the actual host. PostgreSQL
  migrations must be verified in Phase 6 before a production provider is claimed.
- Never persist account passwords. Server session tables contain only token digests;
  Desktop remembered sessions use Windows CurrentUser DPAPI in a separate file from
  preferences. Tokens are endpoint-bound, never logged, and invalid on server change.
- Any mutable user/team/project operation includes expected revision where applicable.
  Conflicts are explicit; do not implement last-write-wins on accepted records.
- Use generic errors for unknown account/team resources where enumeration/privacy is
  at risk. Do not disclose member emails. In-app invitation recipient controls response.
- Server project DTOs must not contain local paths. Local Unity folders are chosen
  explicitly, structurally validated without reading contents, and stored only in
  account/server-scoped LocalAppData. Never implicitly write, copy or delete them.
- Endpoint additions require cross-user auth tests and desktop client tests for errors,
  duplicate submissions, cancellation and ambiguous network writes.

## Database providers (Phase 6)
- Select providers only via `Database:Provider` (`Sqlite` dev/test default or
  `Postgres`). Keep EF migration `ColumnType` annotations provider-neutral; add provider
  mappings only when a type truly cannot be portable. Test migrations for SQLite with
  real isolated files and generate an idempotent PostgreSQL script in tests.
- PostgreSQL credentials come from secret-backed configuration, never appsettings or
  command-line transcripts. Production remote PostgreSQL requires `SSL Mode=VerifyFull`.
  Use `Database:ApplyMigrationsOnStartup=false` for PostgreSQL hosts, apply the reviewed
  idempotent script out-of-band, and fail startup if migrations remain pending.
- Connection/provider tests that only generate DDL are not live-database tests. Record
  the PostgreSQL server version and runtime result before claiming provider validation.

## Realtime transport (Phase 7)
- Authenticate the WebSocket handshake from the bearer header (never query string or
  frame credentials), and independently authorize the target project/session membership.
- Persist each event and allocate its sequence in one transaction. The database log is
  authoritative; Redis pub/sub is only a wake-up hint. Always restore sequence gaps by
  reading the durable log, including when Redis fails.
- Append-only event ids are idempotent. For property writes, compare `BaseRevision`
  against accepted durable property state; on conflict reject the event and return both
  accepted/rejected values. Never increment sequence or silently overwrite on conflict.
- Acknowledge only after the receiving client durably persists/applies an event. Bound
  frame, replay and peer queues. If a peer cannot keep up, disconnect it; replay later
  rather than dropping accepted work.
- Test real Kestrel WebSocket peers and reconnect cursors with an isolated DB. Keep
  separate tests for PostgreSQL SQL generation and Redis behavior. A generated script
  or successful compile is not proof of a live service integration.
- Desktop local event payloads can contain user project data: protect inboxes with
  Windows CurrentUser DPAPI, do not log/publish their contents to application logs,
  and do not claim they have been applied to Unity until an Editor consumer confirms it.

## Verification status for infrastructure dependencies
- Generated PostgreSQL migration SQL is not a substitute for applying it to supported
  PostgreSQL. Likewise, a compiling Redis backplane is not a live Redis integration.
  Keep phase status In Progress until those deployments execute real migration,
  backup/recovery, cross-node fan-out and reconnect tests. Do not mask missing runtime
  services by replacing production behavior with in-memory fakes; the in-process realtime
  bus is only an explicit single-node mode.
- Every realtime accepted event must be durable before pub/sub; every received event
  must be durably stored/applied before client acknowledgment. Pub/sub loss must heal
  from the database sequence log. Queue overflow disconnects instead of dropping an
  accepted event. Never log payload or bearer token.

## Shared project files and recovery (Phases 11, 12 and 14)
- Collaborative script documents are server-side records, not local Unity file edits.
  Restrict document routes to relative C# files under `Assets/`, cap content at 1 MiB,
  require an expected revision on every write, and return conflicts rather than
  overwriting a revision another user accepted. UI refresh is explicit; do not claim
  operation-level real-time merging or Unity Editor integration until implemented.
- Asset paths must be relative and traversal-safe under `Assets/`; use content hashes
  for blob identity and stream to temporary storage before atomic placement. Enforce
  the 32 MiB item, 2,000 item and 512 MiB active-project quotas. Never resolve a user
  supplied asset path against the server filesystem or a local project directory.
- Version restore is a new server-side revision guarded by the current revision. Retain
  bounded history and do not overwrite a local Unity file or Scene without a separately
  reviewed explicit import workflow. Test source revision, stale target revision,
  unauthorized access and local-file non-modification.

## Product scope
- Keep roadmap work centered on multiplayer Unity development, real-time collaboration,
  shared Scenes, collaborative scripts, asset synchronization, developer presence,
  version control and project management. Built-in communication and AI assistants are
  excluded across all phases; do not add placeholder UI, protocol handlers or services
  for those excluded features.

## Git
- Conventional commits: `feat:`, `fix:`, `docs:`, `test:`, `refactor:`.
- Never commit `bin/`, `obj/`, user secrets, or generated Unity `Library/` data.

## Testing
- Every major feature lands with tests in the corresponding `tests/` project.
- `dotnet test` must be green before a phase is marked complete.
- Do not mark a phase complete with build errors or failing tests.

## Documentation
- Update `ARCHITECTURE.md` when a system design changes.
- Update `PHASES.md` at the end of every phase (status, date, notes).
- Update `README.md` when setup steps or component layout change.
