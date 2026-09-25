namespace PoMode.API.Infrastructure;

/// <summary>
/// The fixed set of ONNX models PoMode's local inference pipeline can download and run. Descriptors
/// only — no download happens here; see <see cref="ModelRegistry"/>.
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Spotify Basic Pitch (pitch tracking). Licence: Apache-2.0. File confirmed working in the 2026-08-16 ONNX/ARM64
    /// feasibility spike (real inference benchmarked there). The URL is pinned to the commit that added
    /// the file (<c>dfb20ef559dff1792e11e022f3f0c7008c1dee6d</c>, "Add onnx serialized model") rather
    /// than <c>raw/main</c>, so the bytes behind this URL cannot change under us. The SHA-256 below was
    /// computed from a fresh download of that exact pinned URL (230,444 bytes) during the Task 4 fix
    /// round and cross-checked by loading the file with <c>Microsoft.ML.OnnxRuntime</c> and confirming
    /// its input/output metadata matches what the loader expects.
    /// </summary>
    public static readonly ModelDescriptor BasicPitch = new(
        Key: "basic-pitch",
        FileName: "nmp.onnx",
        Url: "https://github.com/spotify/basic-pitch/raw/dfb20ef559dff1792e11e022f3f0c7008c1dee6d/basic_pitch/saved_models/icassp_2022/nmp.onnx",
        Sha256: "2c3c1d144bfa61ad236e92e169c13535c880469a12a047d4e73451f2c059a0ec");

    /// <summary>
    /// HTDemucs stem separation model. Licence: MIT (Meta's demucs weights). URL and SHA-256 established as the real feasibility-gate values
    /// for Task 8's stem separator. The URL is pinned to the repo's current commit
    /// (<c>d54ed9eb60e258ea82131c6ee14578628816456a</c>, resolved via the Hugging Face models API for
    /// <c>StemSplitio/htdemucs-onnx</c>) rather than <c>resolve/main</c>, so the bytes behind this URL
    /// cannot silently change under us. The SHA-256 below was re-verified against a fresh download of
    /// that exact pinned URL (165,612,636 bytes) during the Phase 4 final-review fix round.
    /// </summary>
    public static readonly ModelDescriptor HtDemucs = new(
        Key: "htdemucs",
        FileName: "htdemucs_fp16weights.onnx",
        Url: "https://huggingface.co/StemSplitio/htdemucs-onnx/resolve/d54ed9eb60e258ea82131c6ee14578628816456a/htdemucs_fp16weights.onnx",
        Sha256: "d05c269d0178d2a72ad484b10b11dd370193fc923201c3b27a99f848745db70a");

    /// <summary>
    /// RMVPE vocal pitch estimator (Wei et al., 2023), the ONNX export shipped with RVC WebUI.
    /// Licence: MIT (the <c>lj1995/VoiceConversionWebUI</c> model card; RVC itself is MIT).
    /// Pinned to repo commit <c>e6d0c1a17da07c33557852f9dfa2bd44cc75737d</c>; 361,688,443 bytes;
    /// SHA-256 computed from a fresh download of that pinned URL on 2026-09-24 and the graph loaded
    /// with <c>Microsoft.ML.OnnxRuntime</c> 1.29: <c>input [1,128,T]</c> log-mel →
    /// <c>output [1,T,360]</c> pitch salience. Large, so it downloads last.
    /// </summary>
    public static readonly ModelDescriptor Rmvpe = new(
        Key: "rmvpe",
        FileName: "rmvpe.onnx",
        Url: "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/e6d0c1a17da07c33557852f9dfa2bd44cc75737d/rmvpe.onnx",
        Sha256: "5370e71ac80af8b4b7c793d27efd51fd8bf962de3a7ede0766dac0befa3660fd");

    /// <summary>
    /// Beat This! (Foscarin, Schlüter &amp; Widmer, ISMIR 2024; CPJKU, checkpoint <c>final0</c>)
    /// beat and downbeat tracker. Licence: MIT (upstream weights and this export). The export is
    /// <c>musetric/beat-this-onnx</c> pinned to its first commit
    /// <c>4e971bd43753023e1bf961c34a0cb74985cfcb88</c> on purpose: later commits rewrite the graph
    /// for mobile WebGPU (static 513-frame windows, im2col convolutions), which buys nothing on the
    /// CPU provider and moves away from the reference 1500-frame chunking. 83,143,431 bytes; the
    /// SHA-256 matches the one the model card publishes for that revision and was re-computed from a
    /// fresh download on 2026-09-24: <c>spect [windows,frames,128]</c> → <c>beat</c>,
    /// <c>downbeat [windows,frames]</c> logits.
    /// </summary>
    public static readonly ModelDescriptor BeatThis = new(
        Key: "beat-this",
        FileName: "beat_this.onnx",
        Url: "https://huggingface.co/musetric/beat-this-onnx/resolve/4e971bd43753023e1bf961c34a0cb74985cfcb88/beat_this.onnx",
        Sha256: "078572af6ca47741e06a82d09525d13c793eaa8e311a8cf15e831dcd7e73f218");

    /// <summary>
    /// The mel filterbank Beat This! was trained with — torchaudio's <c>MelScale.fb</c> (Slaney
    /// scale, 30–11000 Hz) written verbatim as a raw row-major float32 <c>[513,128]</c> matrix, from
    /// the same pinned commit. Downloaded rather than re-derived so the features match the reference
    /// bit for bit. MIT; 262,656 bytes.
    /// </summary>
    public static readonly ModelDescriptor BeatThisMelFilterbank = new(
        Key: "beat-this-mels",
        FileName: "beat_this_mel_filterbank.bin",
        Url: "https://huggingface.co/musetric/beat-this-onnx/resolve/4e971bd43753023e1bf961c34a0cb74985cfcb88/mel-filterbank.bin",
        Sha256: "1ee975d96f44ccf2c3bfe37825c1c1f0b089f5703c7a12a84b1f0a3bce004533");

    /// <summary>
    /// ChordMini's ChordNet "2E1D" (MIT, from <c>ptnghia-j/ChordMini</c>), the ONNX export in
    /// <c>musetric/chordmini-onnx</c> pinned to commit <c>086162411b8c4772774392be195e5c6f065d67ad</c>:
    /// the classifier only, static <c>features [16,108,144]</c> → <c>logits [16,108,170]</c>, with the
    /// normalisation inside the graph. 17,080,918 bytes; the SHA-256 matches the model card and was
    /// re-computed from a fresh download on 2026-09-25.
    /// </summary>
    public static readonly ModelDescriptor ChordMini = new(
        Key: "chordmini",
        FileName: "chordmini_chordnet.onnx",
        Url: "https://huggingface.co/musetric/chordmini-onnx/resolve/086162411b8c4772774392be195e5c6f065d67ad/chordnet.onnx",
        Sha256: "cfe7703434ebd1c28ba2ded6601581ab41d8f7d1b40f285110445460e1b11154");

    /// <summary>
    /// The constant-Q plan ChordNet's features are defined by — librosa 0.11's octave schedule, sparse
    /// FFT basis and half-band resampling filter, baked by musetric — from the same commit. Downloaded
    /// rather than re-derived, for the same reason as the Beat This! filterbank. MIT; 23,896 bytes.
    /// </summary>
    public static readonly ModelDescriptor ChordMiniCqtPlan = new(
        Key: "chordmini-cqt",
        FileName: "chordmini_cqt_plan.bin",
        Url: "https://huggingface.co/musetric/chordmini-onnx/resolve/086162411b8c4772774392be195e5c6f065d67ad/cqt-plan.bin",
        Sha256: "c31f0a6fd2d582d753be6628b5daecdee58acba53cba93b2bc2b5c75dee2ba48");

    public static readonly IReadOnlyList<ModelDescriptor> All =
        [BasicPitch, HtDemucs, BeatThis, BeatThisMelFilterbank, Rmvpe, ChordMini, ChordMiniCqtPlan];

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
    /// <summary>
    /// UVR-MDX-NET-Voc_FT, Ultimate Vocal Remover's fine-tuned MDX-Net vocal model, for the browser
    /// separation tier: <c>input [batch,4,3072,256]</c> (left/right real/imag STFT bins) →
    /// <c>output</c> of the same shape; opset 13, only ops onnxruntime-web's WebGPU provider runs.
    /// From <c>Blane187/all_public_uvr_models</c> (the UVR public model pack, MIT) pinned to commit
    /// <c>fddec39677560e41e3194f24a9e4c4cd32ef0e83</c>; 66,762,490 bytes, SHA-256 computed from a
    /// fresh download on 2026-09-25. UVR asks apps that ship its models to credit it: this is
    /// Ultimate Vocal Remover's work (github.com/Anjok07/ultimatevocalremovergui), and the executor
    /// picker names it so. Its settings (n_fft 7680, dim_f 3072, compensation 1.021) are UVR's
    /// model_data entry for this file's hash, and live in <c>js/infer/mdx.js</c>.
    /// </summary>
    public static readonly ModelDescriptor MdxVocals = new(
        Key: "mdx-voc-ft",
        FileName: "UVR-MDX-NET-Voc_FT.onnx",
        Url: "https://huggingface.co/Blane187/all_public_uvr_models/resolve/fddec39677560e41e3194f24a9e4c4cd32ef0e83/UVR-MDX-NET-Voc_FT.onnx",
        Sha256: "534b2070fcc7df514b13ef660dc8cbb328679c2374d04354a5c42bb14ecce111");

    public static readonly IReadOnlyList<ModelDescriptor> WebRuntime =
        [OrtBundle, OrtWasm, OrtWasmJsep, OrtWasmGlue, OrtWasmJsepGlue, BasicPitch, MdxVocals];
}
