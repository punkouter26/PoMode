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
public sealed partial class PoModeJsonContext : JsonSerializerContext;
