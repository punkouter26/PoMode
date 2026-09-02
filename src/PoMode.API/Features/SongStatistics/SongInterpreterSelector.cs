using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Chooses which <see cref="ISongInterpreter"/> writes an interpretation, and falls through to the
/// next one when the chosen one fails — the same contract <c>AnalysisPipeline.RunWithFallbackAsync</c>
/// gives the pipeline stages, for the same reason: a dead local model server must degrade the answer,
/// not the request.
///
/// <para>Order is this seam's own — see <see cref="Rank"/> — a real local model first, then the
/// deterministic template. No cloud interpreter is registered; if one is added, decide its rank here
/// deliberately rather than borrowing the stage planner's, because the cost question is different.
/// An interpretation is one small prompt, where a pipeline stage falling through to a paid provider
/// is a whole separation or transcription on the bill.</para>
/// </summary>
public sealed class SongInterpreterSelector(
    IEnumerable<ISongInterpreter> interpreters,
    ILogger<SongInterpreterSelector> logger)
{
    /// <summary>
    /// Interpreter order: a real local model, then the deterministic template. Ranking by answer
    /// quality rather than by <see cref="ExecutionPlanner.EffectiveRank"/> is deliberate, because the
    /// cost question a stage planner answers does not apply to one small prompt.
    /// </summary>
    private static int Rank(ISongInterpreter interpreter) => interpreter switch
    {
        { IsClassicFallback: true } => 1,   // the deterministic template: honest, never clever
        _ => 0,                             // a real local model
    };

    /// <summary>Recomputed per call so a configuration change needs no restart.</summary>
    private ISongInterpreter[] Ranked => [.. interpreters.OrderBy(Rank)];

    /// <summary>
    /// Every interpreter with its live availability, for the picker. Each carries its tier, so a paid
    /// model added later is never hidden from whoever is paying.
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
