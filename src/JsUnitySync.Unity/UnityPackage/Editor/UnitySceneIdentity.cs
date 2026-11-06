using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JsUnitySync.UnityPackage.Editor
{
    public static class UnitySceneIdentity
    {
        public static string GetSceneGuid(Scene scene)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return string.Empty;
            return AssetDatabase.AssetPathToGUID(scene.path).ToLowerInvariant();
        }

        public static IReadOnlyList<GameObject> GetSceneObjects(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return Array.Empty<GameObject>();
            var objects = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                objects.AddRange(root.GetComponentsInChildren<Transform>(true).Select(x => x.gameObject));
            return objects.Where(x => x.scene == scene && !EditorUtility.IsPersistent(x) && (x.hideFlags & HideFlags.HideAndDontSave) == 0).Distinct().ToArray();
        }

        /// <summary>Must only be called from the explicit, confirmation-gated Editor action.</summary>
        public static int EnsureStableIds(Scene scene)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var added = 0;
            foreach (var gameObject in GetSceneObjects(scene))
            {
                var identity = gameObject.GetComponent<JsUnitySyncObjectId>();
                if (identity == null)
                {
                    identity = Undo.AddComponent<JsUnitySyncObjectId>(gameObject);
                    identity.AssignNewId();
                    EditorUtility.SetDirty(identity);
                    added++;
                }
                else if (!StableIdFactory.IsValid(identity.StableId) || !seen.Add(identity.StableId))
                {
                    Undo.RecordObject(identity, "Repair J'sUnitySync object id");
                    identity.SetId(StableIdFactory.NewId());
                    EditorUtility.SetDirty(identity);
                    added++;
                }
                else continue;

                if (!string.IsNullOrEmpty(identity.StableId)) seen.Add(identity.StableId);
            }
            if (added > 0) EditorSceneManager.MarkSceneDirty(scene);
            return added;
        }

        public static string[] GetStableObjectIds(Scene scene) => GetSceneObjects(scene)
            .Select(x => x.GetComponent<JsUnitySyncObjectId>())
            .Where(x => x != null && StableIdFactory.IsValid(x.StableId))
            .Select(x => x.StableId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        public static string SceneFileHash(Scene scene)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path) || !System.IO.File.Exists(scene.path))
                throw new InvalidOperationException("Save the active Unity Scene before computing its collaboration fingerprint.");
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.IO.File.ReadAllBytes(scene.path))).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static string ManifestHash(Scene scene)
        {
            // Only the digest is sent: scene bytes are read locally, never uploaded. This
            // prevents a different saved project baseline from being silently overlaid.
            var canonical = SceneFileHash(scene) + "\n" + string.Join("\n", GetStableObjectIds(scene));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static JsUnitySyncObjectId Find(Scene scene, string id)
        {
            if (!StableIdFactory.IsValid(id)) return null;
            foreach (var identity in GetSceneObjects(scene).Select(x => x.GetComponent<JsUnitySyncObjectId>()))
                if (identity != null && identity.StableId == id) return identity;
            return null;
        }
    }
}
