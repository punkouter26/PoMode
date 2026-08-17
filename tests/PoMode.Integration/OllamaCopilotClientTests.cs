using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.Copilot;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Drives the copilot against a local HttpListener standing in for Ollama — the same technique the
/// Phase 4 model-registry tests use — so nothing here needs a real Ollama or a network.
/// </summary>
public sealed class OllamaCopilotClientTests : IAsyncLifetime
{
    private const int Port = 5320;

    private HttpListener _listener = null!;
    private string _baseUrl = null!;

    /// <summary>Model names the fake Ollama reports from /api/tags.</summary>
    private string[] _installed = ["gemma4:26b"];

    /// <summary>Set to override the /api/generate outcome for a single test.</summary>
    private int _generateStatus = 200;
    private string _generateBody = """{"response":"**C major** over a sung 1-3-5. It fits Ionian."}""";

    /// <summary>Models the fake Ollama refuses to load, mimicking a model too big for the machine.</summary>
    private string[] _unloadable = [];

    /// <summary>Model names /api/generate was actually asked for, in order.</summary>
    private readonly List<string> _generateModels = [];

    /// <summary>Every path the fake Ollama was asked for, so tests can assert it was never called.</summary>
    private readonly List<string> _requestedPaths = [];

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

                var path = context.Request.Url?.AbsolutePath ?? "";
                lock (_requestedPaths) { _requestedPaths.Add(path); }

                if (path == "/api/tags")
                {
                    var names = string.Join(",", _installed.Select(name => $"{{\"name\":\"{name}\"}}"));
                    await WriteAsync(context, 200, $"{{\"models\":[{names}]}}");
                    continue;
                }
                if (path == "/api/generate")
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                    var requested = body.RootElement.GetProperty("model").GetString() ?? "";
                    lock (_generateModels) { _generateModels.Add(requested); }

                    if (_unloadable.Contains(requested))
                    {
                        await WriteAsync(context, 500,
                            """{"error":"llama-server startup failed: failed to allocate CPU buffer of size 17384259520\nalloc_tensor_range: failed"}""");
                        continue;
                    }
                    await WriteAsync(context, _generateStatus, _generateBody);
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
        return Task.CompletedTask;
    }

    private OllamaCopilotClient Client(string? baseUrl = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        return new OllamaCopilotClient(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Copilot:BaseUrl"] = baseUrl ?? _baseUrl }).Build(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<OllamaCopilotClient>.Instance);
    }

    private static (ModalResult Result, ModalWindow Window) Analysis(bool insufficient = false)
    {
        IReadOnlyList<ModalMatch> matches = insufficient
            ? []
            : [new ModalMatch(ScaleMode.Ionian, 0.95, [0, 4, 7], [])];
        var window = new ModalWindow(0, 0.0, 2.0, "C", 1, 0b010010010001, [0, 4, 7], insufficient, matches);
        var result = new ModalResult(1, 0, "C", 0.8, ScaleMode.Ionian, 0.9, 120.0, false, [window]);
        return (result, window);
    }

    [Fact]
    public async Task It_explains_a_window_using_an_installed_model()
    {
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.True(reply.Available, reply.Reason);
        Assert.Equal("gemma4:26b", reply.Model);
        Assert.Contains("Ionian", reply.Markdown);
        Assert.Null(reply.Reason);
    }

    [Fact]
    public async Task Spec_preferred_models_win_when_they_are_installed()
    {
        _installed = ["gemma4:26b", "llama3.2:3b", "qwen2.5:7b"];
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        // §5's order is qwen2.5:7b, llama3.3:8b, llama3.2:3b — qwen wins even though it is listed last.
        Assert.Equal("qwen2.5:7b", reply.Model);
    }

    [Fact]
    public async Task With_no_preferred_model_installed_it_falls_through_to_the_first_available()
    {
        _installed = ["mistral:7b", "phi4:14b"];
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.True(reply.Available, reply.Reason);
        Assert.Equal("mistral:7b", reply.Model);
    }

    [Fact]
    public async Task No_installed_models_is_reported_as_unavailable_not_an_error()
    {
        _installed = [];
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.Null(reply.Markdown);
        Assert.Contains("no models", reply.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_connection_degrades_instead_of_throwing()
    {
        var (result, window) = Analysis();
        // Nothing is listening on this port.
        var client = Client($"http://127.0.0.1:{Port + 1}");

        var reply = await client.ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.NotNull(reply.Reason);
    }

    [Fact]
    public async Task An_ollama_error_status_is_reported_with_the_model_it_tried()
    {
        _generateStatus = 500;
        _generateBody = "{}";
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.Equal("gemma4:26b", reply.Model);
        Assert.Contains("500", reply.Reason);
    }

    [Fact]
    public async Task An_empty_answer_is_not_presented_as_an_explanation()
    {
        _generateBody = """{"response":"   "}""";
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.Contains("empty", reply.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_json_degrades_instead_of_throwing()
    {
        _generateBody = "not json at all";
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.NotNull(reply.Reason);
    }

    [Theory]
    [InlineData("http://ollama.example.com:11434")]
    [InlineData("https://api.openai.com")]
    [InlineData("http://10.0.0.5:11434")]
    public async Task A_non_loopback_address_is_refused_without_any_request_being_made(string remote)
    {
        var (result, window) = Analysis();
        lock (_requestedPaths) { _requestedPaths.Clear(); }

        var reply = await Client(remote).ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.Contains("local", reply.Reason, StringComparison.OrdinalIgnoreCase);
        lock (_requestedPaths) { Assert.Empty(_requestedPaths); }
    }

    [Fact]
    public async Task A_garbled_address_is_refused_rather_than_crashing()
    {
        var (result, window) = Analysis();

        var reply = await Client("not-a-url").ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.NotNull(reply.Reason);
    }

    [Fact]
    public async Task A_model_that_is_installed_but_will_not_load_falls_through_to_the_next_one()
    {
        // Observed for real on this machine: gemma4:26b is installed but needs a 17 GB buffer, so
        // Ollama answers 500. Trying one model and giving up would report the copilot as unavailable
        // even though a smaller installed model works fine.
        _installed = ["gemma4:26b", "llama3.2:1b"];
        _unloadable = ["gemma4:26b"];
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.True(reply.Available, reply.Reason);
        Assert.Equal("llama3.2:1b", reply.Model);
        lock (_generateModels) { Assert.Equal(["gemma4:26b", "llama3.2:1b"], _generateModels); }
    }

    [Fact]
    public async Task When_every_model_fails_the_reason_carries_ollamas_own_error_text()
    {
        _installed = ["gemma4:26b"];
        _unloadable = ["gemma4:26b"];
        var (result, window) = Analysis();

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        Assert.False(reply.Available);
        Assert.Contains("500", reply.Reason);
        Assert.Contains("allocate CPU buffer", reply.Reason);
        // Only the first line is kept, so a multi-line dump cannot blow out the UI card.
        Assert.DoesNotContain("alloc_tensor_range", reply.Reason);
    }

    [Fact]
    public async Task No_more_than_three_unusable_models_are_tried()
    {
        _installed = ["a:1b", "b:1b", "c:1b", "d:1b", "e:1b"];
        _unloadable = _installed;
        var (result, window) = Analysis();

        await Client().ExplainAsync(result, window, CancellationToken.None);

        lock (_generateModels) { Assert.Equal(3, _generateModels.Count); }
    }

    [Fact]
    public async Task An_insufficient_evidence_window_is_still_explainable()
    {
        var (result, window) = Analysis(insufficient: true);

        var reply = await Client().ExplainAsync(result, window, CancellationToken.None);

        // The prompt must describe the honest "no confident match" state rather than skip the call.
        Assert.True(reply.Available, reply.Reason);
        lock (_requestedPaths) { Assert.Contains("/api/generate", _requestedPaths); }
    }
}
