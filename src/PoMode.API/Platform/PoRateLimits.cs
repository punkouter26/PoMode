using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace PoMode.API.Platform;

/// <summary>
/// Admission control for the two things on this server that cost real resources: queueing an
/// analysis, and running a language model.
///
/// <para>Deliberately not a global limiter. Reading a finished job's artifacts is cheap and is what
/// the canvas does dozens of times while a user pans a timeline, so a blanket limit would throttle
/// the interactive part of the app to protect the expensive part. Each policy is attached to the
/// endpoints it is actually about.</para>
///
/// <para>Partitioned by authenticated user, falling back to remote address. One noisy client must not
/// be able to exhaust another's budget, and the reverse — a shared global bucket — is the failure
/// mode that makes rate limiting look like an outage.</para>
/// </summary>
public static class PoRateLimits
{
    /// <summary>Queueing an analysis: a whole pipeline run, minutes of CPU, and disk.</summary>
    public const string UploadPolicy = "po-upload";

    /// <summary>Asking a language model. The only endpoint a text box can drive a model from.</summary>
    public const string InterpretPolicy = "po-interpret";

    /// <summary>
    /// Generous by design. These are sized to stop a script, not to ration a person: a musician
    /// uploading an album a track at a time must never meet a limit, and nor must the browser test
    /// suites, which drive the real endpoints at machine speed.
    /// </summary>
    private const int DefaultUploadPerMinute = 30;
    private const int DefaultInterpretPerMinute = 10;

    /// <summary>
    /// Everything off. Exists for the test fixtures, which boot the real app and are the one caller
    /// entitled to say the limits are not the thing under test — the alternative is limits set so
    /// high they protect nothing.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration)
        => configuration.GetValue("RateLimits:Enabled", true);

    /// <summary>
    /// Registers the three policies. They are always registered, even when limiting is switched off —
    /// a disabled policy becomes a no-op limiter rather than disappearing, so the endpoints keep their
    /// <c>RequireRateLimiting</c> metadata and the middleware never has to be conditionally wired.
    /// A missing named policy is a startup exception, and making that possible from a config flag is
    /// how a test setting takes production down.
    /// </summary>
    public static IServiceCollection AddPoRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = IsEnabled(configuration);

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(UploadPolicy, context => Partition(
                context, enabled, configuration.GetValue("RateLimits:UploadPerMinute", DefaultUploadPerMinute)));
            options.AddPolicy(InterpretPolicy, context => Partition(
                context, enabled, configuration.GetValue("RateLimits:InterpretPerMinute", DefaultInterpretPerMinute)));

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                // Retry-After turns "no" into "not yet", which is the difference between a client
                // that backs off and one that hammers the endpoint until something gives.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                await context.HttpContext.Response.WriteAsJsonAsync(
                    "That is more requests than this server will take in a minute. Wait a moment and try again.",
                    ct);
            };
        });

        return services;
    }

    /// <summary>
    /// A fixed window rather than a token bucket, and with no queue.
    ///
    /// <para>No queue because every limited endpoint here is one a person is waiting on: holding a
    /// request for thirty seconds to admit it later is worse than telling the caller to retry, and it
    /// ties up a connection either way. A fixed window because the budget it expresses — "this many
    /// uploads a minute" — is the one a human can be told in a sentence.</para>
    /// </summary>
    private static RateLimitPartition<string> Partition(
        HttpContext context, bool enabled, int permitsPerMinute)
    {
        var key = PartitionKey(context);
        return enabled
            ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(permitsPerMinute, 1),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter(key);
    }

    /// <summary>
    /// Who is being limited. The signed-in name first, because the upload endpoint is anonymous by
    /// design (RadzenUpload posts the file itself and cannot attach auth headers) while every other
    /// costly endpoint is not — so both kinds have to partition sensibly.
    /// </summary>
    private static string PartitionKey(HttpContext context)
        => context.User.Identity?.Name is { Length: > 0 } user
            ? $"user:{user}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
