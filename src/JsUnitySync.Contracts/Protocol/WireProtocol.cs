using System;

namespace JsUnitySync.Contracts.Protocol
{
    /// <summary>The kind of frame carried over the RealTime WebSocket.</summary>
    public enum FrameKind : int
    {
        /// <summary>First frame from the client after connecting: identifies session and developer.</summary>
        Hello = 0,
        /// <summary>Server response to Hello; carries the resume position.</summary>
        Welcome = 1,
        /// <summary>A scene synchronization event delivered to a client.</summary>
        SceneEvent = 2,
        /// <summary>Client acknowledgment of one or more events by sequence.</summary>
        EventAck = 3,
        /// <summary>Keep-alive.</summary>
        Heartbeat = 4,
        /// <summary>Chat message.</summary>
        ChatMessage = 5,
        /// <summary>Presence update (join/leave/away).</summary>
        Presence = 6,
        /// <summary>WebRTC signaling offer/answer/ICE.</summary>
        RtcSignal = 7,
        /// <summary>Recovery: client requests replay from a given sequence.</summary>
        ResumeRequest = 8,
        /// <summary>Server rejects an operation (auth, conflict, protocol error).</summary>
        Error = 9,
    }

    /// <summary>
    /// The universal wire envelope. Ids travel as StableId strings (26 chars);
    /// JSON is the serialization format for Phase 1; a binary format may be
    /// introduced in Phase 7 if measurements require it.
    /// </summary>
    public sealed class WireFrame
    {
        public FrameKind Kind { get; set; }
        /// <summary>StableId of this frame; empty for frames that are responses.</summary>
        public string FrameId { get; set; } = string.Empty;
        /// <summary>Session the frame belongs to; empty for session-less frames.</summary>
        public string SessionId { get; set; } = string.Empty;
        /// <summary>UTC ISO-8601 timestamp.</summary>
        public DateTimeOffset UtcTimestamp { get; set; }
        /// <summary>Server-assigned sequence for reliable delivery (SceneEvent frames only; -1 otherwise).</summary>
        public long Sequence { get; set; } = -1;
        /// <summary>Kind-specific JSON payload.</summary>
        public string PayloadJson { get; set; } = string.Empty;
    }

    /// <summary>Payload of a Hello frame.</summary>
    public sealed class HelloPayload
    {
        public string UserId { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
        public string ProtocolVersion { get; set; } = ProtocolInfo.Version;
        /// <summary>Sequence the client has already acknowledged; -1 for a fresh join.</summary>
        public long LastAckedSequence { get; set; } = -1;
    }

    /// <summary>Payload of an EventAck frame.</summary>
    public sealed class EventAckPayload
    {
        /// <summary>Highest sequence the client has durably applied.</summary>
        public long HighestAppliedSequence { get; set; }
    }

    /// <summary>Payload of a ResumeRequest frame.</summary>
    public sealed class ResumeRequestPayload
    {
        /// <summary>Replay everything after this sequence.</summary>
        public long LastAppliedSequence { get; set; }
    }

    /// <summary>Payload of an Error frame.</summary>
    public sealed class ErrorPayload
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public static class ProtocolInfo
    {
        /// <summary>Wire protocol version. Bumped on breaking changes.</summary>
        public const string Version = "1.0";
    }
}
