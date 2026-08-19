using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Chooses which <see cref="ISongInterpreter"/> writes an interpretation, and falls through to the
/// next one when the chosen one fails — the same contract <c>AnalysisPipeline.RunWithFallbackAsync</c>
/// gives the pipeline stages, for the same reason: a dead local model server must degrade the answer,
/// not the request.
///
/// <para>Order comes from <see cref="ExecutionPlanner.EffectiveRank"/>, so it needs no rules of its
/// own: local LLM (0) → template (5) → cloud (8). Cloud is excluded from the automatic order
/// entirely and is only ever reached when the caller names it, because falling through to a paid
/// provider would spend money the user never asked to spend.</para>
/// </summary>
public sealed class SongInterpreterSelector(
    IEnumerable<ISongInterpreter> interpreters,
    ILogger<SongInterpreterSelector> logger)
{
    private readonly ISongInterpreter[] _ranked =
        [.. interpreters.OrderBy(ExecutionPlanner.EffectiveRank)];

    /// <summary>Every interpreter with its live availability, for the picker. Cloud is listed —
    /// unlike the stage pickers — because naming it is the only way to reach it, so hiding it
    /// would make it unreachable rather than merely non-default.</summary>
    public async Task<List<InterpreterOptionDto>> ListAsync(CancellationToken ct)
    {
        var options = new List<InterpreterOptionDto>(_ranked.Length);
        var defaultAssigned = false;

        foreach (var interpreter in _ranked)
        {
            var available = await SafeIsAvailableAsync(interpreter, ct);
            // The default is the first available non-Cloud entry — the one an unnamed request gets.
            var isDefault = !defaultAssigned && available && interpreter.Tier != ExecutionTier.Cloud;
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
    /// The try order: the named interpreter first if it exists, then every free one in rank order.
    /// A Cloud interpreter that was not named never appears.
    /// </summary>
    private IEnumerable<ISongInterpreter> Candidates(string? requested)
    {
        ISongInterpreter? pick = null;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            pick = _ranked.FirstOrDefault(
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

        foreach (var interpreter in _ranked)
        {
            if (!ReferenceEquals(interpreter, pick) && interpreter.Tier != ExecutionTier.Cloud)
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
