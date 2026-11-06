using System.Security.Claims;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace JsUnitySync.Server.Accounts;

public static class AccountEndpoints
{
    public static string UserId(this ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").RequireRateLimiting("auth");
        auth.MapPost("/register", async (RegisterRequest request, AccountService service, CancellationToken token) =>
            Results.Ok(await service.Register(request, token))).AllowAnonymous();
        auth.MapPost("/login", async (LoginRequest request, AccountService service, CancellationToken token) =>
            Results.Ok(await service.Login(request, token))).AllowAnonymous();
        app.MapPost("/api/v1/auth/logout", async (ClaimsPrincipal user, CollaborationDbContext db, CancellationToken token) =>
        {
            await db.Sessions.Where(x => x.Id == user.FindFirstValue("session_id")).ExecuteDeleteAsync(token);
            return Results.NoContent();
        }).RequireAuthorization();
        app.MapGet("/api/v1/me", async (ClaimsPrincipal user, CollaborationDbContext db, CancellationToken token) =>
            Results.Ok(AccountService.Profile(await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.UserId(), token))))
            .RequireAuthorization();
        app.MapPut("/api/v1/me", async (ProfileUpdateRequest request, ClaimsPrincipal user, AccountService service, CancellationToken token) =>
            Results.Ok(await service.UpdateProfile(user.UserId(), request, token))).RequireAuthorization();
    }
}
