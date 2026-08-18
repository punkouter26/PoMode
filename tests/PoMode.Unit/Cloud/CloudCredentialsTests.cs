using Microsoft.Extensions.Configuration;
using PoMode.API.Features.Cloud;
using PoMode.API.Features.Diagnostics;
using Xunit;

namespace PoMode.Unit.Cloud;

public class CloudCredentialsTests
{
    private static CloudCredentials Credentials(params (string Key, string? Value)[] settings)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build());

    [Fact]
    public void The_cloud_tier_is_enabled_by_default()
        => Assert.True(Credentials().Enabled);

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public void The_kill_switch_disables_the_whole_tier(string value)
    {
        var credentials = Credentials(("Cloud:Enabled", value), ("ReplicateApiToken", "tok"));

        Assert.False(credentials.Enabled);
        // The key still resolves — only the decision to use it changes, so /diag can still report
        // that a key is configured while refusing to spend it.
        Assert.Equal("tok", credentials.TokenFor("ReplicateApiToken"));
        Assert.False(credentials.Has("ReplicateApiToken"));
    }

    [Fact]
    public void A_present_key_is_usable()
    {
        var credentials = Credentials(("ReplicateApiToken", "r8_secret"));

        Assert.True(credentials.Has("ReplicateApiToken"));
        Assert.Equal("r8_secret", credentials.TokenFor("ReplicateApiToken"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void An_absent_or_blank_key_is_not_usable(string? value)
    {
        var credentials = Credentials(("ReplicateApiToken", value));

        Assert.False(credentials.Has("ReplicateApiToken"));
        Assert.Null(credentials.TokenFor("ReplicateApiToken"));
    }

    [Fact]
    public void A_key_is_trimmed_so_a_stray_newline_from_a_secret_store_does_not_break_a_header()
    {
        // Key Vault values and env vars routinely arrive with trailing whitespace; an untrimmed token
        // produces an invalid Authorization header rather than an honest 401.
        var credentials = Credentials(("LalalApiKey", " abc123\n"));

        Assert.Equal("abc123", credentials.TokenFor("LalalApiKey"));
        Assert.True(credentials.Has("LalalApiKey"));
    }

    [Fact]
    public void Every_provider_key_name_in_the_single_source_of_truth_is_resolvable()
    {
        var credentials = Credentials([.. ProviderKeys.All.Select(key => (key, (string?)$"value-for-{key}"))]);

        Assert.All(ProviderKeys.All, key => Assert.True(credentials.Has(key), key));
        Assert.All(ProviderKeys.All, key => Assert.Equal($"value-for-{key}", credentials.TokenFor(key)));
    }

    [Fact]
    public void An_unknown_provider_key_is_simply_absent_rather_than_throwing()
    {
        var credentials = Credentials(("ReplicateApiToken", "tok"));

        Assert.False(credentials.Has("NoSuchProviderKey"));
        Assert.Null(credentials.TokenFor("NoSuchProviderKey"));
    }
}
