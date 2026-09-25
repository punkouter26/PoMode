using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Chooses which <see cref="ISongInterpreter"/> writes a reply, streams it, checks its figures, and
/// falls through to the next interpreter when one fails — the same contract
/// <c>AnalysisPipeline.RunWithFallbackAsync</c> gives the pipeline stages, for the same reason: a dead
/// local model server must degrade the answer, not the request.
///
/// <para>Order is this seam's own — see <see cref="Rank"/> — a real local model first, then the
/// deterministic template. No cloud interpreter is registered; if one is added, decide its rank here
/// deliberately rather than borrowing the stage planner's, because the cost question is different.
/// The browser's built-in model is not here at all: it runs in the page, which builds the same
/// prompt and runs the same <see cref="GroundingCheck"/> itself.</para>
/// </summary>
public sealed class SongInterpreterSelector(
    IEnumerable<ISongInterpreter> interpreters,
    JobStore store,
    ILogger<SongInterpreterSelector> logger)
{
    /// <summary>
    /// Interpreter order: a real local model, then the deterministic template. Ranking by answer
    /// quality rather than by <see cref="Pipeline.ExecutionPlanner.EffectiveRank"/> is deliberate,
    /// because the cost question a stage planner answers does not apply to one small prompt.
    /// </summary>
    private static int Rank(ISongInterpreter interpreter) => interpreter switch
    {
        { IsClassicFallback: true } => 1,   // the deterministic template: honest, never clever
        _ => 0,                             // a real local model
    };

    /// <summary>Recomputed per call so a configuration change needs no restart.</summary>
    private ISongInterpreter[] Ranked => [.. interpreters.OrderBy(Rank)];

    /// <summary>Every interpreter with its live availability, for the picker.</summary>
    public async Task<List<InterpreterOptionDto>> ListAsync(CancellationToken ct)
    {
        var ranked = Ranked;
        var options = new List<InterpreterOptionDto>(ranked.Length);
        var defaultAssigned = false;

        foreach (var interpreter in ranked)
        {
            var available = await SafeIsAvailableAsync(interpreter, ct);
            // Simply the first available entry in rank order — the one an unnamed request gets.
            var isDefault = !defaultAssigned && available;
            defaultAssigned |= isDefault;

            options.Add(new InterpreterOptionDto(
                Name: interpreter.Name,
                Tier: interpreter.Tier,
                Available: available,
                IsDefault: isDefault,
                UsesLlm: !interpreter.IsClassicFallback));
        }

        return options;
    }

    /// <summary>
    /// Writes the summary (<paramref name="question"/> null) or an answer, as events: each field's
    /// text as it is written, a restart when an attempt is abandoned, and the checked result last.
    /// Never throws for a bad name or a failing model: an unknown name is ignored and every failure
    /// falls through. The template is always available, so this always finishes with a result.
    ///
    /// <para>A summary is cached per job and interpreter, keyed on a hash of the whole prompt, so
    /// opening a song again costs a file read rather than a minute of model time, and a change to
    /// the prompt or to the measurements invalidates it by itself. An answer is not cached: asking
    /// again is a deliberate act.</para>
    /// </summary>
    public async IAsyncEnumerable<InterpretationEvent> StreamAsync(
        string jobId,
        SongStats stats,
        string? question,
        IReadOnlyList<InterpretationTurn>? history,
        string? requested,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var prompt = question is null ? InterpretationPrompt.For(stats) : QuestionPrompt.For(stats, history, question);
        var promptHash = Hash(prompt);
        var streamed = false;

        foreach (var interpreter in Candidates(requested))
        {
            if (!await SafeIsAvailableAsync(interpreter, ct))
            {
                continue;
            }

            if (question is null
                && await store.ReadArtifactAsync<CachedInterpretation>(jobId, CacheName(interpreter), ct) is { } cached
                && cached.PromptHash == promptHash)
            {
                yield return new InterpretationEvent(Summary: cached.Result);
                yield break;
            }

            var request = new InterpretationRequest(stats, question, prompt);
            // A model gets one more attempt when it invents a figure, told which ones. The template
            // states only measured figures, in its own rounding, so it is never checked.
            var attempts = interpreter.IsClassicFallback ? 1 : 2;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (streamed)
                {
                    yield return new InterpretationEvent(Restart: true);
                    streamed = false;
                }

                var reader = new ReplyReader();
                var raw = new StringBuilder();
                Exception? failure = null;
                await using (var chunks = interpreter.ReplyAsync(request, ct).GetAsyncEnumerator(ct))
                {
                    while (true)
                    {
                        try
                        {
                            if (!await chunks.MoveNextAsync())
                            {
                                break;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            failure = ex;
                            break;
                        }

                        raw.Append(chunks.Current);
                        foreach (var (field, text) in reader.Push(chunks.Current))
                        {
                            streamed = true;
                            yield return new InterpretationEvent(Field: field, Text: text);
                        }
                    }
                }

                var result = failure is null ? Finish(interpreter, question, reader) : null;
                if (result is null)
                {
                    logger.LogWarning(failure,
                        "Interpreter {Interpreter} failed; falling through to the next one.", interpreter.Name);
                    break;
                }

                var unsupported = interpreter.IsClassicFallback
                    ? []
                    : GroundingCheck.Unsupported(Prose(result), prompt.Shown);
                if (unsupported.Count > 0)
                {
                    logger.LogWarning(
                        "Interpreter {Interpreter} wrote figures the measurements do not contain ({Figures}) "
                        + "on attempt {Attempt}.", interpreter.Name, string.Join(", ", unsupported), attempt);
                    request = request with { Prompt = prompt.Corrected(raw.ToString(), unsupported) };
                    continue;
                }

                if (result.Summary is { } summary)
                {
                    await store.WriteArtifactAsync(
                        jobId, CacheName(interpreter), new CachedInterpretation(promptHash, summary), ct);
                }
                yield return result;
                yield break;
            }
        }

        // Unreachable while TemplateSongInterpreter is registered, but a missing registration must
        // produce a clear message rather than a null-reference somewhere downstream.
        throw new InvalidOperationException("No song interpreter was able to produce a result.");
    }

    /// <summary>The finished result from a complete reply, or null when a required field is empty —
    /// a failure that did not throw, which would otherwise show the reader a blank bubble.</summary>
    private static InterpretationEvent? Finish(ISongInterpreter interpreter, string? question, ReplyReader reply)
    {
        var usedLlm = !interpreter.IsClassicFallback;
        if (question is null)
        {
            return reply[InterpretationPrompt.PlainField]?.Trim() is { Length: > 0 } plain
                ? new InterpretationEvent(Summary: new SongInterpretationDto(
                    interpreter.Name, interpreter.Tier, usedLlm, plain,
                    reply[InterpretationPrompt.TheoryField]?.Trim() is { Length: > 0 } theory ? theory : null))
                : null;
        }

        return reply[QuestionPrompt.AnswerField]?.Trim() is { Length: > 0 } answer
            ? new InterpretationEvent(Answer: new SongAnswerDto(
                question, answer, interpreter.Name, interpreter.Tier, usedLlm,
                Grounded: !bool.TryParse(reply[QuestionPrompt.InDataField], out var inData) || inData))
            : null;
    }

    private static string Prose(InterpretationEvent result)
        => result.Summary is { } summary ? $"{summary.Text}\n{summary.TheoryText}" : result.Answer!.Answer;

    private static string CacheName(ISongInterpreter interpreter) => $"interpretation-{interpreter.Name}.json";

    private static string Hash(ChatPrompt prompt) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{prompt.System}\n{prompt.Schema}\n{prompt.Shown}")));

    internal sealed record CachedInterpretation(string PromptHash, SongInterpretationDto Result);

    /// <summary>
    /// The try order: the named interpreter first if it exists, then everything else in rank order.
    /// Naming one is how a caller reaches an interpreter that does not rank first.
    /// </summary>
    private IEnumerable<ISongInterpreter> Candidates(string? requested)
    {
        var ranked = Ranked;
        ISongInterpreter? pick = null;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            pick = ranked.FirstOrDefault(
                interpreter => string.Equals(interpreter.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (pick is null)
            {
                logger.LogInformation(
                    "Ignoring unknown interpreter '{Requested}'; using the default order.", requested);
            }
            else
            {
                yield return pick;
            }
        }

        foreach (var interpreter in ranked)
        {
            if (!ReferenceEquals(interpreter, pick))
            {
                yield return interpreter;
            }
        }
    }

    /// <summary>
    /// An availability probe reaches the network (Ollama's socket), so a broken one must not turn a
    /// listing into a 500. A throwing probe means "not available".
    /// </summary>
    private async Task<bool> SafeIsAvailableAsync(ISongInterpreter interpreter, CancellationToken ct)
    {
        try
        {
            return await interpreter.IsAvailableAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Availability probe for {Interpreter} threw; treating as unavailable.",
                interpreter.Name);
            return false;
        }
    }
}
