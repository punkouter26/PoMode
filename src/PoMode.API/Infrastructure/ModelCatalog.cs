namespace PoMode.API.Infrastructure;

/// <summary>
/// The fixed set of ONNX models PoMode's local inference pipeline can download and run. Descriptors
/// only — no download happens here; see <see cref="ModelRegistry"/>.
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Spotify Basic Pitch (pitch tracking). File confirmed working in the 2026-08-16 ONNX/ARM64
    /// feasibility spike (real inference benchmarked there). The URL is pinned to the commit that added
    /// the file (<c>dfb20ef559dff1792e11e022f3f0c7008c1dee6d</c>, "Add onnx serialized model") rather
    /// than <c>raw/main</c>, so the bytes behind this URL cannot change under us. The SHA-256 below was
    /// computed from a fresh download of that exact pinned URL (230,444 bytes) during the Task 4 fix
    /// round and cross-checked by loading the file with <c>Microsoft.ML.OnnxRuntime</c> and confirming
    /// its input/output metadata matches the spike report
    /// (<c>.superpowers/spikes/2026-08-16-onnx-arm64-spike.md</c> §4) — see
    /// <c>.superpowers/sdd/2026-08-16-phase4-local-inference/task-4-report.md</c> "Fix Round 1" for the
    /// full verification trail.
    /// </summary>
    public static readonly ModelDescriptor BasicPitch = new(
        Key: "basic-pitch",
        FileName: "nmp.onnx",
        Url: "https://github.com/spotify/basic-pitch/raw/dfb20ef559dff1792e11e022f3f0c7008c1dee6d/basic_pitch/saved_models/icassp_2022/nmp.onnx",
        Sha256: "2c3c1d144bfa61ad236e92e169c13535c880469a12a047d4e73451f2c059a0ec");

    /// <summary>
    /// HTDemucs stem separation model. URL and SHA-256 established as the real feasibility-gate values
    /// for Task 8's stem separator. The URL is pinned to the repo's current commit
    /// (<c>d54ed9eb60e258ea82131c6ee14578628816456a</c>, resolved via the Hugging Face models API for
    /// <c>StemSplitio/htdemucs-onnx</c>) rather than <c>resolve/main</c>, so the bytes behind this URL
    /// cannot silently change under us. The SHA-256 below was re-verified against a fresh download of
    /// that exact pinned URL (165,612,636 bytes) during the Phase 4 final-review fix round — see
    /// <c>.superpowers/sdd/2026-08-16-phase4-local-inference/finalfix-report.md</c> for the trail.
    /// </summary>
    public static readonly ModelDescriptor HtDemucs = new(
        Key: "htdemucs",
        FileName: "htdemucs_fp16weights.onnx",
        Url: "https://huggingface.co/StemSplitio/htdemucs-onnx/resolve/d54ed9eb60e258ea82131c6ee14578628816456a/htdemucs_fp16weights.onnx",
        Sha256: "d05c269d0178d2a72ad484b10b11dd370193fc923201c3b27a99f848745db70a");

    public static readonly IReadOnlyList<ModelDescriptor> All = [BasicPitch, HtDemucs];
}
