using System.Text.Json.Serialization;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Hardware;

namespace PoMode.Shared.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagnosticsReport))]
[JsonSerializable(typeof(JobStatusDto))]
[JsonSerializable(typeof(TakeOrigin))]
[JsonSerializable(typeof(List<LibraryEntryDto>))]
[JsonSerializable(typeof(AnalysisPreviewDto))]
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
[JsonSerializable(typeof(ModeFitDto))]
[JsonSerializable(typeof(SongQuestionRequest))]
[JsonSerializable(typeof(SongAnswerDto))]
public sealed partial class PoModeJsonContext : JsonSerializerContext;
