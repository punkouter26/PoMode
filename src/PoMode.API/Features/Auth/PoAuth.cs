using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using PoMode.API.Features.Analysis;
using PoMode.API.Infrastructure;
using PoMode.Shared.Account;

namespace PoMode.API.Features.Auth;

/// <summary>Whether Microsoft sign-in is wired on this server. Guest sign-in always is.</summary>
public sealed record PoAuthOptions(bool MicrosoftAvailable);

/// <summary>
/// Three ways in, one session cookie out.
/// <list type="bullet">
/// <item><b>Guest</b> — every environment, Production included. A new visitor gets a persistent
/// guest identity so the app works before anyone decides whether to sign in.</item>
/// <item><b>Microsoft</b> — server-side OIDC through Microsoft.Identity.Web, personal and work
/// accounts both, when <c>PoMode:AzureAd:ClientId</c> and <c>ClientSecret</c> are configured (the
/// secret lives in Key Vault as <c>PoMode--AzureAd--ClientSecret</c>). Signing in moves the guest's
/// library across.</item>
/// <item><b>FakeAuth headers</b> — Development and Test only, for the in-process API tests. The
/// scheme is not even registered elsewhere, so a stray <c>X-Fake-User</c> header in Production is
/// just a header.</item>
/// </list>
/// </summary>
public static class PoAuth
{
    public const string SessionScheme = "PoMode.Session";
    private const string SelectorScheme = "PoMode";

    /// <summary>
    /// The shared vault also holds another app's unprefixed <c>AzureAd--*</c> secrets, which surface as
    /// an <c>AzureAd</c> section. Reading this app's own prefixed section is what stops PoMode from
    /// quietly signing people in against someone else's app registration.
    /// </summary>
    private const string AzureAdSection = "PoMode:AzureAd";

    public static void AddPoAuthentication(this WebApplicationBuilder builder)
    {
        var environment = builder.Environment;
        var allowFakeHeaders = environment.IsDevelopment() || environment.IsEnvironment("Test");
        var azureAd = builder.Configuration.GetSection(AzureAdSection);
        var microsoft = !string.IsNullOrWhiteSpace(azureAd["ClientId"])
                        && !string.IsNullOrWhiteSpace(azureAd["ClientSecret"]);
        builder.Services.AddSingleton(new PoAuthOptions(microsoft));

        var auth = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = SelectorScheme;
            options.DefaultChallengeScheme = SelectorScheme;
        });

        auth.AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
            options.ForwardDefaultSelector = context =>
                allowFakeHeaders && context.Request.Headers.ContainsKey(FakeAuthHandler.UserHeader)
                    ? FakeAuthHandler.SchemeName
                    : SessionScheme);

        if (microsoft)
        {
            // Identity.Web registers the cookie scheme itself when it is handed one by name; adding it
            // a second time here would be a duplicate-scheme startup exception.
            auth.AddMicrosoftIdentityWebApp(
                options =>
                {
                    options.Instance = azureAd["Instance"] ?? "https://login.microsoftonline.com/";
                    // "common": personal Microsoft accounts and any work or school tenant.
                    options.TenantId = azureAd["TenantId"] ?? "common";
                    options.ClientId = azureAd["ClientId"];
                    options.ClientSecret = azureAd["ClientSecret"];
                    options.CallbackPath = azureAd["CallbackPath"] ?? "/auth/callback";
                    options.Events ??= new OpenIdConnectEvents();
                    var validated = options.Events.OnTokenValidated;
                    options.Events.OnTokenValidated = async context =>
                    {
                        await validated(context);
                        await OnMicrosoftSignInAsync(context);
                    };
                    var received = options.Events.OnTicketReceived;
                    options.Events.OnTicketReceived = async context =>
                    {
                        await received(context);
                        // Remember the sign-in across browser restarts, like the guest cookie does;
                        // a session cookie would drop a signed-in user back to a fresh guest.
                        context.Properties ??= new AuthenticationProperties();
                        context.Properties.IsPersistent = true;
                    };
                },
                ConfigureSessionCookie,
                OpenIdConnectDefaults.AuthenticationScheme,
                SessionScheme);
        }
        else
        {
            auth.AddCookie(SessionScheme, ConfigureSessionCookie);
        }

        if (allowFakeHeaders)
        {
            auth.AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
        }

        builder.Services.AddAuthorization();
    }

    private static void ConfigureSessionCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "PoMode.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // A year, sliding: for a guest this cookie *is* the account, and expiring it deletes a library.
        options.ExpireTimeSpan = TimeSpan.FromDays(365);
        options.SlidingExpiration = true;
        // This is an API behind a SPA. A redirect to a login page would arrive at fetch() as a 200
        // of HTML; a status code is what the client can act on.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    /// <summary>Stamps PoMode's own claims on the Microsoft principal and brings the guest's jobs along.</summary>
    private static async Task OnMicrosoftSignInAsync(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }
        // oid is the account's stable id (personal accounts included); sub is pairwise per app and
        // would change if the registration were ever replaced.
        var objectId = identity.FindFirst(ClaimConstants.ObjectId)?.Value ?? identity.FindFirst("oid")?.Value
                       ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(objectId))
        {
            context.Fail("The Microsoft token carried no account id.");
            return;
        }
        var userId = $"ms:{objectId}";
        identity.AddClaims(PoUser.Stamp(userId, SessionKind.Microsoft));

        // The guest cookie is still on this callback request: it is the last moment the old identity
        // is knowable, so the library moves now or not at all.
        var previous = await context.HttpContext.AuthenticateAsync(SessionScheme);
        if (previous.Principal is { } guest && PoUser.KindOf(guest) == SessionKind.Guest
            && PoUser.IdOf(guest) is { } guestId)
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<JobStore>();
            var moved = await store.ReassignOwnerAsync(guestId, userId, context.HttpContext.RequestAborted);
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(nameof(PoAuth))
                .LogInformation("Moved {Count} guest job(s) into a Microsoft account on sign-in.", moved);
        }
    }
}
