using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.Audio;
using PoMode.API.Features.Cloud;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Drives the Replicate separator against an HttpListener reproducing the documented prediction
/// protocol. No test here ever contacts api.replicate.com, so no test can spend money.
/// </summary>
public sealed class ReplicateStemSeparatorTests : IAsyncLifetime
{
    private const int Port = 5322;

    private HttpListener _listener = null!;
    private string _baseUrl = null!;
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-replicate-{Guid.NewGuid():N}");

    /// <summary>Statuses returned by successive polls; the last one repeats.</summary>
    private string[] _pollStatuses = ["succeeded"];

    /// <summary>Serialised JSON for the prediction's <c>output</c> field.</summary>
    private string _outputJson = null!;

    private string? _errorText;

    private readonly List<string> _paths = [];
    private readonly List<string?> _authHeaders = [];
    private long _createdBodyBytes;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_jobDir);
        _baseUrl = $"http://127.0.0.1:{Port}";
        _outputJson = $$"""{"vocals":"{{_baseUrl}}/files/vocals","instrumental":"{{_baseUrl}}/files/instrumental"}""";

        _listener = new HttpListener();
        _listener.Prefixes.Add($"{_baseUrl}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            var polls = 0;
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                var path = context.Request.Url?.AbsolutePath ?? "";
                lock (_paths)
                {
                    _paths.Add(path);
                    _authHeaders.Add(context.Request.Headers["Authorization"]);
                }

                if (path.EndsWith("/predictions", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    _createdBodyBytes = (await reader.ReadToEndAsync()).Length;
                    await WriteAsync(context, 201,
                        $$$"""{"id":"pred1","status":"starting","urls":{"get":"{{{_baseUrl}}}/v1/predictions/pred1"}}""");
                    continue;
                }
                if (path.StartsWith("/v1/predictions/", StringComparison.Ordinal))
                {
                    var status = _pollStatuses[Math.Min(polls, _pollStatuses.Length - 1)];
                    polls++;
                    var error = _errorText is null ? "null" : $"\"{_errorText}\"";
                    var output = status == "succeeded" ? _outputJson : "null";
                    await WriteAsync(context, 200,
                        $$"""{"id":"pred1","status":"{{status}}","output":{{output}},"error":{{error}}}""");
                    continue;
                }
                if (path.StartsWith("/files/", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 200;
                    await context.Response.OutputStream.WriteAsync(TestAudio.MakeWav(seconds: 1.0));
                    context.Response.Close();
                    continue;
                }
                await WriteAsync(context, 404, "{}");
            }
        });
        return Task.CompletedTask;
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
        context.Response.Close();
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        if (Directory.Exists(_jobDir)) Directory.Delete(_jobDir, recursive: true);
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> Paths
    {
        get { lock (_paths) { return [.. _paths]; } }
    }

    private ReplicateStemSeparator Separator(
        string? token = "r8_test_token",
        bool cloudEnabled = true,
        string? maxUploadMb = null,
        FakeTimeProvider? time = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ReplicateApiToken"] = token,
            ["Cloud:Enabled"] = cloudEnabled ? "true" : "false",
            ["Cloud:Replicate:BaseUrl"] = _baseUrl,
            ["Cloud:MaxUploadMb"] = maxUploadMb,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();

        return new ReplicateStemSeparator(
            new CloudCredentials(configuration),
            configuration,
            provider.GetRequiredService<IHttpClientFactory>(),
            time ?? new FakeTimeProvider(),
            NullLogger<ReplicateStemSeparator>.Instance);
    }

    private StageContext Context(double seconds = 1.0)
    {
        var inputPath = Path.Combine(_jobDir, "input.wav");
        File.WriteAllBytes(inputPath, TestAudio.MakeWav(seconds));
        return new StageContext("job1", _jobDir, inputPath);
    }

    /// <summary>Drains the fake clock so poll delays elapse without the suite sleeping.</summary>
    private static async Task DrainAsync(FakeTimeProvider time, Task work)
    {
        while (!work.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(10);
        }
        await work;
    }

    [Fact]
    public void It_is_cloud_tier_and_named()
    {
        var separator = Separator();

        Assert.Equal(ExecutionTier.Cloud, separator.Tier);
        Assert.Equal(nameof(ReplicateStemSeparator), separator.Name);
    }

    [Fact]
    public async Task It_is_available_only_with_a_token_and_the_tier_enabled()
    {
        Assert.True(await Separator().IsAvailableAsync(CancellationToken.None));
        Assert.False(await Separator(token: null).IsAvailableAsync(CancellationToken.None));
        Assert.False(await Separator(token: "   ").IsAvailableAsync(CancellationToken.None));
        Assert.False(await Separator(cloudEnabled: false).IsAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task It_writes_both_stems_from_the_prediction_output()
    {
        var time = new FakeTimeProvider();
        var separator = Separator(time: time);

        await DrainAsync(time, separator.SeparateAsync(Context(), CancellationToken.None));

        foreach (var stem in new[] { "vocals.wav", "instrumental.wav" })
        {
            var path = Path.Combine(_jobDir, stem);
            Assert.True(File.Exists(path), $"{stem} was not written");
            // Re-encoded through WavWriter, so the contract "this file is a wav" holds even if the
            // provider hands back some other container.
            var buffer = AudioDecoder.Decode(path);
            Assert.True(buffer.Samples.Length > 0, $"{stem} decoded to nothing");
        }
    }

    [Fact]
    public async Task It_polls_until_the_prediction_reaches_a_terminal_status()
    {
        _pollStatuses = ["starting", "processing", "processing", "succeeded"];
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        Assert.Equal(4, Paths.Count(path => path.StartsWith("/v1/predictions/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_success_status_is_succeeded_not_successful()
    {
        // One published documentation summary calls the terminal success state "successful". Treating
        // that as success would make every real prediction look unfinished, so it must NOT match.
        _pollStatuses = ["successful"];
        var time = new FakeTimeProvider();

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None)));

        Assert.DoesNotContain("vocals.wav", Directory.GetFiles(_jobDir).Select(Path.GetFileName));
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task A_failed_prediction_throws_with_the_providers_own_error_text()
    {
        _pollStatuses = ["processing", "failed"];
        _errorText = "CUDA out of memory";
        var time = new FakeTimeProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None)));

        Assert.Contains("CUDA out of memory", failure.Message);
    }

    [Fact]
    public async Task A_canceled_prediction_throws_so_the_pipeline_can_fall_back()
    {
        _pollStatuses = ["canceled"];
        var time = new FakeTimeProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None)));
    }

    [Fact]
    public async Task An_output_array_is_accepted_as_well_as_an_object()
    {
        // demucs variants differ: some return a keyed object, others a bare array of URLs.
        _outputJson = $$"""["{{_baseUrl}}/files/vocals","{{_baseUrl}}/files/instrumental"]""";
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_jobDir, "vocals.wav")));
        Assert.True(File.Exists(Path.Combine(_jobDir, "instrumental.wav")));
    }

    [Fact]
    public async Task The_no_vocals_alias_is_understood_as_the_instrumental()
    {
        _outputJson = $$"""{"vocals":"{{_baseUrl}}/files/vocals","no_vocals":"{{_baseUrl}}/files/backing"}""";
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_jobDir, "instrumental.wav")));
    }

    [Fact]
    public async Task An_output_without_a_usable_vocals_url_throws_a_clear_error()
    {
        _outputJson = """{"drums":"http://example.invalid/drums"}""";
        var time = new FakeTimeProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None)));

        Assert.Contains("vocals", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_token_is_sent_as_a_bearer_header_and_never_leaks_into_an_error()
    {
        _pollStatuses = ["failed"];
        _errorText = "nope";
        var time = new FakeTimeProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None)));

        lock (_paths)
        {
            Assert.Contains("Bearer r8_test_token", _authHeaders.Where(h => h is not null));
        }
        Assert.DoesNotContain("r8_test_token", failure.ToString());
    }

    [Fact]
    public async Task An_oversized_input_is_refused_before_any_http_call()
    {
        // The input travels as a data URI, so a large file would mean a huge JSON body.
        var separator = Separator(maxUploadMb: "0");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => separator.SeparateAsync(Context(), CancellationToken.None));

        Assert.Contains("too large", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Paths);
    }

    [Fact]
    public async Task The_input_audio_is_sent_in_the_create_request()
    {
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(seconds: 1.0), CancellationToken.None));

        // A base64 data URI of a second of PCM is comfortably over a few kilobytes; this proves the
        // audio actually travelled rather than a bare filename.
        Assert.True(_createdBodyBytes > 10_000, $"create body was only {_createdBodyBytes} bytes");
    }
}
