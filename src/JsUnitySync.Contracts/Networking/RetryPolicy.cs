using System;

namespace JsUnitySync.Contracts.Networking
{
    /// <summary>
    /// Retry policy with exponential backoff and full jitter, used by all
    /// client networking code (HTTP and WebSocket reconnection) so that
    /// network failures never spin-loop or stampede the server.
    /// </summary>
    public sealed class RetryPolicy
    {
        public int MaxAttempts { get; }
        public TimeSpan BaseDelay { get; }
        public TimeSpan MaxDelay { get; }

        public RetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay)
        {
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            if (baseDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDelay));
            if (maxDelay < baseDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay));
            MaxAttempts = maxAttempts;
            BaseDelay = baseDelay;
            MaxDelay = maxDelay;
        }

        public static RetryPolicy Default { get; } = new RetryPolicy(
            maxAttempts: 5,
            baseDelay: TimeSpan.FromMilliseconds(250),
            maxDelay: TimeSpan.FromSeconds(8));

        private static readonly Random _rng = new Random();

        /// <summary>
        /// Computes the randomized delay before attempt <paramref name="attempt"/>
        /// (1-based). Returns null when the attempt number exceeds <see cref="MaxAttempts"/>.
        /// </summary>
        public TimeSpan? DelayBefore(int attempt)
        {
            if (attempt < 1 || attempt > MaxAttempts) return null;
            // exponential cap: base * 2^(attempt-1), capped at MaxDelay
            double exponential = Math.Min(
                BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
                MaxDelay.TotalMilliseconds);
            // full jitter: uniform in [0, exponential)
            double jittered;
            lock (_rng)
            {
                jittered = exponential * _rng.NextDouble();
            }
            return TimeSpan.FromMilliseconds(jittered);
        }

        /// <summary>True when attempt <paramref name="attempt"/> (1-based) is still within budget.</summary>
        public bool CanRetry(int attempt) => attempt >= 1 && attempt <= MaxAttempts;
    }
}
