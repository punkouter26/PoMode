using Azure.Identity;

namespace PoMode.API.Infrastructure;

public enum SecretSource
{
    KeyVault,
    EnvironmentVariables,
}

public sealed record SecretSourceInfo(SecretSource Source, bool FellBack);

public static class SecretsBootstrap
{
    /// <summary>Pure tier decision: Key Vault when configured and reachable, else environment variables.</summary>
    public static SecretSourceInfo Decide(string? vaultUri, Func<bool> tryConnectKeyVault)
    {
        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            return new SecretSourceInfo(SecretSource.EnvironmentVariables, FellBack: false);
        }

        return tryConnectKeyVault()
            ? new SecretSourceInfo(SecretSource.KeyVault, FellBack: false)
            : new SecretSourceInfo(SecretSource.EnvironmentVariables, FellBack: true);
    }

    /// <summary>Wires the Key Vault configuration provider. Reads "KeyVault:VaultUri" (env: KEYVAULT__VAULTURI).</summary>
    public static SecretSourceInfo Configure(WebApplicationBuilder builder)
    {
        var vaultUri = builder.Configuration["KeyVault:VaultUri"];
        return Decide(vaultUri, () =>
        {
            try
            {
                builder.Configuration.AddAzureKeyVault(new Uri(vaultUri!), new DefaultAzureCredential());
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }
}
