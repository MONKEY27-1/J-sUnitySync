using System.Net;
using System.Net.Http.Json;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Server.Persistence;
using JsUnitySync.Server.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JsUnitySync.Server.Tests;

public sealed class AuthenticationTests
{
    private static async Task<AuthResponse> Register(ApiFixture fixture, string email = "dev@example.com", string name = "Dev One", string password = "correct-horse-battery")
    {
        var response = await fixture.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = email, DisplayName = name, Password = password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task Register_NormalizesEmailAndStoresOnlyPasswordHashAndTokenDigest()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        var signedIn = await Register(fixture, "  Dev@Example.com ");
        Assert.Equal("Dev@Example.com", signedIn.Profile.Email);
        Assert.Equal(1, signedIn.Profile.Revision);
        Assert.Equal(64, signedIn.Token.Length);
        fixture.Authenticate(signedIn.Token);
        var current = await fixture.Client.GetFromJsonAsync<UserProfile>("api/v1/me");
        Assert.Equal(signedIn.Profile.Id, current!.Id);
        Assert.Equal(signedIn.Profile.Email, current.Email);
        Assert.Equal(signedIn.Profile.DisplayName, current.DisplayName);
        Assert.Equal(signedIn.Profile.Revision, current.Revision);
        using var scope = fixture.HostServices().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CollaborationDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.DoesNotContain("correct-horse-battery", user.PasswordHash);
        Assert.NotEqual(signedIn.Token, await db.Sessions.Select(x => x.TokenHash).SingleAsync());
        Assert.Equal(AccountService.HashToken(signedIn.Token), await db.Sessions.Select(x => x.TokenHash).SingleAsync());
    }

    [Fact]
    public async Task Register_DuplicateCaseInsensitiveEmailDoesNotRevealExistingAccount()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        await Register(fixture);
        var response = await fixture.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = "DEV@example.com", DisplayName = "Other Name", Password = "another-secure-password" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("registration_unavailable", error!.Code);
        Assert.DoesNotContain("exists", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dev@example.com", "short")]
    [InlineData("not-an-email", "correct-horse-battery")]
    [InlineData("dev@example.com", "correct-horse-battery")]
    public async Task Register_RejectsInvalidPayloadWithoutCreatingAccount(string email, string password)
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        var response = await fixture.Client.PostAsJsonAsync("api/v1/auth/register", new RegisterRequest { Email = email, DisplayName = "D", Password = password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = fixture.HostServices().CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CollaborationDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task Login_WrongAndUnknownCredentialsHaveSamePublicError()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        await Register(fixture);
        var wrong = await fixture.Client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest { Email = "dev@example.com", Password = "wrong-password" });
        var missing = await fixture.Client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest { Email = "missing@example.com", Password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
        Assert.DoesNotContain("dev@example.com", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProtectedProfile_RequiresBearerAndLogoutRevokesOnlyCurrentSession()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        var first = await Register(fixture);
        var second = await fixture.Client.PostAsJsonAsync("api/v1/auth/login", new LoginRequest { Email = "dev@example.com", Password = "correct-horse-battery" });
        var secondSession = (await second.Content.ReadFromJsonAsync<AuthResponse>())!;
        fixture.Authenticate(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("api/v1/me")).StatusCode);
        fixture.Authenticate(first.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.PostAsync("api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("api/v1/me")).StatusCode);
        fixture.Authenticate(secondSession.Token);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Profile_UsesExpectedRevisionAndRejectsStaleWriteWithoutOverwrite()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        var session = await Register(fixture);
        fixture.Authenticate(session.Token);
        var update = await fixture.Client.PutAsJsonAsync("api/v1/me", new ProfileUpdateRequest { DisplayName = "Renamed", ExpectedRevision = 1 });
        Assert.Equal("Renamed", (await update.Content.ReadFromJsonAsync<UserProfile>())!.DisplayName);
        var stale = await fixture.Client.PutAsJsonAsync("api/v1/me", new ProfileUpdateRequest { DisplayName = "Stale overwrite", ExpectedRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Renamed", (await fixture.Client.GetFromJsonAsync<UserProfile>("api/v1/me"))!.DisplayName);
    }

    [Fact]
    public async Task ExpiredBearerIsUnauthorized()
    {
        await using var fixture = new ApiFixture(); await fixture.Start();
        var session = await Register(fixture);
        using (var scope = fixture.HostServices().CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CollaborationDbContext>();
            var row = await db.Sessions.SingleAsync(); row.ExpiresUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(); await db.SaveChangesAsync();
        }
        fixture.Authenticate(session.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("api/v1/me")).StatusCode);
    }
}

internal static class FixtureExtensions
{
    public static IServiceProvider HostServices(this ApiFixture fixture) => fixture.Services;
}
