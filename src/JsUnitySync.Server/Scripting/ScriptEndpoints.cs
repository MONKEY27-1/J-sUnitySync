using System.Security.Claims;
using System.Text.Json;
using JsUnitySync.Contracts.Scripting;
using JsUnitySync.Server.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace JsUnitySync.Server.Scripting;

public static class ScriptEndpoints
{
    public static void MapScriptEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/projects/{projectId}/scripts", async (string projectId, ClaimsPrincipal user, ScriptDocumentService scripts, CancellationToken token) =>
            Results.Ok(await scripts.ListAsync(user.UserId(), projectId, token))).RequireAuthorization();
        app.MapGet("/api/v1/projects/{projectId}/scripts/document", async (string projectId, string path, ClaimsPrincipal user, ScriptDocumentService scripts, CancellationToken token) =>
            Results.Ok(await scripts.GetAsync(user.UserId(), projectId, path, token))).RequireAuthorization();
        app.MapPut("/api/v1/projects/{projectId}/scripts/document", async (HttpContext context, string projectId, string path, ClaimsPrincipal user, ScriptDocumentService scripts, CancellationToken token) =>
        {
            var maxBodyBytes = ScriptDocumentService.MaxDocumentBytes + 128 * 1024;
            if (context.Request.ContentLength is { } contentLength && contentLength > maxBodyBytes)
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Script document request is too large.");
            var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = maxBodyBytes;
            SaveScriptDocumentRequest request;
            try { request = await context.Request.ReadFromJsonAsync<SaveScriptDocumentRequest>(cancellationToken: token) ?? throw ApiFault.Invalid("Script document request is empty."); }
            catch (JsonException) { throw ApiFault.Invalid("Script document request is malformed."); }
            return Results.Ok(await scripts.SaveAsync(user.UserId(), projectId, path, request, token));
        }).RequireAuthorization().RequireRateLimiting("script-write");
    }
}
