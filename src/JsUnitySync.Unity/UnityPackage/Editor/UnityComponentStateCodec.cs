using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace JsUnitySync.UnityPackage.Editor
{
    [Serializable]
    internal sealed class UnityComponentPropertyState
    {
        public string path = "";
        public string valueJson = "";
    }

    [Serializable]
    internal sealed class UnityComponentState
    {
        public List<UnityComponentPropertyState> properties = new List<UnityComponentPropertyState>();
    }

    /// <summary>Transfers only explicitly supported SerializedProperty values; never serializes UnityEngine.Object internals or asset bytes.</summary>
    internal static class UnityComponentStateCodec
    {
        public static bool TryCapture(Component component, out string json)
        {
            json = string.Empty;
            if (component == null) return false;
            var serialized = new SerializedObject(component);
            var property = serialized.GetIterator();
            var state = new UnityComponentState();
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.propertyPath == "m_Script") continue;
                if (!UnityPropertyCodec.TryEncode(property, out var value)) continue;
                state.properties.Add(new UnityComponentPropertyState { path = property.propertyPath, valueJson = value });
                if (state.properties.Count > 128) return false;
            }
            json = JsonUtility.ToJson(state);
            return json.Length <= 16 * 1024;
        }

        public static int Apply(Component component, string json, Func<string, GameObject> findObject)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            var state = JsonUtility.FromJson<UnityComponentState>(json);
            if (state == null || state.properties == null || state.properties.Count > 128)
                throw new InvalidOperationException("Remote component state is invalid or exceeds its property limit.");
            var serialized = new SerializedObject(component);
            var applied = 0;
            foreach (var entry in state.properties)
            {
                if (entry == null || string.IsNullOrEmpty(entry.path)) continue;
                var property = serialized.FindProperty(entry.path);
                if (property != null && UnityPropertyCodec.TryApply(property, entry.valueJson, findObject)) applied++;
            }
            serialized.ApplyModifiedProperties();
            return applied;
        }
    }
}
