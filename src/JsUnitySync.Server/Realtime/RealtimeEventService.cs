using System.Text.Json;
using JsUnitySync.Contracts.Protocol;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Domain.Ids;
using JsUnitySync.Domain.Scenes;
using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JsUnitySync.Server.Realtime;

public sealed record AppendSceneResult(WireFrame? Event, WireFrame? Conflict);

/// <summary>Serializes acceptance through a durable per-session row; only committed events are published.</summary>
public sealed class RealtimeEventService(IDbContextFactory<CollaborationDbContext> contexts, TimeProvider clock)
{
    private const int MaxReplayBatch = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsValidClientId(string value) => Guid.TryParseExact(value, "N", out _);

    public async Task<long> EstablishCursorAsync(string userId, string sessionId, string clientId, long claimedApplied, CancellationToken token)
    {
        if (!IsValidClientId(clientId) || claimedApplied < -1) throw ApiFault.Invalid("Invalid client cursor.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var session = await AuthorizedSession(db, userId, sessionId, token);
        if (claimedApplied > session.LastSequence) throw ApiFault.Conflict();
        var cursor = await db.SessionClientCursors.SingleOrDefaultAsync(x => x.SessionId == sessionId && x.UserId == userId && x.ClientId == clientId, token);
        if (cursor is null)
        {
            cursor = new SessionClientCursorRecord { SessionId = sessionId, UserId = userId, ClientId = clientId, LastAckedSequence = claimedApplied, UpdatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds() };
            db.SessionClientCursors.Add(cursor);
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException error) when (IsUniqueViolation(error))
            {
                await using var retry = await contexts.CreateDbContextAsync(token);
                var stored = await retry.SessionClientCursors.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.UserId == userId && x.ClientId == clientId, token);
                if (stored is null) throw;
                if (claimedApplied < stored.LastAckedSequence) throw ApiFault.Conflict();
                return stored.LastAckedSequence;
            }
            return claimedApplied;
        }
        if (claimedApplied < cursor.LastAckedSequence) throw ApiFault.Conflict();
        if (claimedApplied > cursor.LastAckedSequence)
        {
            cursor.LastAckedSequence = claimedApplied;
            cursor.UpdatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
            await db.SaveChangesAsync(token);
        }
        return cursor.LastAckedSequence;
    }

    public async Task AcknowledgeAsync(string userId, string sessionId, string clientId, long highestApplied, CancellationToken token)
    {
        if (highestApplied < -1) throw ApiFault.Invalid("Invalid acknowledgment sequence.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var session = await AuthorizedSession(db, userId, sessionId, token);
            if (highestApplied > session.LastSequence) throw ApiFault.Invalid("Cannot acknowledge an event sequence the server has not accepted.");
            var cursor = await db.SessionClientCursors.SingleOrDefaultAsync(x => x.SessionId == sessionId && x.UserId == userId && x.ClientId == clientId, token)
                ?? throw ApiFault.Conflict();
            if (highestApplied <= cursor.LastAckedSequence) return; // Concurrent duplicate/older ack is already durably satisfied.
            cursor.LastAckedSequence = highestApplied;
            cursor.UpdatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
            try { await db.SaveChangesAsync(token); return; }
            catch (DbUpdateConcurrencyException) when (attempt < 3) { }
        }
        throw ApiFault.Conflict();
    }

    public async Task<IReadOnlyList<WireFrame>> ReadAfterAsync(string userId, string sessionId, long sequence, int limit, CancellationToken token)
    {
        if (sequence < -1) throw ApiFault.Invalid("Invalid replay cursor.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedSession(db, userId, sessionId, token);
        var rows = await db.SessionEvents.AsNoTracking().Where(x => x.SessionId == sessionId && x.Sequence > sequence)
            .OrderBy(x => x.Sequence).Take(Math.Clamp(limit, 1, MaxReplayBatch)).ToListAsync(token);
        return rows.Select(ToFrame).ToArray();
    }

    public async Task<long> LastSequenceAsync(string userId, string sessionId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return (await AuthorizedSession(db, userId, sessionId, token)).LastSequence;
    }

    public async Task<IReadOnlyList<WireFrame>> RecentConflictsAsync(string userId, string sessionId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedSession(db, userId, sessionId, token);
        var rows = await db.SessionConflicts.AsNoTracking().Where(x => x.SessionId == sessionId)
            .OrderByDescending(x => x.CreatedUnixSeconds).ThenByDescending(x => x.Id).Take(256).ToListAsync(token);
        return rows.OrderBy(x => x.CreatedUnixSeconds).ThenBy(x => x.Id).Select(ToConflictFrame).ToArray();
    }

    public async Task<AppendSceneResult> AppendAsync(string userId, string sessionId, string eventId, SceneMutation mutation, CancellationToken token)
    {
        Validate(eventId, mutation);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var probe = await contexts.CreateDbContextAsync(token);
                var strategy = probe.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(() => AppendTransaction(userId, sessionId, eventId, mutation, token));
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 + Random.Shared.Next(10, 40) * (attempt + 1)), token);
            }
            catch (DbUpdateException error) when (IsUniqueViolation(error))
            {
                await using var verify = await contexts.CreateDbContextAsync(token);
                var existing = await verify.SessionEvents.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.EventId == eventId, token);
                if (existing is not null && existing.AuthorId == userId && existing.PayloadJson == JsonSerializer.Serialize(mutation, Json))
                    return new AppendSceneResult(ToFrame(existing), null);
                var existingConflict = await verify.SessionConflicts.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.RejectedEventId == eventId, token);
                if (existingConflict is not null && existingConflict.RejectedById == userId)
                    return new AppendSceneResult(null, ToConflictFrame(existingConflict));
                if (attempt < 5) continue; // A concurrent first write may have won the unique property-key race; retry to return its typed conflict.
                throw ApiFault.Conflict();
            }
        }
    }

    private async Task<AppendSceneResult> AppendTransaction(string userId, string sessionId, string eventId, SceneMutation mutation, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var session = await AuthorizedSession(db, userId, sessionId, token);
        if (session.SceneGuid.Length == 0 || mutation.SceneGuid != session.SceneGuid)
            throw new ApiFault(409, "scene_mismatch", "Scene event does not match the Unity Scene bound to this session.");
        var serialized = JsonSerializer.Serialize(mutation, Json);
        var priorConflict = await db.SessionConflicts.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.RejectedEventId == eventId, token);
        if (priorConflict is not null)
        {
            if (priorConflict.RejectedById != userId) throw ApiFault.Conflict();
            return new AppendSceneResult(null, ToConflictFrame(priorConflict));
        }
        var duplicate = await db.SessionEvents.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId && x.EventId == eventId, token);
        if (duplicate is not null)
        {
            if (duplicate.AuthorId != userId || duplicate.PayloadJson != serialized) throw ApiFault.Conflict();
            return new AppendSceneResult(ToFrame(duplicate), null);
        }

        var guarded = GuardedField(mutation);
        if (guarded is { } field)
        {
            var property = await db.SessionProperties.SingleOrDefaultAsync(x => x.SessionId == sessionId && x.ObjectId == mutation.ObjectId && x.ComponentType == field.ComponentType && x.PropertyName == field.PropertyName, token);
            var acceptedRevision = property?.Revision ?? 0;
            var acceptedValue = property?.AcceptedValue ?? string.Empty;
            if (mutation.BaseRevision != acceptedRevision)
            {
                var conflict = new SessionConflictRecord
                {
                    SessionId = sessionId, RejectedEventId = eventId, AcceptedById = property?.AcceptedById ?? string.Empty,
                    RejectedById = userId, ObjectId = mutation.ObjectId, ComponentType = field.ComponentType,
                    PropertyName = field.PropertyName, AcceptedRevision = acceptedRevision, AcceptedValue = acceptedValue,
                    RejectedValue = field.Value, RelatedSequence = session.LastSequence, CreatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds()
                };
                db.SessionConflicts.Add(conflict);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return new AppendSceneResult(null, ToConflictFrame(conflict));
            }
            if (property is null)
                db.SessionProperties.Add(new SessionPropertyRecord { SessionId = sessionId, ObjectId = mutation.ObjectId, ComponentType = field.ComponentType, PropertyName = field.PropertyName, Revision = 1, AcceptedValue = field.Value, AcceptedById = userId });
            else
            {
                property.Revision++;
                property.AcceptedValue = field.Value;
                property.AcceptedById = userId;
            }
        }

        session.LastSequence++;
        session.Revision++;
        var row = new SessionEventRecord
        {
            SessionId = sessionId, Sequence = session.LastSequence, EventId = eventId, AuthorId = userId,
            Kind = mutation.Kind, ObjectId = mutation.ObjectId, PayloadJson = serialized,
            UtcTimestampMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds()
        };
        db.SessionEvents.Add(row);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new AppendSceneResult(ToFrame(row), null);
    }

    private static async Task<CollaborationSessionRecord> AuthorizedSession(CollaborationDbContext db, string userId, string sessionId, CancellationToken token)
    {
        var session = await db.CollaborationSessions.Include(x => x.Project).SingleOrDefaultAsync(x => x.Id == sessionId && !x.IsClosed, token) ?? throw ApiFault.Missing();
        var member = await db.Memberships.AnyAsync(x => x.TeamId == session.Project.TeamId && x.UserId == userId, token);
        if (!member) throw ApiFault.Missing();
        return session;
    }

    private static void Validate(string eventId, SceneMutation mutation)
    {
        if (!StableId.TryParse(eventId, out _)) throw ApiFault.Invalid("Event id must be a StableId.");
        if (!Enum.IsDefined(typeof(SceneEventKind), mutation.Kind)) throw ApiFault.Invalid("Unsupported scene event kind.");
        if (mutation.SceneGuid.Length != 32 || !Guid.TryParseExact(mutation.SceneGuid, "N", out _))
            throw ApiFault.Invalid("Scene mutation requires a valid Unity Scene GUID.");
        if (mutation.DataJson.Length > 32_768 || mutation.SerializedValue.Length > 16_384 || mutation.ComponentType.Length > 128 || mutation.PropertyName.Length > 128 ||
            System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(mutation, Json)) > 64 * 1024)
            throw ApiFault.Invalid("Scene event payload exceeds its limit.");
        try { using var _ = JsonDocument.Parse(mutation.DataJson); }
        catch (JsonException) { throw ApiFault.Invalid("Scene event details must be valid JSON."); }
        var kind = (SceneEventKind)mutation.Kind;
        var sceneLevel = kind is SceneEventKind.SceneLoaded or SceneEventKind.SceneSaved;
        if (sceneLevel ? mutation.ObjectId.Length != 0 : !StableId.TryParse(mutation.ObjectId, out _))
            throw ApiFault.Invalid("Scene event has an invalid object id.");
        if (GuardedField(mutation) is not null && mutation.BaseRevision < 0)
            throw ApiFault.Invalid("Property, transform and rename changes require a non-negative base revision.");
        if (kind == SceneEventKind.PropertyChanged && (string.IsNullOrWhiteSpace(mutation.ComponentType) || string.IsNullOrWhiteSpace(mutation.PropertyName)))
            throw ApiFault.Invalid("Property changes require a component and property.");
        if (kind == SceneEventKind.TransformChanged && string.IsNullOrWhiteSpace(mutation.SerializedValue))
            throw ApiFault.Invalid("Transform changes require a serialized accepted value.");
        if (kind == SceneEventKind.GameObjectRenamed && string.IsNullOrWhiteSpace(mutation.SerializedValue))
            throw ApiFault.Invalid("Rename events require the new name as a serialized value.");
    }

    private static (string ComponentType, string PropertyName, string Value)? GuardedField(SceneMutation mutation) => (SceneEventKind)mutation.Kind switch
    {
        SceneEventKind.PropertyChanged => (mutation.ComponentType, mutation.PropertyName, mutation.SerializedValue),
        SceneEventKind.TransformChanged => ("Transform", "localTransform", mutation.SerializedValue),
        SceneEventKind.GameObjectRenamed => ("GameObject", "name", mutation.SerializedValue),
        _ => null
    };

    private static WireFrame ToConflictFrame(SessionConflictRecord row) => new()
    {
        Kind = FrameKind.Conflict, FrameId = row.Id, SessionId = row.SessionId, AuthorId = row.RejectedById,
        UtcTimestamp = DateTimeOffset.FromUnixTimeSeconds(row.CreatedUnixSeconds), Sequence = row.RelatedSequence,
        PayloadJson = JsonSerializer.Serialize(new SceneConflictPayload
        {
            ObjectId = row.ObjectId, ComponentType = row.ComponentType, PropertyName = row.PropertyName,
            AcceptedRevision = row.AcceptedRevision, AcceptedValue = row.AcceptedValue, RejectedValue = row.RejectedValue,
            AcceptedById = row.AcceptedById, RejectedById = row.RejectedById, RejectedEventId = row.RejectedEventId
        }, Json)
    };

    private static WireFrame ToFrame(SessionEventRecord row) => new()
    {
        Kind = FrameKind.SceneEvent, FrameId = row.EventId, SessionId = row.SessionId, AuthorId = row.AuthorId,
        UtcTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(row.UtcTimestampMilliseconds),
        Sequence = row.Sequence, PayloadJson = row.PayloadJson
    };

    private static bool IsUniqueViolation(DbUpdateException error) =>
        error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } ||
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
