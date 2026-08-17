using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Infrastructure;
using Xunit;

namespace PoMode.Integration;

public sealed class ModelRegistryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-models-{Guid.NewGuid():N}");
    private readonly byte[] _payload = Encoding.UTF8.GetBytes("pretend-onnx-bytes");
    private HttpListener _listener = null!;
    private string _baseUrl = null!;

    public Task InitializeAsync()
    {
        var port = 5310;
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

    private ModelRegistry Registry()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        return new ModelRegistry(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Models:RootPath"] = _root }).Build(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ModelRegistry>.Instance);
    }

    [Fact]
    public async Task Downloads_and_verifies_a_model()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);
        var registry = Registry();

        var path = await registry.EnsureAsync(descriptor, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_payload, await File.ReadAllBytesAsync(path));
        Assert.True(registry.IsDownloaded(descriptor));
    }

    [Fact]
    public async Task Second_call_does_not_redownload()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);
        var registry = Registry();

        var first = await registry.EnsureAsync(descriptor, CancellationToken.None);
        var stamp = File.GetLastWriteTimeUtc(first);
        var second = await registry.EnsureAsync(descriptor, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public async Task Hash_mismatch_throws_and_leaves_no_file_behind()
    {
        var descriptor = new ModelDescriptor("bad", "bad.onnx", _baseUrl + "bad.onnx", new string('a', 64));
        var registry = Registry();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.EnsureAsync(descriptor, CancellationToken.None));

        Assert.False(registry.IsDownloaded(descriptor));
        Assert.Empty(Directory.GetFiles(registry.RootPath, "*.part"));
    }

    [Fact]
    public void Status_reports_availability_and_size()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);

        var status = Registry().StatusFor([descriptor]).Single();

        Assert.Equal("test", status.Key);
        Assert.False(status.Available);
        Assert.Equal(0, status.SizeBytes);
    }

    [Fact]
    public async Task Empty_hash_descriptor_is_rejected()
    {
        var descriptor = new ModelDescriptor("nohash", "nohash.onnx", _baseUrl + "nohash.onnx", "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Registry().EnsureAsync(descriptor, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_ensure_calls_download_once_and_do_not_throw()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);
        var registry = Registry();

        var paths = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => registry.EnsureAsync(descriptor, CancellationToken.None)));

        Assert.All(paths, p => Assert.Equal(paths[0], p));
        Assert.True(File.Exists(paths[0]));
        Assert.Empty(Directory.GetFiles(registry.RootPath, "*.part"));
    }

    [Fact]
    public async Task Failed_download_leaves_no_part_file()
    {
        // 404 from the fixture server -> EnsureSuccessStatusCode throws
        var descriptor = new ModelDescriptor("missing", "missing.onnx", _baseUrl + "does-not-exist", new string('b', 64));
        var registry = Registry();

        await Assert.ThrowsAnyAsync<Exception>(() => registry.EnsureAsync(descriptor, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(registry.RootPath, "*.part"));
    }

    /// <summary>
    /// Regression test for the Fix Round 1 finding: a registry configured with an explicit
    /// <c>Models:RootPath</c> (exactly what every test host now does) must never see a model file that
    /// happens to sit at the *default* location (<c>AppContext.BaseDirectory/models</c>) — e.g. left
    /// behind by a run that predates a test's isolation, a stale CI cache, or someone invoking
    /// <see cref="ModelRegistry.EnsureAsync"/> while debugging. <see cref="ModelRegistry.IsDownloaded"/>
    /// is a bare <c>File.Exists</c> against <see cref="ModelRegistry.RootPath"/> with no fallback to the
    /// default location, so an isolated <c>RootPath</c> alone is sufficient — this test proves that
    /// holds even when a decoy with the real catalog filename exists at the default location.
    /// </summary>
    [Fact]
    public async Task Isolated_RootPath_ignores_a_decoy_file_in_the_default_location()
    {
        var decoyPath = Path.Combine(AppContext.BaseDirectory, "models", ModelCatalog.BasicPitch.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(decoyPath)!);
        await File.WriteAllBytesAsync(decoyPath, _payload);
        try
        {
            // Registry() points Models:RootPath at this test's isolated _root, not the default location.
            var isolated = Registry();

            Assert.False(isolated.IsDownloaded(ModelCatalog.BasicPitch));
        }
        finally
        {
            File.Delete(decoyPath);
        }
    }
}
