using System;
using System.Collections;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JsUnitySync.UnityPackage.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace JsUnitySync.UnityPackage.Tests
{
    public sealed class UnityRealtimeIntegrationTests
    {
        [UnityTest]
        public IEnumerator UnityEditor_ClientAndSceneMutationsInteroperateWithLiveServer()
        {
            var server = Environment.GetEnvironmentVariable("JSUNITYSYNC_TEST_SERVER");
            if (string.IsNullOrEmpty(server)) Assert.Ignore("Set JSUNITYSYNC_TEST_SERVER to a live local JsUnitySync API to run the Editor-to-server integration test.");

            server = server.TrimEnd('/') + "/";
            var setup = CreateSessionAsync(server);
            yield return WaitFor(() => setup.IsCompleted, 20);
            if (setup.IsFaulted) throw setup.Exception;
            var account = setup.Result;
            var scenePath = AssetDatabase.GenerateUniqueAssetPath("Assets/JsUnitySync-Network-Test.unity");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            UnityApiClient api = null;
            UnityEncryptedInbox inbox = null;
            UnitySceneTransport transport = null;
            UnitySceneSyncCoordinator coordinator = null;
            ClientWebSocket publisher = null;
            var inboxPath = string.Empty;
            try
            {
                var root = new GameObject("Shared baseline");
                SceneManager.MoveGameObjectToScene(root, scene);
                var rootTag = root.AddComponent<JsUnitySyncObjectId>(); rootTag.SetId(StableIdFactory.NewId());
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, scenePath));
                var sceneGuid = UnitySceneIdentity.GetSceneGuid(scene);
                var manifest = UnitySceneIdentity.ManifestHash(scene);

                api = new UnityApiClient(server);
                var signIn = api.SignInAsync(account.Email, account.Password, CancellationToken.None);
                yield return WaitFor(() => signIn.IsCompleted, 20);
                if (signIn.IsFaulted) throw signIn.Exception;
                inbox = new UnityEncryptedInbox(server.EndsWith("/") ? server : server + "/", api.UserId,
                    account.SessionId, sceneGuid, manifest, UnitySceneIdentity.GetStableObjectIds(scene), UnitySceneIdentity.SceneFileHash(scene));
                inboxPath = UnityEncryptedInbox.PathFor(server.EndsWith("/") ? server : server + "/", api.UserId, account.SessionId);
                var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var statuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
                transport = new UnitySceneTransport(api, inbox, account.SessionId, sceneGuid, inbox.SceneManifestHash);
                transport.Status += message => { statuses.Enqueue(message); if (message.StartsWith("Connected to the central event log", StringComparison.Ordinal)) connected.TrySetResult(true); };
                var editorStatuses = new System.Collections.Concurrent.ConcurrentQueue<string>();
                coordinator = new UnitySceneSyncCoordinator(transport, inbox, scene, sceneGuid);
                coordinator.Status += message => editorStatuses.Enqueue(message);
                coordinator.Begin();
                transport.Start();
                var connectionTimeout = EditorApplication.timeSinceStartup + 15;
                while (!connected.Task.IsCompleted && EditorApplication.timeSinceStartup < connectionTimeout) yield return null;
                Assert.IsTrue(connected.Task.IsCompleted, "Unity Editor transport did not connect (running=" + transport.IsRunning + "): " + string.Join(" | ", statuses.ToArray()));

                publisher = new ClientWebSocket();
                publisher.Options.SetRequestHeader("Authorization", "Bearer " + account.CollaboratorToken);
                var ws = new UriBuilder(new Uri(server)) { Scheme = new Uri(server).Scheme == "https" ? "wss" : "ws", Path = "realtime/v1/sessions/" + account.SessionId }.Uri;
                var connecting = publisher.ConnectAsync(ws, CancellationToken.None);
                yield return WaitFor(() => connecting.IsCompleted, 15);
                if (connecting.IsFaulted) throw connecting.Exception;
                var peerId = Guid.NewGuid().ToString("N");
                var peerHello = new UnityWireFrame { kind = (int)UnityFrameKind.Hello, sessionId = account.SessionId,
                    payloadJson = JsonUtility.ToJson(new UnityHelloPayload { userId = account.CollaboratorUserId, clientId = peerId, sceneGuid = sceneGuid,
                        sceneManifestHash = manifest, lastAckedSequence = -1 }) };
                var peerHelloTask = SendAndReceiveAsync(publisher, peerHello);
                yield return WaitFor(() => peerHelloTask.IsCompleted, 10);
                if (peerHelloTask.IsFaulted) throw peerHelloTask.Exception;
                Assert.AreEqual((int)UnityFrameKind.Welcome, peerHelloTask.Result.kind, "Peer handshake failed: " + peerHelloTask.Result.payloadJson);

                var remoteId = StableIdFactory.NewId();
                var createData = JsonUtility.ToJson(new UnitySceneObjectData { name = "Remote Cube", positionX = 3, positionY = 2, positionZ = 1, rotationW = 1, scaleX = 1, scaleY = 1, scaleZ = 1 });
                var create = SceneFrame(account.SessionId, StableIdFactory.NewId(), new UnitySceneMutation
                { kind = (int)UnitySceneEventKind.GameObjectCreated, objectId = remoteId, sceneGuid = sceneGuid, dataJson = createData });
                var createTask = SendAndReceiveAsync(publisher, create);
                yield return WaitFor(() => createTask.IsCompleted, 10);
                if (createTask.IsFaulted) throw createTask.Exception;
                yield return WaitFor(() => UnitySceneIdentity.Find(scene, remoteId) != null, 10);
                var remoteObject = UnitySceneIdentity.Find(scene, remoteId);
                Assert.IsNotNull(remoteObject, "Accepted server event was not applied on the Unity Editor main thread.");
                Assert.AreEqual(new Vector3(3, 2, 1), remoteObject.transform.localPosition);

                var template = new GameObject("Light template"); template.hideFlags = HideFlags.HideAndDontSave;
                var templateLight = template.AddComponent<Light>(); templateLight.intensity = 4.5f;
                var componentJson = "";
                Assert.IsTrue(UnityComponentStateCodec.TryCapture(templateLight, out componentJson));
                UnityEngine.Object.DestroyImmediate(template);
                var addComponent = SceneFrame(account.SessionId, StableIdFactory.NewId(), new UnitySceneMutation
                { kind = (int)UnitySceneEventKind.ComponentAdded, objectId = remoteId, sceneGuid = sceneGuid,
                    componentType = typeof(Light).FullName, serializedValue = componentJson, dataJson = "{}" });
                var addComponentTask = SendAndReceiveAsync(publisher, addComponent);
                yield return WaitFor(() => addComponentTask.IsCompleted, 10);
                if (addComponentTask.IsFaulted) throw addComponentTask.Exception;
                var componentTimeout = EditorApplication.timeSinceStartup + 10;
                while (remoteObject.GetComponent<Light>() == null && EditorApplication.timeSinceStartup < componentTimeout) yield return null;
                Assert.IsNotNull(remoteObject.GetComponent<Light>(), string.Join(" | ", editorStatuses.ToArray()));
                Assert.AreEqual(4.5f, remoteObject.GetComponent<Light>().intensity, 0.01f);

                var serialized = new SerializedObject(remoteObject.GetComponent<Light>());
                var property = serialized.FindProperty("m_Intensity");
                Assert.IsNotNull(property);
                Assert.IsTrue(UnityPropertyCodec.TryEncode(property, out var propertyValue));
                var encoded = JsonUtility.FromJson<UnitySerializedPropertyValue>(propertyValue); encoded.floatValue = 7.25f;
                Assert.AreEqual(7.25f, encoded.floatValue);
                var propertyEvent = SceneFrame(account.SessionId, StableIdFactory.NewId(), new UnitySceneMutation
                { kind = (int)UnitySceneEventKind.PropertyChanged, objectId = remoteId, sceneGuid = sceneGuid,
                    componentType = typeof(Light).FullName, propertyName = "m_Intensity", baseRevision = 0,
                    serializedValue = JsonUtility.ToJson(encoded), dataJson = "{}" });
                var propertyTask = SendAndReceiveAsync(publisher, propertyEvent);
                yield return WaitFor(() => propertyTask.IsCompleted, 10);
                if (propertyTask.IsFaulted) throw propertyTask.Exception;
                Assert.AreEqual((int)UnityFrameKind.SceneEvent, propertyTask.Result.kind, "Server did not accept the Inspector write: " + propertyTask.Result.payloadJson);
                var echoedMutation = JsonUtility.FromJson<UnitySceneMutation>(propertyTask.Result.payloadJson);
                var echoedValue = JsonUtility.FromJson<UnitySerializedPropertyValue>(echoedMutation.serializedValue);
                Assert.AreEqual(7.25f, echoedValue.floatValue, 0.01f, "Server echoed a different serialized property value.");
                var propertyTimeout = EditorApplication.timeSinceStartup + 10;
                while (Math.Abs(remoteObject.GetComponent<Light>().intensity - 7.25f) >= 0.01f && EditorApplication.timeSinceStartup < propertyTimeout) yield return null;
                var storedPropertyFrame = inbox.AcceptedSnapshot().Find(x => x.sequence == 3);
                var storedMutation = storedPropertyFrame == null ? null : JsonUtility.FromJson<UnitySceneMutation>(storedPropertyFrame.payloadJson);
                var storedValue = storedMutation == null ? null : JsonUtility.FromJson<UnitySerializedPropertyValue>(storedMutation.serializedValue);
                Assert.AreEqual(7.25f, storedValue == null ? -1 : storedValue.floatValue, 0.01f, "Property payload was not preserved in the inbox.");
                Assert.AreEqual(7.25f, remoteObject.GetComponent<Light>().intensity, 0.01f, "Inbox sequence=" + inbox.LastAckedSequence + "; statuses=" + string.Join(" | ", editorStatuses.ToArray()));
                Assert.GreaterOrEqual(inbox.LastAckedSequence, 3);

                // Exercise the actual Editor hierarchy observer: create a local GameObject and
                // verify its serialized stable id and creation event are sent to the server.
                var localCreateEchoTask = ReceivePeerAsync(publisher);
                var localObject = new GameObject("Local Editor Object");
                Undo.RegisterCreatedObjectUndo(localObject, "Create realtime test object");
                SceneManager.MoveGameObjectToScene(localObject, scene);
                yield return WaitFor(() => localCreateEchoTask.IsCompleted, 10);
                if (localCreateEchoTask.IsFaulted) throw localCreateEchoTask.Exception;
                var localCreateEcho = localCreateEchoTask.Result;
                Assert.AreEqual((int)UnityFrameKind.SceneEvent, localCreateEcho.kind);
                var localCreateMutation = JsonUtility.FromJson<UnitySceneMutation>(localCreateEcho.payloadJson);
                Assert.AreEqual((int)UnitySceneEventKind.GameObjectCreated, localCreateMutation.kind);
                Assert.AreEqual(localObject.GetComponent<JsUnitySyncObjectId>().StableId, localCreateMutation.objectId);

                // Component structure uses the package's explicit built-in allow-list.
                var componentEchoTask = ReceivePeerAsync(publisher);
                Undo.AddComponent<Light>(localObject);
                var componentDeadline = EditorApplication.timeSinceStartup + 10;
                while (!componentEchoTask.IsCompleted && EditorApplication.timeSinceStartup < componentDeadline) yield return null;
                Assert.IsTrue(componentEchoTask.IsCompleted, "Local component event was not sent. Components=" + string.Join(",", localObject.GetComponents<Component>().Where(x => x != null).Select(x => x.GetType().FullName)) + "; statuses=" + string.Join(" | ", editorStatuses.ToArray()));
                if (componentEchoTask.IsFaulted) throw componentEchoTask.Exception;
                var componentMutation = JsonUtility.FromJson<UnitySceneMutation>(componentEchoTask.Result.payloadJson);
                Assert.AreEqual((int)UnitySceneEventKind.ComponentAdded, componentMutation.kind);
                Assert.AreEqual(typeof(Light).FullName, componentMutation.componentType);

                // Inspector modification events are emitted through Undo.postprocessModifications.
                var inspectorEchoTask = ReceivePeerAsync(publisher);
                var localLight = localObject.GetComponent<Light>();
                Undo.RecordObject(localLight, "Edit realtime inspector value");
                var localSerialized = new SerializedObject(localLight);
                var localIntensity = localSerialized.FindProperty("m_Intensity");
                localIntensity.floatValue = 8.5f;
                localSerialized.ApplyModifiedProperties();
                Undo.FlushUndoRecordObjects();
                yield return WaitFor(() => inspectorEchoTask.IsCompleted, 10);
                if (inspectorEchoTask.IsFaulted) throw inspectorEchoTask.Exception;
                var inspectorMutation = JsonUtility.FromJson<UnitySceneMutation>(inspectorEchoTask.Result.payloadJson);
                Assert.AreEqual((int)UnitySceneEventKind.PropertyChanged, inspectorMutation.kind);
                Assert.AreEqual("m_Intensity", inspectorMutation.propertyName);
                Assert.AreEqual(8.5f, JsonUtility.FromJson<UnitySerializedPropertyValue>(inspectorMutation.serializedValue).floatValue, 0.01f);

                var rejectedValue = JsonUtility.ToJson(new UnitySerializedPropertyValue { type = "Float", floatValue = 2.0f });
                var conflictTask = SendAndReceiveAsync(publisher, SceneFrame(account.SessionId, StableIdFactory.NewId(), new UnitySceneMutation
                {
                    kind = (int)UnitySceneEventKind.PropertyChanged, objectId = localObject.GetComponent<JsUnitySyncObjectId>().StableId,
                    sceneGuid = sceneGuid, componentType = typeof(Light).FullName, propertyName = "m_Intensity", baseRevision = 0,
                    serializedValue = rejectedValue, dataJson = "{}"
                }));
                yield return WaitFor(() => conflictTask.IsCompleted, 10);
                if (conflictTask.IsFaulted) throw conflictTask.Exception;
                Assert.AreEqual((int)UnityFrameKind.Conflict, conflictTask.Result.kind);
                var conflict = JsonUtility.FromJson<UnityConflictPayload>(conflictTask.Result.payloadJson);
                Assert.AreEqual(api.UserId, conflict.acceptedById);
                Assert.AreEqual(account.CollaboratorUserId, conflict.rejectedById);
                yield return WaitFor(() => inbox.ConflictCount == 1, 10);
                Assert.AreEqual(8.5f, localLight.intensity, 0.01f, "A stale collaborator write must not overwrite the accepted local property.");
            }
            finally
            {
                if (coordinator != null) coordinator.Dispose();
                if (transport != null) transport.Dispose();
                if (publisher != null) { try { publisher.Abort(); } catch (Exception) { } publisher.Dispose(); }
                if (api != null) api.Dispose();
                EditorSceneManager.CloseScene(scene, true);
                AssetDatabase.DeleteAsset(scenePath);
                AssetDatabase.Refresh();
                if (!string.IsNullOrEmpty(inboxPath) && System.IO.File.Exists(inboxPath)) System.IO.File.Delete(inboxPath);
            }
        }

        private static IEnumerator WaitFor(Func<bool> completed, float timeoutSeconds)
        {
            var stop = EditorApplication.timeSinceStartup + timeoutSeconds;
            while (!completed() && EditorApplication.timeSinceStartup < stop) yield return null;
            Assert.IsTrue(completed(), "Timed out waiting for Unity Editor collaboration operation.");
        }

        private static UnityWireFrame SceneFrame(string sessionId, string eventId, UnitySceneMutation mutation) => new UnityWireFrame
        { kind = (int)UnityFrameKind.SceneEvent, sessionId = sessionId, frameId = eventId, sequence = -1, utcTimestamp = DateTime.UtcNow.ToString("o"), payloadJson = JsonUtility.ToJson(mutation) };

        private static async Task<UnityWireFrame> ReceivePeerAsync(ClientWebSocket socket)
        {
            var buffer = new byte[4096]; using (var content = new System.IO.MemoryStream())
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Server closed the test peer.");
                    content.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) break;
                }
                return JsonUtility.FromJson<UnityWireFrame>(Encoding.UTF8.GetString(content.ToArray()));
            }
        }

        private static async Task<UnityWireFrame> SendAndReceiveAsync(ClientWebSocket socket, UnityWireFrame frame)
        {
            if (string.IsNullOrEmpty(frame.utcTimestamp)) frame.utcTimestamp = DateTime.UtcNow.ToString("o");
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(frame));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[4096]; using (var content = new System.IO.MemoryStream())
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Server closed the test peer.");
                    content.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) break;
                }
                return JsonUtility.FromJson<UnityWireFrame>(Encoding.UTF8.GetString(content.ToArray()));
            }
        }

        private static async Task<UnityIntegrationAccount> CreateSessionAsync(string server)
        {
            var unique = Guid.NewGuid().ToString("N");
            var email = "unity-" + unique + "@example.invalid";
            var password = "unity-test-password-very-long";
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                var apiRoot = server.TrimEnd('/');
                var register = await Post(http, apiRoot + "/api/v1/auth/register", JsonUtility.ToJson(new UnityIntegrationRegistration
                { email = email, password = password, displayName = "Unity Integration" }));
                if (!register.IsSuccessStatusCode) throw new InvalidOperationException("Test account registration failed (HTTP " + (int)register.StatusCode + ").");
                var auth = JsonUtility.FromJson<UnityLoginResponse>(await register.Content.ReadAsStringAsync());
                var teamResponse = await Post(http, apiRoot + "/api/v1/teams", JsonUtility.ToJson(new UnityIntegrationNamed { name = "Unity " + unique }), auth.token);
                var team = JsonUtility.FromJson<UnityIntegrationId>(await Success(teamResponse));
                var projectResponse = await Post(http, apiRoot + "/api/v1/teams/" + team.id + "/projects", JsonUtility.ToJson(new UnityIntegrationNamed { name = "Unity Game " + unique }), auth.token);
                var project = JsonUtility.FromJson<UnityIntegrationId>(await Success(projectResponse));
                var sessionResponse = await Post(http, apiRoot + "/api/v1/projects/" + project.id + "/sessions", JsonUtility.ToJson(new UnityIntegrationNamed { name = "Unity Editor Test" }), auth.token);
                var session = JsonUtility.FromJson<UnitySessionSummary>(await Success(sessionResponse));

                var collaboratorEmail = "unity-peer-" + unique + "@example.invalid";
                var collaboratorRegister = await Post(http, apiRoot + "/api/v1/auth/register", JsonUtility.ToJson(new UnityIntegrationRegistration
                { email = collaboratorEmail, password = password, displayName = "Unity Collaborator" }));
                var collaboratorAuth = JsonUtility.FromJson<UnityLoginResponse>(await Success(collaboratorRegister));
                var inviteResponse = await Post(http, apiRoot + "/api/v1/teams/" + team.id + "/invitations",
                    JsonUtility.ToJson(new UnityIntegrationInvite { recipientUserId = collaboratorAuth.profile.id }), auth.token);
                var invitation = JsonUtility.FromJson<UnityIntegrationInvitation>(await Success(inviteResponse));
                var acceptResponse = await Post(http, apiRoot + "/api/v1/me/invitations/" + invitation.id + "/accept",
                    JsonUtility.ToJson(new UnityIntegrationInvitationResponse { expectedRevision = invitation.revision }), collaboratorAuth.token);
                if (!acceptResponse.IsSuccessStatusCode) throw new InvalidOperationException("Test collaborator invitation was not accepted (HTTP " + (int)acceptResponse.StatusCode + ").");
                return new UnityIntegrationAccount { Email = email, Password = password, UserId = auth.profile.id, SessionId = session.id,
                    CollaboratorUserId = collaboratorAuth.profile.id, CollaboratorToken = collaboratorAuth.token };
            }
        }

        private static async Task<HttpResponseMessage> Post(HttpClient http, string url, string json, string bearer = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(bearer)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                return await http.SendAsync(request);
            }
        }

        private static async Task<string> Success(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Session setup failed (HTTP " + (int)response.StatusCode + ").");
            return await response.Content.ReadAsStringAsync();
        }

        [Serializable] private sealed class UnityIntegrationRegistration { public string email; public string password; public string displayName; }
        [Serializable] private sealed class UnityIntegrationNamed { public string name; }
        [Serializable] private sealed class UnityIntegrationId { public string id; }
        [Serializable] private sealed class UnityIntegrationInvite { public string recipientUserId; }
        [Serializable] private sealed class UnityIntegrationInvitation { public string id; public long revision; }
        [Serializable] private sealed class UnityIntegrationInvitationResponse { public long expectedRevision; }
        private sealed class UnityIntegrationAccount { public string Email; public string Password; public string UserId; public string SessionId; public string CollaboratorUserId; public string CollaboratorToken; }
    }
}
