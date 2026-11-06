using System.Collections.Generic;
using JsUnitySync.UnityPackage.Editor;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace JsUnitySync.UnityPackage.Tests
{
    public sealed class StableIdFactoryTests
    {
        [Test]
        public void NewId_IsServerCompatibleAndUnique()
        {
            var ids = new HashSet<string>();
            for (var i = 0; i < 5000; i++)
            {
                var id = StableIdFactory.NewId();
                Assert.IsTrue(StableIdFactory.IsValid(id));
                Assert.IsTrue(ids.Add(id));
            }
        }

        [Test]
        public void EventKindNumbers_MatchDomainContract()
        {
            // Numeric event values mirror the public server contract; Wire protocol v2 uses auth headers.
            Assert.AreEqual(0, (int)UnitySceneEventKind.GameObjectCreated);
            Assert.AreEqual(3, (int)UnitySceneEventKind.TransformChanged);
            Assert.AreEqual(6, (int)UnitySceneEventKind.PropertyChanged);
            Assert.AreEqual(8, (int)UnitySceneEventKind.SceneSaved);
            Assert.AreEqual("2.0", new UnityHelloPayload().protocolVersion);
        }

        [Test]
        public void ExplicitIdentityEnablement_OnlyAddsSerializedIdsAndMarksSceneDirty()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var root = new GameObject("Root");
                var child = new GameObject("Child");
                child.transform.SetParent(root.transform);
                UnityEditor.SceneManagement.EditorSceneManager.MoveGameObjectToScene(root, scene);
                var changed = UnitySceneIdentity.EnsureStableIds(scene);
                Assert.AreEqual(2, changed);
                var rootId = root.GetComponent<JsUnitySyncObjectId>();
                var childId = child.GetComponent<JsUnitySyncObjectId>();
                Assert.IsTrue(StableIdFactory.IsValid(rootId.StableId));
                Assert.IsTrue(StableIdFactory.IsValid(childId.StableId));
                Assert.AreNotEqual(rootId.StableId, childId.StableId);
                Assert.IsTrue(scene.isDirty, "Adding identity components must be visible as unsaved user project changes.");
                Assert.IsNull(UnitySceneIdentity.Find(scene, StableIdFactory.NewId()));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        [Test]
        public void DuplicateSerializedIds_AreRepairedRatherThanMerged()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var first = new GameObject("First");
                var second = new GameObject("Second");
                EditorSceneManager.MoveGameObjectToScene(first, scene);
                EditorSceneManager.MoveGameObjectToScene(second, scene);
                var id = StableIdFactory.NewId();
                first.AddComponent<JsUnitySyncObjectId>().SetId(id);
                second.AddComponent<JsUnitySyncObjectId>().SetId(id);
                var repaired = UnitySceneIdentity.EnsureStableIds(scene);
                Assert.AreEqual(1, repaired);
                Assert.AreNotEqual(first.GetComponent<JsUnitySyncObjectId>().StableId, second.GetComponent<JsUnitySyncObjectId>().StableId);
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
