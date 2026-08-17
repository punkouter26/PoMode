using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.Cloud;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Backoff is driven by the registered <see cref="TimeProvider"/>, so these tests advance a fake clock
/// instead of sleeping — the delays are asserted, and the suite stays fast.
/// </summary>
public sealed class ResilientHttpTests : IAsyncLifetime
{
    private const int Port = 5321;

    private HttpListener _listener = null!;
    private string _baseUrl = null!;

    /// <summary>Status codes to return, one per request, then 200 for anything further.</summary>
    private int[] _statuses = [200];

    private readonly List<string> _requests = [];

    public Task InitializeAsync()
    {
        _baseUrl = $"http://127.0.0.1:{Port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{_baseUrl}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                int index;
                lock (_requests)
                {
                    index = _requests.Count;
                    _requests.Add(context.Request.Url?.AbsolutePath ?? "");
                }
                var status = index < _statuses.Length ? _statuses[index] : 200;
                context.Response.StatusCode = status;
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($"attempt {index}"));
                context.Response.Close();
            }
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        return Task.CompletedTask;
    }

    private int RequestCount
    {
        get { lock (_requests) { return _requests.Count; } }
    }

    /// <summary>
    /// Runs the helper while draining the fake clock, so any backoff the helper waits on elapses
    /// immediately. Without this the call would hang: nothing else advances a FakeTimeProvider.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(FakeTimeProvider time, string path = "/thing")
    {
        using var client = new HttpClient();
        var send = ResilientHttp.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}"),
            time,
            NullLogger.Instance,
            CancellationToken.None);

        while (!send.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
        return await send;
    }

    [Fact]
    public async Task A_successful_request_is_made_exactly_once()
    {
        _statuses = [200];

        using var response = await SendAsync(new FakeTimeProvider());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, RequestCount);
    }

    [Fact]
    public async Task A_server_error_is_retried_and_can_succeed()
    {
        _statuses = [500, 200];

        using var response = await SendAsync(new FakeTimeProvider());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, RequestCount);
        Assert.Equal("attempt 1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Retries_stop_after_three_attempts_and_the_last_response_is_returned()
    {
        _statuses = [500, 502, 503, 200];

        using var response = await SendAsync(new FakeTimeProvider());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, RequestCount); // NOT 4 — the fourth would have succeeded
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    public async Task Timeouts_and_rate_limits_are_retried(int status)
    {
        _statuses = [status, 200];

        using var response = await SendAsync(new FakeTimeProvider());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, RequestCount);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Client_errors_are_not_retried(int status)
    {
        // Retrying a rejected credential or a malformed request only burns time and rate limit.
        _statuses = [status, 200];

        using var response = await SendAsync(new FakeTimeProvider());

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(1, RequestCount);
    }

    [Fact]
    public async Task Backoff_grows_between_attempts()
    {
        _statuses = [500, 500, 200];
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();

        using var response = await SendAsync(time);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, RequestCount);
        // 1 s before attempt 2 and 2 s before attempt 3; the drain loop steps 1 s at a time, so assert
        // the total waited is at least the 3 s of backoff rather than an exact instant.
        Assert.True(time.GetUtcNow() - start >= TimeSpan.FromSeconds(3),
            $"only {time.GetUtcNow() - start} elapsed");
    }

    [Fact]
    public async Task A_fresh_request_message_is_built_for_every_attempt()
    {
        // An HttpRequestMessage cannot be sent twice, so the helper takes a factory. If it reused one
        // message the second attempt would throw InvalidOperationException instead of retrying.
        _statuses = [500, 500, 200];

        using var response = await SendAsync(new FakeTimeProvider());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_transport_failure_is_retried_and_then_surfaces()
    {
        using var client = new HttpClient();
        var time = new FakeTimeProvider();
        // Nothing is listening on this port, so every attempt fails at the socket.
        var send = ResilientHttp.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port + 1}/x"),
            time,
            NullLogger.Instance,
            CancellationToken.None);

        while (!send.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => send);
    }
}
