using System;
using JsUnitySync.Domain.Ids;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Domain.Identity;
using JsUnitySync.Domain.Scenes;

namespace JsUnitySync.Unity
{
    /// <summary>
    /// Pure, Unity-agnostic factory for scene synchronization events.
    /// The Editor extension (Phase 8) captures Unity Editor callbacks and feeds
    /// them through this factory so the event construction rules are identical
    /// across the desktop client and the Unity client, and unit-testable here
    /// without a Unity runtime.
    /// </summary>
    public static class SceneEventFactory
    {
        /// <summary>Creates a GameObject creation event.</summary>
        public static SceneEventEnvelope GameObjectCreated(
            SessionId session, UserId author, StableId objectId, string name,
            float x, float y, float z, StableId? parentId)
        {
            var transform = new TransformState(x, y, z, 0, 0, 0, 1, 1, 1, 1, parentId);
            return new SceneEventEnvelope(
                session, author, SceneEventKind.GameObjectCreated, objectId,
                new GameObjectCreatedPayload(name, transform));
        }

        /// <summary>Creates a transform-changed event.</summary>
        public static SceneEventEnvelope TransformChanged(
            SessionId session, UserId author, StableId objectId,
            float x, float y, float z,
            float rotX, float rotY, float rotZ, float rotW,
            float scaleX, float scaleY, float scaleZ,
            StableId? parentId)
        {
            return new SceneEventEnvelope(
                session, author, SceneEventKind.TransformChanged, objectId,
                new TransformState(x, y, z, rotX, rotY, rotZ, rotW, scaleX, scaleY, scaleZ, parentId));
        }

        /// <summary>Creates a property-changed event with the author's base revision for conflict detection.</summary>
        public static SceneEventEnvelope PropertyChanged(
            SessionId session, UserId author, StableId objectId,
            string componentType, string propertyName, string serializedValue, long baseRevision)
        {
            return new SceneEventEnvelope(
                session, author, SceneEventKind.PropertyChanged, objectId,
                new PropertyChangedPayload(componentType, propertyName, serializedValue, baseRevision));
        }
    }
}
