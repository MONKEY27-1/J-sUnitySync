using System.Security.Cryptography;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JsUnitySync.Server.Assets;

/// <summary>Content-addressed project asset storage. It never accesses a developer's Unity folder.</summary>
public sealed class ProjectAssetService(IDbContextFactory<CollaborationDbContext> contexts, TimeProvider clock, IConfiguration configuration)
{
    public const long MaxAssetBytes = 32L * 1024 * 1024;
    public const long MaxProjectBytes = 512L * 1024 * 1024;
    public const int MaxProjectAssets = 2000;
    private const int RetainedVersionsPerFile = 100;
    private readonly string _root = Path.GetFullPath(configuration["AssetSync:RootPath"] ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JsUnitySync", "server", "assets"));

    public async Task<IReadOnlyList<ProjectAssetSummary>> ListAsync(string userId, string projectId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedProject(db, userId, projectId, token);
        return await db.ProjectAssets.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Path).Take(2000)
            .Select(x => ToSummary(x)).ToListAsync(token);
    }

    public async Task<ProjectAssetSummary> PutAsync(string userId, string projectId, string path, long expectedRevision, Stream content, CancellationToken token)
    {
        path = ValidatePath(path);
        if (expectedRevision < 0) throw ApiFault.Invalid("Asset revision must be non-negative.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var project = await AuthorizedProject(db, userId, projectId, token);
        var record = await db.ProjectAssets.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Path == path, token);
        if ((record?.Revision ?? 0) != expectedRevision) throw ApiFault.Conflict();
        if (record is null && await db.ProjectAssets.CountAsync(x => x.ProjectId == projectId, token) >= MaxProjectAssets)
            throw new ApiFault(413, "project_asset_limit", "Project has reached its 2,000 asset limit.");

        Directory.CreateDirectory(_root);
        var temporaryPath = Path.Combine(_root, $"{Guid.NewGuid():N}.upload");
        string hash;
        long length = 0;
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await content.ReadAsync(buffer, token);
                    if (read == 0) break;
                    length += read;
                    if (length > MaxAssetBytes) throw new ApiFault(413, "asset_too_large", "Asset exceeds the 32 MiB limit.");
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                await output.FlushAsync(token);
            }
            var existingBytes = await db.ProjectAssets.Where(x => x.ProjectId == projectId).SumAsync(x => (long?)x.Length, token) ?? 0;
            if (existingBytes - (record?.Length ?? 0) + length > MaxProjectBytes)
                throw new ApiFault(413, "project_storage_limit", "Project has reached its 512 MiB synchronized asset quota.");
            hash = Convert.ToHexStringLower(digest.GetHashAndReset());
            var destinationDirectory = Path.Combine(_root, hash[..2]);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, hash);
            if (File.Exists(destination)) File.Delete(temporaryPath);
            else File.Move(temporaryPath, destination);

            if (record is null)
            {
                record = new ProjectAssetRecord { ProjectId = projectId, Path = path, Revision = 1 };
                db.ProjectAssets.Add(record);
            }
            else record.Revision++;
            record.ContentHash = hash;
            record.Length = length;
            record.UpdatedById = userId;
            record.UpdatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
            project.AssetCatalogRevision++;
            db.ProjectFileVersions.Add(new ProjectFileVersionRecord
            {
                ProjectId = projectId, Kind = "asset", Path = path, Revision = record.Revision,
                ContentHash = hash, Length = length, SavedById = userId, SavedUnixSeconds = record.UpdatedUnixSeconds
            });
            var oldVersions = await db.ProjectFileVersions.Where(x => x.ProjectId == projectId && x.Kind == "asset" && x.Path == path)
                .OrderByDescending(x => x.Revision).Skip(RetainedVersionsPerFile - 1).ToListAsync(token);
            db.ProjectFileVersions.RemoveRange(oldVersions);
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateConcurrencyException) { throw ApiFault.Conflict(); }
            catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } or Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }) { throw ApiFault.Conflict(); }
            return ToSummary(record);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<(ProjectAssetSummary Summary, FileStream Content)> OpenAsync(string userId, string projectId, string path, CancellationToken token)
    {
        path = ValidatePath(path);
        await using var db = await contexts.CreateDbContextAsync(token);
        await AuthorizedProject(db, userId, projectId, token);
        var record = await db.ProjectAssets.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Path == path, token)
            ?? throw ApiFault.Missing();
        if (record.ContentHash.Length != 64 || record.ContentHash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Stored asset metadata contains an invalid content hash.");
        var contentPath = Path.Combine(_root, record.ContentHash[..2], record.ContentHash);
        try
        {
            var file = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != record.Length) { file.Dispose(); throw new InvalidOperationException("Stored asset length does not match its verified metadata."); }
            return (ToSummary(record), file);
        }
        catch (FileNotFoundException) { throw new InvalidOperationException("Stored asset content is missing; metadata was preserved for operator recovery."); }
    }

    public static string ValidatePath(string? path)
    {
        var value = path?.Trim().Replace('\\', '/') ?? "";
        if (value.Length is < 7 or > 512 || !value.StartsWith("Assets/", StringComparison.Ordinal) || value.StartsWith('/') || value.Contains(':') ||
            value.EndsWith('/') || value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || value.Split('/').Any(segment => segment is "" or "." or "..") || value.Any(char.IsControl))
            throw ApiFault.Invalid("Asset paths must be relative, non-C# files under the Unity project's Assets folder.");
        return value;
    }

    private static async Task<ProjectRecord> AuthorizedProject(CollaborationDbContext db, string userId, string projectId, CancellationToken token)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && !x.IsArchived, token) ?? throw ApiFault.Missing();
        if (!await db.Memberships.AnyAsync(x => x.TeamId == project.TeamId && x.UserId == userId, token)) throw ApiFault.Missing();
        return project;
    }

    private static ProjectAssetSummary ToSummary(ProjectAssetRecord x) => new()
    {
        ProjectId = x.ProjectId, Path = x.Path, ContentHash = x.ContentHash, Length = x.Length,
        Revision = x.Revision, UpdatedById = x.UpdatedById, UpdatedUnixSeconds = x.UpdatedUnixSeconds
    };
}
