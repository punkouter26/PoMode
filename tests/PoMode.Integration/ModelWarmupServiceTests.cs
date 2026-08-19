using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Infrastructure;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Same local <see cref="HttpListener"/> fixture pattern as <see cref="ModelRegistryTests"/> (a
/// different fixed port so the two test classes never collide when xunit runs them in parallel).
/// A test-only catalog override lets these tests exercise the real download path without ever
/// touching the real (large) models in <see cref="ModelCatalog"/>.
/// </summary>
public sealed class ModelWarmupServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-warmup-{Guid.NewGuid():N}");
    private readonly byte[] _payload = Encoding.UTF8.GetBytes("pretend-onnx-bytes-for-warmup");
    private HttpListener _listener = null!;
    private string _baseUrl = null!;

    public Task InitializeAsync()
    {
        var port = 5311;
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                if (context.Request.Url?.AbsolutePath.Contains("does-not-exist") == true)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                await context.Response.OutputStream.WriteAsync(_payload);
                context.Response.Close();
            }
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private string Sha256Hex => Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant();

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ModelRegistry Registry(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        return new ModelRegistry(
            configuration, provider.GetRequiredService<IHttpClientFactory>(), NullLogger<ModelRegistry>.Instance);
    }

    private static async Task RunToCompletionAsync(ModelWarmupService service)
    {
        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
        {
            await service.ExecuteTask;
        }
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Downloads_a_missing_model_on_startup()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);
        var configuration = Config(new() { ["Models:RootPath"] = _root });
        var registry = Registry(configuration);
        var service = new ModelWarmupService(
            registry, configuration, NullLogger<ModelWarmupService>.Instance, [descriptor]);

        await RunToCompletionAsync(service);

        Assert.True(registry.IsDownloaded(descriptor));
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Path.Combine(_root, descriptor.FileName)));
    }

    [Fact]
    public async Task Does_nothing_when_AutoDownload_is_false()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);
        var configuration = Config(new()
        {
            ["Models:RootPath"] = _root,
            ["Models:AutoDownload"] = "false",
        });
        var registry = Registry(configuration);
        var service = new ModelWarmupService(
            registry, configuration, NullLogger<ModelWarmupService>.Instance, [descriptor]);

        await RunToCompletionAsync(service);

        Assert.False(registry.IsDownloaded(descriptor));
    }
}
