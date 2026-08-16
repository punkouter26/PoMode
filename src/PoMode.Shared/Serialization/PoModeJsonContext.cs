using System.Text.Json.Serialization;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Session;

namespace PoMode.Shared.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagnosticsReport))]
[JsonSerializable(typeof(SessionInfo))]
public sealed partial class PoModeJsonContext : JsonSerializerContext;
