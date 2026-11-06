using System;
using System.Collections.Generic;
using UnityEngine;

namespace JsUnitySync.UnityPackage.Editor
{
    /// <summary>Network-added components are restricted to built-in safe components; arbitrary remote scripts are never instantiated.</summary>
    internal static class UnityComponentRegistry
    {
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [typeof(Camera).FullName] = typeof(Camera),
            [typeof(Light).FullName] = typeof(Light),
            [typeof(AudioSource).FullName] = typeof(AudioSource),
            [typeof(AudioListener).FullName] = typeof(AudioListener),
            [typeof(Rigidbody).FullName] = typeof(Rigidbody),
            [typeof(BoxCollider).FullName] = typeof(BoxCollider),
            [typeof(SphereCollider).FullName] = typeof(SphereCollider),
            [typeof(CapsuleCollider).FullName] = typeof(CapsuleCollider),
            [typeof(MeshCollider).FullName] = typeof(MeshCollider),
            [typeof(MeshFilter).FullName] = typeof(MeshFilter),
            [typeof(MeshRenderer).FullName] = typeof(MeshRenderer),
            [typeof(SkinnedMeshRenderer).FullName] = typeof(SkinnedMeshRenderer),
            [typeof(Animator).FullName] = typeof(Animator)
        };

        public static bool IsAllowed(string componentType) => !string.IsNullOrEmpty(componentType) && Types.ContainsKey(componentType);
        public static Type Resolve(string componentType) => Types.TryGetValue(componentType, out var type) ? type : null;
    }
}
