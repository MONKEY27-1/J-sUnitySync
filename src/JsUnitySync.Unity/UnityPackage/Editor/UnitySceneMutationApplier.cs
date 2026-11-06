using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JsUnitySync.UnityPackage.Editor
{
    /// <summary>Idempotent main-thread Scene mutation application, isolated for Unity EditMode tests.</summary>
    public static class UnitySceneMutationApplier
    {
        public static JsUnitySyncObjectId Apply(Scene scene, UnitySceneMutation mutation)
        {
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Target Unity Scene is not loaded.");
            if (mutation == null || mutation.sceneGuid != UnitySceneIdentity.GetSceneGuid(scene))
                throw new InvalidOperationException("Scene event does not match the target Unity Scene.");
            var identity = UnitySceneIdentity.Find(scene, mutation.objectId);
            var kind = (UnitySceneEventKind)mutation.kind;
            if (kind == UnitySceneEventKind.GameObjectCreated)
                identity = CreateOrUpdate(scene, mutation, identity);
            else if (kind == UnitySceneEventKind.GameObjectDestroyed)
            {
                if (identity != null)
                {
                    Undo.DestroyObjectImmediate(identity.gameObject);
                    EditorSceneManager.MarkSceneDirty(scene);
                }
                return null;
            }
            if (identity == null) throw new InvalidOperationException("Stable object id is not present in this Scene: " + mutation.objectId);
            var gameObject = identity.gameObject;
            switch (kind)
            {
                case UnitySceneEventKind.GameObjectCreated:
                    ApplyTransform(scene, gameObject, mutation.dataJson); break;
                case UnitySceneEventKind.GameObjectRenamed:
                    var rename = JsonUtility.FromJson<UnityRenameData>(mutation.dataJson);
                    Undo.RecordObject(gameObject, "Apply remote GameObject rename");
                    gameObject.name = rename == null ? mutation.serializedValue : rename.newName;
                    break;
                case UnitySceneEventKind.TransformChanged:
                    ApplyTransform(scene, gameObject, mutation.dataJson); break;
                case UnitySceneEventKind.PropertyChanged:
                    ApplyProperty(scene, identity, mutation); break;
                case UnitySceneEventKind.ComponentAdded:
                    ApplyComponentAdded(gameObject, mutation); break;
                case UnitySceneEventKind.ComponentRemoved:
                    ApplyComponentRemoved(gameObject, mutation); break;
                case UnitySceneEventKind.GameObjectDestroyed:
                case UnitySceneEventKind.SceneLoaded:
                case UnitySceneEventKind.SceneSaved:
                    break;
                default:
                    throw new InvalidOperationException("Unsupported Unity scene mutation.");
            }
            EditorSceneManager.MarkSceneDirty(scene);
            return identity;
        }

        private static JsUnitySyncObjectId CreateOrUpdate(Scene scene, UnitySceneMutation mutation, JsUnitySyncObjectId identity)
        {
            var data = JsonUtility.FromJson<UnitySceneObjectData>(mutation.dataJson);
            if (data == null) throw new InvalidOperationException("GameObject creation data is invalid.");
            if (identity == null)
            {
                var gameObject = new GameObject(data.name);
                Undo.RegisterCreatedObjectUndo(gameObject, "Apply remote GameObject creation");
                EditorSceneManager.MoveGameObjectToScene(gameObject, scene);
                identity = Undo.AddComponent<JsUnitySyncObjectId>(gameObject);
                identity.SetId(mutation.objectId);
            }
            identity.gameObject.name = data.name;
            return identity;
        }

        private static void ApplyComponentAdded(GameObject gameObject, UnitySceneMutation mutation)
        {
            var type = UnityComponentRegistry.Resolve(mutation.componentType);
            if (type == null) throw new NotSupportedException("This component type is not allow-listed for remote creation: " + mutation.componentType);
            var component = gameObject.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType() == type);
            if (component == null)
            {
                component = Undo.AddComponent(gameObject, type);
                Undo.RegisterCreatedObjectUndo(component, "Apply remote component addition");
            }
            UnityComponentStateCodec.Apply(component, mutation.serializedValue, id => UnitySceneIdentity.Find(gameObject.scene, id)?.gameObject);
        }

        private static void ApplyComponentRemoved(GameObject gameObject, UnitySceneMutation mutation)
        {
            var type = UnityComponentRegistry.Resolve(mutation.componentType);
            if (type == null) throw new NotSupportedException("This component type is not allow-listed for remote removal: " + mutation.componentType);
            var component = gameObject.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType() == type);
            if (component != null) Undo.DestroyObjectImmediate(component);
        }

        private static void ApplyTransform(Scene scene, GameObject gameObject, string dataJson)
        {
            var data = JsonUtility.FromJson<UnitySceneObjectData>(dataJson);
            if (data == null) throw new InvalidOperationException("Transform data is invalid.");
            Transform parent = null;
            if (!string.IsNullOrEmpty(data.parentObjectId))
            {
                var parentId = UnitySceneIdentity.Find(scene, data.parentObjectId);
                if (parentId == null) throw new InvalidOperationException("Remote parent stable id is missing.");
                parent = parentId.transform;
            }
            Undo.RecordObject(gameObject.transform, "Apply remote Transform");
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.localPosition = new Vector3(data.positionX, data.positionY, data.positionZ);
            gameObject.transform.localRotation = new Quaternion(data.rotationX, data.rotationY, data.rotationZ, data.rotationW);
            gameObject.transform.localScale = new Vector3(data.scaleX, data.scaleY, data.scaleZ);
        }

        private static void ApplyProperty(Scene scene, JsUnitySyncObjectId identity, UnitySceneMutation mutation)
        {
            var component = identity.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType().FullName == mutation.componentType);
            if (component == null) throw new InvalidOperationException("Target component is not present: " + mutation.componentType);
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(mutation.propertyName);
            if (property == null) throw new InvalidOperationException("Serialized property is not present: " + mutation.propertyName);
            Undo.RecordObject(component, "Apply remote Inspector property");
            if (!UnityPropertyCodec.TryApply(property, mutation.serializedValue, id => UnitySceneIdentity.Find(scene, id)?.gameObject))
                throw new InvalidOperationException("Serialized property type is unsupported or its referenced object is missing.");
            serialized.ApplyModifiedProperties();
        }
    }
}
