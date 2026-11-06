using System;
using JsUnitySync.Domain.Identity;

namespace JsUnitySync.Domain.Scenes
{
    /// <summary>The kinds of changes that can occur to a synchronized scene.</summary>
    public enum SceneEventKind
    {
        GameObjectCreated = 0,
        GameObjectDestroyed = 1,
        GameObjectRenamed = 2,
        TransformChanged = 3,
        ComponentAdded = 4,
        ComponentRemoved = 5,
        PropertyChanged = 6,
        SceneLoaded = 7,
        SceneSaved = 8,
    }

    /// <summary>
    /// Envelope for one ordered scene synchronization event inside a session.
    /// Sequence numbers are assigned by the collaboration server and are
    /// strictly monotonic per session; clients acknowledge them for reliable
    /// delivery and reconnect replay.
    /// </summary>
    public sealed class SceneEventEnvelope
    {
        public Ids.StableId EventId { get; }
        public SessionId SessionId { get; }
        public UserId AuthorId { get; }
        /// <summary>Server-assigned monotonic sequence within the session. 0 = not yet sequenced.</summary>
        public long Sequence { get; internal set; }
        public DateTimeOffset UtcTimestamp { get; }
        public SceneEventKind Kind { get; }
        /// <summary>Stable id of the object the event applies to (empty for scene-level events).</summary>
        public Ids.StableId ObjectId { get; }
        /// <summary>Kind-specific payload (never null; use <see cref="Payloads.EmptyPayload"/> when unused).</summary>
        public ISceneEventPayload Payload { get; }

        public SceneEventEnvelope(
            SessionId sessionId,
            UserId authorId,
            SceneEventKind kind,
            Ids.StableId objectId,
            ISceneEventPayload payload,
            DateTimeOffset? utcTimestamp = null)
        {
            if (payload is null) throw new ArgumentNullException(nameof(payload));
            EventId = Ids.StableId.NewId();
            SessionId = sessionId;
            AuthorId = authorId;
            Kind = kind;
            ObjectId = objectId;
            Payload = payload;
            UtcTimestamp = utcTimestamp ?? DateTimeOffset.UtcNow;
        }

        /// <summary>Assigns the server-side sequence. Valid exactly once, from sequencer code only.</summary>
        internal void AssignSequence(long sequence)
        {
            if (Sequence != 0)
                throw new InvalidOperationException("Event has already been sequenced.");
            if (sequence <= 0)
                throw new ArgumentOutOfRangeException(nameof(sequence));
            Sequence = sequence;
        }
    }

    /// <summary>Marker for kind-specific scene event payloads.</summary>
    public interface ISceneEventPayload
    {
    }
}
