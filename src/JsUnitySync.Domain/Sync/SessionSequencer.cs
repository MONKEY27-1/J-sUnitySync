using System;
using System.Collections.Generic;
using JsUnitySync.Domain.Scenes;

namespace JsUnitySync.Domain.Sync
{
    /// <summary>
    /// Assigns strictly monotonic per-session sequence numbers to scene events.
    /// The server owns one instance per session; the assigned sequence defines
    /// the authoritative ordering used for delivery, acknowledgment and replay.
    /// </summary>
    public sealed class SessionSequencer
    {
        private readonly object _lock = new object();
        private long _last;

        /// <summary>The highest sequence handed out so far (0 before the first event).</summary>
        public long LastSequence
        {
            get { lock (_lock) return _last; }
        }

        /// <summary>Assigns the next sequence to <paramref name="envelope"/>. Throws if already sequenced.</summary>
        public void AssignNext(SceneEventEnvelope envelope)
        {
            if (envelope is null) throw new ArgumentNullException(nameof(envelope));
            lock (_lock)
            {
                envelope.AssignSequence(_last + 1);
                _last++;
            }
        }

        /// <summary>
        /// Restores the sequencer after a server restart / snapshot recovery by
        /// advancing it past the highest persisted sequence. Returns the new last sequence.
        /// </summary>
        public long RestoreAfter(long highestPersistedSequence)
        {
            if (highestPersistedSequence < 0)
                throw new ArgumentOutOfRangeException(nameof(highestPersistedSequence));
            lock (_lock)
            {
                if (highestPersistedSequence > _last)
                    _last = highestPersistedSequence;
                return _last;
            }
        }
    }
}
