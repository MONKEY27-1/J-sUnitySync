using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JsUnitySync.Contracts.Networking;
using Microsoft.Extensions.Logging;

namespace JsUnitySync.Desktop.Core.Networking;

/// <summary>Read-only probe of the existing API contract; readiness does not imply authentication or session connectivity.</summary>
public sealed class ServerHealthClient(HttpClient http, ILogger<ServerHealthClient> logger) : IServerHealthClient
{
    private static readonly RetryPolicy Retry = new(3, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1));

    public async Task<HealthCheckResult> CheckAsync(Uri server, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Retry.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await http.GetAsync(new Uri(server, "health/ready"), timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadFromJsonAsync<Readiness>(timeout.Token);
                    return payload is { Status: "ready", Service: "JsUnitySync.Api" }
                        ? new(true, "API is ready. Collaboration is not connected.")
                        : new(false, "The response is not a J'sUnitySync readiness response.");
                }
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.RequestTimeout &&
                    response.StatusCode != HttpStatusCode.TooManyRequests)
                    return new(false, $"Server returned HTTP {(int)response.StatusCode}. Check the server address.");
                logger.LogWarning("Readiness probe returned {StatusCode}, attempt {Attempt}", (int)response.StatusCode, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            {
                // Deliberately avoid logging URLs, tokens or server response content.
                logger.LogWarning("Readiness probe failed with {ErrorType}, attempt {Attempt}", error.GetType().Name, attempt);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            {
                return new(false, "Server returned an invalid readiness response.");
            }
            if (attempt < Retry.MaxAttempts)
                await Task.Delay(Retry.DelayBefore(attempt)!.Value, cancellationToken);
        }
        return new(false, "API unavailable after 3 attempts. Check the address and that the backend is running, then retry.");
    }

    private sealed record Readiness(string? Status, string? Service);
}
