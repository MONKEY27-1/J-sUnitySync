using System.Security.Claims;
using JsUnitySync.Contracts.Teams;
using JsUnitySync.Domain.Identity;
using JsUnitySync.Server.Accounts;
using JsUnitySync.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace JsUnitySync.Server.Teams;

public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me/teams", async (ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Ok(await service.ListTeams(user.UserId(), token))).RequireAuthorization();
        app.MapPost("/api/v1/teams", async (CreateTeamRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Created("/api/v1/me/teams", await service.CreateTeam(user.UserId(), request, token))).RequireAuthorization();
        app.MapGet("/api/v1/teams/{teamId}/members", async (string teamId, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Ok(await service.Members(user.UserId(), teamId, token))).RequireAuthorization();
        app.MapPut("/api/v1/teams/{teamId}/members/{memberId}/role", async (string teamId, string memberId, ChangeMemberRoleRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Ok(await service.ChangeRole(user.UserId(), teamId, memberId, request, token))).RequireAuthorization();
        app.MapPost("/api/v1/teams/{teamId}/invitations", async (string teamId, InviteTeamMemberRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Ok(await service.Invite(user.UserId(), teamId, request, token))).RequireAuthorization();
        app.MapGet("/api/v1/me/invitations", async (ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Ok(await service.Invitations(user.UserId(), token))).RequireAuthorization();
        app.MapPost("/api/v1/me/invitations/{id}/accept", async (string id, InvitationResponseRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
        { await service.Respond(user.UserId(), id, request, true, token); return Results.NoContent(); }).RequireAuthorization();
        app.MapPost("/api/v1/me/invitations/{id}/decline", async (string id, InvitationResponseRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
        { await service.Respond(user.UserId(), id, request, false, token); return Results.NoContent(); }).RequireAuthorization();
        app.MapGet("/api/v1/me/friends", async (ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Ok(await service.Friends(user.UserId(), token))).RequireAuthorization();
        app.MapGet("/api/v1/me/friend-requests", async (ClaimsPrincipal user, TeamService service, CancellationToken token) =>
            Results.Ok(await service.FriendRequests(user.UserId(), token))).RequireAuthorization();
        app.MapPost("/api/v1/me/friend-requests", async (FriendRequestRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
        { await service.RequestFriend(user.UserId(), request, token); return Results.Accepted(); }).RequireAuthorization();
        app.MapPost("/api/v1/me/friend-requests/{id}/accept", async (string id, InvitationResponseRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
        { await service.RespondFriend(user.UserId(), id, request, true, token); return Results.NoContent(); }).RequireAuthorization();
        app.MapPost("/api/v1/me/friend-requests/{id}/decline", async (string id, InvitationResponseRequest request, ClaimsPrincipal user, TeamService service, CancellationToken token) =>
        { await service.RespondFriend(user.UserId(), id, request, false, token); return Results.NoContent(); }).RequireAuthorization();
    }
}
