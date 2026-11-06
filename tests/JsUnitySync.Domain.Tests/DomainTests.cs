using JsUnitySync.Contracts.Networking;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Domain.Scenes;
using JsUnitySync.Domain.Sync;
using System;
using System.Linq;
using Xunit;

namespace JsUnitySync.Domain.Tests
{
    public class StableIdTests
    {
        [Fact]
        public void NewId_RoundTripsThroughToStringAndParse()
        {
            var id = Ids.StableId.NewId();
            var text = id.ToString();
            Assert.Equal(Ids.StableId.CharLength, text.Length);
            var parsed = Ids.StableId.Parse(text);
            Assert.Equal(id, parsed);
        }

        [Fact]
        public void NewId_EmbedsCreationTimestamp()
        {
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = Ids.StableId.NewId();
            var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.InRange(id.TimestampMilliseconds, before, after);
        }

        [Fact]
        public void NewIds_AreUniqueAcrossManyGenerations()
        {
            var ids = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 10_000; i++)
                ids.Add(Ids.StableId.NewId().ToString());
            Assert.Equal(10_000, ids.Count);
        }

        [Fact]
        public void NewIds_SortByCreationTime()
        {
            var first = Ids.StableId.NewId();
            System.Threading.Thread.Sleep(2);
            var second = Ids.StableId.NewId();
            Assert.True(first.CompareTo(second) < 0);
        }

        [Theory]
        [InlineData("")]
        [InlineData("01AN4Z07BY")]          // too short
        [InlineData("01AN4Z07BY79KA5C7RKDG4KPW")] // 27 chars
        [InlineData("01AN4Z07BY79KA5C7RKDG4KPU")] // contains U
        public void TryParse_RejectsMalformedInput(string input)
        {
            Assert.False(Ids.StableId.TryParse(input, out _));
        }

        [Fact]
        public void Parse_SpecVector_RoundTrips()
        {
            // Reference ULID from the specification test suite.
            const string vector = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
            var parsed = Ids.StableId.Parse(vector);
            Assert.Equal(vector, parsed.ToString());
            Assert.Equal(1469922850259L, parsed.TimestampMilliseconds);
        }

        [Fact]
        public void TryParse_AcceptsCrockfordSubstitutions()
        {
            var id = Ids.StableId.NewId().ToString();
            var lowered = id.ToLowerInvariant(); // decoding accepts common mis-typings
            Assert.True(Ids.StableId.TryParse(lowered, out var parsed));
            Assert.Equal(id, parsed.ToString());
        }
    }

    public class SessionSequencerTests
    {
        private static SceneEventEnvelope NewEvent() => new(
            new Identity.SessionId(Ids.StableId.NewId()),
            new Identity.UserId(Ids.StableId.NewId()),
            SceneEventKind.SceneLoaded,
            default, EmptyPayload.Instance);

        [Fact]
        public void AssignNext_SequenceIsStrictlyMonotonic()
        {
            var sequencer = new SessionSequencer();
            var events = Enumerable.Range(0, 100).Select(_ => NewEvent()).ToArray();
            foreach (var e in events)
                sequencer.AssignNext(e);
            Assert.Equal(100, sequencer.LastSequence);
            Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), events.Select(e => e.Sequence));
        }

        [Fact]
        public void AssignNext_ThrowsWhenEventAlreadySequenced()
        {
            var sequencer = new SessionSequencer();
            var e = NewEvent();
            sequencer.AssignNext(e);
            Assert.Throws<InvalidOperationException>(() => sequencer.AssignNext(e));
        }

        [Fact]
        public void RestoreAfter_AdvancesPastPersistedSequenceWithoutReuse()
        {
            var sequencer = new SessionSequencer();
            Assert.Equal(500, sequencer.RestoreAfter(500));
            var e = NewEvent();
            sequencer.AssignNext(e);
            Assert.Equal(501, e.Sequence);
        }

        [Fact]
        public void RestoreAfter_NeverMovesBackwards()
        {
            var sequencer = new SessionSequencer();
            sequencer.RestoreAfter(900);
            Assert.Equal(900, sequencer.RestoreAfter(100));
        }
    }

    public class ConflictResolutionTests
    {
        private const string Obj = "01ARZ3NDEKTSV4RRFFQ69G5FAV"; // sample-length string; ids are opaque keys here

        private static PropertyRegistry NewRegistry()
        {
            var registry = new PropertyRegistry();
            registry.Register(Obj, "Light", "m_Intensity", 1, "1.0");
            return registry;
        }

        [Fact]
        public void Apply_FreshWrite_IsAccepted()
        {
            var registry = NewRegistry();
            var result = registry.Apply(Obj, "Light", "m_Intensity", 1, "2.5");
            Assert.Equal(WriteOutcome.Applied, result.Outcome);
            Assert.Equal(2, result.AcceptedRevision);
            Assert.Equal("2.5", result.AcceptedValue);
        }

        [Fact]
        public void Apply_IdenticalWrite_IsIdempotent()
        {
            var registry = NewRegistry();
            var result = registry.Apply(Obj, "Light", "m_Intensity", 1, "1.0");
            Assert.Equal(WriteOutcome.AppliedIdempotent, result.Outcome);
            Assert.Equal(1, result.AcceptedRevision);
        }

        [Fact]
        public void Apply_StaleBaseRevision_NeverOverwritesAcceptedWork()
        {
            var registry = NewRegistry();
            var accepted = registry.Apply(Obj, "Light", "m_Intensity", 1, "2.5"); // developer A
            Assert.Equal(WriteOutcome.Applied, accepted.Outcome);

            // developer B was working from revision 1 and writes afterwards
            var stale = registry.Apply(Obj, "Light", "m_Intensity", 1, "0.3");
            Assert.Equal(WriteOutcome.Conflict, stale.Outcome);
            Assert.Equal("2.5", stale.AcceptedValue);  // A's accepted work intact
            Assert.Equal("0.3", stale.RejectedValue);  // B's write surfaced, not dropped
            Assert.Equal(2, stale.AcceptedRevision);
        }

        [Fact]
        public void Apply_ConcurrentLastWriterWinsOnSameRevision()
        {
            var registry = NewRegistry();
            var first = registry.Apply(Obj, "Light", "m_Intensity", 1, "2.5");
            var second = registry.Apply(Obj, "Light", "m_Intensity", 1, "3.0"); // same base → serialized order decides
            Assert.Equal(WriteOutcome.Applied, first.Outcome);
            Assert.Equal(WriteOutcome.Conflict, second.Outcome);
        }

        [Fact]
        public void Apply_NewProperty_RegistersFromBaseRevision()
        {
            var registry = new PropertyRegistry();
            var result = registry.Apply(Obj, "Camera", "field of view", 0, "60");
            Assert.Equal(WriteOutcome.AppliedIdempotent, result.Outcome);
            Assert.Equal(0, result.AcceptedRevision);
        }
    }

    public class RetryPolicyTests
    {
        [Fact]
        public void DelayBefore_IsAlwaysWithinBackoffBounds()
        {
            var policy = new RetryPolicy(6, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                var delay = policy.DelayBefore(attempt);
                Assert.NotNull(delay);
                Assert.InRange(delay!.Value.TotalMilliseconds, 0, 2000);
            }
        }

        [Fact]
        public void DelayBefore_ReturnsNullAfterMaxAttempts()
        {
            var policy = RetryPolicy.Default;
            Assert.False(policy.CanRetry(6));
            Assert.Null(policy.DelayBefore(7));
        }

        [Fact]
        public void Constructor_RejectsInvalidArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
        }
    }
}
