using System;

namespace JsUnitySync.Domain.Identity
{
    /// <summary>Identity of a J'sUnitySync user account. Backed by a StableId.</summary>
    public readonly struct UserId : IEquatable<UserId>
    {
        public Ids.StableId Value { get; }
        public UserId(Ids.StableId value) => Value = value;
        public static UserId New() => new(Ids.StableId.NewId());
        public override string ToString() => Value.ToString();
        public bool Equals(UserId other) => Value.Equals(other.Value);
        public override bool Equals(object? obj) => obj is UserId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(UserId l, UserId r) => l.Equals(r);
        public static bool operator !=(UserId l, UserId r) => !l.Equals(r);
    }

    /// <summary>Identity of a development team.</summary>
    public readonly struct TeamId : IEquatable<TeamId>
    {
        public Ids.StableId Value { get; }
        public TeamId(Ids.StableId value) => Value = value;
        public static TeamId New() => new(Ids.StableId.NewId());
        public override string ToString() => Value.ToString();
        public bool Equals(TeamId other) => Value.Equals(other.Value);
        public override bool Equals(object? obj) => obj is TeamId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(TeamId l, TeamId r) => l.Equals(r);
        public static bool operator !=(TeamId l, TeamId r) => !l.Equals(r);
    }

    /// <summary>Identity of a Unity project registered with J'sUnitySync.</summary>
    public readonly struct ProjectId : IEquatable<ProjectId>
    {
        public Ids.StableId Value { get; }
        public ProjectId(Ids.StableId value) => Value = value;
        public static ProjectId New() => new(Ids.StableId.NewId());
        public override string ToString() => Value.ToString();
        public bool Equals(ProjectId other) => Value.Equals(other.Value);
        public override bool Equals(object? obj) => obj is ProjectId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(ProjectId l, ProjectId r) => l.Equals(r);
        public static bool operator !=(ProjectId l, ProjectId r) => !l.Equals(r);
    }

    /// <summary>Identity of a live collaboration session (one shared project context).</summary>
    public readonly struct SessionId : IEquatable<SessionId>
    {
        public Ids.StableId Value { get; }
        public SessionId(Ids.StableId value) => Value = value;
        public static SessionId New() => new(Ids.StableId.NewId());
        public override string ToString() => Value.ToString();
        public bool Equals(SessionId other) => Value.Equals(other.Value);
        public override bool Equals(object? obj) => obj is SessionId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(SessionId l, SessionId r) => l.Equals(r);
        public static bool operator !=(SessionId l, SessionId r) => !l.Equals(r);
    }

    /// <summary>Membership role of a user inside a team.</summary>
    public enum TeamRole
    {
        Member = 0,
        Lead = 1,
        Owner = 2,
    }
}
