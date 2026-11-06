# J'sUnitySync Unity Editor package (Phase 8–10)

Unity 6 Editor package: `com.jsunitysync.collaboration`, tested with Unity `6000.5.0f1`.
It implements authenticated Editor login, project-session connection, stable GameObject
identity, durable Scene events and shared Hierarchy/Transform/Inspector mutation.

## Install into an existing Unity project

1. Ensure the project is on the same saved Scene baseline as the session owner. Join a
   session only from a **saved Scene asset**. In the Editor package manager, choose
   **Add package from disk…** and select this package's `package.json`, or add a local
   package reference to your project's `Packages/manifest.json`:

   ```json
   "com.jsunitysync.collaboration": "file:C:/path/to/J'sUnitySync/src/JsUnitySync.Unity/UnityPackage"
   ```

2. Run the backend API and sign in to the same account in **Window → J'sUnitySync →
   Collaboration**. The password is cleared after sign-in; the bearer token is kept
   only in Editor memory (not EditorPrefs, the project, or logs). Enter the session ID
   shown by the desktop Sessions panel.
3. **Enable / repair stable IDs** is an explicit, undoable action. It adds a serialized
   `JsUnitySyncObjectId` component to scene GameObjects and marks the Scene dirty. This
   changes the in-memory Scene, never auto-saves it. Save and share/commit this tagged
   Scene baseline with teammates **before** connecting them. The server binds a session
   to both the Unity scene asset GUID and a SHA-256 fingerprint of the saved Scene bytes
   plus its sorted stable-object-ID baseline. Only the digest reaches the server; scene
   content is not uploaded. A different saved baseline is refused.
4. Connect. New GameObjects in the enabled session receive a stable ID, and rename,
   transform/reparent and supported Inspector changes are sent as typed scene events.
   Events are centrally ordered; a stale same-property edit is rejected and shown as a
   conflict, not silently overwritten. Incoming accepted events are encrypted with
   Windows CurrentUser DPAPI under LocalAppData before acknowledgment, then applied on
   Unity's Editor main thread. The Scene becomes dirty for review; **Save** remains the
   user's action. Scene file bytes, scripts and assets are never uploaded; only the supported typed Scene/Inspector event payloads are transmitted.

## Supported Inspector fields

Primitive integers, enums, booleans, floats, strings, colors, vectors, rectangles,
bounds and references to other `JsUnitySyncObjectId` GameObjects/components. Unsupported
serialized Unity asset references, arrays and arbitrary managed-reference graphs are
not sent. A post-commit server conflict preserves the accepted/rejected values in the
encrypted inbox; resolve it deliberately before continuing.

## Local storage and recovery

The event inbox/outbox/cursor is CurrentUser-DPAPI encrypted outside the project at
`%LOCALAPPDATA%\\JsUnitySync\\Unity\\<opaque-key>.dpapi`. It is bounded to 20,000
accepted events. The client stores every event before sending its durable acknowledgment;
reconnect replays pending outbound events idempotently and restores inbound mutations
from the encrypted inbox. If local storage fails, the client does not acknowledge the
incoming event. No project contents, bearer tokens or passwords are logged or copied to
`Assets`, `ProjectSettings`, or version control.

## Phase boundaries / limitations

This package synchronizes the **active saved Scene** only. All peers must start from a
shared version-controlled baseline. Unity Scene application happens in memory and does
not auto-save. Prefab/asset content synchronization, multi-scene editing, custom/arbitrary component
structure outside the built-in allow-list, Unity Editor asset transfer, shared-script
editing inside the Editor, Scene snapshots and event-log compaction are not implemented
in this package. Script documents, asset blobs and file history currently live only in
the server-backed desktop/API workflows. Redis/PostgreSQL multi-node deployment must be
live-tested separately; locally verified transport uses SQLite/Kestrel and a single-node
in-process backplane.

## Editor tests

The package contains EditMode tests for ULID compatibility/uniqueness, explicit and
undoable identity insertion, duplicate-id repair, Scene event enum compatibility,
remote create/rename/transform/destroy, supported Inspector property application and
DPAPI inbox persistence. In a test Unity project, add the package to `testables` in
`Packages/manifest.json` and install `com.unity.test-framework`, then run EditMode tests
from Test Runner or Unity batch mode.
