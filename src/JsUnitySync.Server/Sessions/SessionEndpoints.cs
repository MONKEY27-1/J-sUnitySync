using System.Security.Claims;
using JsUnitySync.Contracts.Realtime;
using JsUnitySync.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JsUnitySync.Server.Sessions;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/projects/{projectId}/sessions", async (string projectId, CreateSessionRequest request, ClaimsPrincipal user, CollaborationSessionService sessions, CancellationToken token) =>
            Results.Created($"/api/v1/projects/{projectId}/sessions", await sessions.CreateAsync(user.UserId(), projectId, request, token))).RequireAuthorization();
        app.MapGet("/api/v1/projects/{projectId}/sessions", async (string projectId, ClaimsPrincipal user, CollaborationSessionService sessions, CancellationToken token) =>
            Results.Ok(await sessions.ListAsync(user.UserId(), projectId, token))).RequireAuthorization();
        app.MapGet("/api/v1/sessions/{sessionId}", async (string sessionId, ClaimsPrincipal user, CollaborationSessionService sessions, CancellationToken token) =>
            Results.Ok(await sessions.GetAuthorizedAsync(user.UserId(), sessionId, token))).RequireAuthorization();
    }
}
