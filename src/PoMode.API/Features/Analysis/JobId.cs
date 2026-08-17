namespace PoMode.API.Features.Analysis;

/// <summary>Job and batch ids are 32 lowercase hex chars and are concatenated into file paths,
/// so validity is a security boundary, not a formality.</summary>
public static class JobId
{
    public static bool IsValid(string? id)
        => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);
}

/// <summary>Group-level filter: any route with a {jobId} or {batchId} parameter 404s on a
/// malformed id before the handler runs, so no handler needs its own copy of the check.</summary>
public sealed class JobIdEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var route = context.HttpContext.Request.RouteValues;
        if ((route.TryGetValue("jobId", out var jobId) && !JobId.IsValid(jobId as string))
            || (route.TryGetValue("batchId", out var batchId) && !JobId.IsValid(batchId as string)))
        {
            return TypedResults.NotFound();
        }
        return await next(context);
    }
}
