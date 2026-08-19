using System.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class FakeAuthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    [Fact]
    public void FakeAuth_throws_InvalidOperationException_in_production()
    {
        using var factory = Factory()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("FakeAuthHandler must never run in Production",
            FlattenMessages(exception));
    }

    private static string FlattenMessages(Exception ex) =>
        ex is AggregateException agg
            ? string.Join(" | ", agg.InnerExceptions.Select(FlattenMessages))
            : ex.Message + (ex.InnerException is { } inner ? " | " + FlattenMessages(inner) : "");
}
