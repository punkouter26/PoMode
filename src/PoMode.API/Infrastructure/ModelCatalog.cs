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

    /// <summary>
    /// onnxruntime-web 1.27.0, pinned to the immutable npm-versioned jsdelivr URLs (npm packages
    /// cannot be republished under the same version). Served to the browser by
    /// <c>WebRuntimeEndpoints</c> for the ClientDelegated pitch tier (spec §4): the app is
    /// local-first, so the runtime comes from our own origin, downloaded once and SHA-256-verified
    /// like every model, never from a CDN at page load and never committed. Each SHA-256 below was
    /// computed from a fresh download of the exact pinned URL during Phase 8.
    /// </summary>
    public static readonly ModelDescriptor OrtBundle = new(
        Key: "ort-bundle",
        FileName: "ort.all.bundle.min.mjs",
        Url: "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.27.0/dist/ort.all.bundle.min.mjs",
        Sha256: "e1f340eef7b46a331aa7c2c9aa313cfd47b83f2a1892f4016ecfade0d3005036");

    /// <summary>The plain WASM-SIMD execution provider binary — the verified Tier 2 path.</summary>
    public static readonly ModelDescriptor OrtWasm = new(
        Key: "ort-wasm",
        FileName: "ort-wasm-simd-threaded.wasm",
        Url: "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.27.0/dist/ort-wasm-simd-threaded.wasm",
        Sha256: "d1ab1b94b16a65b29d710d0b587b29e7bed336827577623913479b8afe8113e6");

    /// <summary>
    /// The JSEP binary onnxruntime-web fetches when the WebGPU execution provider is selected —
    /// the opportunistic upgrade path, unverifiable on this dev machine (headless Chromium reports
    /// no WebGPU adapter; see the Phase 8 plan's measured capability table).
    /// </summary>
    public static readonly ModelDescriptor OrtWasmJsep = new(
        Key: "ort-wasm-jsep",
        FileName: "ort-wasm-simd-threaded.jsep.wasm",
        Url: "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.27.0/dist/ort-wasm-simd-threaded.jsep.wasm",
        Sha256: "78feeeb3d08f6bcee94d938ed322f69073bb8076b5f9d34697a574ffba8deb48");

    /// <summary>
    /// The Emscripten JS glue for the plain WASM binary. Discovered empirically: the "bundle"
    /// build does not embed the glue modules — the first flow-test run failed with the runtime
    /// fetching them from <c>wasmPaths</c> — so they are catalog assets like everything else.
    /// </summary>
    public static readonly ModelDescriptor OrtWasmGlue = new(
        Key: "ort-wasm-glue",
        FileName: "ort-wasm-simd-threaded.mjs",
        Url: "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.27.0/dist/ort-wasm-simd-threaded.mjs",
        Sha256: "0a1e718d99c41b22c21f2520ff4f9e883a6b5533856e398d21816ee8eb8185d3");

    /// <summary>
    /// The JSEP glue. The <c>ort.all</c> bundle requests this one even for the plain 'wasm'
    /// execution provider (its wasm backend is the JSEP build), so Tier 2 cannot run without it.
    /// </summary>
    public static readonly ModelDescriptor OrtWasmJsepGlue = new(
        Key: "ort-wasm-jsep-glue",
        FileName: "ort-wasm-simd-threaded.jsep.mjs",
        Url: "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.27.0/dist/ort-wasm-simd-threaded.jsep.mjs",
        Sha256: "3ee381d20a80f51a788a1c4a5872f6f1d047538dd4342f4af00062de5f9ea4c6");

    /// <summary>
    /// Everything <c>/web-runtime/{asset}</c> may serve. Deliberately not merged into
    /// <see cref="All"/>: that list drives <c>ModelWarmupService</c>'s startup auto-download and the
    /// /diag model report, and ~40 MB of browser runtime should download on first Tier 2 use, not on
    /// every server start. <see cref="BasicPitch"/> appears in both — the browser runs the very same
    /// pinned model file the local tier runs.
    /// </summary>
    public static readonly IReadOnlyList<ModelDescriptor> WebRuntime =
        [OrtBundle, OrtWasm, OrtWasmJsep, OrtWasmGlue, OrtWasmJsepGlue, BasicPitch];
}
