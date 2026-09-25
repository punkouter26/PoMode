using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PoMode.API.Features.Auth;
using PoMode.Shared.Account;

namespace PoMode.API.Infrastructure;

/// <summary>Dev/test-only header auth (X-Fake-User / X-Fake-Roles). Registered only in Development and
/// Test by <see cref="PoAuth"/>, and still hard-fails in Production per NET_RULES should that ever change.</summary>
public sealed class FakeAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "FakeAuth";
    public const string UserHeader = "X-Fake-User";
    public const string RolesHeader = "X-Fake-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "FakeAuthHandler must never run in Production. Configure a real authentication provider.");
        }

        var userName = Request.Headers[UserHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, userName) };
        claims.AddRange(PoUser.Stamp($"test:{userName}", SessionKind.Test));
        var roles = Request.Headers[RolesHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(roles))
        {
            claims.AddRange(roles
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
