using System;
using System.IO;
using System.Linq;
using System.Text;
using JsUnitySync.UnityPackage.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JsUnitySync.UnityPackage.Tests
{
    public sealed class UnityEditorIntegrationTests
    {
        [Test]
        public void SavedSceneIdentityManifestIsStableAndIdsAreSerialized()
        {
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/JsUnitySync-Editor-Test.unity");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var root = new GameObject("Root");
                var child = new GameObject("Child");
                child.transform.SetParent(root.transform);
                SceneManager.MoveGameObjectToScene(root, scene);
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
                var changed = UnitySceneIdentity.EnsureStableIds(scene);
                Assert.AreEqual(2, changed);
                Assert.IsTrue(scene.isDirty);
                Assert.IsTrue(EditorSceneManager.SaveScene(scene));
                var first = UnitySceneIdentity.ManifestHash(scene);
                Assert.AreEqual(64, first.Length);
                Assert.AreEqual(first, UnitySceneIdentity.ManifestHash(scene));
                Assert.AreEqual(2, UnitySceneIdentity.GetSceneObjects(scene).Count(x => x.GetComponent<JsUnitySyncObjectId>() != null));
                Assert.IsFalse(scene.isDirty);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void EncryptedInboxPersistsDurableEventCursorWithoutPlaintextPayload()
        {
            var server = "https://sync.example.com/";
            var user = StableIdFactory.NewId();
            var session = StableIdFactory.NewId();
            var path = UnityEncryptedInbox.PathFor(server, user, session);
            var sceneGuid = Guid.NewGuid().ToString("N");
            const string secret = "serialized inspector value from a local scene";
            try
            {
                var manifest = new string('a', 64);
                var inbox = new UnityEncryptedInbox(server, user, session, sceneGuid, manifest, new string[0], new string('b', 64));
                var frame = new UnityWireFrame
                {
                    kind = (int)UnityFrameKind.SceneEvent, frameId = StableIdFactory.NewId(), sessionId = session,
                    sequence = 1, payloadJson = JsonUtility.ToJson(new UnitySceneMutation { kind = (int)UnitySceneEventKind.PropertyChanged, sceneGuid = sceneGuid, serializedValue = secret })
                };
                Assert.AreEqual(1, inbox.Accept(frame));
                var bytes = File.ReadAllBytes(path);
                Assert.IsFalse(Encoding.UTF8.GetString(bytes).Contains(secret));
                var restored = new UnityEncryptedInbox(server, user, session, sceneGuid, manifest, new string[0], new string('b', 64));
                Assert.AreEqual(1, restored.LastAckedSequence);
                Assert.IsTrue(restored.SceneFileMatchesLastSave(new string('b', 64)));
                restored.RecordSceneSaved(new string('c', 64));
                Assert.IsTrue(new UnityEncryptedInbox(server, user, session, sceneGuid, manifest, new string[0], new string('b', 64)).SceneFileMatchesLastSave(new string('c', 64)));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
