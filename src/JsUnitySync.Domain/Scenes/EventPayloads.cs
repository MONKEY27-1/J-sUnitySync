using System;
using System.Collections.Generic;

namespace JsUnitySync.Domain.Scenes
{
    /// <summary>A scene-level event with no additional data.</summary>
    public sealed class EmptyPayload : ISceneEventPayload
    {
        public static EmptyPayload Instance { get; } = new EmptyPayload();
        private EmptyPayload() { }
    }

    /// <summary>Unity transform state: position/rotation/scale, each in Unity coordinate space.</summary>
    public sealed class TransformState : ISceneEventPayload
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        /// <summary>Quaternion rotation as (x, y, z, w).</summary>
        public float RotX { get; }
        public float RotY { get; }
        public float RotZ { get; }
        public float RotW { get; }
        public float ScaleX { get; }
        public float ScaleY { get; }
        public float ScaleZ { get; }
        public Ids.StableId? ParentObjectId { get; }

        public TransformState(
            float x, float y, float z,
            float rotX, float rotY, float rotZ, float rotW,
            float scaleX, float scaleY, float scaleZ,
            Ids.StableId? parentObjectId = null)
        {
            X = x; Y = y; Z = z;
            RotX = rotX; RotY = rotY; RotZ = rotZ; RotW = rotW;
            ScaleX = scaleX; ScaleY = scaleY; ScaleZ = scaleZ;
            ParentObjectId = parentObjectId;
        }
    }

    /// <summary>A named property change on a component of a GameObject.</summary>
    public sealed class PropertyChangedPayload : ISceneEventPayload
    {
        public string ComponentType { get; }
        public string PropertyName { get; }
        /// <summary>Serialized new value, in Unity's serialized form.</summary>
        public string SerializedValue { get; }
        /// <summary>Client-side revision the author based this change on, for conflict detection.</summary>
        public long BaseRevision { get; }

        public PropertyChangedPayload(string componentType, string propertyName, string serializedValue, long baseRevision)
        {
            if (string.IsNullOrWhiteSpace(componentType)) throw new ArgumentException("Component type is required.", nameof(componentType));
            if (string.IsNullOrWhiteSpace(propertyName)) throw new ArgumentException("Property name is required.", nameof(propertyName));
            ComponentType = componentType;
            PropertyName = propertyName;
            SerializedValue = serializedValue ?? string.Empty;
            BaseRevision = baseRevision;
        }
    }

    /// <summary>Creation of a GameObject with its initial transform and name.</summary>
    public sealed class GameObjectCreatedPayload : ISceneEventPayload
    {
        public string Name { get; }
        public TransformState InitialTransform { get; }

        public GameObjectCreatedPayload(string name, TransformState initialTransform)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
            Name = name;
            InitialTransform = initialTransform ?? throw new ArgumentNullException(nameof(initialTransform));
        }
    }

    /// <summary>Rename of a GameObject.</summary>
    public sealed class RenamedPayload : ISceneEventPayload
    {
        public string NewName { get; }
        public RenamedPayload(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("New name is required.", nameof(newName));
            NewName = newName;
        }
    }
}
