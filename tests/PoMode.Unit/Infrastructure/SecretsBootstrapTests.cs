using Xunit;
using PoMode.API.Infrastructure;

namespace PoMode.Unit.Infrastructure;

public class SecretsBootstrapTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_vault_uri_means_environment_variables_without_fallback_flag(string? vaultUri)
    {
        var info = SecretsBootstrap.Decide(vaultUri, tryConnectKeyVault: () => throw new Exception("must not be called"));
        Assert.Equal(SecretSource.EnvironmentVariables, info.Source);
        Assert.False(info.FellBack);
    }

    [Fact]
    public void Reachable_vault_wins()
    {
        var info = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => true);
        Assert.Equal(SecretSource.KeyVault, info.Source);
        Assert.False(info.FellBack);
    }

    [Fact]
    public void Unreachable_vault_falls_back_to_environment_and_flags_it()
    {
        var info = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => false);
        Assert.Equal(SecretSource.EnvironmentVariables, info.Source);
        Assert.True(info.FellBack);
    }
}
