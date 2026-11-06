using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Server.Realtime;

namespace JsUnitySync.Server.Tests;

public sealed class RealtimeBackplaneTests
{
    [Fact]
    public async Task InMemoryBackplane_FansOutOnlyWithinTheSession()
    {
        var bus = new InMemoryRealtimeBackplane();
        await using var first = await bus.SubscribeAsync("session-one", default);
        await using var second = await bus.SubscribeAsync("session-one", default);
        await using var otherRoom = await bus.SubscribeAsync("session-two", default);
        var frame = new WireFrame { Kind = FrameKind.SceneEvent, SessionId = "session-one", Sequence = 1, FrameId = "event-one" };
        await bus.PublishAsync("session-one", frame, default);
        Assert.Equal("event-one", (await first.Reader.ReadAsync()).FrameId);
        Assert.Equal("event-one", (await second.Reader.ReadAsync()).FrameId);
        Assert.False(otherRoom.Reader.TryRead(out _));
    }

    [Fact]
    public async Task InMemoryBackplane_ClosesSlowPeerInsteadOfSilentlyDroppingAcceptedEvents()
    {
        var bus = new InMemoryRealtimeBackplane();
        await using var slow = await bus.SubscribeAsync("session", default);
        for (var i = 1; i <= 257; i++)
            await bus.PublishAsync("session", new WireFrame { Kind = FrameKind.SceneEvent, SessionId = "session", Sequence = i }, default);
        var received = 0;
        while (slow.Reader.TryRead(out _)) received++;
        Assert.Equal(256, received);
        var closed = await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(async () => await slow.Reader.ReadAsync());
        Assert.IsType<RealtimeBackpressureException>(closed.InnerException);
    }
}
