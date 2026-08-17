namespace PoMode.API.Infrastructure;

/// <summary>
/// The fixed set of ONNX models PoMode's local inference pipeline can download and run. Descriptors
/// only — no download happens here; see <see cref="ModelRegistry"/>.
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Spotify Basic Pitch (pitch tracking). URL and file confirmed working in the
    /// 2026-08-16 ONNX/ARM64 feasibility spike (real inference benchmarked there). The spike did not
    /// record a SHA-256 for this download, so <see cref="ModelDescriptor.Sha256"/> is left empty here;
    /// <see cref="ModelRegistry.EnsureAsync"/> skips hash verification whenever the hash is empty.
    /// </summary>
    public static readonly ModelDescriptor BasicPitch = new(
        Key: "basic-pitch",
        FileName: "nmp.onnx",
        Url: "https://github.com/spotify/basic-pitch/raw/main/basic_pitch/saved_models/icassp_2022/nmp.onnx",
        Sha256: "");

    /// <summary>
    /// HTDemucs stem separation model. URL and SHA-256 established as the real feasibility-gate values
    /// for Task 8's stem separator.
    /// </summary>
    public static readonly ModelDescriptor HtDemucs = new(
        Key: "htdemucs",
        FileName: "htdemucs_fp16weights.onnx",
        Url: "https://huggingface.co/StemSplitio/htdemucs-onnx/resolve/main/htdemucs_fp16weights.onnx",
        Sha256: "d05c269d0178d2a72ad484b10b11dd370193fc923201c3b27a99f848745db70a");

    public static readonly IReadOnlyList<ModelDescriptor> All = [BasicPitch, HtDemucs];
}
