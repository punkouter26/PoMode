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
