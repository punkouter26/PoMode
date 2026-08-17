namespace PoMode.API.Features.Cloud;

/// <summary>
/// Resolves paid-provider credentials and decides whether the cloud tier may be used at all.
///
/// Values arrive through the existing configuration chain (Key Vault, then environment fallback — see
/// <c>SecretsBootstrap</c>), so this type never reads a secret store itself. Key *names* come from
/// <see cref="Hardware.ProviderKeys"/> so there is one source of truth shared with <c>/diag</c>.
///
/// <para><b>Cloud:Enabled</b> is a deliberate kill switch. Spec §4 falls a failed stage through to a
/// paid tier automatically, so a user must be able to forbid spending without deleting their keys.
/// Note that <see cref="TokenFor"/> still resolves when disabled — only <see cref="Has"/> refuses — so
/// diagnostics can honestly report "a key is configured" while the tier stays shut.</para>
/// </summary>
public sealed class CloudCredentials(IConfiguration configuration)
{
    public bool Enabled => configuration.GetValue("Cloud:Enabled", defaultValue: true);

    /// <summary>
    /// The credential for a provider key name, or null when absent or blank. Trimmed: Key Vault
    /// values and environment variables routinely carry a trailing newline, which would produce an
    /// invalid HTTP header instead of an honest 401.
    /// </summary>
    public string? TokenFor(string providerKey)
    {
        var value = configuration[providerKey];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>True only when the tier is enabled and this provider's credential resolved.</summary>
    public bool Has(string providerKey) => Enabled && TokenFor(providerKey) is not null;
}
