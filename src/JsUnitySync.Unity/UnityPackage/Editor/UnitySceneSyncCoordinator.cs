using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JsUnitySync.UnityPackage.Editor
{
    [Serializable]
    internal sealed class UnityRenameData { public string newName = ""; }

    internal sealed class UnitySceneSyncCoordinator : IDisposable
    {
        private readonly UnitySceneTransport _transport;
        private readonly UnityEncryptedInbox _inbox;
        private readonly Scene _scene;
        private readonly string _sceneGuid;
        private readonly ConcurrentQueue<UnityWireFrame> _incoming = new ConcurrentQueue<UnityWireFrame>();
        private readonly Dictionary<string, UnitySceneSnapshot> _snapshots = new Dictionary<string, UnitySceneSnapshot>();
        private readonly Dictionary<string, long> _propertyRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, RemoteModification> _remoteModificationSuppressions = new Dictionary<string, RemoteModification>(StringComparer.Ordinal);
        private bool _applyingRemote;
        private bool _active;
        private double _nextTransformScan;
        private bool _disposed;

        public event Action<string> Status;

        public UnitySceneSyncCoordinator(UnitySceneTransport transport, UnityEncryptedInbox inbox, Scene scene, string sceneGuid)
        {
            _transport = transport; _inbox = inbox; _scene = scene; _sceneGuid = sceneGuid;
            _transport.AcceptedEvent += OnAcceptedEvent;
            _transport.Conflict += OnConflict;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            EditorApplication.update += Update;
            Undo.postprocessModifications += OnPostprocessModifications;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        public void Begin()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnitySceneSyncCoordinator));
            if (SceneManager.GetActiveScene() != _scene || UnitySceneIdentity.GetSceneGuid(_scene) != _sceneGuid)
                throw new InvalidOperationException("The active Scene changed while connecting. Reopen the selected collaboration session from the correct Scene.");
            _applyingRemote = true;
            try
            {
                foreach (var frame in _inbox.AcceptedSnapshot().OrderBy(x => x.sequence)) ApplyAccepted(frame);
                // Restore local changes that were durably queued but not yet accepted before the last Editor shutdown.
                foreach (var pending in _inbox.PendingOutgoing()) ApplyPendingLocal(pending);
                RestoreLocallyRejectedConflicts();
                var expectedIds = _inbox.ExpectedStableObjectIds();
                var actualIds = UnitySceneIdentity.GetStableObjectIds(_scene);
                if (!expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal))
                    throw new InvalidOperationException("The loaded Scene does not match the encrypted accepted-event history. Preserve local work and restore the shared baseline before reconnecting.");
                CaptureInitialState();
            }
            finally { _applyingRemote = false; }
            _active = true;
            _nextTransformScan = EditorApplication.timeSinceStartup;
            Status?.Invoke("Scene identity tracking is active. Remote events are applied in the Unity Editor; save the Scene explicitly when ready.");
        }

        private void Update()
        {
            while (_incoming.TryDequeue(out var frame))
            {
                _applyingRemote = true;
                try { ApplyAccepted(frame); }
                catch (Exception error) { Status?.Invoke("A durable remote event could not be applied: " + error.GetType().Name + ". It remains in the encrypted inbox; do not save until resolved."); }
                finally { _applyingRemote = false; }
            }
            if (!_active || _disposed) return;
            if (SceneManager.GetActiveScene() != _scene || UnitySceneIdentity.GetSceneGuid(_scene) != _sceneGuid)
            {
                _active = false;
                _transport.Stop();
                Status?.Invoke("The active Unity Scene changed. Reconnect to the matching shared Scene; no further edits will be sent.");
                return;
            }
            PruneRemoteModificationSuppressions();
            if (EditorApplication.timeSinceStartup < _nextTransformScan) return;
            _nextTransformScan = EditorApplication.timeSinceStartup + 0.1; // 10 Hz cap; property edits use Unity's Undo stream.
            ScanLocalChanges();
        }

        private void CaptureInitialState()
        {
            _snapshots.Clear();
            foreach (var gameObject in UnitySceneIdentity.GetSceneObjects(_scene))
            {
                var identity = gameObject.GetComponent<JsUnitySyncObjectId>();
                if (identity != null && StableIdFactory.IsValid(identity.StableId))
                    _snapshots[identity.StableId] = UnitySceneSnapshot.Capture(gameObject, identity.StableId);
            }
        }

        private void ScanLocalChanges()
        {
            var current = new Dictionary<string, UnitySceneSnapshot>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gameObject in UnitySceneIdentity.GetSceneObjects(_scene))
            {
                var identity = gameObject.GetComponent<JsUnitySyncObjectId>();
                if (identity == null)
                {
                    // Once the user explicitly enabled tracking, newly created objects receive
                    // an Undo-recorded identity; the Scene is dirtied but never auto-saved.
                    identity = Undo.AddComponent<JsUnitySyncObjectId>(gameObject);
                    identity.AssignNewId();
                    EditorUtility.SetDirty(identity);
                    EditorSceneManager.MarkSceneDirty(_scene);
                }
                else if (!StableIdFactory.IsValid(identity.StableId) || !ids.Add(identity.StableId))
                {
                    Undo.RecordObject(identity, "Assign unique J'sUnitySync object id");
                    identity.SetId(StableIdFactory.NewId());
                    EditorUtility.SetDirty(identity);
                    EditorSceneManager.MarkSceneDirty(_scene);
                }
                ids.Add(identity.StableId);
                var snapshot = UnitySceneSnapshot.Capture(gameObject, identity.StableId);
                current[identity.StableId] = snapshot;
                if (!_snapshots.TryGetValue(identity.StableId, out var previous))
                {
                    SendMutation(UnitySceneEventKind.GameObjectCreated, identity.StableId, string.Empty, string.Empty, -1, snapshot.ToDataJson(), snapshot.ToDataJson());
                    foreach (var typeName in snapshot.ComponentTypes) SendComponentEvent(gameObject, identity.StableId, typeName, true);
                }
                else
                {
                    foreach (var typeName in snapshot.ComponentTypes.Except(previous.ComponentTypes, StringComparer.Ordinal)) SendComponentEvent(gameObject, identity.StableId, typeName, true);
                    foreach (var typeName in previous.ComponentTypes.Except(snapshot.ComponentTypes, StringComparer.Ordinal)) SendComponentEvent(gameObject, identity.StableId, typeName, false);
                    if (!string.Equals(previous.Name, snapshot.Name, StringComparison.Ordinal))
                    {
                        var key = PropertyKey(identity.StableId, "GameObject", "name");
                        SendMutation(UnitySceneEventKind.GameObjectRenamed, identity.StableId, "GameObject", "name", Revision(key), snapshot.Name, JsonUtility.ToJson(new UnityRenameData { newName = snapshot.Name }));
                    }
                    if (!previous.TransformEquals(snapshot))
                    {
                        var key = PropertyKey(identity.StableId, "Transform", "localTransform");
                        var data = snapshot.ToDataJson();
                        SendMutation(UnitySceneEventKind.TransformChanged, identity.StableId, "Transform", "localTransform", Revision(key), data, data);
                    }
                }
            }
            foreach (var previous in _snapshots.Values)
                if (!current.ContainsKey(previous.Id))
                    SendMutation(UnitySceneEventKind.GameObjectDestroyed, previous.Id, string.Empty, string.Empty, -1, "", "{}");
            _snapshots.Clear();
            foreach (var pair in current) _snapshots[pair.Key] = pair.Value;
        }

        private UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            if (!_active || modifications == null) return modifications;
            foreach (var undoModification in modifications)
            {
                var modification = undoModification.currentValue;
                if (modification == null) continue;
                var component = modification.target as Component;
                if (component == null || component is Transform || component is JsUnitySyncObjectId || component.gameObject.scene != _scene) continue;
                var propertyPath = modification.propertyPath;
                if (_applyingRemote) { RememberRemoteModification(component, propertyPath); continue; }
                if (ConsumeRemoteModification(component, propertyPath)) continue;
                var identity = component.GetComponent<JsUnitySyncObjectId>();
                if (identity == null || !StableIdFactory.IsValid(identity.StableId)) continue;
                EditorApplication.delayCall += () => SendPropertyModification(component, identity.StableId, propertyPath);
            }
            return modifications;
        }

        private void SendComponentEvent(GameObject gameObject, string objectId, string componentType, bool added)
        {
            if (!UnityComponentRegistry.IsAllowed(componentType))
            {
                Status?.Invoke("Component structure for " + componentType + " is not allow-listed for collaboration; it remains local.");
                return;
            }
            var component = gameObject.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType().FullName == componentType);
            var serialized = string.Empty;
            if (added && (component == null || !UnityComponentStateCodec.TryCapture(component, out serialized)))
            {
                Status?.Invoke("Component state for " + componentType + " is unsupported or exceeds the safe event limit; it remains local.");
                return;
            }
            var mutation = new UnitySceneMutation
            {
                kind = (int)(added ? UnitySceneEventKind.ComponentAdded : UnitySceneEventKind.ComponentRemoved),
                objectId = objectId, sceneGuid = _sceneGuid, componentType = componentType, serializedValue = serialized, dataJson = "{}"
            };
            if (_transport.QueueMutation(mutation)) Status?.Invoke("Queued " + (added ? "component addition" : "component removal") + " for " + componentType + ".");
            else Status?.Invoke("Component change remains local and needs attention before saving.");
        }

        private void SendPropertyModification(Component component, string objectId, string propertyPath)
        {
            if (!_active || _applyingRemote || component == null || component.gameObject.scene != _scene || ConsumeRemoteModification(component, propertyPath)) return;
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(propertyPath);
            if (property == null || !UnityPropertyCodec.TryEncode(property, out var value)) return;
            var type = component.GetType().FullName;
            var key = PropertyKey(objectId, type, propertyPath);
            SendMutation(UnitySceneEventKind.PropertyChanged, objectId, type, propertyPath, Revision(key), value, "{}");
        }

        private void SendMutation(UnitySceneEventKind kind, string objectId, string component, string property, long revision, string serialized, string data)
        {
            if (!_active || _applyingRemote) return;
            var mutation = new UnitySceneMutation
            {
                kind = (int)kind, objectId = objectId, sceneGuid = _sceneGuid, componentType = component,
                propertyName = property, baseRevision = revision, serializedValue = serialized, dataJson = data
            };
            if (!_transport.QueueMutation(mutation)) Status?.Invoke("A Scene change is safely queued for reconnect or was not sendable. Check the session status before saving.");
        }

        private void OnAcceptedEvent(UnityWireFrame frame) => _incoming.Enqueue(frame);

        private void OnConflict(UnityWireFrame frame)
        {
            EditorApplication.delayCall += () =>
            {
                var conflict = JsonUtility.FromJson<UnityConflictPayload>(frame.payloadJson);
                if (conflict != null)
                {
                    var key = PropertyKey(conflict.objectId, conflict.componentType, conflict.propertyName);
                    _propertyRevisions[key] = conflict.acceptedRevision;
                    var localWasRejected = frame.authorId == _transport.UserId;
                    Status?.Invoke(localWasRejected
                        ? "Your stale property write was rejected. The server accepted " + conflict.acceptedValue + "; your local scene value " + conflict.rejectedValue + " was preserved for manual reconciliation."
                        : "A collaborator's stale property write was rejected. Your accepted value " + conflict.acceptedValue + " remains authoritative.");
                }
            };
        }

        private void ApplyAccepted(UnityWireFrame frame)
        {
            if (frame.kind != (int)UnityFrameKind.SceneEvent || frame.sessionId.Length == 0) return;
            var mutation = JsonUtility.FromJson<UnitySceneMutation>(frame.payloadJson);
            if (mutation == null || mutation.sceneGuid != _sceneGuid) throw new InvalidOperationException("Scene event does not match the connected Scene.");
            var guarded = GuardedProperty(mutation);
            if (guarded != null && (_inbox.HasUnresolvedConflict(mutation.objectId, guarded.Value.Component, guarded.Value.Property, _transport.UserId) || _inbox.PendingOutgoing().Any(pending =>
            {
                if (pending.frameId == frame.frameId) return false;
                var local = JsonUtility.FromJson<UnitySceneMutation>(pending.payloadJson);
                var localGuarded = local == null ? null : GuardedProperty(local);
                return local != null && local.objectId == mutation.objectId && localGuarded == guarded && local.baseRevision == mutation.baseRevision;
            })))
            {
                // Preserve a local unaccepted edit. Its stale write will receive the explicit server conflict below.
                AdvanceRevision(mutation, guarded.Value.Component, guarded.Value.Property);
                return;
            }
            var kind = (UnitySceneEventKind)mutation.kind;
            var identity = UnitySceneIdentity.Find(_scene, mutation.objectId);
            if (kind == UnitySceneEventKind.GameObjectDestroyed && identity != null)
            {
                foreach (var child in identity.GetComponentsInChildren<JsUnitySyncObjectId>(true)) _snapshots.Remove(child.StableId);
            }
            identity = UnitySceneMutationApplier.Apply(_scene, mutation);
            if (identity != null && kind == UnitySceneEventKind.PropertyChanged)
                RememberRemoteModification(identity.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType().FullName == mutation.componentType), mutation.propertyName);
            else if (identity != null && kind == UnitySceneEventKind.ComponentAdded)
                RememberAllComponentProperties(identity.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType().FullName == mutation.componentType));
            if (kind == UnitySceneEventKind.GameObjectRenamed) AdvanceRevision(mutation, "GameObject", "name");
            else if (kind == UnitySceneEventKind.TransformChanged) AdvanceRevision(mutation, "Transform", "localTransform");
            else if (kind == UnitySceneEventKind.PropertyChanged) AdvanceRevision(mutation, mutation.componentType, mutation.propertyName);
            if (identity == null) _snapshots.Remove(mutation.objectId);
            else _snapshots[identity.StableId] = UnitySceneSnapshot.Capture(identity.gameObject, identity.StableId);
            if (identity != null) Status?.Invoke("Applied server scene event " + frame.sequence + " (" + kind + ") to the active Scene.");
        }

        private void RestoreLocallyRejectedConflicts()
        {
            foreach (var frame in _inbox.ConflictsSnapshot())
            {
                var conflict = JsonUtility.FromJson<UnityConflictPayload>(frame.payloadJson);
                if (conflict == null || conflict.rejectedById != _transport.UserId) continue;
                UnitySceneMutation rejected;
                if (conflict.componentType == "GameObject" && conflict.propertyName == "name")
                    rejected = new UnitySceneMutation { kind = (int)UnitySceneEventKind.GameObjectRenamed, objectId = conflict.objectId, sceneGuid = _sceneGuid,
                        serializedValue = conflict.rejectedValue, dataJson = JsonUtility.ToJson(new UnityRenameData { newName = conflict.rejectedValue }) };
                else if (conflict.componentType == "Transform" && conflict.propertyName == "localTransform")
                    rejected = new UnitySceneMutation { kind = (int)UnitySceneEventKind.TransformChanged, objectId = conflict.objectId, sceneGuid = _sceneGuid,
                        serializedValue = conflict.rejectedValue, dataJson = conflict.rejectedValue };
                else
                    rejected = new UnitySceneMutation { kind = (int)UnitySceneEventKind.PropertyChanged, objectId = conflict.objectId, sceneGuid = _sceneGuid,
                        componentType = conflict.componentType, propertyName = conflict.propertyName, baseRevision = conflict.acceptedRevision, serializedValue = conflict.rejectedValue, dataJson = "{}" };
                UnitySceneMutationApplier.Apply(_scene, rejected);
            }
        }

        private void ApplyPendingLocal(UnityWireFrame frame)
        {
            if (frame.kind != (int)UnityFrameKind.SceneEvent) return;
            var mutation = JsonUtility.FromJson<UnitySceneMutation>(frame.payloadJson);
            if (mutation == null || mutation.sceneGuid != _sceneGuid) return;
            _applyingRemote = true;
            try { UnitySceneMutationApplier.Apply(_scene, mutation); }
            finally { _applyingRemote = false; }
        }

        private static (string Component, string Property)? GuardedProperty(UnitySceneMutation mutation)
        {
            switch ((UnitySceneEventKind)mutation.kind)
            {
                case UnitySceneEventKind.PropertyChanged: return (mutation.componentType, mutation.propertyName);
                case UnitySceneEventKind.TransformChanged: return ("Transform", "localTransform");
                case UnitySceneEventKind.GameObjectRenamed: return ("GameObject", "name");
                default: return null;
            }
        }

        private void RememberAllComponentProperties(Component component)
        {
            if (component == null) return;
            var serialized = new SerializedObject(component);
            var property = serialized.GetIterator();
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                RememberRemoteModification(component, property.propertyPath);
            }
        }

        private void RememberRemoteModification(Component component, string propertyPath)
        {
            if (component == null || string.IsNullOrEmpty(propertyPath)) return;
            var property = new SerializedObject(component).FindProperty(propertyPath);
            if (property != null && UnityPropertyCodec.TryEncode(property, out var value))
                _remoteModificationSuppressions[component.GetHashCode() + "|" + propertyPath] = new RemoteModification(value, EditorApplication.timeSinceStartup + 1.0);
        }

        private bool ConsumeRemoteModification(Component component, string propertyPath)
        {
            if (component == null || string.IsNullOrEmpty(propertyPath)) return false;
            var key = component.GetHashCode() + "|" + propertyPath;
            if (!_remoteModificationSuppressions.TryGetValue(key, out var expected)) return false;
            _remoteModificationSuppressions.Remove(key);
            var property = new SerializedObject(component).FindProperty(propertyPath);
            return property != null && UnityPropertyCodec.TryEncode(property, out var current) && current == expected.SerializedValue;
        }

        private void PruneRemoteModificationSuppressions()
        {
            var now = EditorApplication.timeSinceStartup;
            foreach (var key in _remoteModificationSuppressions.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToArray())
                _remoteModificationSuppressions.Remove(key);
        }

        private void AdvanceRevision(UnitySceneMutation mutation, string component, string property)
        {
            if (string.IsNullOrEmpty(property)) return;
            var key = PropertyKey(mutation.objectId, component, property);
            _propertyRevisions[key] = Math.Max(Revision(key), mutation.baseRevision + 1);
        }

        private long Revision(string key) { return _propertyRevisions.TryGetValue(key, out var revision) ? revision : 0; }
        private static string PropertyKey(string objectId, string component, string property) { return objectId + "|" + component + "|" + property; }

        private void OnHierarchyChanged() { if (_active && !_applyingRemote) _nextTransformScan = 0; }
        private void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            if (_active && !_applyingRemote && stream.isCreated && stream.length > 0) _nextTransformScan = 0;
        }
        private void OnSceneSaved(Scene saved)
        {
            if (saved != _scene || _disposed) return;
            try
            {
                _inbox.RecordSceneSaved(UnitySceneIdentity.SceneFileHash(_scene));
                Status?.Invoke("Saved Scene fingerprint recorded in the protected session inbox. Remote changes remain version-controlled project files; no auto-save occurs.");
            }
            catch (Exception error) { Status?.Invoke("Could not record saved Scene fingerprint: " + error.GetType().Name + ". Reconnect will be refused until the baseline is reconciled."); }
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (_active && next != _scene)
            {
                _active = false;
                _transport.Stop();
                Status?.Invoke("Active Scene changed; collaboration disconnected to prevent edits being applied to the wrong Scene.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _active = false;
            _transport.AcceptedEvent -= OnAcceptedEvent;
            _transport.Conflict -= OnConflict;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            EditorApplication.update -= Update;
            Undo.postprocessModifications -= OnPostprocessModifications;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
        }

        private sealed class RemoteModification
        {
            public readonly string SerializedValue;
            public readonly double ExpiresAt;
            public RemoteModification(string serializedValue, double expiresAt) { SerializedValue = serializedValue; ExpiresAt = expiresAt; }
        }

        private sealed class UnitySceneSnapshot
        {
            public string Id;
            public string Name;
            public string ParentId;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public string[] ComponentTypes;
            public static UnitySceneSnapshot Capture(GameObject gameObject, string id)
            {
                var parentTag = gameObject.transform.parent == null ? null : gameObject.transform.parent.GetComponent<JsUnitySyncObjectId>();
                var t = gameObject.transform;
                var componentTypes = gameObject.GetComponents<Component>()
                    .Where(component => component != null && !(component is Transform) && !(component is JsUnitySyncObjectId))
                    .Select(component => component.GetType().FullName).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                return new UnitySceneSnapshot { Id = id, Name = gameObject.name, ParentId = parentTag == null ? string.Empty : parentTag.StableId,
                    Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale, ComponentTypes = componentTypes };
            }
            public bool TransformEquals(UnitySceneSnapshot other) => ParentId == other.ParentId &&
                (Position - other.Position).sqrMagnitude < 0.0000001f && Quaternion.Angle(Rotation, other.Rotation) < 0.01f &&
                (Scale - other.Scale).sqrMagnitude < 0.0000001f;
            public string ToDataJson() => JsonUtility.ToJson(new UnitySceneObjectData { name = Name, parentObjectId = ParentId,
                positionX = Position.x, positionY = Position.y, positionZ = Position.z,
                rotationX = Rotation.x, rotationY = Rotation.y, rotationZ = Rotation.z, rotationW = Rotation.w,
                scaleX = Scale.x, scaleY = Scale.y, scaleZ = Scale.z });
        }
    }
}
