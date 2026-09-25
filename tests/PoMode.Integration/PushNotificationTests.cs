using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Push;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// The push path from a finished job to the push service, against a stand-in push service: real
/// VAPID signing and payload encryption, real subscription files on disk. What it cannot cover is a
/// browser actually receiving and showing the notification.
/// </summary>
public sealed class PushNotificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-push-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _root + "-push" })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private PushSubscriptionStore NewStore() => new(
        new JobStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(), TimeProvider.System),
        TimeProvider.System);

    [Fact]
    public async Task A_finished_job_notifies_only_its_owner_and_drops_subscriptions_the_push_service_calls_gone()
    {
        var ct = CancellationToken.None;
        var store = NewStore();
        await store.AddAsync("guest:alice", BrowserSubscription("alice-phone"), ct);
        await store.AddAsync("guest:alice", BrowserSubscription("alice-old-laptop"), ct);
        await store.AddAsync("guest:bob", BrowserSubscription("bob-phone"), ct);

        var pushService = new StandInPushService(goneToken: "alice-old-laptop");
        var (publicKey, privateKey) = PushSettings.GenerateKeyPair();
        var notifier = new WebPushOutcomeNotifier(
            new PushSettings(publicKey, privateKey, "https://example.test", Ephemeral: false),
            store, pushService, NullLogger<WebPushOutcomeNotifier>.Instance);
        var job = new JobState
        {
            JobId = Guid.NewGuid().ToString("N"),
            InputFileName = "song.mp3",
            CreatedAt = DateTimeOffset.UtcNow,
            OwnerId = "guest:alice",
            Stage = JobStage.Complete,
            TonicName = "D",
            PrimaryMode = "Dorian",
            TempoBpm = 96,
        };

        await notifier.NotifyAsync(job, ct);

        // Both of Alice's browsers were tried; Bob's job list is not Alice's business, nor is her song.
        Assert.Equal(["alice-old-laptop", "alice-phone"], pushService.Requests.Select(r => r.Token).Order());
        Assert.All(pushService.Requests, request =>
        {
            Assert.StartsWith("vapid ", request.Authorization, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("aes128gcm", request.ContentEncoding);
            Assert.Equal(job.JobId, request.Topic);
            Assert.True(request.BodyLength > 0, "The payload is encrypted and sent, not dropped.");
        });

        // The subscription the service called gone is deleted from disk: a fresh store over the same
        // folder — which is what a restart is — sees only the browser that still works.
        var afterRestart = await NewStore().ListAsync("guest:alice", ct);
        Assert.Equal(PushEndpointFor("alice-phone"), Assert.Single(afterRestart).Endpoint);
        Assert.Single(await NewStore().ListAsync("guest:bob", ct));

        // The words on the lock screen: what was found first, and nothing the job did not measure.
        Assert.Equal(("'song.mp3' is ready", "D Dorian, 96 BPM. Tap to open the analysis."),
            WebPushOutcomeNotifier.Compose(job));
    }

    private static string PushEndpointFor(string token) => $"https://fcm.googleapis.com/fcm/send/{token}";

    /// <summary>What a browser's PushManager hands back: a P-256 public key and a 16-byte secret.</summary>
    private static PushSubscriptionDto BrowserSubscription(string token)
    {
        using var browserKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = browserKey.ExportParameters(includePrivateParameters: false).Q;
        return new PushSubscriptionDto(
            PushEndpointFor(token),
            Base64Url.EncodeToString([0x04, .. q.X!, .. q.Y!]),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));
    }

    private sealed record PushRequest(string Token, string Authorization, string ContentEncoding, string Topic, long BodyLength);

    /// <summary>Accepts every push (201) except for one token, which it reports gone (410).</summary>
    private sealed class StandInPushService(string goneToken) : HttpMessageHandler, IHttpClientFactory
    {
        public List<PushRequest> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.RequestUri!.Segments[^1];
            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            lock (Requests)
            {
                Requests.Add(new PushRequest(
                    token,
                    request.Headers.Authorization?.ToString() ?? "",
                    string.Join(",", request.Content.Headers.ContentEncoding),
                    request.Headers.TryGetValues("Topic", out var topic) ? topic.Single() : "",
                    body.Length));
            }
            return new HttpResponseMessage(token == goneToken ? HttpStatusCode.Gone : HttpStatusCode.Created);
        }
    }
}
