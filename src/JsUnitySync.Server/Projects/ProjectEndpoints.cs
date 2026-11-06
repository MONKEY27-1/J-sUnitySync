using System.Security.Claims;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JsUnitySync.Server.Projects;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/teams/{teamId}/projects", async (string teamId, bool? includeArchived, ClaimsPrincipal user, ProjectService service, CancellationToken token) =>
            Results.Ok(await service.List(user.UserId(), teamId, includeArchived ?? false, token))).RequireAuthorization();
        app.MapPost("/api/v1/teams/{teamId}/projects", async (string teamId, CreateProjectRequest request, ClaimsPrincipal user, ProjectService service, CancellationToken token) =>
            Results.Created($"/api/v1/teams/{teamId}/projects", await service.Create(user.UserId(), teamId, request, token))).RequireAuthorization();
        app.MapPut("/api/v1/teams/{teamId}/projects/{projectId}", async (string teamId, string projectId, UpdateProjectRequest request, ClaimsPrincipal user, ProjectService service, CancellationToken token) =>
            Results.Ok(await service.Update(user.UserId(), teamId, projectId, request, token))).RequireAuthorization();
        app.MapPost("/api/v1/teams/{teamId}/projects/{projectId}/archive", async (string teamId, string projectId, ArchiveProjectRequest request, ClaimsPrincipal user, ProjectService service, CancellationToken token) =>
        { await service.Archive(user.UserId(), teamId, projectId, request, true, token); return Results.NoContent(); }).RequireAuthorization();
        app.MapPost("/api/v1/teams/{teamId}/projects/{projectId}/restore", async (string teamId, string projectId, ArchiveProjectRequest request, ClaimsPrincipal user, ProjectService service, CancellationToken token) =>
        { await service.Archive(user.UserId(), teamId, projectId, request, false, token); return Results.NoContent(); }).RequireAuthorization();
    }
}
