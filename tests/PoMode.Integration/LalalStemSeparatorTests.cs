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
/// Drives the LALAL.AI separator against an HttpListener reproducing the protocol from their official
/// client: upload → split → check → delete. Nothing here contacts lalal.ai.
/// Provider-agnostic plumbing shared with Replicate (availability gating, the poll-until-terminal
/// loop, error-status throwing, "the audio bytes actually travel") is covered once in
/// <see cref="ReplicateStemSeparatorTests"/>; this file keeps the tier/name tagging for both.
/// </summary>
public sealed class LalalStemSeparatorTests : IAsyncLifetime
{
    private const int Port = 5323;

    private HttpListener _listener = null!;
    private string _baseUrl = null!;
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-lalal-{Guid.NewGuid():N}");

    /// <summary>Task statuses returned by successive check calls; the last repeats.</summary>
    private string[] _checkStatuses = ["success"];

    /// <summary>Track labels the fixture reports on success.</summary>
    private (string Label, string Url)[] _tracks = null!;

    /// <summary>When true, check/ nests tracks directly rather than under a "result" object.</summary>
    private bool _flatTracks;

    private readonly List<string> _paths = [];
    private readonly List<string?> _licenceHeaders = [];
    private readonly List<string?> _authHeaders = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_jobDir);
        _baseUrl = $"http://127.0.0.1:{Port}";
        _tracks = [("vocals", $"{_baseUrl}/files/vocals"), ("instrumental", $"{_baseUrl}/files/instrumental")];

        _listener = new HttpListener();
        _listener.Prefixes.Add($"{_baseUrl}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            var checks = 0;
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
                    _licenceHeaders.Add(context.Request.Headers["X-License-Key"]);
                    _authHeaders.Add(context.Request.Headers["Authorization"]);
                }

                if (path.EndsWith("/upload/", StringComparison.Ordinal))
                {
                    // Drain the request body so the client's upload completes cleanly. That the audio
                    // bytes actually travel to the provider is proven once, in the Replicate file.
                    await context.Request.InputStream.CopyToAsync(Stream.Null);
                    await WriteAsync(context, 200, """{"status":"success","id":"src1"}""");
                    continue;
                }
                if (path.EndsWith("/split/stem_separator/", StringComparison.Ordinal))
                {
                    await WriteAsync(context, 200, """{"status":"success","task_id":"task1"}""");
                    continue;
                }
                if (path.EndsWith("/check/", StringComparison.Ordinal))
                {
                    var status = _checkStatuses[Math.Min(checks, _checkStatuses.Length - 1)];
                    checks++;
                    await WriteAsync(context, 200, CheckBody(status));
                    continue;
                }
                if (path.EndsWith("/delete/", StringComparison.Ordinal))
                {
                    await WriteAsync(context, 200, """{"status":"success"}""");
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

    private string CheckBody(string status)
    {
        // Built by concatenation on purpose: raw string literals fight this JSON, because the runs of
        // closing braces collide with the interpolation delimiter no matter how many '$' are used.
        if (status != "success")
        {
            return "{\"status\":\"success\",\"result\":{\"task1\":{\"status\":\"" + status
                + "\",\"progress\":42}}}";
        }

        var tracks = string.Join(",", _tracks.Select(track =>
            "{\"label\":\"" + track.Label + "\",\"url\":\"" + track.Url + "\"}"));
        var inner = _flatTracks
            ? "{\"status\":\"success\",\"progress\":100,\"tracks\":[" + tracks + "]}"
            : "{\"status\":\"success\",\"progress\":100,\"result\":{\"tracks\":[" + tracks + "]}}";
        return "{\"status\":\"success\",\"result\":{\"task1\":" + inner + "}}";
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

    private LalalStemSeparator Separator(
        string? key = "lalal_test_key", bool cloudEnabled = true, FakeTimeProvider? time = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LalalApiKey"] = key,
            ["Cloud:Enabled"] = cloudEnabled ? "true" : "false",
            ["Cloud:Lalal:BaseUrl"] = _baseUrl,
        }).Build();
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();

        return new LalalStemSeparator(
            new CloudCredentials(configuration),
            configuration,
            provider.GetRequiredService<IHttpClientFactory>(),
            time ?? new FakeTimeProvider(),
            NullLogger<LalalStemSeparator>.Instance);
    }

    private StageContext Context()
    {
        var inputPath = Path.Combine(_jobDir, "input.wav");
        File.WriteAllBytes(inputPath, TestAudio.MakeWav(seconds: 1.0));
        return new StageContext("job1", _jobDir, inputPath);
    }

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
        Assert.Equal(nameof(LalalStemSeparator), separator.Name);
    }

    [Fact]
    public async Task It_walks_upload_split_check_and_writes_both_stems()
    {
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        Assert.Contains("/upload/", Paths);
        Assert.Contains("/split/stem_separator/", Paths);
        Assert.Contains("/check/", Paths);
        foreach (var stem in new[] { "vocals.wav", "instrumental.wav" })
        {
            var path = Path.Combine(_jobDir, stem);
            Assert.True(File.Exists(path), $"{stem} was not written");
            Assert.True(AudioDecoder.Decode(path).Samples.Length > 0, $"{stem} decoded to nothing");
        }
    }

    [Fact]
    public async Task The_licence_key_travels_in_its_own_header_and_never_as_a_bearer_token()
    {
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        lock (_paths)
        {
            Assert.Contains("lalal_test_key", _licenceHeaders.Where(h => h is not null));
            // LALAL.AI authenticates with X-License-Key; sending Authorization would be a silent 401.
            Assert.All(_authHeaders, header => Assert.Null(header));
        }
    }

    [Fact]
    public async Task Tracks_are_found_whether_or_not_they_are_nested_under_a_result_object()
    {
        _flatTracks = true;
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_jobDir, "vocals.wav")));
    }

    [Fact]
    public async Task The_no_vocals_label_is_understood_as_the_instrumental()
    {
        _tracks = [("vocals", $"{_baseUrl}/files/vocals"), ("no_vocals", $"{_baseUrl}/files/backing")];
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_jobDir, "instrumental.wav")));
    }

    [Fact]
    public async Task A_missing_stem_label_throws_a_named_error()
    {
        _tracks = [("drums", $"{_baseUrl}/files/drums")];
        var time = new FakeTimeProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None)));

        Assert.Contains("vocals", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_uploaded_source_is_deleted_after_a_success()
    {
        var time = new FakeTimeProvider();

        await DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None));

        Assert.Contains("/delete/", Paths);
    }

    [Fact]
    public async Task The_uploaded_source_is_deleted_even_when_the_split_fails()
    {
        // A failed job must not leave the user's audio sitting on a third-party server.
        _checkStatuses = ["cancelled"];
        var time = new FakeTimeProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(time, Separator(time: time).SeparateAsync(Context(), CancellationToken.None)));

        Assert.Contains("/delete/", Paths);
    }
}
