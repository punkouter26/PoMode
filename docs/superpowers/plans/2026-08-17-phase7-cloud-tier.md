# PoMode Phase 7: Cloud Tier (Tier 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill the `ExecutionTier.Cloud` slot that has been empty since Phase 2, so a stage whose local executor cannot run falls through to a paid provider instead of failing — visibly, and only as a last resort.

**Architecture:** No new plumbing. `ExecutionPlanner` already orders candidates `Local → ClientDelegated → Cloud` by `TierRank`, and `AnalysisPipeline.RunWithFallbackAsync` already retries the next tier when an executor throws and rewrites the recorded plan so the UI badge updates. A cloud executor is therefore just an `IStemSeparator` with `Tier = Cloud` whose `IsAvailableAsync` reports whether its API key resolved. Phase 7 adds executors, a credential resolver, a retry helper, and a kill switch — and nothing else.

**Tech Stack:** .NET 10, `IHttpClientFactory`, the already-registered `TimeProvider`, xUnit, `HttpListener` fixture servers. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-08-16-pomode-design.md` §4 (tier routing table, mid-run fallback), §5 (Cloud clients), §9 (secrets), §13.

---

## Scope rulings decided up front, from verified provider contracts

Before planning, the three providers named in §5 were checked against their real documentation rather than written from memory. That research changed the scope, so it is recorded here as the binding version.

1. **Sonic API is dead — dropped entirely.** §4 makes `Sonic API` the *only* Tier-3 option for chords, and the first for pitch tracking. `sonicapi.com` is now a parked domain listed for sale; its documentation pages no longer resolve. There is nothing to integrate. §4's and §5's Sonic rows are void.

2. **There will be no cloud chord recognizer, ever — it would be unreachable code.** `ChromaChordRecognizer` (Phase 5) is pure DSP with `IsAvailableAsync => true` unconditionally: no model, no download, no network, works in Azure. The planner picks the lowest available tier, so a `Cloud` chord executor could never be selected under any configuration. Building one would be dead code by construction, which CLAUDE.md forbids. This closes §4's chord Tier-3 row permanently rather than deferring it.

3. **Cloud pitch tracking is deferred, not attempted.** §4's order was `Sonic → Replicate`. Sonic is gone, and no Replicate model for note detection was found whose *output* contract could be mapped to `NoteEvent[]` without guessing. Implementing an unverifiable response mapping would produce code that looks correct and fails on first real use. Deferred with the reason recorded; the seam (`IPitchTracker` with `Tier = Cloud`) stays open and costs nothing.

4. **Phase 7 therefore delivers Tier 3 for stem separation only, with two providers** — the one stage where both a real need and two verified contracts exist. Stem separation is also the only stage whose local executor has a heavy prerequisite (a ~80 MB HTDemucs download), so it is the stage most likely to actually need a fallback.

**Verified contracts (use these exactly; do not re-derive from memory):**

- **Replicate.** `POST https://api.replicate.com/v1/predictions` with `Authorization: Bearer <token>`, body `{"version": "<hash>", "input": {…}}`; or `POST /v1/models/{owner}/{name}/predictions` with body `{"input": {…}}` when no version hash is pinned. Response: `id`, `status`, `output`, `error`, `urls.get`. Poll `urls.get` until `status` is terminal. **Status values are `starting`, `processing`, `succeeded`, `failed`, `canceled`** — note `succeeded`, *not* `successful`; one documentation summary got this wrong and it would have silently made every prediction look unfinished. Predictions time out server-side after 30 minutes.
- **LALAL.AI.** Base `https://www.lalal.ai/api/v1/`. Auth header is `X-License-Key` (not a bearer token). `POST upload/` with the raw file bytes as the body plus a `Content-Disposition` filename header → `{"id": "<source_id>"}`. `POST split/stem_separator/` with `{"source_id", "presets"}` → `{"task_id"}`. `POST check/` with `{"task_ids": [...]}` → `result[task_id]` carrying `status` (`success` | `progress` | `cancelled`), `progress`, and on success `result.tracks[]` with `url` and `label`. `POST delete/` with `{"source_id"}` releases server-side storage.

**Plan-level rulings:**

- **Money is never spent by a test.** No test may contact a real provider. Every cloud test drives an `HttpListener` fixture reproducing the documented protocol, exactly as `OllamaCopilotClientTests` does. There are no provider keys in this environment, so cloud executors also report unavailable by default, and the local tier keeps winning.
- **A kill switch, because the fallback is automatic.** `Cloud:Enabled` (default `true`) short-circuits `IsAvailableAsync` for every cloud executor. §4 wants automatic fall-through to a paid tier; a user must be able to forbid that outright without deleting their keys.
- **Hand-rolled retry instead of §5's "standard resilience handler".** `Microsoft.Extensions.Http.Resilience` would add a dependency whose delays are real and jittered, making backoff untestable without sleeping. A ~30-line retry driven by the already-registered `TimeProvider` is deterministic under `FakeTimeProvider` (already a test package) and adds nothing to the dependency graph. Deviation recorded in §13.8.
- **Retry only what is retryable.** 5xx, 408 and 429 are retried with exponential backoff; 401/403 (bad key) and 4xx (bad request) fail immediately, because retrying a rejected credential just burns time and rate limit.
- **Cloud calls must stay visible.** The recorded `StagePlan` already carries the tier, and the UI already renders ☁️💰 via `TierBadge`. No new UI is needed — but a test must prove the badge flips when a stage falls back to cloud.

## Global Constraints

- All prior constraints hold: `net10.0`, Nullable + TreatWarningsAsErrors from `Directory.Build.props`; CPM (versions ONLY in `Directory.Packages.props`); `PoMode.` prefixes; endpoints via `MapGroup()` + `TypedResults`; zero inline CSS.
- **Secrets:** keys resolve through the existing `SecretsBootstrap` (Key Vault → env fallback). No key may be logged, echoed by `/diag`, or written into `job.json`. A test must assert redaction.
- **TDD with log-file evidence is mandatory.** Tee every RED and GREEN run to `<workspace>/task-N-{red,green}.log` and quote them.
- **Test-run procedure (do not use a bare `dotnet test`):** run `tests/PoMode.Unit`, `tests/PoMode.Integration`, `tests/PoMode.E2EAPI` as separate invocations, then `tests/PoMode.E2EUI` **alone and last**. A full-solution run leaves a `PoMode.API` process holding `PoMode.Shared.dll` → next build dies `MSB3027`. `dotnet test` also rejects two project paths in one invocation (`MSB1008`). Fixture ports already taken: 5199, 5200, 5251, 5310, 5311, 5320, 5398, 5399.
- Commit hygiene: stage only each task's listed paths. Never stage `.claude/`, `.superpowers/`, `models/`, `bin/`, `obj/`, `*.mp3`, `*.wav`, `*.onnx`.
- Commits: conventional style ending with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: Credentials, Kill Switch and Retry

**Files:**
- Create: `src/PoMode.API/Features/Cloud/CloudCredentials.cs`, `src/PoMode.API/Features/Cloud/ResilientHttp.cs`
- Modify: `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.Unit/Cloud/CloudCredentialsTests.cs`, `tests/PoMode.Integration/ResilientHttpTests.cs`

**Interfaces:**
- Consumes: `IConfiguration` (already carrying Key Vault + env values), `TimeProvider` (already a registered singleton), `ProviderKeys`.
- Produces:
  - `CloudCredentials` — `bool Enabled { get; }` from `Cloud:Enabled` (default `true`); `string? TokenFor(string providerKey)` returning null/whitespace-normalised; `bool Has(string providerKey)` = `Enabled && token present`.
  - `ResilientHttp.SendAsync(HttpClient, Func<HttpRequestMessage>, TimeProvider, ILogger, CancellationToken)` — a request *factory* (a sent `HttpRequestMessage` cannot be reused), 3 attempts, exponential backoff 1 s / 2 s, retrying 5xx/408/429 only. Returns the final response; throws only on transport failure after the last attempt.

- [ ] **Step 1: Write the failing tests.** Unit: `Has` is false when the key is absent, whitespace, or `Cloud:Enabled=false`; `Enabled` defaults true when unset; keys are read via `ProviderKeys` names so there is one source of truth. Integration (fixture server + `FakeTimeProvider`): a 500 then 200 succeeds on retry; three 500s give up and return the last response; a 401 is returned immediately with exactly one request recorded; a 429 is retried; backoff advances the fake clock by 1 s then 2 s, so the test asserts the delays without sleeping.
- [ ] **Step 2: RED** — tee `task-1-red.log`.
- [ ] **Step 3: Implement**, register `CloudCredentials` as a singleton.
- [ ] **Step 4: GREEN** — Unit then Integration (tee `task-1-green.log`). Commit:

```powershell
git add src/PoMode.API/Features/Cloud src/PoMode.API/Program.cs tests/PoMode.Unit/Cloud tests/PoMode.Integration/ResilientHttpTests.cs
git commit -m "feat: cloud credential resolution, kill switch and testable retry"
```

---

### Task 2: Replicate Stem Separator

**Files:**
- Create: `src/PoMode.API/Features/StemSeparation/ReplicateStemSeparator.cs`
- Modify: `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.Integration/ReplicateStemSeparatorTests.cs`

**Interfaces:**
- Consumes: Task 1, `StageContext`, `WavWriter`/`AudioDecoder` (Phase 4), `JobStore`.
- Produces: `ReplicateStemSeparator : IStemSeparator` — `Tier = Cloud`, `IsAvailableAsync` = `credentials.Has("ReplicateApiToken")`, `SeparateAsync` writes `vocals.wav` and `instrumental.wav` into `context.JobDir`.

**Protocol:** create the prediction (model reference from `Cloud:Replicate:StemModel`, default `cjwbw/demucs`; `owner/name` uses `/v1/models/{owner}/{name}/predictions`, `owner/name:hash` uses `/v1/predictions` with `version`) → poll `urls.get` (or the canonical prediction URL) every 3 s until `status` is terminal → on `succeeded`, read the stem URLs out of `output` and download them. Deliberately tolerant of `output` shape: it may be an object keyed by stem name or an array, so resolve `vocals`/`instrumental` (also accepting `no_vocals`/`accompaniment`, which demucs variants use) and fail with a clear message if neither is present. `failed`/`canceled` throw so the pipeline falls back.

**Input audio:** Replicate needs a fetchable URL or a data URI. There is no public URL for a local job file, so the input is sent as a `data:` URI built from the job's input bytes. A guard rejects inputs above `Cloud:MaxUploadMb` (default 25) with a clear message rather than building a hundred-megabyte JSON body.

- [ ] **Step 1: Write the failing tests** against a fixture Replicate. Cover: the happy path writes both stems with the downloaded bytes; `status: "successful"` is **not** treated as success (guards the documentation error); a `failed` status throws with the provider's `error` text; polling loops through `starting` → `processing` → `succeeded` without sleeping (fake time); `output` as an array and as an object both resolve; a missing vocals URL throws a named error; unavailable when the token is absent or `Cloud:Enabled=false`; an oversized input throws before any HTTP call is made; the token is sent as `Authorization: Bearer …` and never appears in the thrown message.
- [ ] **Step 2: RED** — tee `task-2-red.log`.
- [ ] **Step 3: Implement** and register **after** `OnnxStemSeparator` and `FakeStemSeparator` (registration order does not decide precedence — `TierRank` does — but keeping the file order Local-first matches how every other stage reads).
- [ ] **Step 4: GREEN** — Integration (tee `task-2-green.log`). Commit:

```powershell
git add src/PoMode.API tests/PoMode.Integration/ReplicateStemSeparatorTests.cs
git commit -m "feat: Replicate cloud stem separation with polled predictions"
```

---

### Task 3: LALAL.AI Stem Separator

**Files:**
- Create: `src/PoMode.API/Features/StemSeparation/LalalStemSeparator.cs`
- Modify: `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.Integration/LalalStemSeparatorTests.cs`

**Interfaces:**
- Produces: `LalalStemSeparator : IStemSeparator` — `Tier = Cloud`, `IsAvailableAsync` = `credentials.Has("LalalApiKey")`, same output contract as every other separator.

**Protocol:** `POST upload/` (raw bytes, `Content-Disposition: attachment; filename="…"`, `X-License-Key`) → `id` → `POST split/stem_separator/` with `{"source_id", "presets"}` → `task_id` → poll `POST check/` with `{"task_ids":[task_id]}` until `result[task_id].status` is `success` or `cancelled` → download `result.tracks[]` matching the vocals and instrumental `label`s → `POST delete/` with the `source_id` so the provider does not retain the user's audio.

- [ ] **Step 1: Write the failing tests** against a fixture LALAL.AI. Cover: the happy path writes both stems; auth travels in `X-License-Key` and never in `Authorization`; `status: "progress"` keeps polling and `cancelled` throws; the `source_id` is deleted after a success **and also after a failure**, so a failed job never leaves the user's audio on a third-party server; a missing track label throws a named error; unavailable without a key or with `Cloud:Enabled=false`.
- [ ] **Step 2: RED** — tee `task-3-red.log`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: GREEN** — Integration (tee `task-3-green.log`). Commit:

```powershell
git add src/PoMode.API tests/PoMode.Integration/LalalStemSeparatorTests.cs
git commit -m "feat: LALAL.AI cloud stem separation with server-side cleanup"
```

---

### Task 4: Fallback Visibility, Diagnostics and Spec

**Files:**
- Modify: `src/PoMode.API/Features/Hardware/HardwareProbe.cs` if it does not already report cloud availability, `docs/superpowers/specs/2026-08-16-pomode-design.md`
- Test: `tests/PoMode.Integration/CloudFallbackTests.cs`, `tests/PoMode.E2EAPI/DiagnosticsTests.cs` (extend)

**Interfaces:**
- Produces: proof that the whole point of the phase works — a stage whose local executor throws lands on cloud, and the user can see it.

- [ ] **Step 1: Write the failing tests.**
  - Integration: run `AnalysisPipeline` with a local separator that throws and a fixture-backed cloud separator registered; assert the job completes, the recorded `StagePlan` for `Separating` ends up `Tier = Cloud` with the cloud executor's name, and `vocals.wav` exists. Then the same with `Cloud:Enabled=false`: the job must **fail** rather than silently spend money.
  - E2EAPI: `/diag` reports which providers resolved and **never** echoes a key value — assert a planted fake token string is absent from the whole response body.
- [ ] **Step 2: RED** — tee `task-4-red.log`.
- [ ] **Step 3: Implement** whatever the tests expose.
- [ ] **Step 4: GREEN** — all four suites in the prescribed order (tee `task-4-green.log`).
- [ ] **Step 5: Record the truth in the spec** as `### 13.8 Phase 7 rulings`: Sonic API is defunct and dropped; the chord Tier-3 row is closed permanently because a pure-DSP local executor is unconditionally available; cloud pitch tracking is deferred with its reason; the verified Replicate and LALAL.AI contracts including the `succeeded` vs `successful` trap; the hand-rolled retry deviation from §5's standard resilience handler and why; the `Cloud:Enabled` kill switch; `Cloud:MaxUploadMb`; and the honest statement that **no real provider call has ever been made** — every path is verified against fixture servers only, because there are no keys in this environment.
- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.API tests docs/superpowers/specs/2026-08-16-pomode-design.md
git commit -m "feat: prove cloud fallback and record Phase 7 rulings"
```

---

## Phase 7 Exit Criteria

- All four suites green in the prescribed order; zero build warnings; no `Version=` in any csproj; no secrets in source, logs, `/diag` or `job.json`.
- A stage whose local executor fails falls through to a cloud provider and the recorded plan says `Cloud`, so the UI shows ☁️💰.
- `Cloud:Enabled=false` prevents every paid call, even when keys are present.
- No test ever contacts a real provider; **no real money has been spent, and that is stated plainly rather than implied.**
- The spec no longer describes Sonic API as an executor that will exist.
- Remaining after Phase 7: **Tier 2 (ClientDelegated WebGPU pitch tracking)** — the last unbuilt tier — plus cloud pitch tracking if a model with a mappable output contract is ever identified, the artifact read/write race (§13.4), and the `Jobs:RootPath` fix required before any deploy.
