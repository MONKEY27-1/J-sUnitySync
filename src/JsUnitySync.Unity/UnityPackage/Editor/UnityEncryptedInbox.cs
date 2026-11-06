using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace JsUnitySync.UnityPackage.Editor
{
    [Serializable]
    public sealed class UnityInboxState
    {
        public string clientId = "";
        public string sceneGuid = "";
        public string sceneManifestHash = "";
        public string lastSavedSceneFileHash = "";
        public bool hasBaselineObjectIds;
        public List<string> baselineObjectIds = new List<string>();
        public long lastAckedSequence = -1;
        public List<UnityWireFrame> acceptedEvents = new List<UnityWireFrame>();
        public List<UnityWireFrame> pendingOutgoing = new List<UnityWireFrame>();
        public List<UnityWireFrame> conflicts = new List<UnityWireFrame>();
    }

    /// <summary>CurrentUser-DPAPI storage outside the Unity project. Accepted frames are persisted before any ack.</summary>
    public sealed class UnityEncryptedInbox
    {
        private const int MaxEvents = 20000;
        private const int MaxBytes = 128 * 1024 * 1024;
        private static readonly object Gate = new object();
        private readonly string _path;
        private readonly string _sceneGuid;
        private readonly string _userId;
        private UnityInboxState _state;

        public string ClientId { get { lock (Gate) return _state.clientId; } }
        public long LastAckedSequence { get { lock (Gate) return _state.lastAckedSequence; } }
        public string SceneGuid { get { return _sceneGuid; } }
        public string UserId { get { return _userId; } }
        public string SceneManifestHash { get { lock (Gate) return _state.sceneManifestHash; } }
        public bool SceneFileMatchesLastSave(string sceneFileHash) { lock (Gate) return _state.lastSavedSceneFileHash == sceneFileHash; }
        public UnityInboxState Identity { get { lock (Gate) return _state; } }

        public UnityEncryptedInbox(string server, string userId, string sessionId, string sceneGuid, string initialManifestHash, IEnumerable<string> baselineObjectIds, string currentSceneFileHash)
        {
            if (string.IsNullOrEmpty(sceneGuid) || sceneGuid.Length != 32) throw new ArgumentException("A saved Unity Scene asset GUID is required.", nameof(sceneGuid));
            _sceneGuid = sceneGuid.ToLowerInvariant();
            _userId = userId;
            _path = PathFor(server, userId, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            lock (Gate)
            {
                _state = Load();
                if (string.IsNullOrEmpty(_state.clientId)) _state.clientId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(_state.sceneGuid)) _state.sceneGuid = _sceneGuid;
                if (_state.sceneGuid != _sceneGuid) throw new InvalidOperationException("The encrypted inbox belongs to a different Unity Scene asset.");
                if (string.IsNullOrEmpty(_state.sceneManifestHash)) _state.sceneManifestHash = initialManifestHash;
                if (_state.sceneManifestHash.Length != 64) throw new InvalidDataException("The stored Scene baseline fingerprint is invalid.");
                if (string.IsNullOrEmpty(_state.lastSavedSceneFileHash)) _state.lastSavedSceneFileHash = currentSceneFileHash;
                if (_state.lastSavedSceneFileHash.Length != 64) throw new InvalidDataException("The stored saved-Scene fingerprint is invalid.");
                if (!_state.hasBaselineObjectIds)
                {
                    _state.baselineObjectIds = baselineObjectIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (_state.baselineObjectIds.Any(id => !StableIdFactory.IsValid(id))) throw new InvalidDataException("Stable Scene baseline contains an invalid object id.");
                    _state.hasBaselineObjectIds = true;
                }
                Save();
            }
        }

        public static bool ExistsFor(string server, string userId, string sessionId) => File.Exists(PathFor(server, userId, sessionId));

        public static string PathFor(string server, string userId, string sessionId)
        {
            var identity = Encoding.UTF8.GetBytes(server + "|" + userId + "|" + sessionId);
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(identity);
            var file = BitConverter.ToString(digest).Replace("-", string.Empty);
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsUnitySync", "Unity");
            return Path.Combine(root, file + ".dpapi");
        }

        public void RecordSceneSaved(string sceneFileHash)
        {
            if (sceneFileHash == null || sceneFileHash.Length != 64) throw new ArgumentException("Scene file hash must be SHA-256 hex.", nameof(sceneFileHash));
            lock (Gate) { _state.lastSavedSceneFileHash = sceneFileHash; Save(); }
        }

        public string[] ExpectedStableObjectIds()
        {
            lock (Gate)
            {
                var ids = new HashSet<string>(_state.baselineObjectIds, StringComparer.Ordinal);
                foreach (var frame in _state.acceptedEvents.OrderBy(x => x.sequence)) ApplyObjectIdentityChange(ids, frame);
                foreach (var frame in _state.pendingOutgoing) ApplyObjectIdentityChange(ids, frame);
                return ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
        }

        private static string GuardedKey(UnitySceneMutation mutation)
        {
            if (mutation.kind == (int)UnitySceneEventKind.PropertyChanged) return GuardedKey(mutation.objectId, mutation.componentType, mutation.propertyName);
            if (mutation.kind == (int)UnitySceneEventKind.TransformChanged) return GuardedKey(mutation.objectId, "Transform", "localTransform");
            if (mutation.kind == (int)UnitySceneEventKind.GameObjectRenamed) return GuardedKey(mutation.objectId, "GameObject", "name");
            return null;
        }
        private static string GuardedKey(string objectId, string component, string property) => objectId + "|" + component + "|" + property;

        private static void ApplyObjectIdentityChange(HashSet<string> ids, UnityWireFrame frame)
        {
            if (frame.kind != (int)UnityFrameKind.SceneEvent) return;
            var mutation = JsonUtility.FromJson<UnitySceneMutation>(frame.payloadJson);
            if (mutation == null || !StableIdFactory.IsValid(mutation.objectId)) return;
            if (mutation.kind == (int)UnitySceneEventKind.GameObjectCreated) ids.Add(mutation.objectId);
            else if (mutation.kind == (int)UnitySceneEventKind.GameObjectDestroyed) ids.Remove(mutation.objectId);
        }

        public List<UnityWireFrame> AcceptedSnapshot()
        {
            lock (Gate) return new List<UnityWireFrame>(_state.acceptedEvents);
        }

        public List<UnityWireFrame> ConflictsSnapshot()
        {
            lock (Gate) return new List<UnityWireFrame>(_state.conflicts);
        }

        public bool HasUnresolvedConflict(string objectId, string componentType, string propertyName, string rejectedById)
        {
            lock (Gate)
            {
                return _state.conflicts.Exists(frame =>
                {
                    var conflict = JsonUtility.FromJson<UnityConflictPayload>(frame.payloadJson);
                    return conflict != null && conflict.rejectedById == rejectedById && conflict.objectId == objectId && conflict.componentType == componentType && conflict.propertyName == propertyName;
                });
            }
        }

        public List<UnityWireFrame> PendingOutgoing()
        {
            lock (Gate) return new List<UnityWireFrame>(_state.pendingOutgoing);
        }

        public void QueueOutgoing(UnityWireFrame frame)
        {
            lock (Gate)
            {
                if (_state.pendingOutgoing.Exists(x => x.frameId == frame.frameId)) return;
                if (_state.pendingOutgoing.Count >= MaxEvents) throw new IOException("The encrypted pending-event queue is full; no project files were changed by J'sUnitySync.");
                _state.pendingOutgoing.Add(frame);
                Save();
            }
        }

        public long Accept(UnityWireFrame frame)
        {
            lock (Gate)
            {
                if (frame.kind != (int)UnityFrameKind.SceneEvent || frame.sessionId.Length == 0)
                    throw new InvalidDataException("Unexpected collaboration frame.");
                var mutation = JsonUtility.FromJson<UnitySceneMutation>(frame.payloadJson);
                if (mutation == null || mutation.sceneGuid != _sceneGuid)
                    throw new InvalidDataException("Collaboration event belongs to a different Unity Scene.");
                if (frame.sequence <= _state.lastAckedSequence)
                {
                    _state.pendingOutgoing.RemoveAll(x => x.frameId == frame.frameId);
                    Save();
                    return _state.lastAckedSequence;
                }
                var expected = _state.lastAckedSequence < 0 ? 1 : _state.lastAckedSequence + 1;
                if (frame.sequence != expected) throw new InvalidDataException("Server event sequence gap; disconnecting so the server can replay from the durable cursor.");
                if (_state.acceptedEvents.Count >= MaxEvents) throw new IOException("The encrypted event inbox is full; the event will not be acknowledged.");
                _state.acceptedEvents.Add(frame);
                _state.pendingOutgoing.RemoveAll(x => x.frameId == frame.frameId);
                var acceptedMutation = JsonUtility.FromJson<UnitySceneMutation>(frame.payloadJson);
                if (acceptedMutation != null)
                {
                    var acceptedKey = GuardedKey(acceptedMutation);
                    if (acceptedKey != null)
                        _state.conflicts.RemoveAll(conflictFrame =>
                        {
                            var conflict = JsonUtility.FromJson<UnityConflictPayload>(conflictFrame.payloadJson);
                            return conflict != null && GuardedKey(conflict.objectId, conflict.componentType, conflict.propertyName) == acceptedKey;
                        });
                }
                _state.lastAckedSequence = frame.sequence;
                Save();
                return _state.lastAckedSequence;
            }
        }

        public void StoreConflict(UnityWireFrame frame)
        {
            lock (Gate)
            {
                if (_state.conflicts.Exists(x => x.frameId == frame.frameId)) return;
                var conflict = JsonUtility.FromJson<UnityConflictPayload>(frame.payloadJson);
                _state.conflicts.Add(frame);
                if (conflict != null && !string.IsNullOrEmpty(conflict.rejectedEventId))
                    _state.pendingOutgoing.RemoveAll(x => x.frameId == conflict.rejectedEventId);
                if (_state.conflicts.Count > 100) _state.conflicts.RemoveRange(0, _state.conflicts.Count - 100);
                Save();
            }
        }

        public int ConflictCount { get { lock (Gate) return _state.conflicts.Count; } }

        private UnityInboxState Load()
        {
            if (!File.Exists(_path)) return new UnityInboxState();
            var encrypted = File.ReadAllBytes(_path);
            if (encrypted.Length > MaxBytes) throw new InvalidDataException("Protected collaboration inbox exceeds its safe size limit.");
            var plain = UnityDpapi.Unprotect(encrypted);
            try
            {
                var state = JsonUtility.FromJson<UnityInboxState>(Encoding.UTF8.GetString(plain));
                if (state == null || state.acceptedEvents == null || state.pendingOutgoing == null || state.conflicts == null || state.baselineObjectIds == null || state.acceptedEvents.Count > MaxEvents || state.pendingOutgoing.Count > MaxEvents)
                    throw new InvalidDataException("Protected collaboration inbox is invalid; the original file was preserved.");
                return state;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        private void Save()
        {
            var plain = Encoding.UTF8.GetBytes(JsonUtility.ToJson(_state));
            byte[] encrypted;
            try { encrypted = UnityDpapi.Protect(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
            if (encrypted.Length > MaxBytes) throw new IOException("Protected collaboration inbox reached its size limit; the event was not acknowledged.");
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, encrypted);
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            finally { Array.Clear(encrypted, 0, encrypted.Length); if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal static class UnityDpapi
    {
        private const int UiForbidden = 1;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JsUnitySync.Unity.SceneInbox.v1");

        [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(byte[] plain) => Transform(plain, true);
        public static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, false);

        private static byte[] Transform(byte[] input, bool protect)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                throw new PlatformNotSupportedException("The Unity collaboration inbox currently requires the Windows Editor and CurrentUser DPAPI.");
            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            var inputBlob = new DataBlob { Length = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Length = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            DataBlob output;
            IntPtr description = IntPtr.Zero;
            try
            {
                var succeeded = protect
                    ? CryptProtectData(ref inputBlob, "J'sUnitySync Unity session inbox", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref inputBlob, out description, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!succeeded) throw new CryptographicException(Marshal.GetLastWin32Error());
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                LocalFree(output.Data);
                if (description != IntPtr.Zero) LocalFree(description);
                return result;
            }
            finally
            {
                if (inputHandle.IsAllocated) inputHandle.Free();
                if (entropyHandle.IsAllocated) entropyHandle.Free();
            }
        }
    }

    public enum UnityFrameKind { Hello = 0, Welcome = 1, SceneEvent = 2, EventAck = 3, Heartbeat = 4, Presence = 6, ResumeRequest = 8, Error = 9, Conflict = 10 }
}
