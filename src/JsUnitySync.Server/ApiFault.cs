using JsUnitySync.Contracts.Accounts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JsUnitySync.Server;

public sealed class ApiFault(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static ApiFault Invalid(string message) => new(400, "validation", message);
    public static ApiFault Missing() => new(404, "not_found", "The resource is unavailable or you do not have access.");
    public static ApiFault Conflict() => new(409, "conflict", "This record changed or already exists. Refresh before trying again; accepted work was not overwritten.");
}

public sealed class ApiFaultMiddleware(RequestDelegate next, ILogger<ApiFaultMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (ApiFault fault)
        {
            await Write(context, fault.Status, fault.Code, fault.Message);
        }
        catch (DbUpdateConcurrencyException) { await Write(context, 409, "conflict", ApiFault.Conflict().Message); }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 })
        { await Write(context, 409, "conflict", ApiFault.Conflict().Message); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (error is DbUpdateException or SqliteException)
        {
            logger.LogError("Database request failed: {ErrorType}", error.GetType().Name);
            await Write(context, 503, "storage_unavailable", "Storage is temporarily unavailable. A write may have completed; refresh before retrying.");
        }
    }

    private static Task Write(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ApiError { Code = code, Message = message }, context.RequestAborted);
    }
}
