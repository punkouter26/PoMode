using System.Text.Json;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Serialization;
using Xunit;

namespace PoMode.Unit.Serialization;

public class JsonContextTests
{
    /// <summary>
    /// One pass over every contract the source-generated context has to carry that nothing else
    /// covers. Note and chord lists are left out on purpose: the E2EAPI artifact tests deserialize
    /// those straight off the wire, which is stronger evidence than a round trip here.
    /// </summary>
    [Fact]
    public void Every_contract_without_wire_coverage_round_trips_through_the_source_gen_context()
    {
        var report = new DiagnosticsReport(
            EnvironmentName: "Development",
            IsAzureHosted: false,
            SecretSource: "EnvironmentVariables",
            SecretFellBack: true,
            Hardware: null);

        var backReport = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(report, PoModeJsonContext.Default.DiagnosticsReport),
            PoModeJsonContext.Default.DiagnosticsReport);

        Assert.NotNull(backReport);
        Assert.Equal("Development", backReport.EnvironmentName);
        Assert.True(backReport.SecretFellBack);
        Assert.Equal("EnvironmentVariables", backReport.SecretSource);

        var status = new JobStatusDto(
            JobId: "abc123",
            Stage: JobStage.PitchTracking,
            Progress: 0.25,
            Plan: [new StagePlan("Separating", ExecutionTier.Local, "FakeStemSeparator")],
            CompletedStages: ["Separating"],
            Error: null,
            CreatedAt: DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

        var backStatus = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(status, PoModeJsonContext.Default.JobStatusDto),
            PoModeJsonContext.Default.JobStatusDto);

        Assert.NotNull(backStatus);
        Assert.Equal(JobStage.PitchTracking, backStatus.Stage);
        Assert.Equal("FakeStemSeparator", backStatus.Plan[0].Executor);
        Assert.Equal(["Separating"], backStatus.CompletedStages);

        var result = new ModalResult(
            SchemaVersion: 1,
            TonicPitchClass: 2,
            TonicName: "D",
            TonicConfidence: 0.82,
            PrimaryMode: ScaleMode.Dorian,
            PrimaryConfidence: 0.9,
            TempoBpm: 120.0,
            TempoEstimated: true,
            Windows:
            [
                new ModalWindow(
                    Index: 0,
                    StartSec: 0,
                    EndSec: 2,
                    ChordSymbol: "Dm7",
                    MeasureNumber: 1,
                    VocalMask: 0b011010101101,
                    SungIntervals: [0, 2, 3, 5, 7, 9, 10],
                    InsufficientEvidence: false,
                    Matches: [new ModalMatch(ScaleMode.Dorian, 1.0, [0, 2, 3], [])])
            ],
            TuningOffsetCents: -6.5);

        var backResult = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(result, PoModeJsonContext.Default.ModalResult),
            PoModeJsonContext.Default.ModalResult);

        Assert.NotNull(backResult);
        Assert.Equal(ScaleMode.Dorian, backResult.PrimaryMode);
        Assert.Equal("D", backResult.TonicName);
        Assert.Equal(1, backResult.Windows[0].MeasureNumber);
        Assert.Equal(ScaleMode.Dorian, backResult.Windows[0].Matches[0].Mode);
        Assert.True(backResult.TempoEstimated);
        // result.json is persisted, so the measured offset has to survive a round trip.
        Assert.Equal(-6.5, backResult.TuningOffsetCents);
    }

    /// <summary>A result written before tuning measurement existed must still load, at zero.</summary>
    [Fact]
    public void A_result_written_without_a_tuning_offset_loads_as_uncorrected()
    {
        const string legacy = """
            {"schemaVersion":1,"tonicPitchClass":0,"tonicName":"C","tonicConfidence":0.5,
             "primaryMode":null,"primaryConfidence":0,"tempoBpm":120,"tempoEstimated":true,"windows":[]}
            """;

        var back = JsonSerializer.Deserialize(legacy, PoModeJsonContext.Default.ModalResult);

        Assert.NotNull(back);
        Assert.Equal(0.0, back.TuningOffsetCents);
    }
}
