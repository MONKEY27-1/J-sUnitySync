using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Domain.Ids;
using JsUnitySync.Server.Persistence;
using JsUnitySync.Server.Teams;
using Microsoft.EntityFrameworkCore;

namespace JsUnitySync.Server.Sessions;

public sealed class CollaborationSessionService(IDbContextFactory<CollaborationDbContext> contexts, TeamService teams, TimeProvider clock)
{
    public async Task<SessionSummary> CreateAsync(string userId, string projectId, CreateSessionRequest request, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId && !x.IsArchived, token)
            ?? throw ApiFault.Missing();
        await teams.RequireRole(userId, project.TeamId, JsUnitySync.Domain.Identity.TeamRole.Member, token);
        var session = new CollaborationSessionRecord
        {
            ProjectId = project.Id,
            Name = JsUnitySync.Server.Accounts.AccountService.Name(request.Name, 64),
            CreatedById = userId,
            CreatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds()
        };
        db.CollaborationSessions.Add(session);
        await db.SaveChangesAsync(token);
        return ToSummary(session, project.Name);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(string userId, string projectId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, token)
            ?? throw ApiFault.Missing();
        await teams.RequireRole(userId, project.TeamId, JsUnitySync.Domain.Identity.TeamRole.Member, token);
        return await db.CollaborationSessions.AsNoTracking().Where(x => x.ProjectId == projectId && !x.IsClosed)
            .OrderByDescending(x => x.CreatedUnixSeconds)
            .Select(x => new SessionSummary { Id = x.Id, ProjectId = x.ProjectId, ProjectName = x.Project.Name, Name = x.Name, CreatedById = x.CreatedById, CreatedUnixSeconds = x.CreatedUnixSeconds, LastSequence = x.LastSequence, SceneGuid = x.SceneGuid, SceneManifestHash = x.SceneManifestHash })
            .ToListAsync(token);
    }

    public async Task<(string SceneGuid, string ManifestHash)> BindSceneAsync(string userId, string sessionId, string sceneGuid, string manifestHash, CancellationToken token)
    {
        var normalized = sceneGuid?.Trim().ToLowerInvariant() ?? "";
        var normalizedHash = manifestHash?.Trim().ToLowerInvariant() ?? "";
        if (normalized.Length != 0 && (!Guid.TryParseExact(normalized, "N", out _) || normalizedHash.Length != 64 || normalizedHash.Any(c => !Uri.IsHexDigit(c))))
            throw ApiFault.Invalid("Unity Scene GUID and SHA-256 stable-object manifest hash are required for Editor clients.");
        if (normalized.Length == 0 && normalizedHash.Length != 0)
            throw ApiFault.Invalid("A scene manifest cannot be supplied without a Unity Scene GUID.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var session = await db.CollaborationSessions.Include(x => x.Project).SingleOrDefaultAsync(x => x.Id == sessionId && !x.IsClosed, token)
                ?? throw ApiFault.Missing();
            await teams.RequireRole(userId, session.Project.TeamId, JsUnitySync.Domain.Identity.TeamRole.Member, token);
            if (normalized.Length == 0) return (session.SceneGuid, session.SceneManifestHash);
            if (session.SceneGuid == normalized && session.SceneManifestHash == normalizedHash) return (session.SceneGuid, session.SceneManifestHash);
            if (session.SceneGuid.Length != 0) throw new ApiFault(409, "scene_mismatch", "This session is bound to a different Unity Scene or stable-object baseline. Sync the same shared scene before joining.");
            session.SceneGuid = normalized;
            session.SceneManifestHash = normalizedHash;
            session.Revision++;
            try { await db.SaveChangesAsync(token); return (normalized, normalizedHash); }
            catch (DbUpdateConcurrencyException) when (attempt < 4) { }
        }
        throw ApiFault.Conflict();
    }

    public async Task<SessionSummary> GetAuthorizedAsync(string userId, string sessionId, CancellationToken token)
    {
        if (!StableId.TryParse(sessionId, out _)) throw ApiFault.Missing();
        await using var db = await contexts.CreateDbContextAsync(token);
        var info = await db.CollaborationSessions.AsNoTracking().Where(x => x.Id == sessionId)
            .Select(x => new { Session = x, x.Project.TeamId, ProjectName = x.Project.Name }).SingleOrDefaultAsync(token)
            ?? throw ApiFault.Missing();
        await teams.RequireRole(userId, info.TeamId, JsUnitySync.Domain.Identity.TeamRole.Member, token);
        if (info.Session.IsClosed) throw ApiFault.Missing();
        return ToSummary(info.Session, info.ProjectName);
    }

    public static SessionSummary ToSummary(CollaborationSessionRecord session, string projectName) => new()
    {
        Id = session.Id, ProjectId = session.ProjectId, ProjectName = projectName, Name = session.Name, SceneGuid = session.SceneGuid, SceneManifestHash = session.SceneManifestHash,
        CreatedById = session.CreatedById, CreatedUnixSeconds = session.CreatedUnixSeconds, LastSequence = session.LastSequence
    };
}
