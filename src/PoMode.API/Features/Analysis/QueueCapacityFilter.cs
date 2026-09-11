using System.Globalization;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// Refuses new work when the analysis queue is already backed up.
///
/// <para>A rate limit and this are not the same guard and neither substitutes for the other. The rate
/// limit bounds how fast <em>one</em> client may ask; this bounds how much the <em>server</em> has
/// agreed to do. Ten people each politely uploading one track while stem separation grinds through a
/// nine-minute mix is inside every rate limit and still more than one worker can hold.</para>
///
/// <para>The alternative is what happens without it: <c>JobQueue</c>'s channel is bounded and waits
/// when full, so an eleventh upload does not fail — it hangs, holding a request and a large multipart
/// body, until a worker frees a slot. A 503 with a Retry-After is a better answer than a request that
/// never comes back, and it is the honest one.</para>
/// </summary>
public sealed class QueueCapacityFilter(JobQueue queue, IConfiguration configuration) : IEndpointFilter
{
    /// <summary>
    /// Below the channel's own bound of 32, so the refusal happens before the enqueue would block.
    /// A queue this deep already means the last job in it waits many minutes.
    /// </summary>
    private const int DefaultMaxQueueDepth = 24;

    /// <summary>Roughly how long one job occupies the single worker; enough for a client to pace
    /// itself rather than immediately retry into the same refusal.</summary>
    private const int RetryAfterSeconds = 30;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var max = configuration.GetValue("Jobs:MaxQueueDepth", DefaultMaxQueueDepth);
        if (max > 0 && queue.Depth >= max)
        {
            context.HttpContext.Response.Headers.RetryAfter =
                RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return TypedResults.Json(
                $"The analysis queue is full ({queue.Depth} jobs waiting). Try again shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return await next(context);
    }
}
