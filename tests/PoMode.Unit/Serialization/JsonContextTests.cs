using System.Text.Json;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Serialization;
using PoMode.Shared.Session;
using Xunit;

namespace PoMode.Unit.Serialization;

public class JsonContextTests
{
    [Fact]
    public void DiagnosticsReport_round_trips_via_source_gen_context()
    {
        var report = new DiagnosticsReport(
            EnvironmentName: "Development",
            IsAzureHosted: false,
            SecretSource: "EnvironmentVariables",
            SecretFellBack: true,
            ProviderKeys: [new ProviderKeyStatus("ReplicateApiToken", Configured: true)]);

        var json = JsonSerializer.Serialize(report, PoModeJsonContext.Default.DiagnosticsReport);
        var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.DiagnosticsReport);

        Assert.NotNull(back);
        Assert.Equal("Development", back.EnvironmentName);
        Assert.True(back.SecretFellBack);
        Assert.Single(back.ProviderKeys);
        Assert.True(back.ProviderKeys[0].Configured);
    }

    [Fact]
    public void SessionInfo_round_trips_via_source_gen_context()
    {
        var session = new SessionInfo("alice", ["admin", "user"]);
        var json = JsonSerializer.Serialize(session, PoModeJsonContext.Default.SessionInfo);
        var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.SessionInfo);
        Assert.NotNull(back);
        Assert.Equal("alice", back.UserName);
        Assert.Equal(2, back.Roles.Count);
    }
}
