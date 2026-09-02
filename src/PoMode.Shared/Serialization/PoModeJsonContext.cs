using System.Text.Json.Serialization;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Hardware;
using PoMode.Shared.Session;

namespace PoMode.Shared.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagnosticsReport))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(JobStatusDto))]
[JsonSerializable(typeof(AnalyzeUrlRequest))]
[JsonSerializable(typeof(BatchStatusDto))]
[JsonSerializable(typeof(List<LibraryEntryDto>))]
[JsonSerializable(typeof(LiveAnalyzeRequest))]
[JsonSerializable(typeof(AnalysisPreviewDto))]
[JsonSerializable(typeof(LiveAnalysisDto))]
[JsonSerializable(typeof(HardwareReport))]
[JsonSerializable(typeof(List<NoteEvent>))]
[JsonSerializable(typeof(List<ChordSpan>))]
[JsonSerializable(typeof(List<StageExecutorsDto>))]
[JsonSerializable(typeof(ModalResult))]
[JsonSerializable(typeof(SongStats))]
[JsonSerializable(typeof(SongInterpretationDto))]
[JsonSerializable(typeof(List<InterpreterOptionDto>))]
[JsonSerializable(typeof(BeatGridDto))]
[JsonSerializable(typeof(TempoMapDto))]
[JsonSerializable(typeof(ChordProgressionDefinition))]
[JsonSerializable(typeof(List<ChordProgressionDefinition>))]
[JsonSerializable(typeof(ModalMelodyRequest))]
[JsonSerializable(typeof(GeneratedMelodyDto))]
[JsonSerializable(typeof(ModeComparisonItemDto))]
[JsonSerializable(typeof(ModalComparisonResponse))]
public sealed partial class PoModeJsonContext : JsonSerializerContext;
