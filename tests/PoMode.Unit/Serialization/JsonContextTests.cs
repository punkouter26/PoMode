using System.Text.Json;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Hardware;
using PoMode.Shared.Serialization;
using Xunit;

namespace PoMode.Unit.Serialization;

public class JsonContextTests
{
    [Fact]
    public void DiagnosticsReport_round_trips_via_source_gen_context()
    {
        var report = new DiagnosticsReport(
            EnvironmentName: "Development",
            IsAzureHosted: false,
            SecretSource: "EnvironmentVariables",
            SecretFellBack: true,
            Hardware: null);

        var json = JsonSerializer.Serialize(report, PoModeJsonContext.Default.DiagnosticsReport);
        var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.DiagnosticsReport);

        Assert.NotNull(back);
        Assert.Equal("Development", back.EnvironmentName);
        Assert.True(back.SecretFellBack);
        Assert.Equal("EnvironmentVariables", back.SecretSource);
    }

    [Fact]
    public void JobStatusDto_round_trips_via_source_gen_context()
    {
        var dto = new JobStatusDto(
            JobId: "abc123",
            Stage: JobStage.PitchTracking,
            Progress: 0.25,
            Plan: [new StagePlan("Separating", ExecutionTier.Local, "FakeStemSeparator")],
            CompletedStages: ["Separating"],
            Error: null,
            CreatedAt: DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

        var json = JsonSerializer.Serialize(dto, PoModeJsonContext.Default.JobStatusDto);
        var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.JobStatusDto);

        Assert.NotNull(back);
        Assert.Equal(JobStage.PitchTracking, back.Stage);
        Assert.Equal("FakeStemSeparator", back.Plan[0].Executor);
        Assert.Equal(["Separating"], back.CompletedStages);
    }

    // Note/chord list contexts are exercised end-to-end by the E2EAPI artifact tests, which
    // deserialize them straight off the wire; only the families with no such coverage stay here.
    [Fact]
    public void ModalResult_round_trips_via_source_gen_context()
    {
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
            ]);

        var json = JsonSerializer.Serialize(result, PoModeJsonContext.Default.ModalResult);
        var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.ModalResult);

        Assert.NotNull(back);
        Assert.Equal(ScaleMode.Dorian, back.PrimaryMode);
        Assert.Equal("D", back.TonicName);
        Assert.Equal(1, back.Windows[0].MeasureNumber);
        Assert.Equal(ScaleMode.Dorian, back.Windows[0].Matches[0].Mode);
        Assert.True(back.TempoEstimated);
    }
}
