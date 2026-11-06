using System.Text;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Domain.Ids;
using JsUnitySync.Server.Assets;
using JsUnitySync.Server.Persistence;
using JsUnitySync.Server.Scripting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JsUnitySync.Server.Projects;

/// <summary>Bounded project file history; restores append a revision and never overwrite local Unity files.</summary>
public sealed class ProjectHistoryService(IDbContextFactory<CollaborationDbContext> contexts, TimeProvider clock, IConfiguration configuration)
{
    private const int RetainedVersionsPerFile = 100;
    private readonly string _assetRoot = Path.GetFullPath(configuration["AssetSync:RootPath"] ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsUnitySync", "server", "assets"));

    public async Task<IReadOnlyList<ProjectFileVersionSummary>> ListAsync(string userId, string projectId, string? kind, string? path, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedProject(db, userId, projectId, token);
        var query = db.ProjectFileVersions.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            kind = NormalizeKind(kind);
            query = query.Where(x => x.Kind == kind);
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (kind is null) throw ApiFault.Invalid("A history kind is required when filtering by path.");
            path = kind == "asset" ? ProjectAssetService.ValidatePath(path) : ScriptDocumentService.ValidatePath(path);
            query = query.Where(x => x.Path == path);
        }
        var rows = await query.OrderByDescending(x => x.SavedUnixSeconds).ThenByDescending(x => x.Revision).Take(100).ToListAsync(token);
        return rows.Select(ToSummary).ToArray();
    }

    public async Task<ProjectFileVersionSummary> RestoreAsync(string userId, string projectId, RestoreProjectFileVersionRequest request, CancellationToken token)
    {
        var kind = NormalizeKind(request.Kind ?? "");
        var path = kind == "asset" ? ProjectAssetService.ValidatePath(request.Path) : ScriptDocumentService.ValidatePath(request.Path);
        if (request.SourceRevision < 1 || request.ExpectedCurrentRevision < 1 || !StableId.TryParse(projectId, out _))
            throw ApiFault.Invalid("A source and current file revision are required for recovery.");

        await using var db = await contexts.CreateDbContextAsync(token);
        var project = await AuthorizedProject(db, userId, projectId, token);
        var source = await db.ProjectFileVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Kind == kind && x.Path == path && x.Revision == request.SourceRevision, token)
            ?? throw ApiFault.Missing();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        ProjectFileVersionRecord recovered;
        if (kind == "script")
        {
            var document = await db.ScriptDocuments.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Path == path, token)
                ?? throw ApiFault.Missing();
            if (document.Revision != request.ExpectedCurrentRevision) throw ApiFault.Conflict();
            document.Content = source.Content;
            document.Revision++;
            document.UpdatedById = userId;
            document.UpdatedUnixSeconds = now;
            recovered = new ProjectFileVersionRecord
            {
                ProjectId = projectId, Kind = kind, Path = path, Revision = document.Revision, Content = document.Content,
                Length = Encoding.UTF8.GetByteCount(document.Content), SavedById = userId, SavedUnixSeconds = now
            };
        }
        else
        {
            var asset = await db.ProjectAssets.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Path == path, token)
                ?? throw ApiFault.Missing();
            if (asset.Revision != request.ExpectedCurrentRevision) throw ApiFault.Conflict();
            var activeBytes = await db.ProjectAssets.Where(x => x.ProjectId == projectId).SumAsync(x => (long?)x.Length, token) ?? 0;
            if (activeBytes - asset.Length + source.Length > ProjectAssetService.MaxProjectBytes)
                throw new ApiFault(413, "project_storage_limit", "Restoring this version would exceed the 512 MiB synchronized asset quota.");
            if (source.ContentHash.Length != 64 || source.ContentHash.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("Stored asset history contains an invalid content hash.");
            var blob = Path.Combine(_assetRoot, source.ContentHash[..2], source.ContentHash);
            if (!File.Exists(blob)) throw new InvalidOperationException("A historical asset blob is missing; metadata was preserved for operator recovery.");
            asset.ContentHash = source.ContentHash;
            asset.Length = source.Length;
            asset.Revision++;
            project.AssetCatalogRevision++;
            asset.UpdatedById = userId;
            asset.UpdatedUnixSeconds = now;
            recovered = new ProjectFileVersionRecord
            {
                ProjectId = projectId, Kind = kind, Path = path, Revision = asset.Revision, ContentHash = asset.ContentHash,
                Length = asset.Length, SavedById = userId, SavedUnixSeconds = now
            };
        }
        db.ProjectFileVersions.Add(recovered);
        var oldVersions = await db.ProjectFileVersions.Where(x => x.ProjectId == projectId && x.Kind == kind && x.Path == path)
            .OrderByDescending(x => x.Revision).Skip(RetainedVersionsPerFile - 1).ToListAsync(token);
        db.ProjectFileVersions.RemoveRange(oldVersions);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw ApiFault.Conflict(); }
        return ToSummary(recovered);
    }

    private static string NormalizeKind(string value) => value?.Trim().ToLowerInvariant() switch
    {
        "script" => "script",
        "asset" => "asset",
        _ => throw ApiFault.Invalid("History kind must be script or asset.")
    };

    private static async Task<ProjectRecord> AuthorizedProject(CollaborationDbContext db, string userId, string projectId, CancellationToken token)
    {
        if (!StableId.TryParse(projectId, out _)) throw ApiFault.Missing();
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && !x.IsArchived, token) ?? throw ApiFault.Missing();
        if (!await db.Memberships.AnyAsync(x => x.TeamId == project.TeamId && x.UserId == userId, token)) throw ApiFault.Missing();
        return project;
    }

    private static ProjectFileVersionSummary ToSummary(ProjectFileVersionRecord x) => new()
    {
        Id = x.Id, Kind = x.Kind, Path = x.Path, Revision = x.Revision, ContentHash = x.ContentHash,
        Length = x.Length, SavedById = x.SavedById, SavedUnixSeconds = x.SavedUnixSeconds
    };
}
