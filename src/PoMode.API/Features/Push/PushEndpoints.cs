using System.Buffers.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Auth;
using PoMode.Shared.Account;

namespace PoMode.API.Features.Push;

public static class PushEndpoints
{
    /// <summary>
    /// The push services browsers actually subscribe through: Chrome, Edge-on-Android, Samsung and
    /// Opera use FCM; Firefox uses Mozilla's autopush; Edge on Windows uses WNS; Safari uses Apple's.
    /// The server POSTs to whatever endpoint a subscription names, so without this list a subscription
    /// is a way to make it send requests anywhere — an internal address included.
    /// </summary>
    private static readonly string[] PushServiceHosts =
    [
        "fcm.googleapis.com",
        "push.services.mozilla.com",
        "notify.windows.com",
        "push.apple.com",
    ];

    public static IEndpointRouteBuilder MapPush(this IEndpointRouteBuilder app)
    {
        // Signed-in only, like the library: a subscription belongs to the person whose jobs it is for.
        var group = app.MapGroup("/api/push").RequireAuthorization().WithTags("Push");

        group.MapGet("", (PushSettings settings)
                => TypedResults.Ok(new PushConfigDto(settings.Available, settings.Available ? settings.PublicKey : null)))
            .WithSummary("Whether this server sends push notifications, and the VAPID key to subscribe with.");

        group.MapPost("/subscriptions", async Task<Results<NoContent, BadRequest<string>, NotFound>> (
                PushSubscriptionDto subscription,
                HttpContext context,
                PushSettings settings,
                PushSubscriptionStore store,
                CancellationToken ct) =>
            {
                if (!settings.Available || PoUser.IdOf(context.User) is not { } owner)
                {
                    return TypedResults.NotFound();
                }
                if (Problem(subscription) is { } problem)
                {
                    return TypedResults.BadRequest(problem);
                }
                await store.AddAsync(owner, subscription, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Notify this browser when the caller's analyses finish. Idempotent per endpoint.");

        // The endpoint rides the query string rather than a DELETE body, which proxies may drop.
        group.MapDelete("/subscriptions", async Task<Results<NoContent, NotFound>> (
                string endpoint,
                HttpContext context,
                PushSubscriptionStore store,
                CancellationToken ct) =>
            {
                if (PoUser.IdOf(context.User) is not { } owner)
                {
                    return TypedResults.NotFound();
                }
                await store.RemoveAsync(owner, endpoint, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Stop notifying this browser about the caller's analyses.");

        return app;
    }

    private static string? Problem(PushSubscriptionDto subscription)
    {
        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !PushServiceHosts.Any(host => endpoint.Host == host || endpoint.Host.EndsWith("." + host, StringComparison.Ordinal)))
        {
            return "The endpoint is not a recognised browser push service.";
        }
        return DecodedLength(subscription.P256dh) == 65 && DecodedLength(subscription.Auth) == 16
            ? null
            : "The subscription's keys are not a P-256 public key and a 16-byte auth secret.";
    }

    private static int DecodedLength(string? value)
    {
        try
        {
            return string.IsNullOrEmpty(value) ? 0 : Base64Url.DecodeFromChars(value).Length;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}
