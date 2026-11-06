using System.Security.Claims;
using System.Text.Encodings.Web;
using JsUnitySync.Server.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JsUnitySync.Server.Accounts;

public sealed class BearerAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, CollaborationDbContext db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.Length == 0) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.Fail("Bearer token required.");
        var raw = header[7..];
        if (raw.Length != 64 || raw.Any(c => !Uri.IsHexDigit(c))) return AuthenticateResult.Fail("Invalid session.");
        var digest = AccountService.HashToken(raw);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == digest && x.ExpiresUnixSeconds > now, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Session expired or revoked.");
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, session.UserId), new Claim("session_id", session.Id) };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}
