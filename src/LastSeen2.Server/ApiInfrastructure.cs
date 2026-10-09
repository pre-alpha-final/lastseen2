using System.Security.Cryptography;
using System.Text;
using LastSeen2.Contracts;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace LastSeen2.Server;

public sealed class ApiFailure(int status, string code, string message, string? fieldName = null) : Exception(message)
{
    public IResult Result => Results.Problem(statusCode: status, title: Message,
        extensions: fieldName is null
            ? new Dictionary<string, object?> { ["code"] = code }
            : new Dictionary<string, object?> { ["code"] = code,
                ["errors"] = new Dictionary<string, string[]> { [fieldName] = [Message] } });

    public static ApiFailure Invalid(string field, string message) => new(400, "validation_failed", message, field);
    public static ApiFailure Missing() => new(404, "not_found", "Bookmark not found");
    public static ApiFailure Conflict() => new(409, "revision_conflict", "Board changed; reload before saving again");
}

// One bounded, process-wide failed-attempt window. After the budget is spent,
// reject all attempts before comparing keys to avoid exposing a guessing oracle.
// No attacker-controlled keys/IPs are retained in memory.
public sealed class AccessKeyGuard(IOptions<AccessKeyOptions> options, TimeProvider clock)
{
    private readonly byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.AccessKey));
    private readonly object gate = new();
    private DateTimeOffset window;
    private int failures;

    public IResult? Check(HttpContext context)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (now >= window.AddMinutes(1)) { window = now; failures = 0; }
            if (failures >= 30)
            {
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((window.AddMinutes(1) - now).TotalSeconds)).ToString();
                return new ApiFailure(429, "rate_limited", "Too many failed authentication attempts; retry later").Result;
            }
            var values = context.Request.Headers[ApiRoutes.AccessKeyHeader];
            if (values.Count == 1 && values[0] is { Length: <= 4096 } key
                && CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(key))))
                return null;
            failures++;
        }
        return new ApiFailure(401, "unauthorized", "A valid access key is required").Result;
    }
}

public sealed class ApiBoundaryMiddleware(RequestDelegate next)
{
    private static bool IsProtectedPath(PathString path) =>
        path.Equals(ApiRoutes.CheckKey, StringComparison.OrdinalIgnoreCase)
        || path.Equals(ApiRoutes.Board, StringComparison.OrdinalIgnoreCase)
        || path.Equals(ApiRoutes.Metadata, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(ApiRoutes.Videos) || path.StartsWithSegments(ApiRoutes.Channels);

    public async Task InvokeAsync(HttpContext context, AccessKeyGuard guard, ILogger<ApiBoundaryMiddleware> logger)
    {
        if (!context.Request.Path.StartsWithSegments(ApiRoutes.Prefix)) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            // Protect resource paths even when routing rejects a missing/incorrect content type.
            // Every declared handler is protected before binding its body.
            if (context.GetEndpoint()?.Metadata.GetMetadata<ProtectedApi>() is not null
                || IsProtectedPath(context.Request.Path))
            {
                var rejection = guard.Check(context);
                if (rejection is not null) { await rejection.ExecuteAsync(context); return; }
            }
            await next(context);
        }
        catch (ApiFailure failure) { await failure.Result.ExecuteAsync(context); }
        catch (BadHttpRequestException)
        {
            await ApiFailure.Invalid("request", "Invalid or oversized request body, route, or query").Result.ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Deliberately omit exception objects/messages and request headers from logs.
            var storageFailure = error is MongoException or TimeoutException or OperationCanceledException;
            logger.LogWarning("API operation failed ({Category})", storageFailure ? "storage" : "internal");
            await new ApiFailure(storageFailure ? 503 : 500,
                storageFailure ? "storage_unavailable" : "internal_error",
                storageFailure ? "Storage unavailable; reload the board before retrying a save" : "The operation could not be completed")
                .Result.ExecuteAsync(context);
        }
    }
}

public sealed class ProtectedApi;
