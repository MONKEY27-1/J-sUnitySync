using System;
using System.Collections.Generic;

namespace JsUnitySync.Domain.Sync
{
    /// <summary>Outcome of attempting to apply a property write to the synchronized scene state.</summary>
    public enum WriteOutcome
    {
        /// <summary>The write was applied because it was based on the current accepted revision.</summary>
        Applied,
        /// <summary>The write matched the current accepted value (idempotent, no revision bump).</summary>
        AppliedIdempotent,
        /// <summary>
        /// The write was based on an outdated revision. It is NOT applied —
        /// the other developer's accepted work is never silently overwritten.
        /// The caller must surface the conflict to both developers.
        /// </summary>
        Conflict,
    }

    /// <summary>Result of a property write attempt.</summary>
    public sealed class WriteResult
    {
        public WriteOutcome Outcome { get; }
        /// <summary>The accepted revision after the attempt.</summary>
        public long AcceptedRevision { get; }
        /// <summary>The current accepted value (for conflict display), or the newly accepted value.</summary>
        public string AcceptedValue { get; }
        /// <summary>The rejected value when <see cref="Outcome"/> is Conflict.</summary>
        public string? RejectedValue { get; }

        private WriteResult(WriteOutcome outcome, long acceptedRevision, string acceptedValue, string? rejectedValue)
        {
            Outcome = outcome;
            AcceptedRevision = acceptedRevision;
            AcceptedValue = acceptedValue;
            RejectedValue = rejectedValue;
        }

        public static WriteResult Applied(long revision, string value) => new(WriteOutcome.Applied, revision, value, null);
        public static WriteResult AppliedIdempotent(long revision, string value) => new(WriteOutcome.AppliedIdempotent, revision, value, null);
        public static WriteResult Conflicted(long revision, string accepted, string rejected) =>
            new(WriteOutcome.Conflict, revision, accepted, rejected);
    }

    /// <summary>
    /// Tracks the accepted state and revision of one property of one component of
    /// one synchronized GameObject, and applies incoming writes with conflict
    /// detection. Per-property last-writer-wins on top of per-property optimistic
    /// revision checks: a write based on a stale revision never clobbers the
    /// accepted value — it is reported as a conflict instead.
    /// </summary>
    public sealed class PropertyState
    {
        public long Revision { get; private set; }
        public string Value { get; private set; }

        public PropertyState(long initialRevision, string initialValue)
        {
            if (initialRevision < 0) throw new ArgumentOutOfRangeException(nameof(initialRevision));
            Value = initialValue ?? throw new ArgumentNullException(nameof(initialValue));
            Revision = initialRevision;
        }

        public WriteResult TryWrite(long baseRevision, string newValue)
        {
            if (newValue is null) throw new ArgumentNullException(nameof(newValue));
            if (baseRevision < Revision)
                return WriteResult.Conflicted(Revision, Value, newValue);
            if (newValue == Value)
                return WriteResult.AppliedIdempotent(Revision, Value);
            Revision++;
            Value = newValue;
            return WriteResult.Applied(Revision, Value);
        }
    }

    /// <summary>
    /// Registry of synchronized property states for a scene: keyed by
    /// (object, component, property). Shared scene state is composed of these.
    /// </summary>
    public sealed class PropertyRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, PropertyState> _properties =
            new Dictionary<string, PropertyState>(StringComparer.Ordinal);

        private static string Key(string objectId, string componentType, string propertyName) =>
            string.Join("|", objectId, componentType, propertyName);

        /// <summary>Registers a property's initial accepted state. Throws if already registered.</summary>
        public void Register(string objectId, string componentType, string propertyName, long revision, string value)
        {
            var key = Key(objectId, componentType, propertyName);
            lock (_lock)
            {
                if (_properties.ContainsKey(key))
                    throw new InvalidOperationException($"Property '{key}' is already registered.");
                _properties[key] = new PropertyState(revision, value);
            }
        }

        /// <summary>Registers a property if absent, otherwise returns the existing state.</summary>
        public PropertyState GetOrRegister(string objectId, string componentType, string propertyName, long revision, string value)
        {
            var key = Key(objectId, componentType, propertyName);
            lock (_lock)
            {
                if (_properties.TryGetValue(key, out var existing))
                    return existing;
                var state = new PropertyState(revision, value);
                _properties[key] = state;
                return state;
            }
        }

        public bool TryGet(string objectId, string componentType, string propertyName, out PropertyState state)
        {
            var key = Key(objectId, componentType, propertyName);
            lock (_lock)
            {
                if (_properties.TryGetValue(key, out var found))
                {
                    state = found;
                    return true;
                }
                state = null!;
                return false;
            }
        }

        /// <summary>
        /// Applies a write. If the property is not yet known it is registered from
        /// the write's base revision (first write after a snapshot gap). Never
        /// silently discards accepted work: stale-base writes come back as conflicts.
        /// </summary>
        public WriteResult Apply(string objectId, string componentType, string propertyName, long baseRevision, string newValue)
        {
            var state = GetOrRegister(objectId, componentType, propertyName, baseRevision, newValue);
            return state.TryWrite(baseRevision, newValue);
        }
    }
}
