using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JsUnitySync.UnityPackage.Editor
{
    public sealed class UnitySessionWindow : EditorWindow
    {
        private const string EndpointKey = "JsUnitySync.Unity.Server";
        private const string EmailKey = "JsUnitySync.Unity.Email";
        private string _server = "http://localhost:5080/";
        private string _email = "";
        private string _password = "";
        private string _sessionId = "";
        private string _status = "Sign in to a collaboration server; passwords are never saved by the Editor extension.";
        private UnityApiClient _api;
        private UnityEncryptedInbox _inbox;
        private UnitySceneTransport _transport;
        private UnitySceneSyncCoordinator _coordinator;
        private Task _operation;
        private bool _connecting;

        [MenuItem("Window/J'sUnitySync/Collaboration")]
        public static void Open() => GetWindow<UnitySessionWindow>("J'sUnitySync");

        private void OnEnable()
        {
            _server = EditorPrefs.GetString(EndpointKey, _server);
            _email = EditorPrefs.GetString(EmailKey, "");
        }

        private void OnDisable() => Disconnect();

        private void OnGUI()
        {
            EditorGUILayout.LabelField("J'sUnitySync Collaboration", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Sessions are centrally sequenced. Only the active, explicitly selected Unity Scene is tracked. J'sUnitySync never auto-saves your project; remote edits mark the Scene dirty for you to review and save.", MessageType.Info);
            EditorGUILayout.Space();
            _server = EditorGUILayout.TextField("Server origin", _server);
            _email = EditorGUILayout.TextField("Account email", _email);
            using (new EditorGUI.DisabledScope(_operation != null && !_operation.IsCompleted))
            {
                if (_api == null)
                {
                    _password = EditorGUILayout.PasswordField("Password", _password);
                    if (GUILayout.Button("Sign in (password is not saved)")) SignIn();
                }
                else
                {
                    EditorGUILayout.LabelField("Signed in", _api.DisplayName + " · " + _api.UserId);
                    _sessionId = EditorGUILayout.TextField("Session id", _sessionId);
                    DrawSceneState();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (_transport == null && GUILayout.Button("Connect to session")) Connect();
                        if (_transport != null && GUILayout.Button("Disconnect")) Disconnect();
                        if (GUILayout.Button("Sign out")) { Disconnect(); _api.Dispose(); _api = null; _status = "Signed out. No password or token was saved."; }
                    }
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_status, MessageType.None);
            if (_inbox != null)
                EditorGUILayout.LabelField("Encrypted local inbox", _inbox.LastAckedSequence + " accepted event(s); " + _inbox.ConflictCount + " unresolved conflict(s)");
            EditorGUILayout.HelpBox("Before the first Editor peer joins, add stable IDs, save the Scene and share that baseline with collaborators. Scene identity and the stable-ID manifest are bound to this session; mismatched Scene baselines are refused. Supported Inspector values are primitive, enum, vector, color, rectangle, bounds and in-Scene object references.", MessageType.Warning);
        }

        private void DrawSceneState()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                EditorGUILayout.HelpBox("Save the active Scene as a project asset before starting collaboration.", MessageType.Warning);
                return;
            }
            var objects = UnitySceneIdentity.GetSceneObjects(scene);
            var missing = objects.Count(go => go.GetComponent<JsUnitySyncObjectId>() == null || !StableIdFactory.IsValid(go.GetComponent<JsUnitySyncObjectId>().StableId));
            var duplicateIds = objects.Select(go => go.GetComponent<JsUnitySyncObjectId>()).Where(id => id != null).GroupBy(id => id.StableId).Any(group => group.Count() > 1);
            EditorGUILayout.LabelField("Active Scene", scene.name + " · " + UnitySceneIdentity.GetSceneGuid(scene));
            EditorGUILayout.LabelField("Stable object ids", (objects.Count - missing) + " / " + objects.Count + " tracked");
            if (missing > 0 || duplicateIds)
            {
                EditorGUILayout.HelpBox("Stable IDs are serialized components. Adding or repairing IDs changes the in-memory Scene and marks it dirty; it will not be saved automatically. Save and share the same Scene baseline before connecting collaborators.", MessageType.Warning);
                if (GUILayout.Button("Enable / repair stable IDs (Undo available)")) EnableStableIds(scene, objects, missing, duplicateIds);
            }
            else if (scene.isDirty)
                EditorGUILayout.HelpBox("Save and share this tracked Scene baseline before joining. The session rejects a different stable-ID manifest.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Tracked Scene is saved. Connect only if collaborators have the same Scene baseline.", MessageType.Info);
        }

        private void EnableStableIds(Scene scene, System.Collections.Generic.IReadOnlyList<GameObject> objects, int missing, bool duplicates)
        {
            if (!EditorUtility.DisplayDialog("Enable J'sUnitySync stable ids?",
                    "This adds or repairs serialized identity components on " + objects.Count + " GameObject(s). It changes the in-memory Scene, creates Undo entries, and marks the Scene dirty. Nothing is saved automatically. Save/commit this Scene and make sure collaborators use the same baseline before starting a session.",
                    "Add ids", "Cancel")) return;
            var changed = UnitySceneIdentity.EnsureStableIds(scene);
            _status = "Added/repaired " + changed + " object id(s). Review the Scene, then save and share this baseline before connecting.";
        }

        private async void SignIn()
        {
            var password = _password;
            _password = string.Empty;
            Repaint();
            _operation = SignInAsync(password);
            await _operation;
            _operation = null;
            Repaint();
        }

        private async Task SignInAsync(string password)
        {
            UnityApiClient api = null;
            try
            {
                api = new UnityApiClient(_server);
                await api.SignInAsync(_email, password, default);
                _api = api;
                EditorPrefs.SetString(EndpointKey, _server);
                EditorPrefs.SetString(EmailKey, _email);
                _status = "Signed in to " + _api.ServerAddress + ". The bearer token is memory-only.";
            }
            catch (Exception error)
            {
                api?.Dispose();
                _status = "Sign-in failed: " + error.Message;
            }
            finally { password = string.Empty; }
        }

        private async void Connect()
        {
            _connecting = true;
            _status = "Validating session and Scene baseline…";
            _operation = ConnectAsync();
            await _operation;
            _operation = null;
            _connecting = false;
            Repaint();
        }

        private async Task ConnectAsync()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path) || scene.isDirty)
            {
                _status = "Save the active Scene after enabling IDs and ensure teammates share the same baseline before connecting.";
                return;
            }
            var sceneGuid = UnitySceneIdentity.GetSceneGuid(scene);
            var sceneFileHash = UnitySceneIdentity.SceneFileHash(scene);
            var manifest = UnitySceneIdentity.ManifestHash(scene);
            var stableIds = UnitySceneIdentity.GetStableObjectIds(scene);
            if (stableIds.Length != UnitySceneIdentity.GetSceneObjects(scene).Count || stableIds.Distinct(StringComparer.Ordinal).Count() != stableIds.Length)
            {
                _status = "The active Scene has missing or duplicate stable ids. Repair them, save, and share the same baseline before connecting.";
                return;
            }
            if (string.IsNullOrEmpty(sceneGuid) || manifest.Length != 64 || UnitySceneIdentity.GetSceneObjects(scene).Any(go => go.GetComponent<JsUnitySyncObjectId>() == null))
            {
                _status = "This saved Scene has missing stable IDs. Use Enable / repair stable IDs, save, then reconnect.";
                return;
            }
            try
            {
                var session = await _api.GetSessionAsync(_sessionId.Trim(), default);
                if (!string.IsNullOrEmpty(session.sceneGuid) && session.sceneGuid != sceneGuid)
                    throw new InvalidOperationException("This session is bound to a different Unity Scene asset. Select the shared Scene or create a new session.");
                if (!string.IsNullOrEmpty(session.sceneManifestHash) &&
                    !UnityEncryptedInbox.ExistsFor(_api.ServerAddress, _api.UserId, session.id) && session.sceneManifestHash != manifest)
                    throw new InvalidOperationException("This new Editor installation does not have the session's initial shared Scene baseline. Restore the original baseline or create a new session.");
                _inbox = new UnityEncryptedInbox(_api.ServerAddress, _api.UserId, session.id, sceneGuid, manifest, stableIds, sceneFileHash);
                if (!_inbox.SceneFileMatchesLastSave(sceneFileHash))
                    throw new InvalidOperationException("The saved Scene changed outside this Editor collaboration inbox. Preserve local work and reconcile the shared Scene before reconnecting.");
                if (!string.IsNullOrEmpty(session.sceneManifestHash) && session.sceneManifestHash != _inbox.SceneManifestHash)
                    throw new InvalidOperationException("This session is bound to a different stable-object baseline. Pull the matching shared Scene baseline or create a new session.");
                _transport = new UnitySceneTransport(_api, _inbox, session.id, sceneGuid, _inbox.SceneManifestHash);
                _coordinator = new UnitySceneSyncCoordinator(_transport, _inbox, scene, sceneGuid);
                _coordinator.Status += SetStatusFromBackground;
                _coordinator.Begin();
                _transport.Status += SetStatusFromBackground;
                _transport.Start();
                _status = "Connecting to the authenticated session event log…";
            }
            catch (Exception error) { Disconnect(); _status = "Could not connect: " + error.Message; }
        }

        private void SetStatusFromBackground(string message)
        {
            EditorApplication.delayCall += () => { if (this != null) { _status = message; Repaint(); } };
        }

        private void Disconnect()
        {
            if (_coordinator != null) _coordinator.Dispose();
            if (_transport != null) _transport.Dispose();
            _coordinator = null;
            _transport = null;
            if (!_connecting && _api != null) _status = "Disconnected. Accepted events remain in the encrypted inbox and replay from its cursor.";
        }
    }
}
