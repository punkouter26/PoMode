using System.ClientModel;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// The optional cloud interpreter: a small Azure OpenAI deployment, for the hosted app, which has no
/// local model server. It answers the same prompt under the same JSON schema as Ollama, and its
/// replies go through the same <see cref="GroundingCheck"/>, so nothing about the contract changes
/// with where the words are written.
///
/// <para>Available only when <c>PoMode:AzureOpenAI:Endpoint</c> and <c>:Deployment</c> are set (the
/// Bicep sets both when the cloud interpreter is switched on). No key exists anywhere: the account
/// has local auth disabled and this signs in with <see cref="DefaultAzureCredential"/>, the app's
/// managed identity in Azure and the developer's own sign-in locally.</para>
///
/// <para>It ranks after a local model and before the template (SongInterpreterSelector): free first,
/// then paid, then the honest fallback. What it costs is in docs/ai-cost.md.</para>
/// </summary>
public sealed class AzureOpenAiSongInterpreter(IConfiguration configuration) : ISongInterpreter
{
    /// <summary>Same cap as the local model, for the same reason: a reply that runs into it is a loop,
    /// and is treated as a failure so the selector falls through.</summary>
    private const int MaxReplyTokens = 1536;

    private ChatClient? _chat;

    public string Name => nameof(AzureOpenAiSongInterpreter);

    public ExecutionTier Tier => ExecutionTier.Cloud;

    private string? Endpoint => configuration["PoMode:AzureOpenAI:Endpoint"] is { Length: > 0 } value ? value : null;

    private string? Deployment => configuration["PoMode:AzureOpenAI:Deployment"] is { Length: > 0 } value ? value : null;

    /// <summary>Configured, that is all: probing a paid endpoint on every page load would cost a call.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Endpoint is not null && Deployment is not null);

    public async IAsyncEnumerable<string> ReplyAsync(InterpretationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        _chat ??= new AzureOpenAIClient(new Uri(Endpoint!), new DefaultAzureCredential()).GetChatClient(Deployment!);
        var prompt = request.Prompt;
        List<OpenAI.Chat.ChatMessage> messages = [new SystemChatMessage(prompt.System)];
        foreach (var message in prompt.Messages)
        {
            messages.Add(message.Role == "user"
                ? new UserChatMessage(message.Content)
                : new AssistantChatMessage(message.Content));
        }
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("reply", BinaryData.FromString(prompt.Schema)),
            MaxOutputTokenCount = MaxReplyTokens,
            Temperature = 0.4f,
        };

        AsyncCollectionResult<StreamingChatCompletionUpdate> updates = _chat.CompleteChatStreamingAsync(messages, options, ct);
        await foreach (var update in updates.WithCancellation(ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (part.Text is { Length: > 0 } text)
                {
                    yield return text;
                }
            }
            if (update.FinishReason == ChatFinishReason.Length)
            {
                throw new InvalidOperationException($"The reply ran past {MaxReplyTokens} tokens and was cut off.");
            }
        }
    }
}
