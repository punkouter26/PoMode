using System.Net;

namespace PoMode.API.Features.Cloud;

/// <summary>
/// Retry with exponential backoff for paid-provider calls (spec §5 asks for retry/backoff).
///
/// Deliberately hand-rolled rather than using <c>Microsoft.Extensions.Http.Resilience</c>: that
/// package's delays are real and jittered, so asserting backoff would mean sleeping in tests. Driving
/// the delay through the already-registered <see cref="TimeProvider"/> makes it deterministic under
/// <c>FakeTimeProvider</c> and adds nothing to the dependency graph.
/// </summary>
public static class ResilientHttp
{
    private const int MaxAttempts = 3;

    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        Exception? transportFailure = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                // 1 s, then 2 s.
                var delay = FirstDelay * Math.Pow(2, attempt - 2);
                logger.LogInformation("Retrying cloud request in {Delay} (attempt {Attempt}).", delay, attempt);
                await Task.Delay(delay, time, ct);
            }

            response?.Dispose();
            response = null;
            try
            {
                // A sent HttpRequestMessage cannot be sent again, so each attempt builds a fresh one.
                using var request = requestFactory();
                response = await client.SendAsync(request, ct);
                transportFailure = null;
            }
            catch (HttpRequestException ex)
            {
                transportFailure = ex;
                continue;
            }

            if (!IsRetryable(response.StatusCode))
            {
                return response;
            }
            logger.LogWarning("Cloud request returned {Status} on attempt {Attempt}.",
                (int)response.StatusCode, attempt);
        }

        if (transportFailure is not null)
        {
            response?.Dispose();
            throw transportFailure;
        }
        // Out of attempts: hand back the last response so the caller can report the real status.
        return response!;
    }

    /// <summary>
    /// Transient failures only. A 401/403 means the key is wrong and a 4xx means the request is wrong;
    /// retrying either just burns time and rate limit.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode status)
        => (int)status >= 500
            || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}
