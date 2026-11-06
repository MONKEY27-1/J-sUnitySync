using System.Security.Claims;
using JsUnitySync.Contracts.Projects;
using JsUnitySync.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JsUnitySync.Server.Projects;

public static class ProjectHistoryEndpoints
{
    public static void MapProjectHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/projects/{projectId}/history", async (string projectId, string? kind, string? path, ClaimsPrincipal user, ProjectHistoryService history, CancellationToken token) =>
            Results.Ok(await history.ListAsync(user.UserId(), projectId, kind, path, token))).RequireAuthorization();
        app.MapPost("/api/v1/projects/{projectId}/history/restore", async (string projectId, RestoreProjectFileVersionRequest request, ClaimsPrincipal user, ProjectHistoryService history, CancellationToken token) =>
            Results.Ok(await history.RestoreAsync(user.UserId(), projectId, request, token))).RequireAuthorization();
    }
}
