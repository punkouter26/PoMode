using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Chooses which <see cref="ISongInterpreter"/> writes an interpretation, and falls through to the
/// next one when the chosen one fails — the same contract <c>AnalysisPipeline.RunWithFallbackAsync</c>
/// gives the pipeline stages, for the same reason: a dead local model server must degrade the answer,
/// not the request.
///
/// <para>Order is this seam's own — see <see cref="Rank"/> — and by default puts the cloud model
/// first, then a local model, then the template. That is a different rule from the stage planner's
/// on purpose: an interpretation is one small prompt costing a fraction of a cent, where a pipeline
/// stage falling through to a paid provider is a whole separation or transcription on the bill.</para>
/// </summary>
public sealed class SongInterpreterSelector(
    IEnumerable<ISongInterpreter> interpreters,
    IConfiguration configuration,
    ILogger<SongInterpreterSelector> logger)
{
    /// <summary>
    /// Whether the cloud interpreter may be chosen automatically. On by default: a measured
    /// interpretation is about 1,800 tokens, which on the configured nano-tier deployment is a
    /// fraction of a cent, and it answers faster and better than the local model. Set
    /// <c>Llm:PreferCloud</c> to false for the local-first order instead.
    /// </summary>
    private bool PreferCloud => configuration.GetValue("Llm:PreferCloud", defaultValue: true);

    /// <summary>
    /// Interpreter order, deliberately NOT <see cref="ExecutionPlanner.EffectiveRank"/>.
    ///
    /// <para>That ranking puts Cloud last because a pipeline stage falling through to a paid provider
    /// means a whole stem separation or transcription on someone's bill. An interpretation is nothing
    /// like that: one small prompt, a fraction of a cent, no per-second billing. So this seam ranks by
    /// answer quality — cloud model, then local model, then the template — while the stage planner
    /// keeps its own stricter rule untouched. The two questions only looked alike.</para>
    /// </summary>
    private int Rank(ISongInterpreter interpreter) => interpreter switch
    {
        { Tier: ExecutionTier.Cloud } => PreferCloud ? 0 : 3,
        { IsClassicFallback: true } => 2,   // the deterministic template: honest, never clever
        _ => 1,                             // a real local model
    };

    /// <summary>Recomputed per call so a configuration change needs no restart.</summary>
    private ISongInterpreter[] Ranked => [.. interpreters.OrderBy(Rank)];

    /// <summary>
    /// Every interpreter with its live availability, for the picker. The cloud entry is listed and,
    /// with <c>Llm:PreferCloud</c> on, is the default — it is still tagged as a paid model in the UI
    /// so the choice is never hidden from whoever is paying.
    /// </summary>
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
    /// Writes an interpretation, trying <paramref name="requested"/> first when it names a
    /// registered interpreter. Never throws for a bad name or a failing model: an unknown name is
    /// logged and ignored, and every failure falls through. The template interpreter is always
    /// available, so this always returns something.
    /// </summary>
    public async Task<SongInterpretationDto> InterpretAsync(
        SongStats stats, string? requested, CancellationToken ct)
    {
        foreach (var interpreter in Candidates(requested))
        {
            if (!await SafeIsAvailableAsync(interpreter, ct))
            {
                continue;
            }

            try
            {
                // Every interpreter returns one string with both audiences separated by the shared
                // delimiter; splitting here means the template and the LLMs need no separate handling
                // and a model that ignores the delimiter degrades to a single summary rather than an
                // error.
                var (plain, theory) = InterpretationPrompt.Split(await interpreter.InterpretAsync(stats, ct));
                return new SongInterpretationDto(
                    Interpreter: interpreter.Name,
                    Tier: interpreter.Tier,
                    UsedLlm: !interpreter.IsClassicFallback,
                    Text: plain,
                    TheoryText: theory);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Interpreter {Interpreter} failed; falling through to the next one.", interpreter.Name);
            }
        }

        // Unreachable while TemplateSongInterpreter is registered, but a missing registration must
        // produce a clear message rather than a null-reference somewhere downstream.
        throw new InvalidOperationException("No song interpreter was able to produce a result.");
    }

    /// <summary>
    /// The try order: the named interpreter first if it exists, then everything else in rank order.
    /// With <c>Llm:PreferCloud</c> off the cloud entry ranks last, so it is then reachable only by
    /// name — the behaviour this seam had before cloud became the default.
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

        // Everything else follows, cloud included. The fallthrough matters more now that cloud can be
        // the default: a dropped network or an expired credential has to degrade to the local model
        // and then the template, never to an error.
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
