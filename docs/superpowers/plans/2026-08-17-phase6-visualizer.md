# PoMode Phase 6: Visualizer Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the current text-list results page into the visualizer §7 describes: a dual-lane canvas (piano roll + chord blocks), a Radzen HUD that explains the modal analysis, a Web Audio stem mixer, and the local Ollama copilot. After this phase a user uploads a song and *sees* the analysis instead of reading a list of numbers.

**Architecture:** All analysis data already exists as DTOs (`NoteEvent`, `ChordSpan`, `ModalResult`/`ModalWindow`/`ModalMatch`) served by the four existing artifact endpoints. Phase 6 adds no new analysis. It adds:
- a **pure C# colouring/lookup layer in the API** (unit-testable, no canvas involved) that answers "what colour class is this note?" and "which modal window covers time *t*?", served to the client as a finished `VisualizationPayload` — see Task 1's ruling on why this is not in `PoMode.Shared`;
- a **JS interop module** (`wwwroot/js/canvas.js`) that draws from a flat, pre-computed payload — the Blazor render tree never touches per-note elements;
- **Radzen HUD components** bound to the selected window;
- a **Web Audio module** (`wwwroot/js/mixer.js`) owning playback, gain ramps, and the clock the scrubber reads;
- a **server-side copilot endpoint** proxying local Ollama (keys never involved — it is localhost only).

**Tech Stack:** .NET 10, Blazor WASM, Radzen, plain ES modules (no npm, no bundler, no new packages), xUnit, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-16-pomode-design.md` §5 (Copilot), §7 (Frontend), §13.6 (Phase 5 rulings).

**Plan-level rulings (decided now):**
- **The colouring rules live in C#, not JS.** JS receives numbers and draws rectangles. This keeps every musical decision unit-testable and stops the two languages disagreeing about what "in-mode" means.
- **No npm, no bundler, no charting library.** Plain ES modules loaded via `IJSRuntime.InvokeAsync<IJSObjectReference>("import", ...)`. Consistent with "no new packages" and keeps the WASM payload small.
- **Zero inline CSS** (CLAUDE.md). Scoped `.razor.css` and CSS variables only. Canvas colours are read from CSS variables via `getComputedStyle` so light/dark auto-detect keeps working for the canvas too.
- **The stem mixer needs stem audio over HTTP.** Phase 4 writes `vocals.wav`/`instrumental.wav` into the job dir but nothing serves them. Task 4 adds `GET /api/analysis/{jobId}/stems/{name}` reusing the existing `MapArtifact` pattern and the same 32-hex `jobId` validation, with `name` restricted to a fixed allow-list (`mix`, `vocals`, `instrumental`) — never a caller-supplied path.
- **Copilot is localhost-only and always optional.** No Ollama ⇒ the HUD shows a "Copilot unavailable" card. It is never a hard dependency and never routes to a cloud provider (§5).
- **The mock-data banner logic is untouched.** It stays gated on `PlanContainsFakeExecutor` (§13.6). Phase 6 must not "fix" the banner by weakening that check.

## Global Constraints

- All prior constraints hold: `net10.0`, Nullable + TreatWarningsAsErrors from `Directory.Build.props` only; CPM (versions ONLY in `Directory.Packages.props`); `PoMode.` prefixes; no secrets; endpoints via `MapGroup()` + `TypedResults`; zero inline CSS.
- **TDD with log-file evidence is mandatory.** Tee every RED and GREEN run to `<workspace>/task-N-{red,green}.log` and quote them. Check the full log yourself before reporting DONE.
- **Test-run procedure (learned in Phase 5 — follow it, do not use a bare `dotnet test`):** run `tests/PoMode.Unit`, then `tests/PoMode.Integration`, then `tests/PoMode.E2EAPI`, each as its own invocation, then `tests/PoMode.E2EUI` **alone and last**. A full-solution run leaves a `PoMode.API` process from the E2EUI fixture holding `PoMode.Shared.dll`, and the next build dies with `MSB3027`. `dotnet test` also rejects two project paths in one invocation (`MSB1008`). If a build fails with a file-in-use error, find the stale process (`netstat -ano | grep :5199`, `taskkill //PID <pid> //F`) and say so.
- Commit hygiene: stage only each task's listed paths. Never stage `.claude/`, `.superpowers/`, `models/`, `bin/`, `obj/`, `*.mp3`, `*.wav`, `*.onnx`.
- Commits: conventional style ending with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: Note Colouring and Window Lookup (server-side, pure)

**Ruling made while starting this task — the plan's first draft was wrong.** The draft put the colouring logic in `PoMode.Shared` so the client could compute it. That breaks CLAUDE.md's "`Shared`: DTOs, Enums, Interfaces, JSON contexts. **Zero business logic**", because deciding whether a note is in-mode needs `ModeDefinitions` — the modal engine's own interval and characteristic-degree tables. Duplicating those tables in `Shared` would also create a second source of truth for the masks §6 insists are derived once, in code. So: **the builder lives in the API, the DTOs live in `Shared`, and the client fetches a finished payload.** The client draws; it decides nothing.

**Files:**
- Create: `src/PoMode.Shared/Analysis/VisualContracts.cs`, `src/PoMode.API/Features/Visualization/VisualizationBuilder.cs`
- Modify: `src/PoMode.API/Features/Analysis/AnalysisEndpoints.cs` (serve the payload), `src/PoMode.Client/Services/AnalysisClient.cs`
- Test: `tests/PoMode.Unit/Visualization/VisualizationBuilderTests.cs`, `tests/PoMode.E2EAPI/VisualEndpointTests.cs`

**Interfaces:**
- Consumes: `NoteEvent`, `ChordSpan`, `ModalResult`, `ModalWindow`, `ModalMatch`, `ScaleMode`, and the API's existing `ModeDefinitions` + `PitchNames` (reused, not reimplemented).
- Produces (consumed by Tasks 2–4):
  - `enum NoteRole { ChordTone, InMode, Characteristic, Outside }` (in `Shared`)
  - `record VisualNote(int MidiPitch, double StartSec, double DurationSec, int Velocity, NoteRole Role, string PitchLabel, string DegreeLabel)`
  - `record VisualChord(string Symbol, double StartSec, double EndSec, int MeasureNumber, string? ModeTag)`
  - `record VisualizationPayload(int SchemaVersion, IReadOnlyList<VisualNote> Notes, IReadOnlyList<VisualChord> Chords, double DurationSec, int MinPitch, int MaxPitch)`
  - `static VisualizationPayload VisualizationBuilder.Build(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords, ModalResult result)`
  - `static int? VisualizationBuilder.WindowIndexAt(ModalResult result, double timeSec)` — the window covering `timeSec`, or `null` past the end
  - `GET /api/analysis/{jobId}/visual` → `VisualizationPayload`, built on demand from the three existing artifacts; 404 if any is missing. The `notes`/`chords`/`result` endpoints stay as they are.

**Role rules (in priority order, first match wins):**
1. **ChordTone** — the note's pitch class is in the triad of the `ChordSpan` covering its start (root/third/fifth, derived by rotation from `Root` + `Quality`, never a hand-written table).
2. **Characteristic** — the interval above the tonic is in `ModeDefinitions.CharacteristicIntervals` for the window's top-ranked mode. **Use that existing table, do not write a new one** — it already encodes Dorian `[9,3]`, Lydian `[6]`, Phrygian `[1]`, Mixolydian `[10,4]`, Aeolian `[8,3]`, Locrian `[6,1]`, Ionian `[11]`, and both pentatonics.
3. **InMode** — the interval is set in the top-ranked mode's `ModeDefinitions.Mask`.
4. **Outside** — everything else. Notes in a window with `InsufficientEvidence`, or past the last window, fall back to the **primary** mode; with no primary mode they are `InMode` only if a chord tone, else `Outside`.

**Labels:** `PitchLabel` is scientific pitch (`"B4"`, `"F#3"`) built from `PitchNames.Name` plus the octave; `DegreeLabel` is the bracketed degree relative to the tonic (`"[b3]"`, `"[4]"`, `"[#4]"`) from `PitchNames.IntervalLabel` — the dual label §7 asks for.

- [ ] **Step 1: Write the failing tests.** Cover: a C-major chord's C/E/G become `ChordTone`; a ♮6 over a Dorian window becomes `Characteristic` and not merely `InMode`; a chromatic outsider becomes `Outside`; role priority (a note that is both a chord tone and characteristic is `ChordTone`); `MinPitch`/`MaxPitch` bracket the input and stay valid for an empty note list; `DurationSec` is the max of the last note end and last chord end; `PitchLabel`/`DegreeLabel` formats; `WindowIndexAt` at a boundary picks the later window and returns `null` past the end; `InsufficientEvidence` windows fall back to the primary mode; a `null` `PrimaryMode` does not throw. E2EAPI: `/visual` returns a well-formed payload for a completed job and 404 for an unknown or incomplete one.
- [ ] **Step 2: RED** — `dotnet test tests/PoMode.Unit --filter VisualizationBuilderTests` (tee `task-1-red.log`).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — Unit, then E2EAPI (tee `task-1-green.log`). Commit:

```powershell
git add src/PoMode.Shared src/PoMode.API src/PoMode.Client/Services tests/PoMode.Unit/Visualization tests/PoMode.E2EAPI
git commit -m "feat: server-built visualization payload with note roles and dual labels"
```

---

### Task 2: Dual-Lane Canvas

**Files:**
- Create: `src/PoMode.Client/wwwroot/js/canvas.js`, `src/PoMode.Client/Components/AnalysisCanvas.razor`, `src/PoMode.Client/Components/AnalysisCanvas.razor.css`
- Modify: `src/PoMode.Client/Pages/Home.razor` (replace the two `<ul>` result lists with the canvas), `src/PoMode.Client/wwwroot/css/app.css` (role colour variables)
- Test: `tests/PoMode.E2EUI/CanvasTests.cs`

**Interfaces:**
- Consumes: `VisualizationPayload` (Task 1).
- Produces (consumed by Tasks 3–4):
  - `AnalysisCanvas` parameters: `Model`, `SelectedWindowIndex`, `PlayheadSec`, and an `EventCallback<double>` `OnSeek` raised when the user clicks a measure.
  - JS module exports: `init(canvas, dotNetRef)`, `setModel(canvas, payload)`, `setPlayhead(canvas, sec)`, `setSelection(canvas, index)`, `dispose(canvas)`. Every export takes the canvas element, and per-canvas state lives in a module-level `Map` keyed by it — so a second canvas on a page can never share state with the first.

**Rendering:** one `<canvas>`, two lanes. Top lane = note capsules, x from time, y from pitch (scaled between `MinPitch`/`MaxPitch`), fill from `NoteRole`, dual labels drawn only when a capsule is wide enough to fit them. Bottom lane = chord blocks with symbol, measure number, and mode tag. Zoom/pan on wheel and drag, with **virtualized drawing** — only items intersecting the visible time range are drawn, so a 5-minute song stays smooth. Redraw on `requestAnimationFrame`, never per-event. Colours are read once from CSS variables (`--note-chord-tone` etc.) via `getComputedStyle` and re-read on a `prefers-color-scheme` change so dark mode works.

- [ ] **Step 1: Write the failing Playwright test.** Upload the fixture, then assert: the `<canvas>` is visible with non-zero width/height; the canvas has actually painted (read back pixel data via `page.EvaluateAsync` on the 2D context and assert not-all-transparent); a wheel-zoom changes the reported time range (expose it as a `data-` attribute or a JS getter for the test to read); clicking in the chord lane raises a seek that moves the playhead.
- [ ] **Step 2: RED** — E2EUI alone (tee `task-2-red.log`).
- [ ] **Step 3: Implement** the module and component. `AnalysisCanvas` serialises `Model` once into a flat payload (typed arrays where practical) and calls `setModel`; it must `dispose()` the module reference in `IAsyncDisposable`.
- [ ] **Step 4: GREEN** — Unit, Integration, E2EAPI, then E2EUI alone (tee `task-2-green.log`). Commit:

```powershell
git add src/PoMode.Client tests/PoMode.E2EUI
git commit -m "feat: dual-lane analysis canvas with zoom, pan and measure selection"
```

---

### Task 3: Modal HUD

**Files:**
- Create: `src/PoMode.Client/Components/ModalHud.razor`, `src/PoMode.Client/Components/ModalHud.razor.css`, `src/PoMode.Client/Components/TierBadges.razor`, `src/PoMode.Client/Components/TierBadges.razor.css`
- Modify: `src/PoMode.Client/Pages/Home.razor`, `src/PoMode.Client/Components/ModalResultView.razor` (fold its content into the HUD and delete it if it becomes redundant — CLAUDE.md requires purging dead code)
- Test: `tests/PoMode.E2EUI/ModalHudTests.cs`, `tests/PoMode.Unit/Visualization/DegreeBadgeTests.cs`

**Interfaces:**
- Consumes: `ModalResult`, `ModalWindow`, `JobStatusDto.Plan`, Task 1's labels.
- Produces: `ModalHud` with parameters `Result`, `SelectedWindowIndex`, `Plan`.

**Cards (Radzen):** primary mode + `RadzenProgressBar` confidence; harmonic context (active chord, tonic name, `VocalMask` as hex — e.g. `0x6AD`); a scale-degree badge grid marking sung / in-mode / characteristic degrees; ranked alternative modes with confidences; per-stage tier badges (💻 Local / 🌐 ClientDelegated / ☁️💰 Cloud) driven by `StagePlan.Tier`. Every card must render an honest empty state — `InsufficientEvidence` windows say so rather than showing a fake 0 % match, and a `null` `PrimaryMode` must not crash the page.

- [ ] **Step 1: Write the failing tests.** Unit-test the degree-badge derivation (pure). Playwright-test that the HUD shows the primary mode and tonic after an upload, that clicking a measure in the canvas changes the displayed chord and hex mask, that an `InsufficientEvidence` window renders its explicit message, and that the tier badges match the plan the API reported.
- [ ] **Step 2: RED** — tee `task-3-red.log`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — all four suites in the prescribed order (tee `task-3-green.log`). Commit:

```powershell
git add src/PoMode.Client tests/PoMode.E2EUI tests/PoMode.Unit/Visualization
git commit -m "feat: modal HUD with degree badges, alternatives and tier badges"
```

---

### Task 4: Stem Mixer and Playback Scrubber

**Files:**
- Create: `src/PoMode.Client/wwwroot/js/mixer.js`, `src/PoMode.Client/Components/StemMixer.razor`, `src/PoMode.Client/Components/StemMixer.razor.css`
- Modify: `src/PoMode.API/Features/Analysis/AnalysisEndpoints.cs` (serve stems), `src/PoMode.Client/Pages/Home.razor`
- Test: `tests/PoMode.E2EAPI/StemEndpointTests.cs`, `tests/PoMode.E2EUI/StemMixerTests.cs`

**Interfaces:**
- Consumes: Task 2's canvas (`setPlayhead`), the new stems endpoint.
- Produces:
  - `GET /api/analysis/{jobId}/stems/{name}` → `audio/wav`, `name` ∈ `{mix, vocals, instrumental}` (allow-list, **not** a path), 404 for an unknown job, unknown name, or a stem the pipeline did not write.
  - JS module exports: `load(urls)`, `play()`, `pause()`, `seek(sec)`, `setMode(mode)`, `currentTime()`, `dispose()`.

**Behaviour (§7):** three `AudioBufferSourceNode`s started sample-synchronised through `GainNode`s. Full Mix / Solo Vocals / Solo Backing switch by 50 ms `linearRampToValueAtTime` gain ramps — no pops, and **position is preserved** across a mode switch. The scrubber reads the Web Audio clock on `requestAnimationFrame` and pushes it into `canvas.setPlayhead`; the canvas never owns the clock. Clicking the canvas calls `seek`.

- [ ] **Step 1: Write the failing tests.** E2EAPI: each allow-listed name returns wav bytes with a `RIFF` magic when the stem exists, 404 otherwise; a traversal attempt (`../../job.json`, encoded variants) is 404, never a file read. E2EUI: the three mode buttons render, clicking Solo Vocals keeps `currentTime()` monotonic (position preserved), and the playhead advances after `play()`.
- [ ] **Step 2: RED** — tee `task-4-red.log`.
- [ ] **Step 3: Implement.** Reuse the existing `IsValidJobId` and the `MapArtifact` shape; map the allow-listed name to a fixed file name server-side.
- [ ] **Step 4: GREEN** — all four suites in order (tee `task-4-green.log`). Commit:

```powershell
git add src/PoMode.API src/PoMode.Client tests/PoMode.E2EAPI tests/PoMode.E2EUI
git commit -m "feat: stem endpoints and Web Audio mixer driving the playhead"
```

---

### Task 5: Ollama Copilot

**Files:**
- Create: `src/PoMode.API/Features/Copilot/OllamaCopilotClient.cs`, `src/PoMode.API/Features/Copilot/CopilotEndpoints.cs`, `src/PoMode.Shared/Analysis/CopilotContracts.cs`, `src/PoMode.Client/Components/CopilotCard.razor`, `src/PoMode.Client/Components/CopilotCard.razor.css`
- Modify: `src/PoMode.API/Program.cs`, `src/PoMode.Client/Components/ModalHud.razor`
- Test: `tests/PoMode.Integration/OllamaCopilotClientTests.cs`, `tests/PoMode.E2EAPI/CopilotEndpointTests.cs`, `tests/PoMode.E2EUI/CopilotCardTests.cs`

**Interfaces:**
- Consumes: `ModalResult`, `ModalWindow`.
- Produces:
  - `record CopilotRequest(string JobId, int WindowIndex)`, `record CopilotReply(bool Available, string? Markdown, string? Model, string? Reason)`
  - `POST /api/copilot/explain` → `CopilotReply`
  - `OllamaCopilotClient` — lists installed models at `GET http://localhost:11434/api/tags`, picks by preference, generates at `POST /api/generate`.

**Model preference (correcting §5):** §5's list (`qwen2.5:7b`, `llama3.3:8b`, `llama3.2:3b`) does not match this machine — §13.1 recorded that the installed model is `gemma4:26b`. Implement it as **preference list, then first available model**, so it works on any box. Record this in the spec as part of Step 4.

**Prompt (§5):** active chord, global key, sung intervals, detected mode + confidence, hex bitmask → a two-sentence musicological explanation, rendered as Markdown. **Never cloud** — the base URL is localhost and is not configurable to a remote host.

- [ ] **Step 1: Write the failing tests.** Integration against a **local HTTP fixture server** (the same technique Phase 4 used for the model registry — no real Ollama in tests): model preference order, fall-through to first-available, `Available = false` with a `Reason` when the host refuses the connection, and a timeout that degrades rather than throwing. E2EAPI: the endpoint returns a well-formed `CopilotReply` (never a 500) when Ollama is absent. E2EUI: the card shows "Copilot unavailable" when there is no Ollama, and a Regenerate button is present.
- [ ] **Step 2: RED** — tee `task-5-red.log`.
- [ ] **Step 3: Implement.** Register a named `HttpClient` with a short timeout. `/health` should already treat Ollama as degraded-not-unhealthy (§9) — verify, and fix if it does not.
- [ ] **Step 4: GREEN** — all four suites in order (tee `task-5-green.log`). Then add a spec subsection `### 13.7 Phase 6 rulings` recording: the colouring layer lives in `PoMode.Shared` and why; no npm/bundler; the stems endpoint's allow-list; the corrected copilot model-preference behaviour; and an honest note on how the canvas performed on the real 162 s track. Commit:

```powershell
git add src/PoMode.API src/PoMode.Client src/PoMode.Shared tests docs/superpowers/specs/2026-08-16-pomode-design.md
git commit -m "feat: local Ollama copilot with graceful unavailability, and Phase 6 rulings"
```

---

## Phase 6 Exit Criteria

- All four suites green in the prescribed order; zero build warnings; no `Version=` in any csproj; no secrets; no inline CSS anywhere.
- Uploading a track produces a **visible, zoomable canvas** with coloured note capsules and chord blocks — not a text list. The old `<ul>` result lists are gone.
- Clicking a measure updates the HUD (chord, hex mask, degree badges, alternatives).
- The stem mixer plays, switches Full Mix / Solo Vocals / Solo Backing without pops, and preserves position; the playhead tracks the Web Audio clock.
- The copilot explains the selected window when Ollama is present and degrades to a visible "Copilot unavailable" card when it is not — never a 500, never a cloud call.
- Light and dark themes both render the canvas legibly (colours come from CSS variables).
- The mock-data banner logic is unchanged and still honest.
- Remaining after Phase 6: the **cloud tier (Tier 3)** and **WebGPU/ClientDelegated pitch tracking (Tier 2)** — both still entirely unbuilt — plus the deferred artifact read/write race (§13.4) and the `Jobs:RootPath` fix required before any deploy.
