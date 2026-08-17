# PoMode Phase 8: Browser Tier (Tier 2 — ClientDelegated) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill `ExecutionTier.ClientDelegated`, the last empty tier. When the server cannot run Basic Pitch itself — the real case being Azure mode, where §4 hard-disables local ONNX — the pitch stage is handed to the user's browser instead of to a paid provider. Free, private, and it keeps the audio on the user's machine.

**Architecture:** The pipeline already falls through tiers and already resumes stages, so Tier 2 is an `IPitchTracker` with `Tier = ClientDelegated` whose `TrackAsync` *parks*: it registers a waiter, asks the browser to do the work over the existing SignalR hub, and awaits the result the browser POSTs back. Everything else is plumbing around that one idea — plus the security work that client-supplied note events demand.

**Tech Stack:** .NET 10, existing SignalR hub, `onnxruntime-web`, plain ES modules (no npm, no bundler), xUnit, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-16-pomode-design.md` §4 (ClientDelegated protocol), §5 (Stage 2 — "the decoder algorithm is specified once and implemented twice"), §13.

---

## Measured facts this plan is built on

Probed in headless Chromium through this repo's own Playwright before planning, rather than assumed:

| Capability | Result |
|---|---|
| `navigator.gpu` present | yes |
| `navigator.gpu.requestAdapter()` | **null** — even with `--enable-unsafe-webgpu --enable-features=Vulkan` |
| WebAssembly | yes |
| WASM SIMD | yes |
| WASM threads (`SharedArrayBuffer`) | **no** — the page is not cross-origin isolated |

**Consequences, and they shape the whole phase:**

1. **The WASM SIMD backend is the verified path; WebGPU is an opportunistic upgrade.** §4 names WebGPU first with a WASM fallback. On this machine WebGPU cannot be exercised at all, so building "WebGPU or nothing" would ship a feature no test here can prove. Inverted: the client probes for a WebGPU adapter and uses it when one is really available, otherwise WASM SIMD — and **the tests run the WASM path**, which is the one that always works. A real user with a working GPU silently gets the faster backend.
2. **No threaded WASM, so no COOP/COEP.** Enabling cross-origin isolation to unlock threads would change response headers app-wide for a 230 KB model that does not need it. Not done.
3. **Basic Pitch is 230,444 bytes.** Small enough to send to a browser without ceremony — this is what makes the whole tier practical.

## Scope rulings decided up front

- **The decoder is ported, not reinvented.** §5 requires the note-event decoder to exist twice and be tested against the same fixtures. `BasicPitchDecoder` is already a pure static class, so the JS port is a direct translation and both sides get asserted against one shared fixture file. Any divergence is a bug in the port, and the test says which.
- **Client-supplied notes are untrusted input.** The browser posts note events that go straight into `notes.json` and then into the modal engine and the MIDI export. Validation is not a nicety: bounds on count, MIDI pitch, times, duration and velocity, all rejected server-side with a 400. A malicious or broken client must not be able to make the server write a 500 MB artifact or a note at bar 10^9.
- **Availability is per-job, not global.** A browser tier only exists if *that job's* browser said it can do the work. `ExecutionPlanner.PlanAsync` currently takes no job, so it gains an optional capability argument. Without a declared capability the tier is simply absent and the planner behaves exactly as it does today.
- **Parking must never wedge a job.** A browser that navigates away, crashes, or lies about its capability must not leave a job stuck forever. A bounded wait (`Tier2:TimeoutSeconds`, default 300 per §4's five minutes) expires and the stage falls through to the next tier — which is exactly the behaviour `AnalysisPipeline.RunWithFallbackAsync` already provides once the executor throws.
- **`onnxruntime-web` is served by us, not by a CDN.** The app is local-first and must work without internet after first use. The runtime's assets are fetched and hash-verified by the existing `ModelRegistry` machinery and served from our own origin, exactly like the ONNX models — and, like them, never committed.

## Global Constraints

- All prior constraints hold: `net10.0`, Nullable + TreatWarningsAsErrors; CPM (versions ONLY in `Directory.Packages.props`); `PoMode.` prefixes; endpoints via `MapGroup()` + `TypedResults`; zero inline CSS; no secrets.
- **TDD with log-file evidence is mandatory.** Tee every RED and GREEN run to `<workspace>/task-N-{red,green}.log`.
- **Test-run procedure (do not use a bare `dotnet test`):** `tests/PoMode.Unit`, `tests/PoMode.Integration`, `tests/PoMode.E2EAPI` as separate invocations, then `tests/PoMode.E2EUI` **alone and last**. A full-solution run leaves a `PoMode.API` process holding `PoMode.Shared.dll` → `MSB3027`; `dotnet test` also rejects two project paths in one invocation (`MSB1008`).
- **HttpListener fixture rule (learned in Phase 7):** one port per test instance, and disable connection pooling when asserting request counts. Ports taken: 5199, 5200, 5251, 5310, 5311, 5320, 5322, 5323, 5330, 5340+, 5398, 5399.
- Commit hygiene: never stage `.claude/`, `.superpowers/`, `models/`, `bin/`, `obj/`, `*.mp3`, `*.wav`, `*.onnx`, `*.wasm`.
- Commits: conventional style ending with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: Parking, the Waiter Registry and Per-Job Availability

**Files:**
- Create: `src/PoMode.API/Features/PitchTracking/ClientWorkRegistry.cs`, `src/PoMode.API/Features/PitchTracking/ClientDelegatedPitchTracker.cs`
- Modify: `src/PoMode.Shared/Analysis/JobContracts.cs` (add `AwaitingClient` to `JobStage`, add capability to `JobStatusDto`), `src/PoMode.API/Pipeline/ExecutionPlanner.cs`, `src/PoMode.API/Features/Analysis/JobState.cs`, `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.Unit/PitchTracking/ClientWorkRegistryTests.cs`, `tests/PoMode.Unit/Pipeline/ExecutionPlannerTests.cs` (extend)

**Interfaces:**
- `ClientWorkRegistry` — `Task<IReadOnlyList<NoteEvent>> WaitAsync(string jobId, TimeSpan timeout, CancellationToken ct)`; `bool TryComplete(string jobId, IReadOnlyList<NoteEvent> notes)`; `bool IsWaiting(string jobId)`. Backed by a `ConcurrentDictionary<string, TaskCompletionSource<...>>`, timing out through the injected `TimeProvider` so tests need no sleeping. Completing an unknown job returns false rather than throwing. The waiter is always removed, on every path.
- `ClientDelegatedPitchTracker : IPitchTracker` — `Tier = ClientDelegated`; `TrackAsync` registers the waiter, sets the job to `AwaitingClient`, notifies, and awaits. On timeout it throws so the pipeline falls through.
- `ExecutionPlanner.PlanAsync(ClientCapability capability, CancellationToken ct)` — a `ClientDelegated` candidate is considered only when the capability says the browser can run inference. The existing no-argument overload keeps working and means "no browser help".

- [ ] **Step 1: Write the failing tests.** Registry: a waiter completes with the posted notes; `TryComplete` on an unknown job is false; a second `TryComplete` is false; the timeout throws `TimeoutException` and removes the waiter; a cancelled token removes the waiter; concurrent waits for different jobs do not interfere. Planner: with no capability the browser tier is invisible even when registered; with capability it outranks `Cloud` and loses to `Local`.
- [ ] **Step 2: RED** — tee `task-1-red.log`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — Unit (tee `task-1-green.log`). Commit.

---

### Task 2: The `client-result` Endpoint and Its Validation

**Files:**
- Create: `src/PoMode.API/Features/PitchTracking/ClientResultValidator.cs`
- Modify: `src/PoMode.API/Features/Analysis/AnalysisEndpoints.cs`
- Test: `tests/PoMode.Unit/PitchTracking/ClientResultValidatorTests.cs`, `tests/PoMode.E2EAPI/ClientResultTests.cs`

**Interfaces:**
- `POST /api/analysis/{jobId}/client-result` with `IReadOnlyList<NoteEvent>` → `200` when accepted, `400` with a reason when the payload is rejected, `404` when the job is unknown or is not waiting.
- `ClientResultValidator.Validate(notes, trackDurationSec)` → `string?` (null = valid).

**Validation rules (§4 step 3, made concrete):** at most 20,000 notes; MIDI pitch within 21–108; `StartSec` ≥ 0 and within the track duration plus one second of slack; `DurationSec` > 0 and ≤ 30 s; velocity 1–127; every number finite (no `NaN`/`Infinity`, which would otherwise reach the MIDI writer and the JSON artifact). Reject the whole payload rather than silently dropping bad notes — a partially-accepted analysis is worse than a rejected one.

- [ ] **Step 1: Write the failing tests**, one per rule plus the happy path, plus: posting for a job that is not waiting is 404; posting twice — the second is 404 because the waiter is gone; an oversized payload is rejected; `NaN` is rejected.
- [ ] **Step 2: RED** — tee `task-2-red.log`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — Unit then E2EAPI (tee `task-2-green.log`). Commit.

---

### Task 3: Timeout, Fallback and the Hub Signal

**Files:**
- Modify: `src/PoMode.API/Features/Analysis/AnalysisHub.cs` / `SignalRAnalysisNotifier`, `src/PoMode.API/Features/Analysis/AnalysisPipeline.cs` if needed
- Test: `tests/PoMode.Integration/ClientDelegatedFallbackTests.cs`

**Behaviour:** the existing single `JobStatusChanged(JobStatusDto)` event carries the new `AwaitingClient` stage (§13.5 already replaced §4's five named events with this one, so no new event is invented). The DTO gains the stem URL the browser should fetch.

- [ ] **Step 1: Write the failing tests.** A job whose browser never answers times out and falls through to the next tier, and the recorded plan updates. A job whose browser answers completes with the client's notes and the plan records `ClientDelegated`. Cancelling a parked job does not leave a waiter behind.
- [ ] **Step 2: RED** — tee `task-3-red.log`. **Step 3: Implement. Step 4: GREEN** — Integration. Commit.

---

### Task 4: The JS Decoder Port, Verified Against the C# Fixtures

**Files:**
- Create: `src/PoMode.Client/wwwroot/js/basic-pitch-decoder.js`, `tests/TestCommon/BasicPitchFixture.cs` (shared fixture generation/loading)
- Test: `tests/PoMode.Unit/PitchTracking/BasicPitchDecoderFixtureTests.cs`, `tests/PoMode.E2EUI/DecoderPortTests.cs`

**The point (§5):** the decoder exists twice and must agree. A fixture file of posterior tensors plus the expected note events is generated once from the C# decoder; the C# test asserts it still reproduces them, and a Playwright test loads the JS module in a real browser, runs it on the same fixture, and asserts the same note events within tolerance. If the two ever disagree, the test names the note.

- [ ] **Step 1: Write the failing tests. Step 2: RED** — tee `task-4-red.log`. **Step 3: Port. Step 4: GREEN** — Unit then E2EUI alone. Commit.

---

### Task 5: In-Browser Inference and End-to-End Delegation

**Files:**
- Create: `src/PoMode.Client/wwwroot/js/pitch-worker.js`, `src/PoMode.API/Features/PitchTracking/WebRuntimeEndpoints.cs`
- Modify: `src/PoMode.Client/Pages/Home.razor`, `src/PoMode.API/Infrastructure/ModelCatalog.cs`
- Test: `tests/PoMode.E2EUI/ClientDelegatedFlowTests.cs`

**Behaviour:** the client probes for a WebGPU adapter, declares its capability on upload, and on `AwaitingClient` fetches `vocals.wav` plus the model, runs Basic Pitch through `onnxruntime-web` (WebGPU when a real adapter exists, else WASM SIMD), decodes with the Task 4 module, and POSTs the notes. Runtime assets and the model are served from our own origin, hash-verified by `ModelRegistry`, never committed.

- [ ] **Step 1–4:** as above. The browser test must run and assert the **WASM** path, because WebGPU cannot be exercised here.
- [ ] **Step 5: Record the truth in the spec** as `### 13.9 Phase 8 rulings`: the measured capability table; WASM-SIMD verified and WebGPU opportunistic-and-unverified-here; the per-job availability change to `ExecutionPlanner`; the concrete validation limits and why the whole payload is rejected rather than filtered; and an honest statement of whether real in-browser inference was achieved or not.

---

## Phase 8 Exit Criteria

- All four suites green in the prescribed order; zero build warnings; no secrets; nothing large committed.
- A job can be pitch-tracked **by the browser**, end to end, with the recorded plan showing `ClientDelegated` so the UI badges it 🌐.
- A browser that never answers cannot wedge a job: the stage times out and falls through.
- Client-posted notes are validated hard, and every rule has a test that proves the rejection.
- The JS and C# decoders agree on a shared fixture, and the test says so.
- The honest state of WebGPU on this machine is recorded rather than implied.
- Remaining after Phase 8: **all three tiers exist.** What is left is the deferred artifact read/write race (§13.4), the `Jobs:RootPath` deploy blocker, cloud pitch tracking (no mappable model found), and chord-quality improvements (§13.6).
