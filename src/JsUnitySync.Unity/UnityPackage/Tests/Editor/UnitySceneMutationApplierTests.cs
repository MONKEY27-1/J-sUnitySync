using System;
using JsUnitySync.UnityPackage.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JsUnitySync.UnityPackage.Tests
{
    public sealed class UnityTestInspectable : MonoBehaviour
    {
        public float intensity;
        public Vector3 direction;
    }

    public sealed class UnitySceneMutationApplierTests
    {
        [Test]
        public void RemoteCreateRenameTransformAndDestroy_AreAppliedByStableId()
        {
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/JsUnitySync-Apply-Test.unity");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var parent = new GameObject("Parent");
                SceneManager.MoveGameObjectToScene(parent, scene);
                var parentId = parent.AddComponent<JsUnitySyncObjectId>(); parentId.SetId(StableIdFactory.NewId());
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
                var sceneGuid = AssetDatabase.AssetPathToGUID(path);
                var id = StableIdFactory.NewId();
                var data = new UnitySceneObjectData { name = "Remote", parentObjectId = parentId.StableId, positionX = 2, positionY = 3, positionZ = 4 };
                var create = new UnitySceneMutation { kind = (int)UnitySceneEventKind.GameObjectCreated, objectId = id, sceneGuid = sceneGuid, dataJson = JsonUtility.ToJson(data) };
                var created = UnitySceneMutationApplier.Apply(scene, create);
                Assert.IsNotNull(created);
                Assert.AreEqual(id, created.StableId);
                Assert.AreEqual(parent.transform, created.transform.parent);
                Assert.AreEqual(new Vector3(2, 3, 4), created.transform.localPosition);

                var rename = new UnitySceneMutation { kind = (int)UnitySceneEventKind.GameObjectRenamed, objectId = id, sceneGuid = sceneGuid,
                    serializedValue = "Renamed", dataJson = JsonUtility.ToJson(new UnityRenameData { newName = "Renamed" }) };
                UnitySceneMutationApplier.Apply(scene, rename);
                Assert.AreEqual("Renamed", created.gameObject.name);

                var transform = new UnitySceneMutation { kind = (int)UnitySceneEventKind.TransformChanged, objectId = id, sceneGuid = sceneGuid,
                    dataJson = JsonUtility.ToJson(new UnitySceneObjectData { name = "Renamed", parentObjectId = "", positionX = 10, positionY = 0, positionZ = 0, rotationW = 1, scaleX = 1, scaleY = 1, scaleZ = 1 }) };
                UnitySceneMutationApplier.Apply(scene, transform);
                Assert.IsNull(created.transform.parent);
                Assert.AreEqual(new Vector3(10, 0, 0), created.transform.localPosition);

                UnitySceneMutationApplier.Apply(scene, new UnitySceneMutation { kind = (int)UnitySceneEventKind.GameObjectDestroyed, objectId = id, sceneGuid = sceneGuid, dataJson = "{}" });
                Assert.IsNull(UnitySceneIdentity.Find(scene, id));
            }
            finally { EditorSceneManager.CloseScene(scene, true); AssetDatabase.DeleteAsset(path); AssetDatabase.Refresh(); }
        }

        [Test]
        public void InspectorPropertyCodec_ChangesOnlySupportedValues()
        {
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/JsUnitySync-Inspector-Test.unity");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var gameObject = new GameObject("Light");
                SceneManager.MoveGameObjectToScene(gameObject, scene);
                var identity = gameObject.AddComponent<JsUnitySyncObjectId>(); identity.SetId(StableIdFactory.NewId());
                var component = gameObject.AddComponent<UnityTestInspectable>();
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
                var serialized = new SerializedObject(component);
                var property = serialized.FindProperty(nameof(UnityTestInspectable.intensity));
                Assert.IsTrue(UnityPropertyCodec.TryEncode(property, out var encoded));
                var expected = JsonUtility.FromJson<UnitySerializedPropertyValue>(encoded);
                expected.floatValue = 7.25f;
                var mutation = new UnitySceneMutation { kind = (int)UnitySceneEventKind.PropertyChanged, objectId = identity.StableId,
                    sceneGuid = AssetDatabase.AssetPathToGUID(path), componentType = component.GetType().FullName,
                    propertyName = nameof(UnityTestInspectable.intensity), serializedValue = JsonUtility.ToJson(expected), dataJson = "{}" };
                UnitySceneMutationApplier.Apply(scene, mutation);
                Assert.AreEqual(7.25f, component.intensity);
            }
            finally { EditorSceneManager.CloseScene(scene, true); AssetDatabase.DeleteAsset(path); AssetDatabase.Refresh(); }
        }

        [Test]
        public void ComponentStructureMutation_UsesAllowListAndSerializedProperties()
        {
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/JsUnitySync-Component-Test.unity");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var source = new GameObject("Source"); var target = new GameObject("Target");
                SceneManager.MoveGameObjectToScene(source, scene); SceneManager.MoveGameObjectToScene(target, scene);
                var targetId = target.AddComponent<JsUnitySyncObjectId>(); targetId.SetId(StableIdFactory.NewId());
                var sourceLight = source.AddComponent<Light>(); sourceLight.intensity = 3.25f;
                Assert.IsTrue(UnityComponentRegistry.IsAllowed(typeof(Light).FullName));
                Assert.IsTrue(UnityComponentStateCodec.TryCapture(sourceLight, out var serialized));
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
                var mutation = new UnitySceneMutation { kind = (int)UnitySceneEventKind.ComponentAdded, objectId = targetId.StableId,
                    sceneGuid = AssetDatabase.AssetPathToGUID(path), componentType = typeof(Light).FullName, serializedValue = serialized, dataJson = "{}" };
                UnitySceneMutationApplier.Apply(scene, mutation);
                Assert.AreEqual(3.25f, target.GetComponent<Light>().intensity);
                UnitySceneMutationApplier.Apply(scene, new UnitySceneMutation { kind = (int)UnitySceneEventKind.ComponentRemoved,
                    objectId = targetId.StableId, sceneGuid = AssetDatabase.AssetPathToGUID(path), componentType = typeof(Light).FullName, dataJson = "{}" });
                Assert.IsNull(target.GetComponent<Light>());
                Assert.IsFalse(UnityComponentRegistry.IsAllowed(typeof(UnityTestInspectable).FullName));
            }
            finally { EditorSceneManager.CloseScene(scene, true); AssetDatabase.DeleteAsset(path); AssetDatabase.Refresh(); }
        }
    }
}
