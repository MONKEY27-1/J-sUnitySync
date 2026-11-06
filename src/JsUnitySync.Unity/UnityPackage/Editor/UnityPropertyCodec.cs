using System;
using UnityEditor;
using UnityEngine;

namespace JsUnitySync.UnityPackage.Editor
{
    [Serializable]
    internal sealed class UnitySerializedPropertyValue
    {
        public string type = "";
        public string stringValue = "";
        public int intValue;
        public float floatValue;
        public bool boolValue;
        public Color colorValue;
        public Vector2 vector2Value;
        public Vector3 vector3Value;
        public Vector4 vector4Value;
        public Rect rectValue;
        public Bounds boundsValue;
        public string objectStableId = "";
    }

    internal static class UnityPropertyCodec
    {
        public static bool TryEncode(SerializedProperty property, out string serialized)
        {
            serialized = string.Empty;
            var value = new UnitySerializedPropertyValue { type = property.propertyType.ToString() };
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.LayerMask:
                    value.intValue = property.intValue; break;
                case SerializedPropertyType.Boolean:
                    value.boolValue = property.boolValue; break;
                case SerializedPropertyType.Float:
                    value.floatValue = property.floatValue; break;
                case SerializedPropertyType.String:
                    value.stringValue = property.stringValue ?? string.Empty; break;
                case SerializedPropertyType.Color:
                    value.colorValue = property.colorValue; break;
                case SerializedPropertyType.Vector2:
                    value.vector2Value = property.vector2Value; break;
                case SerializedPropertyType.Vector3:
                    value.vector3Value = property.vector3Value; break;
                case SerializedPropertyType.Vector4:
                    value.vector4Value = property.vector4Value; break;
                case SerializedPropertyType.Rect:
                    value.rectValue = property.rectValue; break;
                case SerializedPropertyType.Bounds:
                    value.boundsValue = property.boundsValue; break;
                case SerializedPropertyType.ObjectReference:
                    var target = property.objectReferenceValue as Component;
                    var gameObject = target == null ? property.objectReferenceValue as GameObject : target.gameObject;
                    if (gameObject == null) return false;
                    var identity = gameObject.GetComponent<JsUnitySyncObjectId>();
                    if (identity == null || !StableIdFactory.IsValid(identity.StableId)) return false;
                    value.objectStableId = identity.StableId;
                    break;
                default:
                    return false;
            }
            serialized = JsonUtility.ToJson(value);
            return true;
        }

        public static bool TryApply(SerializedProperty property, string serialized, Func<string, GameObject> findObject)
        {
            UnitySerializedPropertyValue value;
            try { value = JsonUtility.FromJson<UnitySerializedPropertyValue>(serialized); }
            catch (Exception) { return false; }
            if (value == null || value.type != property.propertyType.ToString()) return false;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.LayerMask: property.intValue = value.intValue; return true;
                case SerializedPropertyType.Boolean: property.boolValue = value.boolValue; return true;
                case SerializedPropertyType.Float: property.floatValue = value.floatValue; return true;
                case SerializedPropertyType.String: property.stringValue = value.stringValue; return true;
                case SerializedPropertyType.Color: property.colorValue = value.colorValue; return true;
                case SerializedPropertyType.Vector2: property.vector2Value = value.vector2Value; return true;
                case SerializedPropertyType.Vector3: property.vector3Value = value.vector3Value; return true;
                case SerializedPropertyType.Vector4: property.vector4Value = value.vector4Value; return true;
                case SerializedPropertyType.Rect: property.rectValue = value.rectValue; return true;
                case SerializedPropertyType.Bounds: property.boundsValue = value.boundsValue; return true;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = findObject(value.objectStableId);
                    return !string.IsNullOrEmpty(value.objectStableId) ? property.objectReferenceValue != null : true;
                default: return false;
            }
        }
    }
}
