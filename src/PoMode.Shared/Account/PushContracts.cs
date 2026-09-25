namespace PoMode.Shared.Account;

/// <summary>Whether this server can send push notifications, and the VAPID public key a browser
/// subscribes with. <see cref="PublicKey"/> is null exactly when <see cref="Available"/> is false.</summary>
public sealed record PushConfigDto(bool Available, string? PublicKey);

/// <summary>A browser's push subscription as <c>PushSubscription.toJSON()</c> gives it, flattened:
/// the push service URL plus the two keys the payload is encrypted to (both base64url).</summary>
public sealed record PushSubscriptionDto(string Endpoint, string P256dh, string Auth);
