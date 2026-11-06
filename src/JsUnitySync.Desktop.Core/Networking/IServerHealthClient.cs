namespace JsUnitySync.Desktop.Core.Networking;

public sealed record HealthCheckResult(bool IsReady, string Message);

public interface IServerHealthClient
{
    Task<HealthCheckResult> CheckAsync(Uri server, CancellationToken cancellationToken);
}
