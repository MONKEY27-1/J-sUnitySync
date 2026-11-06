using System.Net.Mail;
using System.Security.Cryptography;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Server.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace JsUnitySync.Server.Accounts;

public sealed class AccountService(CollaborationDbContext db, IPasswordHasher<UserAccount> passwords, TimeProvider clock)
{
    public static string NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim() ?? "";
        if (trimmed.Length > 254 || !MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed || !trimmed.Contains('@'))
            throw ApiFault.Invalid("Enter a valid email address.");
        return trimmed.ToUpperInvariant();
    }

    public static string Name(string? name, int maximum = 64)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length < 2 || trimmed.Length > maximum || trimmed.Any(char.IsControl))
            throw ApiFault.Invalid($"Name must contain 2–{maximum} characters, without control characters.");
        return trimmed;
    }

    public async Task<AuthResponse> Register(RegisterRequest request, CancellationToken token)
    {
        var normalized = NormalizeEmail(request.Email);
        if (request.Password is null || request.Password.Length < 12 || request.Password.Length > 128)
            throw ApiFault.Invalid("Use a password containing 12–128 characters.");
        var user = new UserAccount { Email = request.Email.Trim(), NormalizedEmail = normalized, DisplayName = Name(request.DisplayName) };
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, token))
            throw new ApiFault(409, "registration_unavailable", "Unable to register this email. Try signing in instead.");
        user.PasswordHash = passwords.HashPassword(user, request.Password);
        db.Users.Add(user);
        return await Issue(user, token);
    }

    public async Task<AuthResponse> Login(LoginRequest request, CancellationToken token)
    {
        string normalized;
        try { normalized = NormalizeEmail(request.Email); }
        catch (ApiFault) { throw InvalidCredentials(); }
        if (request.Password is null || request.Password.Length > 128) throw InvalidCredentials();
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, token);
        var verification = passwords.VerifyHashedPassword(user ?? new UserAccount(), user?.PasswordHash ?? DummyHash.Value, request.Password);
        if (user is null || verification == PasswordVerificationResult.Failed) throw InvalidCredentials();
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwords.HashPassword(user, request.Password);
            user.Revision++;
        }
        return await Issue(user, token);
    }

    private async Task<AuthResponse> Issue(UserAccount user, CancellationToken cancellationToken)
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = clock.GetUtcNow().AddHours(12);
        // Bound retained session metadata and device count per account without storing plaintext bearer values.
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var existingSessions = await db.Sessions.Where(x => x.UserId == user.Id).OrderBy(x => x.ExpiresUnixSeconds).ToListAsync(cancellationToken);
        db.Sessions.RemoveRange(existingSessions.Where(x => x.ExpiresUnixSeconds <= now));
        var active = existingSessions.Where(x => x.ExpiresUnixSeconds > now).ToArray();
        if (active.Length >= 10)
            db.Sessions.RemoveRange(active.Take(active.Length - 9));
        db.Sessions.Add(new UserSession { UserId = user.Id, User = user, TokenHash = HashToken(raw), ExpiresUnixSeconds = expires.ToUnixTimeSeconds() });
        await db.SaveChangesAsync(cancellationToken);
        return new AuthResponse { Token = raw, ExpiresAt = expires, Profile = Profile(user) };
    }

    public async Task<UserProfile> UpdateProfile(string id, ProfileUpdateRequest request, CancellationToken token)
    {
        var user = await db.Users.SingleAsync(x => x.Id == id, token);
        if (user.Revision != request.ExpectedRevision) throw ApiFault.Conflict();
        user.DisplayName = Name(request.DisplayName);
        user.Revision++;
        await db.SaveChangesAsync(token);
        return Profile(user);
    }

    public static UserProfile Profile(UserAccount user) => new() { Id = user.Id, Email = user.Email, DisplayName = user.DisplayName, Revision = user.Revision };
    public static string HashToken(string raw) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
    private static ApiFault InvalidCredentials() => new(401, "invalid_credentials", "Email or password is incorrect.");

    private static class DummyHash
    {
        public static readonly string Value = new PasswordHasher<UserAccount>(Microsoft.Extensions.Options.Options.Create(
            new PasswordHasherOptions { IterationCount = 210_000 })).HashPassword(new UserAccount(), "timing-only-not-an-account");
    }
}
