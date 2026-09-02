using PoMode.API.Infrastructure;
using Xunit;

namespace PoMode.Unit.Infrastructure;

public class SecretsBootstrapTests
{
    [Fact]
    public void No_vault_uri_means_environment_variables_without_fallback_flag()
    {
        var infoNull = SecretsBootstrap.Decide(null, tryConnectKeyVault: () => throw new Exception("must not be called"));
        Assert.Equal(SecretSource.EnvironmentVariables, infoNull.Source);
        Assert.False(infoNull.FellBack);

        var infoBlank = SecretsBootstrap.Decide("   ", tryConnectKeyVault: () => throw new Exception("must not be called"));
        Assert.Equal(SecretSource.EnvironmentVariables, infoBlank.Source);
        Assert.False(infoBlank.FellBack);
    }

    [Fact]
    public void Vault_connectivity_determines_source_and_fallback_flag()
    {
        var infoReachable = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => true);
        Assert.Equal(SecretSource.KeyVault, infoReachable.Source);
        Assert.False(infoReachable.FellBack);

        var infoUnreachable = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => false);
        Assert.Equal(SecretSource.EnvironmentVariables, infoUnreachable.Source);
        Assert.True(infoUnreachable.FellBack);
    }
}
