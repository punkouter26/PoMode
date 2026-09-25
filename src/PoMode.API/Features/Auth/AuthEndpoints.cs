using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using PoMode.Shared.Account;

namespace PoMode.API.Features.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/session", (HttpContext context, PoAuthOptions options)
                => TypedResults.Ok(SessionOf(context.User, options)))
            .AllowAnonymous()
            .WithTags("Auth")
            .WithSummary("Who the browser is signed in as, and whether Microsoft sign-in is offered.");

        // Idempotent on purpose: the client calls this whenever it finds itself signed out, and a
        // second tab racing the first must not replace a guest (and orphan their library) with another.
        app.MapPost("/auth/guest", async (HttpContext context, PoAuthOptions options) =>
            {
                if (PoUser.IdOf(context.User) is not null)
                {
                    return TypedResults.Ok(SessionOf(context.User, options));
                }

                var id = Guid.NewGuid();
                var displayName = $"Guest {(id.GetHashCode() & 0x7FFFFFFF) % 9000 + 1000}";
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, displayName), .. PoUser.Stamp($"guest:{id:N}", SessionKind.Guest)],
                    PoAuth.SessionScheme);
                var principal = new ClaimsPrincipal(identity);
                await context.SignInAsync(PoAuth.SessionScheme, principal,
                    new AuthenticationProperties { IsPersistent = true });
                return TypedResults.Ok(SessionOf(principal, options));
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("Auth")
            .WithSummary("Starts a persistent guest session, or returns the current one if already signed in.");

        // A top-level navigation, not a fetch: the OIDC round trip leaves this origin.
        app.MapGet("/auth/login/microsoft", (string? returnUrl, PoAuthOptions options) =>
                options.MicrosoftAvailable
                    ? Results.Challenge(
                        new AuthenticationProperties { RedirectUri = LocalOnly(returnUrl) },
                        [OpenIdConnectDefaults.AuthenticationScheme])
                    : Results.Redirect(LocalOnly(returnUrl)))
            .AllowAnonymous()
            .WithTags("Auth")
            .WithSummary("Starts Microsoft sign-in. Redirects straight back when it is not configured.");

        // Local sign-out only. Signing the person out of Microsoft everywhere is not this app's call,
        // and the client mints a fresh guest straight after, so the page keeps working.
        app.MapPost("/auth/logout", async (HttpContext context, PoAuthOptions options) =>
            {
                await context.SignOutAsync(PoAuth.SessionScheme);
                return TypedResults.Ok(new SessionDto(SessionKind.None, null, options.MicrosoftAvailable));
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("Auth")
            .WithSummary("Ends this browser's session.");

        return app;
    }

    private static SessionDto SessionOf(ClaimsPrincipal user, PoAuthOptions options)
        => new(PoUser.KindOf(user), PoUser.DisplayNameOf(user), options.MicrosoftAvailable);

    /// <summary>A rooted local path, or "/". Anything else would make the sign-in an open redirect.</summary>
    internal static string LocalOnly(string? returnUrl)
        => returnUrl is { Length: > 0 } url
           && url[0] == '/'
           && !url.StartsWith("//", StringComparison.Ordinal)
           && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
