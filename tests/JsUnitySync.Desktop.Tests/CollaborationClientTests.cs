using System.Net;
using System.Text;
using JsUnitySync.Contracts.Accounts;
using JsUnitySync.Desktop.Core.Accounts;
using JsUnitySync.Desktop.Core.Networking;

namespace JsUnitySync.Desktop.Tests;

public sealed class CollaborationClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(reply(request));
        }
    }

    [Fact]
    public async Task Send_AttachesBearerOnlyToAuthenticatedRequests()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"u\",\"email\":\"e@x.com\",\"displayName\":\"User\",\"revision\":1}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var client = new CollaborationClient(http);
        await client.SendAsync<UserProfile>(HttpMethod.Post, "api/v1/auth/login", new { email = "e", password = "p" }, default, false);
        Assert.Null(handler.Requests[0].Headers.Authorization);
        client.SetSession(new StoredSession(client.ServerAddress, new string('A', 64), DateTimeOffset.UtcNow.AddHours(1)));
        await client.SendAsync<UserProfile>(HttpMethod.Get, "api/v1/me", null, default);
        Assert.Equal("Bearer", handler.Requests[1].Headers.Authorization!.Scheme);
    }

    [Theory]
    [InlineData("../auth/login")]
    [InlineData("api/v1/me?token=secret")]
    [InlineData("https://evil.example/api/v1/me")]
    public async Task Send_RejectsNonVersionedOrUntrustedRoutes(string route)
    {
        var client = new CollaborationClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("Should not send"))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync<object>(HttpMethod.Get, route, null, default));
    }

    [Fact]
    public async Task Send_AllowsOnlyTheArchivedProjectFilterQuery()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var client = new CollaborationClient(http);
        client.SetSession(new StoredSession(client.ServerAddress, new string('A', 64), DateTimeOffset.UtcNow.AddHours(1)));
        await client.SendAsync<object[]>(HttpMethod.Get, "api/v1/teams/01ARZ3NDEKTSV4RRFFQ69G5FAV/projects?includeArchived=false", null, default);
        Assert.Single(handler.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync<object>(HttpMethod.Get, "api/v1/teams/01ARZ3NDEKTSV4RRFFQ69G5FAV/projects?userId=secret", null, default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Send_AllowsOnlySafeRelativeCSharpDocumentQuery()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var client = new CollaborationClient(http);
        client.SetSession(new StoredSession(client.ServerAddress, new string('A', 64), DateTimeOffset.UtcNow.AddHours(1)));
        var route = $"api/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/scripts/document?path={Uri.EscapeDataString("Assets/Scripts/Ship.cs")}";
        await client.SendAsync<object>(HttpMethod.Get, route, null, default);
        Assert.Single(handler.Requests);
        var traversal = $"api/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/scripts/document?path={Uri.EscapeDataString("Assets/../Ship.cs")}";
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync<object>(HttpMethod.Get, traversal, null, default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_ClearsBearerAndNotifiesSessionOwner()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"code\":\"expired\",\"message\":\"Expired\"}") });
        using var http = new HttpClient(handler);
        var client = new CollaborationClient(http);
        client.SetSession(new StoredSession(client.ServerAddress, new string('A', 64), DateTimeOffset.UtcNow.AddHours(1)));
        var invalidated = 0;
        client.SessionInvalidated += (_, _) => invalidated++;
        await Assert.ThrowsAsync<ApiClientException>(() => client.SendAsync<object>(HttpMethod.Get, "api/v1/me", null, default));
        Assert.Equal(1, invalidated);
        await Assert.ThrowsAsync<ApiClientException>(() => client.SendAsync<object>(HttpMethod.Get, "api/v1/me", null, default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WriteTransportFailureIsSurfacedWithoutAutomaticRetry()
    {
        using var handler = new Handler(_ => throw new HttpRequestException("network"));
        using var http = new HttpClient(handler);
        var client = new CollaborationClient(http);
        client.SetSession(new StoredSession(client.ServerAddress, new string('A', 64), DateTimeOffset.UtcNow.AddHours(1)));
        var error = await Assert.ThrowsAsync<ApiClientException>(() => client.SendAsync<object>(HttpMethod.Put, "api/v1/me", new { displayName = "write" }, default));
        Assert.Contains("may have completed", error.Message);
        Assert.Single(handler.Requests);
    }
}
