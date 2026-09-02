# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```powershell
docker compose up -d                     # one shared Azurite (blob emulator) for all PoMode* repos
dotnet build                             # builds PoMode.slnx (kill any running PoMode.API first — it locks DLLs)
dotnet run --project src/PoMode.API      # serves API + Blazor client at http://localhost:5000 / https://localhost:5001
dotnet test tests/PoMode.Unit            # pure business logic, fast
dotnet test tests/PoMode.Integration     # needs Azurite running
dotnet test tests/PoMode.E2EAPI          # HTTP contract tests (in-process app)
dotnet test tests/PoMode.E2EUI           # Playwright browser tests (boots the real app)
dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~TempoEstimator"   # single test/class
```

- `tests/PoMode.Integration` includes `ModelAccuracyReportTests`: it renders a known-truth sample MP3, races every free pitch/chord executor against it, and rewrites `test-reports/model-accuracy.html`. It is a reporting tool, not a test, so it is opt-in: `POMODE_MODEL_REPORT=1 dotnet test tests/PoMode.Integration`. Otherwise it skips.

- First E2EUI run: install browsers with `pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium`.
- API reference UI: `/scalar`. Health: `/health`, `/health/live`, `/health/ready`. Diagnostics: `/diag`.

## Working rules

- **Do not run the test suites after making code changes.** Build to check it compiles, then stop and
  hand back. The suites are the user's to run when they want them; running them unprompted burns
  minutes on every edit. This overrides any instinct to verify by testing — say what you changed and
  what you did not verify, rather than testing to find out.

## Architecture

One process: `PoMode.API` hosts the Blazor WASM client (`PoMode.Client`), the REST endpoints, a SignalR hub (`/hubs/analysis`), and the background analysis worker. `PoMode.Shared` holds DTOs and the source-generated `PoModeJsonContext`, plus — as the one deliberate carve-out from NET_RULES' "zero business logic" — pure, dependency-free lookup extensions over those DTOs that both API and Client need (e.g. `ModalResultExtensions.WindowIndexAt`, `TimelineSearch`); anything with I/O, state, or musical judgment stays out.

### Analysis pipeline (the core)

Each uploaded song becomes a job that runs 4 stages in `AnalysisPipeline`: **Separating → PitchTracking → ChordDetecting → ModalAnalysis**. Every stage has multiple executors registered in `Program.cs` behind seams (`IStemSeparator`, `IPitchTracker`, `IChordRecognizer`), each tagged with an `ExecutionTier` (Local ONNX model, Cloud API, ClientDelegated browser inference, Fake). `ExecutionPlanner.EffectiveRank` fixes the selection order: local model → browser → classic model-less DSP (`IsClassicFallback`: `YinPitchTracker`, `ViterbiChordRecognizer`) → Fake placeholder → paid Cloud; within a rank, DI registration order breaks ties, so register new executors *after* the one that should stay the default. `AnalysisPipeline.RunWithFallbackAsync` falls through the same order when an executor fails and records who actually ran in `StageHistory`. If any Fake executor ran, the client shows the "USING MOCK DATA" banner.

Users can pin an executor per stage: `GET /api/analysis/executors` feeds the home page's radio groups (Cloud and Fake are filtered out — never user-selectable), the pick rides on upload query params (`stemSeparator`/`pitchTracker`/`chordRecognizer`), and the planner honours it only if it is available and not Cloud.

Jobs are restart-safe: `JobStore` persists `job.json` plus artifacts (`notes.json`, `notes-backing.json`, `chords.json`, `beats.json`, `result.json`, stem WAVs) in a per-job folder under a per-job semaphore, mirroring everything to Azure Blob (Azurite locally). `JobRecoveryService` re-enqueues incomplete jobs on boot; `JobCleanupService` purges old ones. Stage progress is pushed over SignalR only — never polled, never written per-tick.

**Tier 2 (client-delegated)**: the browser probes onnxruntime-web support (`pitch-worker.js`), uploads declare `clientCanInfer=true`, and when a job reaches `AwaitingClient` the browser runs the model and POSTs validated notes back to `/api/analysis/{jobId}/client-result`.

### Mode Lab harmony

A mode is a tonal centre, not just a note set, so the Mode Lab's harmony has to move with the mode or
every card sounds like the parent key with a displaced melody. Each progression in
`ModalMelodyGenerator.Presets` declares `RootsOn`: pop progressions count their roman numerals from
the parent key (`I` is the key), modal ones count from the mode root (`i` is the mode's own tonic).
Rooting the second kind on the parent is what used to put an E flat under a D Dorian melody.

Nine presets carry `IsModeSignature` — one per card on the strip — and every one is still built only
from the parent key's seven notes, which is the point: the note set never changes, only which note
the harmony treats as home. The client's "Match to mode" toggle (default on) swaps in
`ProgressionCatalog.SignatureFor(mode)` when a card is picked, and `FirstSharedHarmony()` when it is
switched off, which restores the older one-progression-under-all-modes lesson. The melody pitch pool
is the mode's own scale, not the parent's — identical for the seven diatonic modes, and the reason a
pentatonic card no longer sounds the two notes its scale exists to omit.

### Song statistics and interpretation

`GET /api/analysis/{id}/stats` derives every melody/harmony statistic on demand from the stored
artifacts — `SongStatsBuilder` takes the `VisualizationPayload` (so note roles and pitch labels are
reused, never recomputed) plus `chords.json`, `result.json` and the optional `beats.json`. Nothing is
persisted, same ruling as `/visual`. `SongFingerprint` then writes the same numbers as one
plain-English paragraph; its rule is that a weak figure (unconfident mode, missing beat grid) is
*omitted*, never hedged.

`GET /api/analysis/{id}/interpretation?interpreter=` turns those statistics into prose behind the
`ISongInterpreter` seam. It extends `IStageExecutor`, so `ExecutionPlanner.EffectiveRank` orders the
implementations without new rules: `OllamaSongInterpreter` (Local, uses whatever model Ollama has
installed) → `TemplateSongInterpreter` (deterministic, always available, `IsClassicFallback`). There
is no cloud interpreter; a paid one was documented here for a while but never existed in the repo.
`SongInterpreterSelector` falls through on failure exactly like `RunWithFallbackAsync`, and ranks by
answer quality rather than by `ExecutionPlanner.EffectiveRank`, because one small prompt is not the
cost question a pipeline stage poses. `InterpretationPrompt` contains only measured numbers, no audio,
title or artist, so a model cannot report what it was never given.
Ollama requests set `think: false`: reasoning models otherwise spend the whole output budget on
`thinking` and return empty `content`.

### Client conventions

- Heavy UI lives in plain JS modules, not Blazor: `canvas.js` (dual-lane visualization, pan/zoom, virtualized drawing) and `mixer.js` (Web Audio stem playback, synth note overlays, metronome clicks, Space/comma transport keys). `mixer.js` owns the transport clock and drives the canvas playhead directly — no per-frame Blazor renders. Blazor components only issue commands and receive discrete events.
- JS state is mirrored onto `data-*` attributes (`data-mixer-status`, `data-playhead`, …) precisely so Playwright tests can assert without reaching into module internals. Keep that contract when changing these modules.
- Musical decisions (note colours, labels, measure numbers) are made server-side in `VisualizationBuilder`; `canvas.js` only maps numbers to pixels. Keep music theory out of JS. Same rule for audio: the mixer's chord-pad layer plays notes voiced server-side by `ChordPadBuilder` (served as `/api/analysis/{id}/notes-chords`, derived from chords.json, not stored) — mixer.js treats them as just another note list ('vocal'/'backing'/'chords').

### Infrastructure notes

- `SecretsBootstrap` wires Key Vault via `DefaultAzureCredential` with an env-var fallback (logged as a warning). Never add connection strings or appsettings secrets.
- `Program.cs` throws in Production by design: `FakeAuthHandler` (`X-Fake-User` / `X-Fake-Roles` headers) is the only auth configured.
- E2EUI's `AppFixture` boots the real app and uploads real WAVs generated by `TestCommon`'s `TestAudio`; E2EAPI uses `AuthedFactory` (in-process, FakeAuth headers). `TestCommon` holds shared audio/fixture helpers, not an app fixture.

# NET_RULES (New project: apply all / Existing project: verify & fix)

## 1. Core Principles & Architecture
* **Naming Standard:** Prefix solutions, projects, and root namespaces with `Po{Name}`.
* **Tech Stack:** .NET 10 / C# 15 with Centralized Package Management (`/Directory.Packages.props`).
* **Compiler Guards:** Enforce `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` globally in `Directory.Build.props`.
* **Solution Layout:**
  * `src/Po{Name}.API/`: Minimal API host using autonomous, decoupled Vertical Slice Architecture (`Features/{FeatureName}`).
  * `src/Po{Name}.Client/`: Blazor WASM UI hosted directly by `Po{Name}.API`.
  * `src/Po{Name}.Shared/`: DTOs, Enums, Interfaces, JSON contexts. Zero business logic or data access.
  * `tests/`: `Po{Name}.Unit` (business logic), `Po{Name}.Integration` (Testcontainers/Azurite), `Po{Name}.E2EAPI` (HTTP contract tests), `Po{Name}.E2EUI` (Playwright tests).

## 2. API, Security & Infrastructure
* **Endpoints:** Map via `IEndpointRouteBuilder` + `MapGroup()`. Auto-document with `Microsoft.AspNetCore.OpenApi` and serve via Scalar UI.
* **Dev/Test Auth:** Use `FakeAuthHandler` reading `X-Fake-User` and `X-Fake-Roles` headers. MUST throw `InvalidOperationException` in Production.
* **Secrets & Identity:** Resource Group `PoShared` (or `Po{Name}`). Authenticate exclusively via System-Assigned Managed Identity / `DefaultAzureCredential` + Azure Key Vault (Local & Azure). Connection strings, `appsettings` secrets, and `dotnet-secrets` are strictly forbidden. Get keys from key vault in dev env and prod env.
* **Health & Diagnostics:**
  * `/health`: Native .NET health status for external dependencies.
  * `/diag`: Real-time operational summary. Must strictly redact all secrets, tokens, and connection strings.

## 3. UI/UX & Blazor WASM
* **Layout Structure:** Header format: `[Left: Branding | Center: Contextual Actions | Right: Session / Logout]`.
* **UI Controls & Styling:** Radzen Blazor library (prefer advanced Radzen controls when possible). Zero inline CSS—use scoped `.razor.css` and global CSS variables only. Auto-detect system Light/Dark themes.
* **Mock Indicator:** Display a persistent warning banner ("USING MOCK DATA") whenever an active state uses mock/local data.
* **Code Hygiene:** Continuously purge unused files, dead code, orphaned assets, and unused `using` directives across all commits.
* **Ports:** HTTP 5000, HTTPS 5001.
* **Home page title:** exactly `Po{Name}` (this app: `PoMode`).
