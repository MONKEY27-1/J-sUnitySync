using System.Net;
using System.Text;
using JsUnitySync.Desktop.Core.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace JsUnitySync.Desktop.Tests;

public sealed class ServerHealthClientTests
{
    private sealed class Handler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            respond(++Calls, request, token);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    private static ServerHealthClient Create(HttpClient http) => new(http, NullLogger<ServerHealthClient>.Instance);
    private static readonly Uri Server = new("https://sync.example.com/");

    [Fact]
    public async Task Check_ValidatesExistingApiContractAndRoute()
    {
        using var handler = new Handler((_, request, _) =>
        {
            Assert.Equal("https://sync.example.com/health/ready", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json("{\"status\":\"ready\",\"service\":\"JsUnitySync.Api\",\"lastSequence\":0}"));
        });
        using var http = new HttpClient(handler);
        var result = await Create(http).CheckAsync(Server, default);
        Assert.True(result.IsReady);
        Assert.Contains("Collaboration is not connected", result.Message);
    }

    [Theory]
    [InlineData("{\"status\":\"ready\",\"service\":\"another-api\"}")]
    [InlineData("{\"status\":\"offline\",\"service\":\"JsUnitySync.Api\"}")]
    [InlineData("not-json")]
    [InlineData("null")]
    public async Task Check_InvalidResponseNeverClaimsReady(string content)
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Json(content)));
        using var http = new HttpClient(handler);
        Assert.False((await Create(http).CheckAsync(Server, default)).IsReady);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Check_ClientErrorDoesNotRetry()
    {
        using var handler = new Handler((_, _, _) => Task.FromResult(Json("{}", HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var result = await Create(http).CheckAsync(Server, default);
        Assert.False(result.IsReady);
        Assert.Contains("401", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Check_TransientFailureRetriesThenRecovers()
    {
        using var handler = new Handler((call, _, _) => Task.FromResult(call == 1
            ? Json("{}", HttpStatusCode.ServiceUnavailable)
            : Json("{\"status\":\"ready\",\"service\":\"JsUnitySync.Api\"}")));
        using var http = new HttpClient(handler);
        Assert.True((await Create(http).CheckAsync(Server, default)).IsReady);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Check_NetworkFailureHasBoundedRetries()
    {
        using var handler = new Handler((_, _, _) => throw new HttpRequestException("Network unreachable"));
        using var http = new HttpClient(handler);
        Assert.False((await Create(http).CheckAsync(Server, default)).IsReady);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Check_CancellationStopsWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(async (_, _, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(http).CheckAsync(Server, cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }
}
