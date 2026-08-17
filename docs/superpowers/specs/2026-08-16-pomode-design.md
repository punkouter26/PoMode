# PoMode — Audio Modal Analyzer: Design Specification

**Date:** 2026-08-16
**Status:** Approved. Phases 1–2 implemented and merged; see §13 for corrections discovered during implementation.
**Source:** PRD v1.0.0 ("Audio Modal Analyzer") + brainstorming session decisions

---

## 1. Overview

PoMode is a music information retrieval (MIR) application. It accepts a polyphonic audio track (MP3/WAV, ≤ 100 MB), separates the vocal lead from the backing accompaniment, transcribes the vocal into discrete note events, detects the backing chord progression and tempo map, and deterministically derives the musical scale modes (Ionian…Locrian + pentatonics) active across the song timeline. Results render in an interactive Blazor WASM visualizer and export as a multi-track Standard MIDI File.

**Stack:** .NET 10 / C# 15, ASP.NET Core Minimal APIs (Vertical Slice Architecture), Blazor WebAssembly hosted by the API, Radzen UI. Governed by NET_RULES in `CLAUDE.md`.

## 2. Decisions from Brainstorming

| Decision | Choice |
|---|---|
| V1 scope | **All three execution tiers** (Local ONNX / WebGPU in-browser / Cloud APIs) |
| Tier preference | **Local → WebGPU → Cloud.** Paid APIs are last resort; a paid call never happens silently (UI badge per stage) |
| Cloud providers | All three: Replicate, Sonic API, LALAL.AI (user holds keys for each) |
| Secrets | **Azure Key Vault (`PoShared` RG) via `DefaultAzureCredential`, with environment-variable fallback** when the vault is unreachable (offline dev), logged as a startup warning. Never in `appsettings.json`, never sent to the client |
| Naming | `PoMode` — solution, projects, root namespaces |
| Dev hardware | ~~NVIDIA GPU ≥ 6 GB VRAM~~ **Superseded — see §13.1.** Actual: Windows-on-ARM, Qualcomm Adreno GPU, Ollama installed |
| Architecture approach | **A — server-orchestrated job pipeline with client compute delegation** (chosen over client-centric and synchronous-per-stage alternatives) |
| Copilot | Local Ollama only; no cloud LLM fallback (card degrades to "Copilot unavailable") |

## 3. Solution Layout

```
PoMode.sln
Directory.Build.props           net10.0, <Nullable>enable</Nullable>, <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
Directory.Packages.props        central package management
src/
  PoMode.API/
    Features/
      Analysis/                 upload endpoint, AnalysisJob orchestrator, job store, SignalR hub
      StemSeparation/           IStemSeparator + LocalOnnx / Replicate / Lalal implementations
      PitchTracking/            IPitchTracker + LocalOnnx / ClientDelegated / Sonic / Replicate impls
      ChordRecognition/         IChordRecognizer + LocalOnnx(BTC) / Sonic impls; local tempo estimator
      ModalAnalysis/            ModalAnalysisEngine (pure C#, deterministic)
      Copilot/                  OllamaClient + prompt builder
      MidiExport/               DryWetMidi SMF Type 1 builder + endpoint
      Hardware/                 VRAM/environment probe; feeds /diag
    Pipeline/                   stage contracts + ExecutionPlanner (the one shared seam slices plug into)
    Infrastructure/             Key Vault config w/ env fallback, FakeAuthHandler, job queue, audio decode
  PoMode.Client/                Blazor WASM: canvas visualizer, HUD, mixer, WebGPU worker (JS interop)
  PoMode.Shared/                DTOs, enums, JSON source-gen contexts. Zero logic
tests/
  PoMode.Unit/  PoMode.Integration/  PoMode.E2EAPI/  PoMode.E2EUI/
```

**Key packages (central):** `Microsoft.ML.OnnxRuntime.Gpu`, `Microsoft.ML.OnnxRuntime.DirectML`, `Melanchall.DryWetMidi`, `Radzen.Blazor`, `Azure.Identity`, `Azure.Security.KeyVault.Secrets`, `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore`, `NAudio`, `NLayer` (cross-platform MP3 decode).

### Core abstractions

- **Stage contracts** (`PoMode.API/Pipeline/`): `IStemSeparator`, `IPitchTracker`, `IChordRecognizer`. Each implementation self-reports `Availability` (e.g., CUDA present + model file downloaded; client has WebGPU; API key configured) and a `Tier` (`Local`, `ClientDelegated`, `Cloud`).
- **`ExecutionPlanner`**: given the hardware probe result, resolves each stage to a concrete executor at job start using the preference order Local → ClientDelegated → Cloud. The plan is stamped into the job and surfaced in the UI.
- **`AnalysisJob`**: state machine `Uploaded → Separating → PitchTracking → ChordDetecting → ModalAnalysis → Complete | Failed | Cancelled`. Persisted as `jobs/{id}/job.json` plus artifacts (`vocals.wav`, `instrumental.wav`, `notes.json`, `chords.json`, `result.json`). Restart-safe: a completed stage (artifact present + recorded) is never re-run.

## 4. Pipeline Execution & Tier Routing

### Job lifecycle
- `POST /api/analysis` — multipart upload, ≤ 100 MB, `.mp3`/`.wav` (magic-byte validated, not just extension). Creates job folder, enqueues, returns `jobId` + `JobStatusDto` immediately.
- A hosted `BackgroundService` consumes a bounded `System.Threading.Channels` queue, **concurrency 1** (GPU stages must not share VRAM).
- `GET /api/analysis/{id}` — polling status endpoint (same DTO the hub pushes); `DELETE /api/analysis/{id}` — cancellation via `CancellationToken` threaded through every executor.
- Nightly sweep purges job folders older than 7 days.
- No database in v1; the job folder is the source of truth.

### Progress
SignalR hub `/hubs/analysis`; client subscribes by `jobId`. Events: `StageStarted`, `StageProgress(pct)`, `StageCompleted(tierUsed)`, `ClientWorkRequested`, `JobCompleted`, `JobFailed`. Polling endpoint is the fallback and the E2EAPI contract surface.

### Hardware & environment probe (startup + `/diag`)
1. **Environment:** `WEBSITE_INSTANCE_ID` / container markers ⇒ Azure mode: local ONNX + Ollama hard-disabled.
2. **GPU:** NVML (`nvml.dll`) for free VRAM on NVIDIA; DXGI adapter enumeration fallback (total VRAM, vendor). → `GpuReport { Vendor, TotalVramMb, FreeVramMb, CudaAvailable, DmlAvailable }`.
3. **Ollama:** `GET http://localhost:11434/api/tags`, 1 s timeout → installed model list.
4. **Cloud:** which API keys resolved (Key Vault or env). `/diag` reports all of the above with secrets redacted.

### Execution plan (preference order per stage)

| Stage | 1st | 2nd | 3rd |
|---|---|---|---|
| Stem separation | Local ONNX (Roformer/HTDemucs; CUDA→DML) if free VRAM ≥ 6 GB | — (model too heavy for browser) | Replicate demucs → LALAL.AI |
| Pitch tracking | Local ONNX Basic Pitch | ClientDelegated WebGPU Basic Pitch | Sonic → Replicate |
| Chords + tempo | Local ONNX BTC (risk-flagged) + local tempo estimator | — | Sonic API |
| Modal engine | Always local C# | — | — |

**Mid-run fallback:** if a chosen executor fails (OOM, HTTP failure after retries), the stage falls through to the next tier automatically; the recorded plan and UI badge update. Paid tiers are visually marked (☁️💰) — a cloud call is always visible.

### ClientDelegated protocol (Tier 2 — pitch tracking only)
1. Stage parks as `AwaitingClient`; hub pushes `ClientWorkRequested(jobId, stage, stemUrl)`.
2. Blazor client fetches `vocals.wav`, runs Basic Pitch via `onnxruntime-web` (WebGPU backend, WASM fallback) in a JS module/worker.
3. `POST /api/analysis/{id}/client-result` returns note events; server **validates the payload** (note count bounds, pitch range 24–96, times within track duration) since it is client-supplied data.
4. Pipeline resumes. Guard: no client post within 5 minutes ⇒ fall through to Tier 3.

## 5. Stage Integrations

### Model management
ONNX models are never committed. `ModelRegistry` downloads on first use from pinned URLs (HuggingFace / GitHub releases), verifies SHA-256, stores under `models/`. `/diag` shows per-model status (`Downloaded | Missing | Downloading`). Disabled entirely in Azure mode.

### Stage 1 — Stem separation (local)
Decode (NAudio/NLayer) → 44.1 kHz stereo float PCM → STFT → chunked inference with overlap-add → `vocals.wav`; instrumental = mix − vocals (Roformer) or the model's instrumental head (HTDemucs). Execution-provider order inside the executor: CUDA → DirectML → CPU (with warning). Performance target: ≤ 15 s for a 3.5-min track on the dev GPU.

### Stage 2 — Pitch tracking (local + browser)
Official Spotify Basic Pitch ONNX (`nmp`). Resample vocal stem to 22.05 kHz mono → windowed inference → onset/frame posteriors → note-event decoding (onset threshold, frame threshold, min note length ≈ 58 ms) ported to C#. Output: `NoteEvent { MidiPitch, StartSec, DurationSec, Velocity }` (velocity from frame energy). **The decoder algorithm is specified once and implemented twice** (C# executor, browser JS worker); both are tested against the same precomputed tensor fixtures.

### Stage 3 — Chords + tempo (local)
- **Chords:** BTC (Bidirectional Transformer for Chord Recognition) checkpoint exported PyTorch→ONNX as an offline build-time task (**risk: export may fail — see §11**). Executor: CQT-chroma frames → per-frame chord labels → merged `ChordSpan { Symbol, Root, Quality, StartSec, EndSec }`.
- **Tempo:** local onset-envelope autocorrelation (deterministic C#, no model) → BPM curve + beat grid → measure numbering + MIDI tempo map.
- Fallback: Sonic API `/analyze/chords` + `/analyze/tempo` while everything else stays local.

### Cloud clients (Tier 3)
Three typed `HttpClient`s with retry/backoff (standard resilience handler): **Replicate** (create prediction → poll → download), **Sonic API** (upload → job poll → parse), **LALAL.AI** (upload → split → download). Each maps responses into the same stage DTOs — the pipeline cannot tell tiers apart.

### Copilot (local Ollama)
`POST http://localhost:11434/api/generate`; model chosen from installed list preferring `qwen2.5:7b`, then `llama3.3:8b`, `llama3.2:3b`. Prompt: active chord, global key, sung intervals, detected mode + confidence, hex bitmask → 2-sentence musicological explanation, rendered as Markdown in the HUD. No Ollama ⇒ "Copilot unavailable" card. Never cloud.

## 6. Modal Analysis Engine (deterministic C#)

**Correction to PRD:** several PRD mask literals are 13 bits long (typos). The engine derives masks from interval sets in code — no magic literals. Canonical values (bit *i* = interval *i* semitones above tonic):

| Mode | Intervals | Mask (bin) | Hex |
|---|---|---|---|
| Ionian | 0 2 4 5 7 9 11 | `101010110101` | 0xAB5 |
| Dorian | 0 2 3 5 7 9 10 | `011010101101` | 0x6AD |
| Phrygian | 0 1 3 5 7 8 10 | `010110101011` | 0x5AB |
| Lydian | 0 2 4 6 7 9 11 | `101011010101` | 0xAD5 |
| Mixolydian | 0 2 4 5 7 9 10 | `011010110101` | 0x6B5 |
| Aeolian | 0 2 3 5 7 8 10 | `010110101101` | 0x5AD |
| Locrian | 0 1 3 5 6 8 10 | `010101101011` | 0x56B |
| Minor pentatonic | 0 3 5 7 10 | `010010101001` | 0x4A9 |
| Major pentatonic | 0 2 4 7 9 | `001010010101` | 0x295 |

**Algorithm:**
1. **Global tonic:** Krumhansl-Schmuckler key-finding over the whole track's duration-weighted pitch-class histogram (melody notes + chord roots/thirds). Reported with confidence; user-overridable in the UI (re-runs modal stage only — cheap).
2. **Per chord window** `[tStart, tEnd)`: aggregate distinct sung pitch classes → `VocalMask = OR(1 << ((midi − tonicPc + 12) % 12))`.
3. **Score each mode:** coverage (`popcount(VocalMask & ModeMask) / popcount(VocalMask)`), bonus weight for characteristic degrees present (♮6 Dorian, ♯4 Lydian, ♭2 Phrygian, ♭7 Mixolydian, ♭5 Locrian…), penalty for out-of-mode notes. Windows with < 3 distinct pitch classes → `InsufficientEvidence` instead of a fake match.
4. **Output:** ranked `ModalMatch[]` per window (mode, confidence 0–1, matched/missing/outside intervals) + whole-song primary mode. Consumed by HUD, canvas coloring, MIDI markers, copilot prompts.

## 7. Frontend (Blazor WASM + Radzen)

**Header (NET_RULES):** `[PoMode branding | Upload · Analyze · Export MIDI | session/logout]`. Scoped `.razor.css` only; global CSS variables; auto light/dark. Persistent **"USING MOCK DATA"** banner whenever displaying the bundled demo analysis rather than a real job.

**Dual-track canvas** — one HTML5 `<canvas>` via a JS interop module (Blazor render tree is too slow for thousands of capsules):
- Top lane: piano roll of note capsules, dual labels (`B4` + `[b3]`), colors: chord tone / in-mode / characteristic modal note (accent) / outside.
- Bottom lane: chord blocks (symbol, measure number, mode tag).
- Zoom/pan with virtualized drawing; playback scrubber synced to the Web Audio clock via `requestAnimationFrame`; clicking a measure selects the analysis window (HUD + copilot update).

**HUD (Radzen cards):** primary mode + confidence bar; harmonic context (chord, tonic, hex mask); scale-degree badge grid; ranked alternatives; copilot Markdown box with Regenerate; per-stage tier badges (💻 local / 🌐 browser / ☁️💰 paid).

**Stem mixer (Web Audio):** three `AudioBufferSourceNode`s (mix/vocals/instrumental) started sample-synchronized through `GainNode`s; Full Mix / Solo Vocals / Solo Backing switch via 50 ms `linearRampToValueAtTime` gain ramps — no pops, position preserved.

## 8. MIDI Export

`GET /api/analysis/{id}/midi` builds SMF Type 1 server-side with DryWetMidi:
- **Track 0:** tempo map from the BPM curve + time signature.
- **Track 1:** vocal note events, GM program 80 (Lead 1 Square), velocities from analysis.
- **Track 2:** chord voicings, GM program 0 (piano), root-position 7th voicings per `ChordSpan`.
- **Track 3:** text/marker meta-events at measure starts: `"Mode: D Dorian | Chord: G7"`.

## 9. API Surface & Cross-Cutting

- Endpoints via `IEndpointRouteBuilder` + `MapGroup("/api/analysis")` etc., `TypedResults`, OpenAPI + Scalar UI.
- **Auth:** `FakeAuthHandler` reading `X-Fake-User` / `X-Fake-Roles`; throws `InvalidOperationException` in Production.
- **`/health`:** checks disk space for job storage, Key Vault reachability, Ollama (degraded-not-unhealthy), cloud API key presence.
- **`/diag`:** hardware probe report, execution plan defaults, model registry status, job queue depth, configured providers — all secrets redacted.
- **Secrets:** Key Vault (`PoShared`) via `DefaultAzureCredential` → env-var fallback with startup warning. Keys never reach the client; all cloud calls are server-side.

## 10. Testing Strategy

- **PoMode.Unit:** modal engine (mask derivation vs canonical table, scoring, characteristic weighting, K-S tonic detection on known-key fixtures), Basic Pitch note-event decoder vs precomputed tensor fixtures, `ExecutionPlanner` across probe permutations, chord-span merging, tempo estimator on click-track fixtures, MIDI builder (parse bytes back, assert tracks/programs/markers).
- **PoMode.Integration:** full pipeline with fake executors (no GPU in CI): upload → queue → SignalR events → artifacts → resume-after-restart; Key Vault→env fallback; ModelRegistry download + SHA-256 against a local HTTP fixture server; client-result validation.
- **PoMode.E2EAPI:** real HTTP with `FakeAuthHandler` headers: upload validation (size/format/magic bytes), status contract, client-result endpoint, `/health`, `/diag` secret-redaction assertions.
- **PoMode.E2EUI (Playwright):** app in mock-data mode (bundled fixture analysis, no GPU/cloud): banner visible, canvas renders, mixer toggles, HUD updates on measure click, MIDI downloads.
- **GPU smoke test:** `[Trait("RequiresGpu")]`, excluded from CI — one 30 s clip through the real ONNX stack end-to-end.

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| BTC PyTorch→ONNX export fails or accuracy degrades | Chord stage is swappable; Sonic API fallback works day one; export attempted as an isolated offline task that cannot block v1 |
| Roformer ONNX community exports vary in quality | HTDemucs v4 ONNX as second local model; Replicate demucs as cloud fallback |
| Basic Pitch decoder parity (C# vs JS) drifts | Single spec, shared tensor fixtures, identical unit tests both sides |
| 100 MB uploads on Azure App Service limits | Multipart streaming to disk (no buffering); request size limits configured explicitly |
| VRAM probe misreads free memory → OOM | Mid-run fallback catches OOM and demotes the stage to the next tier |
| Ollama/Key Vault absent in some dev sessions | Both degrade gracefully (copilot card off; env-var fallback + warning) |

## 12. Out of Scope for v1


- User accounts / persistence beyond job folders (7-day retention)
- Real (non-fake) authentication provider
- Cloud LLM copilot fallback
- Batch/multi-file analysis; live microphone input
- Non-Windows local GPU tier testing (DirectML path is code-complete but validated only via CI fakes)

---

## 13. Corrections Discovered During Implementation

Recorded as they were found; each supersedes the corresponding statement above.

### 13.1 Dev hardware is Windows-on-ARM with a Qualcomm Adreno GPU (found in Phase 2, Task 6)

The §2 assumption of an NVIDIA GPU ≥ 6 GB was wrong. The NVML probe returns `null` on this machine (no `nvml.dll`), so `GpuReport` is absent from `/diag` here, and the installed Ollama model is `gemma4:26b` — not the `qwen2.5:7b` / `llama3.3:8b` / `llama3.2:3b` list in §5.

Consequences for **Phase 4 (local ONNX tier)**, which must be re-planned before it starts:
- **CUDA is not testable locally.** The §4 execution table's "CUDA→DML" ordering still holds as code, but only the DirectML and CPU paths can be exercised on this box — and DirectML on ARM64 is unproven for these models.
- The §4 "free VRAM ≥ 6 GB" gate for local stem separation will never pass here, so the planner will route stem separation to the cloud tier by default on this machine.
- Options to decide with the user: target DirectML/CPU on ARM, use a different machine for Tier-1 validation, or accept cloud-first locally and treat Tier 1 as CI/other-hardware only.
- §5's Copilot model preference list must include whatever is actually installed (`gemma4:26b`) or fall back to "first available model".

### 13.2 Deferred from Phase 2 into Phase 4 (agreed scope moves)

- **Startup re-enqueue of interrupted jobs.** Persistence and stage-skip resumption are implemented and tested, but nothing re-enqueues a job left mid-stage by a hard crash. Deliberately deferred: with instant fake executors the crash window is negligible; it becomes real once stages take minutes.
- **Optimistic concurrency on `job.json`.** A `DELETE` on a still-queued job can be overwritten by the worker's first write (last-writer-wins). Per-job locking and atomic writes are in place, but there is no versioning, so cancel remains best-effort until Phase 4.
- **Intra-stage progress** (`StageProgress(pct)` in §4) and **DXGI adapter enumeration** in the hardware probe.

### 13.3 Phase 3 rulings and deviations (modal engine & MIDI)

- **Tempo detection moved to Phase 4** (user decision). Phase 3 uses a fixed 120 BPM for MIDI Track 0 and measure numbering; `ModalResult.TempoEstimated` is `true` and the UI labels it "(estimated)". Phase 4 passes a real BPM into `ModalAnalysisEngine.Analyze(notes, chords, tempoBpm)` and flips the flag — the MIDI builder and client need no other change.
- **The characteristic-degree bonus is 0.05, not an arbitrary weight.** §6 step 3 says "bonus weight for characteristic degrees". Implementation constraint discovered in review: the bonus must stay strictly below the smallest possible coverage step (1/12 ≈ 0.083), or a mode explaining *less* of the sung material can outrank one explaining more once 7+ distinct pitch classes are sung. The bonus therefore only orders modes *within* a coverage level; it can never invert coverage ordering.
- **Tonic histogram uses chord roots only**, not "roots/thirds" as §6 step 1 says — thirds would require chord-quality inference that duplicates the mode scoring.
- **No explicit out-of-mode penalty.** §6 step 3 mentions one; coverage already divides by the count of sung classes, so an outside note reduces the score inherently. A separate penalty would double-count.
- **`ModalMatch` carries matched and outside intervals, not "missing".** Missing degrees are derivable (`modeMask & ~vocalMask`) and unused by the HUD.
- **Insufficient evidence** is `< 3` distinct pitch classes in a window; such windows report no matches and are excluded from the primary-mode vote.

### 13.4 Known defect carried into Phase 4

**Artifact read/write race (found at the end of Phase 3, deliberately unfixed).** `notes.json`, `chords.json`, and `result.json` are written by the pipeline and the modal analyzer *outside* the per-job `SemaphoreSlim` that Phase 2 added for `job.json`, while `/api/analysis/{id}/notes|chords|result` serve them with `TypedResults.PhysicalFile`, which opens its own handle. On Windows a write or `File.Move` over an open handle throws `UnauthorizedAccessException`, which the pipeline's catch-all turns into `Stage = Failed` — a healthy job reporting failure purely because a client read an artifact at the wrong moment. Observed intermittently under parallel test load. Fix in Phase 4 before real long-running stages make mid-write reads common: extend the per-job lock to cover every artifact, or serve artifacts as byte copies read under that lock instead of streaming the file in place.

### 13.5 Phase 2 additions not in the original spec

- Kestrel's `MaxRequestBodySize` must be raised explicitly to 100 MB; `FormOptions.MultipartBodyLengthLimit` alone leaves a ~28.6 MB effective cap (§11's "request size limits configured explicitly" now covers both).
- `jobId` route parameters are validated as 32 lowercase hex characters before reaching `Path.Combine`/`PhysicalFile`.
- The SignalR contract is a single `JobStatusChanged(JobStatusDto)` event rather than §4's five named events; the DTO carries stage, tier plan, progress, and error.
- The solution file is `PoMode.slnx` (current SDK format), not `PoMode.sln` as written in §3.

### 13.6 Phase 5 rulings (chord recognition)

- **Chord recognition is chroma template matching, not BTC** (user decision). §5 lists "NNLS-Chroma/Chordino" alongside BTC for Stage 3, so this is the spec's own sanctioned precedent, not a downgrade. The BTC path and the PyTorch→ONNX export risk flagged in §11 were **not attempted** in this phase and remain unattempted. Consequence: Stage 3 needs no model, no download, and no network — it runs on a fresh clone and in Azure unchanged. `ChromaChordRecognizer.IsAvailableAsync` therefore returns `true` unconditionally.
- **Vocabulary is 24 triads (12 major + 12 minor) plus `"N"` (no chord).** Sevenths are deliberately excluded: with 12-bin chroma they are frequently indistinguishable from their relative triads, and a confidently-wrong `Cmaj7` is worse input for the modal engine (§6) than a correct `C`. Revisit only with measured evidence. `ChordSpan.Quality` stays `"maj"`/`"min"` so `MidiFileBuilder`'s existing voicing map is untouched.
- **Beat-synchronous chord boundaries are deferred.** Chords are recognised on a fixed frame grid (4096-sample window, 2048-sample hop) and merged by median smoothing; aligning span boundaries to the Phase-4 beat grid is a later refinement, not part of this phase.
- **§4's local stem-separation gate is superseded.** The "free VRAM ≥ 6 GB" precondition and the "CUDA→DML" execution-provider ordering in §4's execution table describe an executor that will never exist in this project. §13.1 established the dev box has no NVIDIA GPU, and Phase 4 then **measured** CPU as the fastest provider available here (Basic Pitch 12.3 ms on CPU vs 40.6 ms on DirectML). The shipped local tier is **CPU-EP only**; stem separation additionally requires `IntraOpNumThreads = 2` (6 threw `bad allocation`), and `Microsoft.ML.OnnxRuntime` must never be referenced alongside the DirectML/QNN packages (`onnxruntime.dll` collision → `EntryPointNotFoundException`). Read §4's table as historical intent, not as behaviour.

**Real-track check (recorded honestly, as required).** `2017_LonelyHill2.mp3` (162 s, 44.1 kHz stereo) was run through the full chain via a throwaway harness. Decode 2.2 s, chromagram 0.4 s, match + segment 0.1 s — the whole stage is under 3 s, negligible next to stem separation. Result: 75 spans, 1.7 % of frames labelled `"N"`, mean match score 0.669, median span 1.72 s.

- **What is genuinely right:** the time-weighted histogram is dominated by A (43.9 s), E (20.0 s), Am (18.8 s), Dm (17.0 s), D (13.5 s), with F#m and Bm trailing. That is a coherent A-centred tonality — A/D/E are I–IV–V in A major and Am/Dm/E point at A minor — so the progression is not noise, and it is exactly the kind of input §6's modal engine needs.
- **What is genuinely wrong:** A# (10.2 s) and A#m (8.5 s) are semitone-neighbour errors of A/Am — classic chroma leakage. The 4096-sample window at 44.1 kHz gives ~10.8 Hz bin spacing, which cannot resolve adjacent semitones in the bass register, and the pitch-class mapping rounds those smeared bins into the wrong class. A 6.7 s `C#m` opening and brief `Fm`/`Gm` spans are the same defect. Many spans sit at the 0.51–0.79 s floor, meaning the 0.5 s minimum-duration rule is absorbing flicker the median filter did not remove.
- **Honest verdict:** usable, not clean. Good enough to feed the modal engine a correct key centre; not good enough to present as a chord transcription a musician would trust bar-by-bar. The two named fixes if this is revisited are (a) resample to 22.05 kHz and/or use a log-frequency (constant-Q-style) mapping so bass semitones separate, and (b) beat-synchronous segmentation instead of the duration floor.

### 13.7 Phase 6 rulings (visualizer frontend)

- **Every musical decision stays server-side; the client draws.** §7's canvas colouring and HUD readouts are derived by `VisualizationBuilder` in the API and served as one `VisualizationPayload` from `GET /api/analysis/{jobId}/visual` (notes with `NoteRole` + dual labels, chord blocks, and per-window mask hex / mode tag / twelve degree badges / ranked alternatives). The DTOs live in `PoMode.Shared`; the logic does not. Putting the logic in `Shared` was the plan's first draft and was wrong twice over: it breaks CLAUDE.md's "zero business logic in `Shared`", and it would fork the mode masks §6 requires to be derived exactly once from `ModeDefinitions`. Consequence: clicking a chord needs no round trip, and the colouring rules are unit-tested rather than eyeballed in a browser.
- **No npm, no bundler, no charting or Markdown library.** `canvas.js` and `mixer.js` are plain ES modules loaded through `IJSRuntime`; the canvas reads its palette from CSS variables via `getComputedStyle` and re-reads on a `prefers-color-scheme` change, so no colour is hard-coded in JS and the canvas follows light/dark like the rest of the UI.
- **The mixer owns the transport clock.** `mixer.js` imports `setPlayhead` from `canvas.js` and drives the playhead directly, so playback costs zero Blazor renders. Blazor issues commands and hears discrete events only.
- **Stems are served through a fixed allow-list.** `GET /api/analysis/{jobId}/stems/{name}` accepts only `mix`, `vocals`, `instrumental`; the name selects a stem and never becomes part of a path.
- **Radzen needed its dark stylesheet — a pre-existing defect.** `index.html` loaded only `material-base.css`, so in dark mode Radzen cards stayed white while `app.css` flipped to light text, leaving the HUD unreadable. Both bases now load under `prefers-color-scheme` media queries, which is what §7's auto light/dark actually requires.
- **Copilot Markdown is rendered through a deliberately tiny converter.** `MiniMarkdown` escapes everything first and then re-adds only `**bold**`, `*italic*`, inline code, paragraphs and line breaks. The copilot's text is language-model output landing in a `MarkupString`, so a full Markdown library would mean a wider injection surface for no user benefit. Unit-tested against script/attribute payloads.

**Copilot model selection — correcting §5 and §13.1.** §5's preference list (`qwen2.5:7b`, `llama3.3:8b`, `llama3.2:3b`) matches nothing installed here, so the client treats it as a *preference* and then falls through to the remaining installed models. §13.1 said the installed model is `gemma4:26b`; **measured now, that is incomplete and misleading**. Two models are installed — `llama3.2:1b` and `gemma4:26b` — and **`gemma4:26b` cannot run on this machine at all**: Ollama fails to load it with `failed to allocate CPU buffer of size 17384259520` (≈17 GB) and answers HTTP 500. A model can therefore be installed and still unusable, so the client tries up to three candidates in order and falls through on failure rather than reporting the copilot as unavailable after one 500. Ollama's own `error` text is surfaced (first line only) as the reason.

**Copilot real-track check (recorded honestly).** End-to-end against the live Ollama on `2017_LonelyHill2.mp3` (75 windows): the request succeeded in **4.4 s** using `llama3.2:1b`. The plumbing is correct. **The answer quality is not.** Asked about window 0 (chord `Am`, tonic A, sung degrees 1 2 ♭3 ♭6 ♭7, mask `0x50D`, Aeolian at 100 %), the model got the first clause right — those degrees are indeed all in Aeolian — and then produced two false statements: it listed the Aeolian scale as "a, b, c, d, e, f" (six notes, wrong spelling) and claimed "the chord sounding in the first measure is a perfect fourth, supporting the expectation of a major scale progression", which is meaningless and contradicts the detected minor mode. A 1B model is too weak for musicology. The honest position: the copilot feature is complete and correct, and on this hardware it has no model good enough to be trusted. Fixing that is a model-availability problem (install a mid-size model that fits in RAM), not a code problem — so the card's output should be read as a suggestion, never as analysis. The deterministic analysis in the HUD is the authoritative part of the UI.

### 13.8 Phase 7 rulings (cloud tier)

**Sonic API is dead — §4 and §5's Sonic rows are void.** `sonicapi.com` is now a parked domain listed for sale and its documentation no longer resolves. §4 made it the *only* Tier-3 option for chords and the *first* for pitch tracking; neither is achievable. This was found by checking the provider before writing code, not after.

**There will never be a cloud chord recognizer, and this row is closed rather than deferred.** `ChromaChordRecognizer` (§13.6) is pure DSP with `IsAvailableAsync => true` unconditionally — no model, no download, no network, works in Azure. `ExecutionPlanner` selects the lowest available tier, so a `Cloud` chord executor could never be chosen under any configuration. It would be unreachable code by construction.

**Cloud pitch tracking is deferred with its reason stated.** §4's order was `Sonic → Replicate`. Sonic is gone, and no Replicate model was identified whose *output* contract maps to `NoteEvent[]` without guessing. Writing an unverifiable response mapping would produce code that reads correctly and fails on first real use, so it was not written. The `IPitchTracker` + `Tier = Cloud` seam stays open and costs nothing.

**Phase 7 therefore delivers Tier 3 for stem separation only, via two providers.** That is the one stage with both a real need — its local executor depends on an ~80 MB HTDemucs download — and two contracts that could be verified.

- **Replicate** (`ReplicateStemSeparator`): create prediction → poll `urls.get` → read stem URLs from `output` → download. **The terminal success status is `succeeded`.** A published documentation summary calls it `successful`; taking that at face value would have made every prediction appear unfinished forever, so there is a test asserting `successful` is *not* treated as success. `output` is accepted as a keyed object, a bare array, or a single URL, and the instrumental stem is matched against `instrumental`/`no_vocals`/`accompaniment`/`other`/`backing`, because demucs variants genuinely differ. The model reference is configuration (`Cloud:Replicate:StemModel`, default `cjwbw/demucs`): `owner/name` runs the default version via `/v1/models/{owner}/{name}/predictions`, `owner/name:hash` pins a version via `/v1/predictions`, so no version hash is hard-coded to rot. The input travels as a `data:` URI because a local job file has no fetchable URL, which is why `Cloud:MaxUploadMb` (default 25) refuses an oversized track *before* any HTTP call.
- **LALAL.AI** (`LalalStemSeparator`): `upload/` → `split/stem_separator/` → poll `check/` → download `tracks[]` → `delete/`. Authentication is the **`X-License-Key` header, not a bearer token**. The uploaded source is deleted in a `finally`, so a cancelled or failed split never leaves the user's audio on a third-party server; deletion is best-effort and cannot mask the real failure.

**Deviation from §5's "standard resilience handler".** `ResilientHttp` is a hand-rolled retry (3 attempts, 1 s then 2 s, retrying only 5xx/408/429 — a 401 means the key is wrong and retrying it just burns rate limit). `Microsoft.Extensions.Http.Resilience` would add a dependency whose delays are real and jittered, making backoff untestable without sleeping. Driving the delay through the already-registered `TimeProvider` makes it deterministic under `FakeTimeProvider` and adds nothing to the dependency graph.

**`Cloud:Enabled` kill switch (new, not in the spec).** §4 falls a failed stage through to a paid tier *automatically*. A user must be able to forbid that without deleting their keys, so `Cloud:Enabled` (default `true`) short-circuits `IsAvailableAsync` on every cloud executor. `CloudCredentials.TokenFor` still resolves when disabled and only `Has` refuses, so `/diag` can honestly report "credential present, spending forbidden" — and it now does, via a `CloudEnabled` field. `SonicApiKey` was removed from `ProviderKeys` for the same honesty reason: advertising a key slot for a dead service invites a user to configure something that can never work.

**No real provider call has ever been made — stated plainly rather than implied.** There are no provider keys in this environment, so both cloud executors correctly report unavailable and the local tier keeps winning. Every path is verified against `HttpListener` fixtures reproducing the documented protocols (13 Replicate tests, 12 LALAL.AI tests), exactly as the Ollama tests do. **Nothing has been spent, and no code path has been exercised against a live paid API.** The first real call will be the first genuine test of these mappings; the contracts are documented above so that call can be checked against them.

**Mock-data banner status — correcting the plan's optimism.** The Phase 5 plan predicted the "USING MOCK DATA" banner would switch off. It does **not** yet, and the reason is not chords. `FakeChordRecognizer` is now unreachable in production (a real, unconditionally-available recogniser is registered ahead of it), so no fake *chord* executor is ever selected. But `MockDataState.PlanContainsFakeExecutor` looks at all four stages, and `FakeStemSeparator`/`FakePitchTracker` are still selected whenever their ONNX models are absent — which is the case in the E2EUI fixture by design (`Models:AutoDownload=false`, isolated `Models:RootPath`, so browser tests stay fast and network-free) and on this box, where only `htdemucs_fp16weights.onnx` has been fetched. The banner going dark is gated on model availability, a Phase-4 concern, not on Phase 5. Related cleanup left undone: `FakeChordRecognizer` is now dead in production yet still registered in `Program.cs` and used as a stand-in in unit/integration tests.
