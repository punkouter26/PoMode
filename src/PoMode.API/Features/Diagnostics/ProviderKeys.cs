namespace PoMode.API.Features.Diagnostics;

/// <summary>
/// Single source of truth for the configuration keys backing external provider credentials.
///
/// <para><c>SonicApiKey</c> was removed in Phase 7: sonicapi.com is a parked domain listed for sale, so
/// there is no service to hold a key for. Advertising a slot for a dead provider in <c>/diag</c> would
/// invite a user to configure something that can never work.</para>
/// </summary>
public static class ProviderKeys
{
    public static readonly string[] All = ["ReplicateApiToken", "LalalApiKey"];
}
