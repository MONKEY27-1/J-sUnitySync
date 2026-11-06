# J'sUnitySync — Phase Tracker

| # | Phase | Status | Completed | Notes |
|---|-------|--------|-----------|-------|
| 1 | Foundation and architecture | **Complete** | 2025-11-06 | Solution structure (`.slnx`), canonical ULID `StableId`, domain identities, scene event model, session sequencer, conflict resolution (never silently overwrites accepted work), wire protocol contracts, retry policy, API host with Serilog structured logging + DI + live health endpoints, 22 passing tests, docs. |
| 2 | Desktop application UI | **Complete** | 2026-11-06 | WPF shell, six-page navigation, reusable theme/accessibility/reduced motion, WPF-independent MVVM, atomic local preferences, cancellable API checks, real account/team/project/session workflows, shared-script editor, file history and bundled local API startup. Latest Release build passes; all 123 .NET tests pass. |
| 3 | Authentication and profiles | **Complete** | 2026-11-06 | Durable SQLite accounts/session tables and versioned EF migration, PBKDF2 password hashes, random revocable 256-bit opaque bearer sessions stored only as SHA-256 digests, expiry/logout, enumeration-resistant login/registration, IP rate limit, profile revisions, HTTPS outside loopback, DPAPI-current-user remembered sessions, bound desktop account/profile controls. 9 API integration tests and Windows DPAPI/client tests pass (81 total solution tests). |
| 4 | Teams, friends and invitations | **Complete** | 2026-11-06 | Persistent teams/memberships, Owner/Lead/Member authorization, versioned promotion/demotion, seven-day in-app invitations with recipient-only accept/decline, normalized friendships and recipient acceptance, privacy-safe public names. Real Kestrel + SQLite tests cover user isolation, stale/duplicate invitation requests, role access and friend flows. |
| 5 | Project management | **Complete** | 2026-11-06 | Real team project create/list/rename/archive/restore APIs, role checks, case-insensitive unique names and optimistic revisions. Local folder picker validates Unity structure read-only and stores path in account/server-scoped LocalAppData; server DTOs never receive it. Project/team/roster and link controls are in WPF. Real desktop→Kestrel→SQLite workflow proves link/unlink leaves files unchanged and path absent from server response. |
| 6 | Backend and database | **In progress** | | SQLite/PostgreSQL provider selection, provider-neutral idempotent migration SQL, Npgsql pool/timeout/retry/production TLS policy, explicit script-based PostgreSQL migration mode, DB/backplane readiness, migration SQL/provider config tests. Live PostgreSQL SQL application, backup/restore and failover remain unverified (no server runtime available). |
| 7 | Real-time networking | **In progress** | | Authenticated/rate-limited WSS, project sessions, atomic ordered event log, idempotency, durable cursors/acks, durable conflict broadcasts, reconnecting desktop client and DPAPI inbox; Unity transport consumes the same protocol. SQLite/Kestrel, two-user peers and Unity Editor integration pass; live Redis multi-node behavior remains unverified. |
| 8 | Unity Editor integration | **Complete** | 2026-11-06 | Unity 6 UPM package (`UnityPackage`) with Runtime/Editor asmdefs; Editor account sign-in over API, bearer WSS and DPAPI inbox/outbox; explicit Undoable ID setup and scene/baseline binding; package compiled and EditMode tested on Unity 6000.5.0f1, including a live Kestrel/SQLite session test. |
| 9 | Shared Scene collaboration | **Complete** | 2026-11-06 | Durable server-sequenced scene events applied on the Unity Editor main thread; stable serialized IDs; remote/local GameObject create/destroy/rename/transform/reparent; scene GUID and SHA-256 baseline match; DPAPI event replay; no auto-save. Live Unity Editor↔Kestrel/SQLite test verifies remote application, local hierarchy publication and server delivery. |
| 10 | Inspector and Hierarchy synchronization | **Complete** | 2026-11-06 | Undo postprocess captures supported Inspector values; primitive/enum/string/float/color/vector/rect/bounds and stable in-scene references; allow-listed built-in component add/remove with safe SerializedProperty filtering. Revision-checked transform/rename/property writes, durable conflict notification to both peers, no silent overwrite. Unity EditMode applier/codec tests and live Inspector/hierarchy integration pass; unsupported component/asset graphs remain explicitly local. |
| 11 | Collaborative script editing | **In Progress** | | Authorized shared C# documents under `Assets/**/*.cs`, 1 MiB bound, optimistic revisions, and WPF editor with explicit refresh/save. Concurrent stale saves are rejected. No Unity local-file writes. Operation-level real-time co-editing, automatic remote refresh, Unity Editor integration and richer code-editor tooling remain. |
| 12 | Asset synchronization | **In Progress** | | Authenticated project asset list/upload/download APIs; SHA-256 content-addressed host storage, 32 MiB per-asset bound, revision checks and traversal-safe `Assets/` paths. API integration tests verify exact bytes/hash. Unity Editor transfer, dependency closure, object-storage deployment, retention/GC and production-scale transfer tests remain. |
| 13 | Reserved — Out of scope | **Out of scope** | | Not part of the product roadmap. |
| 14 | Version history and recovery | **In Progress** | | Script/asset snapshots retain up to 100 versions per file; restore is guarded by current revision and creates a new server-side revision. WPF history/restore controls and Kestrel tests exist. Unity Scene snapshot/compaction, local-project recovery, Git integration, database backup/restore, and broader audit/retention controls remain. |
| 15 | Reserved — Out of scope | **Out of scope** | | Not part of the product roadmap. |
| 16 | Security, testing, optimization and release | **In Progress** | | Added endpoint authorization, path/content/frame bounds, content hashes, revision checks, upload rate limiting and real SQLite/Kestrel tests. Release audit, high-DPI/accessibility review, performance/load tests, production storage, PostgreSQL/Redis live validation and deployment/recovery drills remain. |

## Roadmap scope decision

The roadmap stays focused on multiplayer Unity development, real-time collaboration,
shared Scenes, collaborative scripting, asset synchronization, developer presence,
version control, and project management. Phases 13 and 15 are reserved and **Out of
scope**, not deferred feature phases. No communication or AI assistant work is planned.

## Phases 11, 12, 14 and 16 implementation checkpoint (2026-11-06)

The user requested these phases while the external Phase 6–7 PostgreSQL/Redis acceptance
gap remains open. Work was performed against SQLite/Kestrel and the existing single-node
backplane; this does not claim that the deployment gate is closed.

- Phase 11 adds authorized, project-scoped C# documents, explicit WPF load/refresh/edit/save,
  1 MiB content bounds and optimistic revisions. Concurrent stale writes conflict rather
  than silently overwrite. This is not real-time operation-level co-editing; no Unity Editor
  integration or local Unity file modification is implemented.
- Phase 12 adds streaming content-addressed asset upload/download endpoints, path-safe
  `Assets/` names (excluding `.cs`), SHA-256 verification by construction, 32 MiB per item,
  2,000 active item / 512 MiB active catalog quotas and revision checking. Unity Editor
  transfer, dependency closure, cloud object storage and blob garbage collection remain.
- Phase 14 records up to 100 server-side versions per script/asset path. Restore is guarded
  by the current revision and creates a new version. WPF history controls and real API tests
  cover script and asset recovery. Unity Scene snapshots/compaction, local project recovery,
  and production backup/restore are not implemented.
- Phase 16 hardening adds per-IP upload/script-write limits, route/path/body bounds, auth
  checks, content-addressed storage, and authorization/conflict/large-body/history tests.
  Production security review, server content encryption-at-rest/key management,
  load/fuzz, presence, high-DPI/screen-reader and deployment recovery acceptance remain.
- Verification: `dotnet build JsUnitySync.slnx -c Release` succeeded with 0 warnings/errors;
  `dotnet test JsUnitySync.slnx -c Release --no-build` passed **123/123** (22 domain,
  37 server, 64 Windows desktop); EF reports no pending model changes. Unity EditMode tests
  were not rerun in this checkpoint. A hosted-API test verifies the bundled local
  backend starts migrations and stops only its owned loopback host. PostgreSQL and Redis
  live tests remain unavailable.

## Phases 6–7 implementation plan (combined request)

Proceed sequentially with verification checkpoints:

1. Phase 6: select SQLite/PostgreSQL explicitly; keep migrations provider-neutral and
   verify SQL generation for both; add migration tooling/command, env-only connection
   secrets, connection resilience and accurate readiness diagnostics. Preserve SQLite
   integration tests; do not claim live PostgreSQL without a server runtime.
2. Phase 7: add authorized project sessions and persistent per-session monotonic event
   sequences; WebSocket hello, event acceptance/broadcast, durable acks, replay/resume,
   bounded frames, conflict rejection and real desktop client reconnect/cursor storage.
   Add a Redis pub/sub backplane with a true in-memory single-node implementation.
3. Test across real Kestrel + SQLite and actual WebSocket client connections, including
   two peers, reconnect/replay, unauthorized access, conflicting writes and backpressure.
   Verify PostgreSQL-generated migrations; disclose if no live PostgreSQL/Redis service
   is present rather than claiming those integrations work in production.
4. Update run/config docs and tracker separately for both phases; only mark complete
   when actual supported tests and verification have passed.

### Environment constraint

PostgreSQL, Docker and Redis executables were not found on PATH at the start of this
request. Package/build/script generation can be verified, but a live connection to
those servers cannot be claimed unless a runtime becomes available.

## Phases 6–7 verification and handoff

- `dotnet build JsUnitySync.slnx -c Release`: passed, 0 warnings, 0 errors.
- `dotnet test JsUnitySync.slnx -c Release`: **108 passed**, 0 failed/skipped
  (22 foundation, 24 server, 62 Windows desktop).
- Phase 6 tests generate idempotent PostgreSQL migrations with the Npgsql provider,
  assert `BIGINT`/`BOOLEAN`, quoted filtered indexes and absence of SQLite DDL; provider
  DI configuration is tested without connecting. All Kestrel/account/team/project/
  session/event integration runs use real isolated SQLite databases.
- Phase 7 opens real Kestrel WebSockets for two authenticated peers; verifies project
  access control, ordered shared delivery, duplicate event idempotency, durable ack,
  disconnect-before-ack replay, server rejection of stale property writes, bounded
  backpressure behavior, and desktop DPAPI inbox write-before-ack.
- **Remaining acceptance gap:** PostgreSQL and Redis executables were unavailable.
  PostgreSQL migration SQL has not been applied to a live server; Redis pub/sub has not
  been exercised against Redis. Phases 6–7 remain In Progress for those live-provider/
  multi-node checks; no production PostgreSQL/Redis claim is made.
- The user explicitly requested Phases 8–10 next. Their Unity package is implemented
  and tested against the locally verified SQLite single-node WebSocket server. This is
  a documented environment-limited validation gap, not a claim that PostgreSQL/Redis
  multi-node deployment is complete.
- When live services are available, execute/upgrade/rollback PostgreSQL migrations,
  backup/restore and health checks, then launch two API nodes on shared PostgreSQL+
  Redis and verify cross-node fan-out/reconnect to close Phases 6–7. Next planned
  feature phase is 11 (collaborative script editing), subject to this infrastructure
  acceptance gate.

## Phases 3–5 implementation plan (combined request)

Execute in order, retaining separate build/test checkpoints:

1. Phase 3: introduce a separate Server infrastructure library and shared DTOs;
   persist users/password hashes/hashed session tokens; registration/login/logout,
   profile read/update with optimistic revisions, rate limits and authorization;
   desktop account UI and endpoint-bound Windows DPAPI credential persistence.
2. Phase 4: durable teams and memberships, owner/member authorization, team
   invitations and friend requests with explicit recipient accept/decline;
   duplicate/self-request prevention and desktop team/social panels.
3. Phase 5: authorized team project create/list/update/archive, revision conflicts,
   explicit read-only Unity-folder validation, and account/server-scoped local links
   stored outside the project. No uploading, cloning or synchronization claims.
4. At each checkpoint build/test before proceeding. Add real HTTP/database tests,
   cross-user authorization/conflict tests, desktop service/view-model tests and
   WPF resource/binding smoke coverage. Update architecture/setup/conventions.

### Technical dependency decision

Durable account/team/project metadata is a prerequisite, so the minimum database
foundation is pulled forward from Phase 6 rather than using fake in-memory stores.
Use EF Core with a real SQLite file for local development/tests in these phases.
The provider-selection and migration-SQL work is now in Phase 6, but live PostgreSQL
migration execution, backups, Redis runtime and deployment hardening remain unverified;
no PostgreSQL or Redis runtime is available on PATH.
Use revocable opaque bearer tokens (store only SHA-256 digests server-side) rather
than introducing JWT signing secrets before infrastructure/key management exists.
No project content or credential is logged. Later networking must reuse the same
authentication and resource authorization rather than inventing a second identity.

## Phase 3 verification and handoff

- `dotnet test JsUnitySync.slnx -c Release`: passed (22 foundation, 9 server, 50
  desktop tests); includes real Kestrel/SQLite registration, login, duplicate, password,
  unauthenticated, revocation, expiration, profile conflict, and DPAPI round-trip checks.
- Accounts/passwords/session metadata are separate from local desktop preferences;
  remember-me uses CurrentUser DPAPI; no plaintext token is placed in JSON or logs.
- Migration `Accounts` is created and applied by API startup. SQLite is the explicitly
  documented local bootstrap; PostgreSQL is not yet configured or tested.
- No JWT signing keys or hardcoded shared secrets; bearer tokens are random/revocable.
- **Phase 3 complete. Continue sequentially into Phase 4** as requested.

## Phase 4 verification and handoff

- Release backend tests pass (17 server tests): team isolation and membership,
  recipient-only invitations, duplicate/stale invite responses, Owner/Lead/Member
  authorization, revision-checked role change, social request acceptance/decline,
  duplicate/reverse/self requests, and no email disclosure.
- EF `TeamsAndSocial` migration is applied before tests; all sensitive collection
  endpoints require bearer authentication. Non-members receive a consistent 404.
- **Phase 4 complete. Continue sequentially into Phase 5** as requested.

## Phase 5 implementation plan

1. Add team-scoped project metadata APIs with member read, Lead/Owner create/rename/
   archive, duplicate-name prevention (case-insensitive), and expected-revision checks.
2. Add read-only structural validation of an explicitly picked Unity folder; keep
   its absolute local path in account/server-scoped LocalAppData, never in API DTOs.
3. Integrate project/roster/team panels and operations in WPF, with local unlink
   clearly separated from server archive and no project-file mutation.
4. Test migrations, real HTTP permissions/conflicts, path validation, local link
   scoping/atomic storage and an end-to-end desktop→API→SQLite project/folder flow.

## Phase 5 verification and handoff

- `dotnet build JsUnitySync.slnx -c Release`: passed, 0 warnings, 0 errors.
- `dotnet test JsUnitySync.slnx -c Release --no-build`: 99 passed, 0 failed,
  0 skipped (22 foundation, 17 server integration, 60 Windows desktop).
- Real API tests cover team project create/list/rename/archive/restore, normalized
  duplicate names, permissions and stale revisions. Read-only folder tests cover valid
  Unity layout, invalid/oversized manifests, link persistence/scoping/duplicates and
  corrupted-store preservation.
- End-to-end Windows desktop workflow uses real SQLite/Kestrel, creates account,
  team/project, links a test Unity folder, checks raw API response for path absence,
  unlinks without deleting, signs out and verifies server token revocation.
- WPF STA smoke tests load account/team/project themes and navigation at both window
  sizes, verify page-panel visibility and bindings, no binding errors and rendered
  output. Standalone Release desktop opens and closes cleanly (exit 0).
- Updated README, ARCHITECTURE and DEVELOPMENT. At the Phase 5 handoff, PostgreSQL,
  Redis and multiplayer behavior were not yet implemented. Phases 3–5 were completed
  sequentially with checkpoints; the minimum durable SQLite foundation was pulled
  forward from Phase 6.
- **At that handoff, Next was Phase 6 — Backend and database.** The current
  PostgreSQL/Redis implementation and remaining live-service validation are recorded
  separately in the Phases 6–7 status above. No phase was skipped.

## Phases 8–10 verification and handoff

- Unity 6 UPM package compiled in the installed Unity Editor **6000.5.0f1**. Its
  EditMode suite passed **10/10** with `JSUNITYSYNC_TEST_SERVER` pointing at the live
  local Kestrel/SQLite API (the end-to-end test is intentionally skipped without this
  test-only environment variable).
- The live Unity test creates two authorized accounts/team/project/session, binds a
  saved scene+stable-ID baseline, opens both Unity and peer WebSockets, applies remote
  GameObject/component/Inspector events, captures local Hierarchy/component/Inspector
  edits and verifies a conflicting second-user write is rejected without replacing the
  accepted Unity value. Inbox event/cursor storage is CurrentUser-DPAPI protected.
- Package unit tests cover ID uniqueness/repair, remote Scene create/rename/transform/
  destroy, allow-listed component state, Inspector serialization and encrypted inbox.
  No automatic saves occur; scene IDs/baselines require explicit user review/commit.
- Release .NET build remains 0 warnings/errors and all **108 .NET tests** pass (22
  foundation, 24 server, 62 desktop). Existing WPF navigation tests render the Session
  and Projects panels. The standalone desktop opens/closes successfully.
- Phases 8–10 are complete for the explicitly supported single-active-Scene feature
  set. Phases 6–7 remain In Progress only for live PostgreSQL migration/backup and
  Redis multi-node deployment tests; no PostgreSQL/Redis runtime was available. No
  production/multi-node claim is made. Unsupported custom component creation and Unity
  Editor-side script/asset integration remain incomplete.
- At the earlier Phase 8–10 handoff, Phase 11 was next, with external Phase 6–7 checks
  still pending. The later user request explicitly advanced work using the tested local
  SQLite/single-node deployment; that does not close the PostgreSQL/Redis gates.

## Phase 2 implementation plan

1. Preserve the Phase 1 protocol/API and establish a WPF `net10.0-windows` host.
2. Keep presentation logic in `Desktop.Core` (`net10.0`), independent of WPF and
   server infrastructure. Compose services in `App.xaml.cs` with DI and logging.
3. Build a reusable charcoal/purple/blue resource dictionary, resizable native
   window, sidebar navigation, overview, empty-state workspace panels and settings.
   Retain native window controls, keyboard focus cues, readable contrast and scrolling.
4. Implement real, cancellable API readiness checks and validated endpoint settings
   persisted atomically in LocalAppData. Never access Unity project files. Later-phase
   actions remain explicitly unavailable; no fake activity, accounts or sessions.
5. Test navigation/notifications, commands, settings validation/persistence, HTTP
   failures/cancellation, and actual WPF resource loading/navigation on an STA thread.
6. Build/test the solution, run the desktop smoke checks and update all central docs.

## Phase 2 verification and handoff

- Completed the six implementation steps above without changing the existing API,
  wire protocol or Unity event factory. No phase was skipped.
- `dotnet build JsUnitySync.slnx -c Release`: passed, 0 warnings, 0 errors.
- `dotnet test JsUnitySync.slnx -c Release --no-build`: 64 passed, 0 failed/skipped.
- WPF smoke: six pages render at default/minimum sizes; settings bindings work;
  no WPF binding errors. Rendered screenshot also visually reviewed.
- Real Kestrel compatibility test: API ready → server stopped/unavailable → restart
  on the same address → API ready. Checks never claim session connectivity.
- Standalone Release executable: workspace window opened; native close exited 0.
- Updated README, architecture and development conventions. Settings and logs are
  app-owned LocalAppData files; Unity projects remain untouched.
- High-DPI, screen-reader and broad usability/security-release validation remain
  part of Phase 16, beyond the automated rendering and focus infrastructure checks.

## Rules
- A phase is only marked **Complete** when the solution builds, all tests pass, and
  documentation is updated.
- Phases are never skipped without a documented technical reason recorded here.
