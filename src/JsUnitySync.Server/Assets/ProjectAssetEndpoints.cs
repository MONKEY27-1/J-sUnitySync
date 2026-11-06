using System.Security.Claims;
using JsUnitySync.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace JsUnitySync.Server.Assets;

public static class ProjectAssetEndpoints
{
    public static void MapProjectAssetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/projects/{projectId}/assets", async (string projectId, ClaimsPrincipal user, ProjectAssetService assets, CancellationToken token) =>
            Results.Ok(await assets.ListAsync(user.UserId(), projectId, token))).RequireAuthorization();

        app.MapPut("/api/v1/projects/{projectId}/assets/content", async (HttpContext context, string projectId, string path, long expectedRevision, ClaimsPrincipal user, ProjectAssetService assets, CancellationToken token) =>
        {
            if (context.Request.ContentLength is > ProjectAssetService.MaxAssetBytes)
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Asset exceeds the 32 MiB limit.");
            var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = ProjectAssetService.MaxAssetBytes;
            var saved = await assets.PutAsync(user.UserId(), projectId, path, expectedRevision, context.Request.Body, token);
            return Results.Ok(saved);
        }).RequireAuthorization().RequireRateLimiting("asset-upload");

        app.MapGet("/api/v1/projects/{projectId}/assets/content", async (HttpContext context, string projectId, string path, ClaimsPrincipal user, ProjectAssetService assets, CancellationToken token) =>
        {
            var (summary, content) = await assets.OpenAsync(user.UserId(), projectId, path, token);
            context.Response.Headers.ETag = $"\"{summary.ContentHash}\"";
            return Results.File(content, "application/octet-stream", enableRangeProcessing: true);
        }).RequireAuthorization();
    }
}
