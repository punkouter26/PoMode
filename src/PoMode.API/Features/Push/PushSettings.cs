using System.Buffers.Text;
using System.Security.Cryptography;

namespace PoMode.API.Features.Push;

/// <summary>
/// This server's VAPID identity — the key pair push services use to know every message for a
/// subscription comes from the server that created it. Resolved once, at startup.
///
/// <para>The public key may sit in appsettings (<c>PoMode:Push:VapidPublicKey</c>); the private key
/// comes from Key Vault as <c>PoMode--Push--VapidPrivateKey</c>, like every other secret here. Push is
/// on only when both are present and well-formed. Development without them gets an ephemeral pair so
/// the feature can be exercised locally; everywhere else it is simply off, and the client never
/// offers it.</para>
/// </summary>
public sealed record PushSettings(string? PublicKey, string? PrivateKey, string Subject, bool Ephemeral)
{
    public const string Section = "PoMode:Push";

    /// <summary>The contact push services are given for this sender. Must be an https or mailto URI;
    /// the project page rather than a person's address, since it travels in every request header.</summary>
    private const string DefaultSubject = "https://github.com/punkouter26/PoMode";

    public bool Available => PublicKey is not null && PrivateKey is not null;

    public static PushSettings Resolve(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var section = configuration.GetSection(Section);
        var subject = section["Subject"] is { Length: > 0 } configured ? configured : DefaultSubject;
        var publicKey = section["VapidPublicKey"];
        var privateKey = section["VapidPrivateKey"];

        if (!string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(privateKey))
        {
            if (IsKeyPair(publicKey.Trim(), privateKey.Trim()))
            {
                return new PushSettings(publicKey.Trim(), privateKey.Trim(), subject, Ephemeral: false);
            }
            // Off rather than a startup exception: a bad key costs notifications, and an app that
            // will not boot over a notification key costs everything else too.
            logger.LogError("{Section} keys are configured but are not a P-256 VAPID pair; push notifications are off.", Section);
            return new PushSettings(null, null, subject, Ephemeral: false);
        }

        if (environment.IsDevelopment())
        {
            var (ephemeralPublic, ephemeralPrivate) = GenerateKeyPair();
            logger.LogWarning(
                "No VAPID key pair configured; generated an ephemeral one for this run. Push subscriptions made now stop working when the API restarts (the browser re-subscribes on its next visit).");
            return new PushSettings(ephemeralPublic, ephemeralPrivate, subject, Ephemeral: true);
        }

        return new PushSettings(null, null, subject, Ephemeral: false);
    }

    /// <summary>A fresh P-256 pair in the encoding browsers and VAPID use: base64url, the public key
    /// as a 65-byte uncompressed point and the private key as the 32-byte scalar.</summary>
    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        var point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1);
        parameters.Q.Y!.CopyTo(point, 33);
        return (Base64Url.EncodeToString(point), Base64Url.EncodeToString(parameters.D!));
    }

    private static bool IsKeyPair(string publicKey, string privateKey)
    {
        try
        {
            var point = Base64Url.DecodeFromChars(publicKey);
            var scalar = Base64Url.DecodeFromChars(privateKey);
            return point.Length == 65 && point[0] == 0x04 && scalar.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
