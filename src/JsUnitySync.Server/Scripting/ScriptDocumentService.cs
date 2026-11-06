using System.Text;
using JsUnitySync.Contracts.Scripting;
using JsUnitySync.Domain.Ids;
using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JsUnitySync.Server.Scripting;

/// <summary>Stores collaborative C# documents separately from developers' local Unity files.</summary>
public sealed class ScriptDocumentService(IDbContextFactory<CollaborationDbContext> contexts, TimeProvider clock)
{
    public const int MaxDocumentBytes = 1_048_576;
    private const int RetainedVersionsPerFile = 100;

    public async Task<IReadOnlyList<ScriptDocumentSummary>> ListAsync(string userId, string projectId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedProject(db, userId, projectId, token);
        return await db.ScriptDocuments.AsNoTracking().Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Path).Take(200)
            .Select(x => new ScriptDocumentSummary { ProjectId = x.ProjectId, Path = x.Path, Revision = x.Revision, UpdatedById = x.UpdatedById, UpdatedUnixSeconds = x.UpdatedUnixSeconds })
            .ToListAsync(token);
    }

    public async Task<ScriptDocumentSummary> GetAsync(string userId, string projectId, string path, CancellationToken token)
    {
        path = ValidatePath(path);
        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedProject(db, userId, projectId, token);
        var document = await db.ScriptDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Path == path, token)
            ?? throw ApiFault.Missing();
        return ToSummary(document);
    }

    public async Task<ScriptDocumentSummary> SaveAsync(string userId, string projectId, string path, SaveScriptDocumentRequest request, CancellationToken token)
    {
        path = ValidatePath(path);
        if (request.ExpectedRevision < 0 || request.Content is null || request.Content.Contains('\0') || Encoding.UTF8.GetByteCount(request.Content) > MaxDocumentBytes)
            throw ApiFault.Invalid("Script content or expected revision is invalid or exceeds the 1 MiB document limit.");
        if (!StableId.TryParse(projectId, out _)) throw ApiFault.Missing();

        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedProject(db, userId, projectId, token);
        var document = await db.ScriptDocuments.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Path == path, token);
        var revision = document?.Revision ?? 0;
        if (revision != request.ExpectedRevision) throw ApiFault.Conflict();
        if (document is null)
        {
            document = new ScriptDocumentRecord { ProjectId = projectId, Path = path, Content = request.Content, Revision = 1 };
            db.ScriptDocuments.Add(document);
        }
        else
        {
            document.Content = request.Content;
            document.Revision++;
        }
        document.UpdatedById = userId;
        document.UpdatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
        db.ProjectFileVersions.Add(new ProjectFileVersionRecord
        {
            ProjectId = projectId, Kind = "script", Path = path, Revision = document.Revision,
            Content = document.Content, Length = System.Text.Encoding.UTF8.GetByteCount(document.Content),
            SavedById = userId, SavedUnixSeconds = document.UpdatedUnixSeconds
        });
        var oldVersions = await db.ProjectFileVersions.Where(x => x.ProjectId == projectId && x.Kind == "script" && x.Path == path)
            .OrderByDescending(x => x.Revision).Skip(RetainedVersionsPerFile - 1).ToListAsync(token);
        db.ProjectFileVersions.RemoveRange(oldVersions);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw ApiFault.Conflict(); }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } or Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { throw ApiFault.Conflict(); }
        return ToSummary(document);
    }

    public static string ValidatePath(string? path)
    {
        var value = path?.Trim().Replace('\\', '/') ?? "";
        if (value.Length is < 10 or > 512 || value.StartsWith('/') || value.Contains(':') ||
            !value.StartsWith("Assets/", StringComparison.Ordinal) || !value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            value.Split('/').Any(segment => segment is "" or "." or "..") || value.Any(char.IsControl))
            throw ApiFault.Invalid("Script paths must be relative C# files under the Unity project's Assets folder.");
        return value;
    }

    private static async Task AuthorizedProject(CollaborationDbContext db, string userId, string projectId, CancellationToken token)
    {
        var project = await db.Projects.Include(x => x.Team).SingleOrDefaultAsync(x => x.Id == projectId && !x.IsArchived, token)
            ?? throw ApiFault.Missing();
        var member = await db.Memberships.AnyAsync(x => x.TeamId == project.TeamId && x.UserId == userId, token);
        if (!member) throw ApiFault.Missing();
    }

    private static ScriptDocumentSummary ToSummary(ScriptDocumentRecord x) => new()
    {
        ProjectId = x.ProjectId, Path = x.Path, Content = x.Content, Revision = x.Revision,
        UpdatedById = x.UpdatedById, UpdatedUnixSeconds = x.UpdatedUnixSeconds
    };
}
