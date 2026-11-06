# J'sUnitySync — Architecture

J'sUnitySync is a real-time multiplayer Unity development platform. It consists of a
standalone Windows desktop application, a native Unity Editor extension, a backend
collaboration server, and supporting infrastructure.

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
                  |  - AssetSync service   |
                  |  - AI service          |
                  +------------+------------+
                               |
        +-----------+---------+---------+-----------+
        |           |                   |           |
   PostgreSQL     Redis          S3-compatible   Git
   (source of     (presence,     object storage  (version
    truth)         sessions,     (assets)        history)
                   pub/sub)
```

## Systems

1. **Desktop application** (`src/JsUnitySync.Desktop`, WPF) — sessions dashboard, team
   management, chat/voice/screen-sharing UI, collaborative Monaco script editor,
   version history browser. Premium dark theme: charcoal backgrounds, electric
   purple accents, electric blue highlights.

2. **Unity Editor extension** (`src/JsUnitySync.Unity`, netstandard2.1) — hooks into
   scene lifecycle, maps Unity objects to stable object identifiers, emits and
   applies scene synchronization events, surfaces presence and chat inside the Editor.

3. **Backend API** (`src/JsUnitySync.Api`, ASP.NET Core) — authentication, teams,
   projects, invitations, project metadata, version history endpoints. Stateless,
   horizontally scalable behind a load balancer.

4. **Real-time collaboration service** (`src/JsUnitySync.RealTime`) — WebSocket hub.
   Session rooms, ordered event delivery with acknowledgment, conflict resolution,
   presence, snapshot recovery. Uses Redis backplane for multi-instance fan-out.

5. **Database and caching infrastructure** — PostgreSQL is the source of truth for
   users, teams, projects, sessions, and version history. Redis holds presence,
   session state, and pub/sub for event fan-out.

6. **Asset synchronization service** (`src/JsUnitySync.AssetSync`) — content-addressed
   upload/download of Unity assets to S3-compatible storage, with a dependency graph
   (Guid-based) so an asset is never distributed without its dependencies.

7. **Communication services** — text chat (WebSocket), voice and screen sharing
   (WebRTC, mesh for small teams; SFU introduced if needed). Signaling rides the
   RealTime WebSocket.

8. **AI integration service** — LLM-backed development assistant for script
   generation, refactoring, and error explanation. Explicitly a later phase.

## Core synchronization model (established in Phase 1, elaborated in Phases 7–12)

- **Stable object identifiers**: every synchronized scene object has a
  `StableId` (ULID) that survives renames, reparenting, and Unity GUID changes.
  It is the join key for all events and snapshots.
- **Scene synchronization events**: typed, ordered, idempotent events
  (`SceneEventEnvelope`) carrying the author, session, sequence number, and payload.
- **Reliable delivery**: server assigns monotonic per-session sequence numbers;
  clients acknowledge; unacknowledged events are re-sent after reconnect.
- **Conflict resolution**: last-writer-wins per property by default with explicit
  ownership claims for guarded objects. The system **never silently overwrites
  another developer's accepted work** — conflicting accepted writes are surfaced
  to both developers for resolution.
- **Snapshot recovery**: periodic session snapshots plus an event log allow any
  client to reconstruct scene state after a disconnect, and allow restore of
  previous states (Phase 14).
- **Asset dependency tracking**: Unity's asset GUID dependency graph is mirrored
  server-side so asset sync distributes complete dependency closures.

## Architectural rules

- **Dependency direction**: `Domain` and `Contracts` depend on nothing.
  Infrastructure (API, RealTime, AssetSync) depends on Domain and Contracts.
  Clients (Desktop, Unity) depend on Contracts only.
- **Dependency injection everywhere**: composition roots are the app hosts
  (`Program.cs` in Api/RealTime/Desktop). Libraries register their services via
  `IServiceCollection` extension methods.
- **Structured logging**: `Microsoft.Extensions.Logging` with Serilog sinks
  (console + file). Every log carries a `SessionId`/`CorrelationId` scope where
  applicable.
- **Network failure handling**: all client–server calls have retry with exponential
  backoff and jitter; the WebSocket client auto-reconnects with event replay.
- **Security**: JWT authentication for API and RealTime; authorization enforced
  per-team/per-project; user project files are never modified without an explicit
  user-confirmed action; secrets never enter source control.
- **No fake implementations**: every phase ships real, tested functionality.
  Stubs that must exist temporarily are marked `[PhasePreview(phase: n)]` and are
  never presented as complete features.

## Testing strategy

- Domain and protocol logic: xUnit unit tests (`tests/JsUnitySync.Domain.Tests`,
  `tests/JsUnitySync.Contracts.Tests`, ...).
- API: integration tests with `WebApplicationFactory` (added Phase 6).
- Real-time: in-memory transport tests (Phase 7).
- Desktop: WPF UI logic kept in testable view models (Phase 2 onward).
- Unity: protocol-level tests run in the .NET test projects; the Editor package
  additionally ships a Unity Test Runner suite (Phase 8).
