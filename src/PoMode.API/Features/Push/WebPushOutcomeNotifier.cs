using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Push;

/// <summary>
/// Sends a Web Push notification to a job's owner when it finishes, on every browser they opted in
/// from. The job's owner only: the id on the job is the whole of the targeting, so nobody else's
/// browser can learn a song name from it. Nobody subscribed, nothing sent.
/// </summary>
public sealed partial class WebPushOutcomeNotifier(
    PushSettings settings,
    PushSubscriptionStore subscriptions,
    IHttpClientFactory httpClients,
    ILogger<WebPushOutcomeNotifier> logger) : IJobOutcomeNotifier
{
    public const string HttpClientName = "web-push";

    /// <summary>A day: a phone that is off overnight still hears about last night's upload, and a
    /// notice older than that is about a job the owner has long since found in their library.</summary>
    private const int TimeToLiveSeconds = 24 * 60 * 60;

    public async Task NotifyAsync(JobState state, CancellationToken ct)
    {
        if (!settings.Available || state.OwnerId is not { } owner
            || state.Stage is not (JobStage.Complete or JobStage.Failed))
        {
            return;
        }
        var targets = await subscriptions.ListAsync(owner, ct);
        if (targets.Count == 0)
        {
            return;
        }

        var (title, body) = Compose(state);
        var payload = JsonSerializer.Serialize(
            new PushPayload(title, body, $"/?job={state.JobId}", state.JobId), JobStore.JsonOptions);
        var client = new PushServiceClient(httpClients.CreateClient(HttpClientName))
        {
            DefaultAuthentication = new VapidAuthentication(settings.PublicKey!, settings.PrivateKey!)
            {
                Subject = settings.Subject,
            },
        };

        foreach (var target in targets)
        {
            var subscription = new PushSubscription { Endpoint = target.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, target.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, target.Auth);
            try
            {
                await client.RequestPushMessageDeliveryAsync(subscription, new PushMessage(payload)
                {
                    TimeToLive = TimeToLiveSeconds,
                    // The job id is 32 url-safe characters, exactly a valid topic: a re-run of the
                    // same job replaces its undelivered notice rather than stacking a second one.
                    Topic = state.JobId,
                    Urgency = PushMessageUrgency.Normal,
                }, ct);
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // The push service's way of saying the browser unsubscribed or the subscription
                // expired. It will never work again, so it goes rather than being retried forever.
                await subscriptions.RemoveAsync(owner, target.Endpoint, ct);
                logger.LogInformation("Removed an expired push subscription for job {JobId}'s owner.", state.JobId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One unreachable browser must not stop the others being told.
                logger.LogWarning(ex, "Push delivery failed for job {JobId}.", state.JobId);
            }
        }
    }

    /// <summary>
    /// The notification's words, decided here like every other sentence the app says about music.
    /// A finished job leads with what was found — the reason to tap — and says nothing it does not
    /// know: no key without a tonic, no tempo without a figure. A failure does not put the
    /// exception's text on a lock screen; the analysis page shows it to the person who opens it.
    /// </summary>
    public static (string Title, string Body) Compose(JobState state)
    {
        var name = state.InputFileName;
        if (state.Stage == JobStage.Failed)
        {
            return ($"'{name}' could not be analyzed", "The analysis stopped with an error. Tap to see what happened.");
        }

        var facts = new List<string>(2);
        if (state.TonicName is { Length: > 0 } tonic)
        {
            facts.Add(state.PrimaryMode is { Length: > 0 } mode ? $"{tonic} {Spaced(mode)}" : $"Key of {tonic}");
        }
        if (state.TempoBpm is { } bpm && bpm > 0)
        {
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"{bpm:0} BPM"));
        }
        var lead = facts.Count == 0 ? "" : string.Join(", ", facts) + ". ";
        return ($"'{name}' is ready", $"{lead}Tap to open the analysis.");
    }

    /// <summary>"MinorPentatonic" → "Minor Pentatonic": the enum name, readable.</summary>
    private static string Spaced(string mode) => WordBoundary().Replace(mode, " ");

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>What service-worker.js's push handler reads.</summary>
    private sealed record PushPayload(string Title, string Body, string Url, string Tag);
}
