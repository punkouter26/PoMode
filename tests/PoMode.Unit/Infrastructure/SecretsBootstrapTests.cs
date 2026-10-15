using PoMode.API.Infrastructure;
using Xunit;

namespace PoMode.Unit.Infrastructure;

public class SecretsBootstrapTests
{
    [Fact]
    public void The_secret_source_follows_the_vault_uri_and_its_reachability()
    {
        // No vault uri means environment variables without fallback flag
        {
            var infoNull = SecretsBootstrap.Decide(null, tryConnectKeyVault: () => throw new Exception("must not be called"));
            Assert.Equal(SecretSource.EnvironmentVariables, infoNull.Source);
            Assert.False(infoNull.FellBack);

            var infoBlank = SecretsBootstrap.Decide("   ", tryConnectKeyVault: () => throw new Exception("must not be called"));
            Assert.Equal(SecretSource.EnvironmentVariables, infoBlank.Source);
            Assert.False(infoBlank.FellBack);
        }

        // Vault connectivity determines source and fallback flag
        {
            var infoReachable = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => true);
            Assert.Equal(SecretSource.KeyVault, infoReachable.Source);
            Assert.False(infoReachable.FellBack);

            var infoUnreachable = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => false);
            Assert.Equal(SecretSource.EnvironmentVariables, infoUnreachable.Source);
            Assert.True(infoUnreachable.FellBack);
        }
    }
}
