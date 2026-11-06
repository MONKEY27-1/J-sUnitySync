using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using JsUnitySync.Contracts.Protocol;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace JsUnitySync.Server.Realtime;

public sealed class RealtimeSubscription : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;
    private int _disposed;
    public ChannelReader<WireFrame> Reader { get; }
    public RealtimeSubscription(ChannelReader<WireFrame> reader, Func<ValueTask> dispose) { Reader = reader; _dispose = dispose; }
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? _dispose() : ValueTask.CompletedTask;
}

/// <summary>Pub/sub is a low-latency hint; durable event rows remain the recovery source of truth.</summary>
public interface IRealtimeBackplane
{
    bool IsHealthy { get; }
    ValueTask<RealtimeSubscription> SubscribeAsync(string sessionId, CancellationToken cancellationToken);
    ValueTask PublishAsync(string sessionId, WireFrame frame, CancellationToken cancellationToken);
}

/// <summary>Real single-process backplane for local development and one-node hosting.</summary>
public sealed class InMemoryRealtimeBackplane : IRealtimeBackplane
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<WireFrame>>> _rooms = new(StringComparer.Ordinal);
    public bool IsHealthy => true;

    public ValueTask<RealtimeSubscription> SubscribeAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<WireFrame>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        _rooms.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, Channel<WireFrame>>())[id] = channel;
        return ValueTask.FromResult(new RealtimeSubscription(channel.Reader, () =>
        {
            channel.Writer.TryComplete();
            if (_rooms.TryGetValue(sessionId, out var room))
            {
                room.TryRemove(id, out _);
                if (room.IsEmpty) _rooms.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Channel<WireFrame>>>(sessionId, room));
            }
            return ValueTask.CompletedTask;
        }));
    }

    public ValueTask PublishAsync(string sessionId, WireFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_rooms.TryGetValue(sessionId, out var room))
        {
            foreach (var channel in room.Values)
                if (!channel.Writer.TryWrite(frame))
                    channel.Writer.TryComplete(new RealtimeBackpressureException());
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class RedisRealtimeBackplane(IConnectionMultiplexer redis, ILogger<RedisRealtimeBackplane> logger) : IRealtimeBackplane
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static RedisChannel Topic(string sessionId) => RedisChannel.Literal($"jsunitysync:v1:session:{sessionId}");
    public bool IsHealthy => redis.IsConnected;

    public ValueTask<RealtimeSubscription> SubscribeAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channel = Channel.CreateBounded<WireFrame>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        var queue = redis.GetSubscriber().Subscribe(Topic(sessionId));
        queue.OnMessage(message =>
        {
            try
            {
                var frame = JsonSerializer.Deserialize<WireFrame>((string?)message.Message ?? "", Json);
                if (frame is null || !channel.Writer.TryWrite(frame))
                    channel.Writer.TryComplete(new RealtimeBackpressureException());
            }
            catch (JsonException error)
            {
                logger.LogWarning("Discarded malformed realtime backplane message {ErrorType}", error.GetType().Name);
            }
        });
        return ValueTask.FromResult(new RealtimeSubscription(channel.Reader, async () =>
        {
            channel.Writer.TryComplete();
            await queue.UnsubscribeAsync();
        }));
    }

    public async ValueTask PublishAsync(string sessionId, WireFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(frame, Json);
        try
        {
            await redis.GetSubscriber().PublishAsync(Topic(sessionId), payload);
        }
        catch (RedisException error)
        {
            // The event is already durable. Each socket also polls the ordered event log,
            // so a transient pub/sub failure increases latency but does not lose the event.
            logger.LogWarning("Realtime Redis publish failed with {ErrorType}; durable poll will deliver", error.GetType().Name);
        }
    }
}

public sealed class RealtimeBackpressureException() : Exception("Realtime client exceeded its bounded send queue; reconnect to replay durable events.");
