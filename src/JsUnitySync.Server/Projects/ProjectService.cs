using JsUnitySync.Contracts.Projects;
using JsUnitySync.Domain.Identity;
using JsUnitySync.Server.Accounts;
using JsUnitySync.Server.Persistence;
using JsUnitySync.Server.Teams;
using Microsoft.EntityFrameworkCore;

namespace JsUnitySync.Server.Projects;

public sealed class ProjectService(CollaborationDbContext db, TeamService teams, TimeProvider clock)
{
    public async Task<IReadOnlyList<ProjectSummary>> List(string userId, string teamId, bool includeArchived, CancellationToken token)
    {
        await teams.RequireRole(userId, teamId, TeamRole.Member, token);
        return await db.Projects.AsNoTracking().Where(x => x.TeamId == teamId && (includeArchived || !x.IsArchived))
            .OrderBy(x => x.Name).Select(x => new ProjectSummary { Id = x.Id, TeamId = x.TeamId, TeamName = x.Team.Name, Name = x.Name, CreatedById = x.CreatedById, Revision = x.Revision, IsArchived = x.IsArchived, CreatedUnixSeconds = x.CreatedUnixSeconds }).ToListAsync(token);
    }

    public async Task<ProjectSummary> Create(string userId, string teamId, CreateProjectRequest request, CancellationToken token)
    {
        await teams.RequireRole(userId, teamId, TeamRole.Lead, token);
        var name = AccountService.Name(request.Name, 96);
        var project = new ProjectRecord { TeamId = teamId, Name = name, NormalizedName = name.ToUpperInvariant(), CreatedById = userId, CreatedUnixSeconds = clock.GetUtcNow().ToUnixTimeSeconds() };
        db.Projects.Add(project);
        await db.SaveChangesAsync(token);
        return await db.Projects.AsNoTracking().Where(x => x.Id == project.Id).Select(x => new ProjectSummary { Id = x.Id, TeamId = x.TeamId, TeamName = x.Team.Name, Name = x.Name, CreatedById = x.CreatedById, Revision = x.Revision, IsArchived = x.IsArchived, CreatedUnixSeconds = x.CreatedUnixSeconds }).SingleAsync(token);
    }

    public async Task<ProjectSummary> Update(string userId, string teamId, string projectId, UpdateProjectRequest request, CancellationToken token)
    {
        await teams.RequireRole(userId, teamId, TeamRole.Lead, token);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && x.TeamId == teamId, token) ?? throw ApiFault.Missing();
        if (project.IsArchived || project.Revision != request.ExpectedRevision) throw ApiFault.Conflict();
        project.Name = AccountService.Name(request.Name, 96);
        project.NormalizedName = project.Name.ToUpperInvariant();
        project.Revision++;
        await db.SaveChangesAsync(token);
        return await db.Projects.AsNoTracking().Where(x => x.Id == project.Id).Select(x => new ProjectSummary { Id = x.Id, TeamId = x.TeamId, TeamName = x.Team.Name, Name = x.Name, CreatedById = x.CreatedById, Revision = x.Revision, IsArchived = x.IsArchived, CreatedUnixSeconds = x.CreatedUnixSeconds }).SingleAsync(token);
    }

    public async Task Archive(string userId, string teamId, string projectId, ArchiveProjectRequest request, bool archive, CancellationToken token)
    {
        await teams.RequireRole(userId, teamId, TeamRole.Lead, token);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId && x.TeamId == teamId, token) ?? throw ApiFault.Missing();
        if (project.IsArchived == archive || project.Revision != request.ExpectedRevision) throw ApiFault.Conflict();
        project.IsArchived = archive;
        project.Revision++;
        await db.SaveChangesAsync(token);
    }
}
