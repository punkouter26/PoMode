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

These override any default instinct. Where one contradicts a habit, the rule wins.

### Orientation

- **Read `docs/` first.** The root `docs/` folder carries the project's own summaries and background
  (currently `ai-cost-audit.md`). Skim it before planning work, so a decision already made and written
  down is not re-litigated from scratch.

### Verifying a change

- **Build, restart, then check the restart actually worked.** After a code change: `dotnet build`,
  then restart `PoMode.API` and confirm it came up — `/health/ready` answering is the check, not the
  absence of a crash in the first second. Kill any running instance before building; it locks the DLLs.
- **Run only the tests that cover what you changed**, with `--filter`. Never the whole suite, and
  never all four suites — a full run costs minutes on every edit and tells you almost nothing about a
  two-line change. `dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~ModeExerciseGrader"` is
  the shape. If nothing existing covers the change, say so rather than running everything to feel safe.
- **Say what you did not verify.** A targeted test run plus a successful restart is not a full pass;
  report it as what it is.

### Git

- **Work on `master`.** Do not create, switch to, or work on another branch unless explicitly asked.
- **Never push without being asked.** Committing locally is fine; `git push` happens only when the
  user says so, or types **`git sync`**.
- **`git sync` means: commit everything, then push.** Stage all outstanding changes first — a sync
  that leaves work uncommitted is not a sync — then commit and push in one go.
- **Commit messages are short and casual**, written the way a person actually types them. American
  English, plain and offhand ("fix the busted tempo math", "wire up practice page"), not a formal
  changelog entry. One line is usually right.

### Secrets

- **No `dotnet user-secrets`, ever.** Configuration goes in `appsettings*.json`; anything genuinely
  secret goes in Azure Key Vault and is read through `SecretsBootstrap` / `DefaultAzureCredential`.
  This matches NET_RULES below, which also forbids connection strings in `appsettings`.

### Working with the user

- **Run the command yourself.** If a step can be done from the tools available, do it rather than
  printing an instruction for the user to copy into a terminal. Hand them a command only when it
  genuinely needs their machine, their credentials, or their decision.
- **Answers over ~100 words end with a TLDR.** One line, about 20 words, saying what happened.

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

### Hum takes (sing over the chords)

The Mode Lab's "New Chords" dice rolls a progression from the same catalog the dropdown uses (the
key is deliberately left alone — a singer picks a key for their own range), and Hum Along records the
microphone over that progression looping until Stop. `hum-recorder.js` is a third capture path beside
`audio-recorder.js` and `live-session.js`, and the only one that *wants* the browser's echo
cancellation, because it is the only one recording while the page plays audio out of the speakers.
It shares `modal-player.js`'s AudioContext so the two clocks are comparable, and trims the take so
sample zero is bar one — the count-in and the mic-open gap come off the front.

`POST /api/modal-melodies/hum` takes the recording as multipart and the backing as the same query
parameters `/wav` and `/midi` use. `HumTakeSeeder` then hands the job what it already knows, through
`AnalysisIntake.StartAsync`'s `seed` hook (which runs after planning and *before* enqueue, so the
worker cannot start detecting what it is about to be given): the progression regenerated
deterministically from that request and tiled across every loop pass as `chords.json`, and the
slider's BPM as `beats.json` at confidence 1.0. Separation and chord detection are then marked
complete without running — a solo hum has no stems to split and no harmony to find, and asking a
recognizer to look would invent a chord track nobody sang. Both stages' plan entries are rewritten to
`SkippedDryVocal` / `ModeLabBacking` so the UI never credits an executor with work it did not do.
The pipeline needs no special case for any of this: a pre-completed stage with its artifact on disk
is exactly the shape it already restarts from. The take is then an ordinary job — library row,
restart-safe, blob-mirrored — whose melody is the user's and whose harmony is real.

Before saving, the take can be played back against its own chords: `reviewTake` decodes the recording
and schedules it and the loop from one instant on the shared context (hence `play`'s optional
`startAtTime`). Hearing the voice alone says nothing about whether the phrase landed; the question is
always how it sits against the harmony.

The seeder also stamps `JobState.Origin` — a `TakeOrigin` carried on `JobStatusDto` and
`LibraryEntryDto` — whose sentence is worded server-side like every other musical statement. It
describes the backing that was *played to* the singer and never asserts a mode for their voice: that
is the answer the analyzer exists to give, and stating it alongside would pre-empt it. An ordinary
upload has no origin; a dropped file is its own explanation.

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

`POST /api/analysis/{id}/interpretation/ask` turns that one-shot write-up into a conversation, over
the same seam and the same measurements. `ISongInterpreter.AnswerAsync` sits beside `InterpretAsync`
rather than on a seam of its own - it is the same capability asked a narrower question, and splitting
it would mean a second availability probe and a second ranking for the same Ollama socket.
`QuestionPrompt` restates the full statistics block on *every* turn ahead of the transcript, because a
model asked to recall a figure from six messages back approximates it, and an approximated statistic
presented as measured is the failure the grounding exists to prevent. What the question prompt adds is
permission to decline: a summary can always be written from the data but a question need not be
answerable from it, so the model is given the `NOT IN THE DATA` marker and told to use it rather than
guess. `QuestionPrompt.Split` strips it tolerantly - same reasoning as the `===FOR MUSICIANS===`
delimiter - and the client labels the answer rather than hiding the refusal.
`TemplateSongInterpreter` answers by routing the question to the measurement it is about (mode, tempo,
range, rhythm, harmony, motion, phrasing, tension) and declines anything else, which is honest and
also tells the reader a local model would get them further. The conversation is held client-side; the
server stores no transcript, same ruling as the statistics themselves.

### Practice (scored ear training)

`/practice` is the other half of the app's purpose: the analyzer tells you a song is Dorian, this asks
whether you can *sing* Dorian. `GET /api/practice/exercise` issues a phrase and
`POST /api/practice/attempt` scores a take. Nothing is persisted - an attempt is a conversation, not a
job, the same ruling `/api/live/analyze` follows.

`ModeExerciseBuilder` is deterministic in the way `ModalMelodyGenerator` is, and for the same reason a
hum take can reference its backing instead of copying it: an attempt carries the six values (kind,
mode, tonic, bpm, seed, octave) that regenerate the identical phrase server-side, so grading always
runs against the notes the server issued rather than whatever the browser claims it was shown. The
pitch pool is the mode's **own** scale on its own tonic, not the parent key's - same rule as the Mode
Lab melody pool, and the pentatonics are where getting it wrong would show. `CharacteristicLeap` is
the exercise the feature exists for: Dorian and Aeolian share six notes out of seven, so singing the
scale proves almost nothing, and singing the natural 6th against the tonic is the entire difference.

`ModeExerciseGrader` reports three components rather than one number, because they fail independently
and the page's advice is chosen from whichever failed. Purity is measured over *every* note heard, not
only the matched ones - hitting each target while filling the gaps with parent-key notes is exactly
the habit the feature exists to break. Matching is greedy in target order rather than globally
optimal, so a phrase sung a bar late scores as a different performance instead of being quietly
realigned. Nothing is reported in cents: the browser's note collector rounds to the nearest semitone
before anything is posted, so a cents figure would be arithmetic performed on a rounding.

`practice-session.js` is a fourth capture path beside `audio-recorder.js`, `live-session.js` and
`hum-recorder.js`, and timing is why. The others zero their take on "when the singer started" or on
bar one of a loop; this one needs notes timed from a downbeat the *server* chose, which is what
`live-pitch.js`'s `snapshotFrom` provides - as against `snapshot`, which rebases onto the first note
and would score a singer who came in two beats late as perfectly in time. The count-in and metronome
come from `click-track.js`, shared with the hum recorder; its clicks sit at 1050/1600 Hz, above
`live-pitch.js`'s 1000 Hz ceiling, which is what lets the click keep sounding while the mic is open.
The streak lives in `localStorage` - a per-viewer convenience like the theme override, and this app
has no user store to hang one off.

### Second opinion (MusicBrainz / AcousticBrainz)

`GET /api/analysis/{id}/reference` puts a public catalogue's reading of the same recording next to
ours. Two free, unauthenticated sources: MusicBrainz for identity, AcousticBrainz for the community's
key and tempo. Both are optional exactly as the Ollama tier is - absent, unreachable or silent is an
ordinary answer - and `ReferenceLookupDto` deliberately distinguishes "no such recording" from "the
catalogue did not answer", because a dropped connection must not read as an obscure recording.
AcousticBrainz has served a frozen dataset since 2022, so a miss there is the common case.

Only the cleaned-up file name leaves the machine; there is no fingerprinting and no audio upload.
`ReferenceQuery` does that cleaning and also knows when *not* to ask: audio this app generated
(`live-take-`, `Hum_`, `ModeLab_`) names a setting, not a release, and searching for it would return a
confident match for a recording the user never uploaded. `MusicBrainzCatalog` paces itself to one
request a second behind a semaphore and sends an identifying User-Agent, both conditions of use rather
than suggestions; answers are cached six hours to stay inside the limit.

`ReferenceComparison` writes the comparison sentence server-side, because comparing two readings of a
key is a musical judgment. The case it exists for is the one that looks like a disagreement and is
not: a catalogue classifier only ever answers major or minor, so a melody we read as D Dorian comes
back as F major - the same seven notes with a different note treated as home, which is the exact
distinction this whole app draws. Reporting that as a plain mismatch would throw away the most
interesting thing the comparison produces. A doubled tempo is likewise explained as one pulse counted
two ways rather than shown as "119 vs 238".

### Operational guards

`PoRateLimits` limits the three things that cost real resources - queueing an analysis, running a
language model, calling a catalogue - and deliberately nothing else: reading a finished job's
artifacts is what the canvas does dozens of times while a user pans a timeline, and a blanket limit
would throttle the interactive part of the app to protect the expensive part. Partitioned by
signed-in user, falling back to remote address, and `UseRateLimiter` runs *after* `UseAuthentication`
so that partition is available. The policies are always registered even when limiting is off (a
disabled policy becomes a no-op limiter), because a missing named policy is a startup exception and
making that reachable from a config flag is how a test setting takes production down. Both test
fixtures set `RateLimits:Enabled=false` and `Reference:Enabled=false`.

`QueueCapacityFilter` is a different guard, not a redundant one: the rate limit bounds how fast one
client may ask, this bounds how much the server has agreed to do. Without it `JobQueue`'s bounded
channel makes an over-quota upload *hang* - holding a request and a large multipart body until a
worker frees a slot - where a 503 with a Retry-After is the honest answer.

`PoTelemetry` exists to answer the question the tier system poses: which executor is actually doing
the work, and how long is it taking. `pomode.stage.duration` is tagged with the executor that really
ran (recorded in `CompleteStageAsync`, after a fallback has rewritten the plan entry - a duration
attributed to the planned executor would be worse than none), and `pomode.stage.fallbacks` is the
single most useful number here, because a local model failing on every job is invisible from outside:
the DSP fallback answers and the page renders. Exporting is opt-in on `OTEL_EXPORTER_OTLP_ENDPOINT`;
with nothing configured the instruments still exist and nothing leaves the machine. `/diag` reports
which guards are on, as booleans only - an OTLP endpoint can carry credentials in a header.

### Effects layer (graphics and sound)

Every decorative module answers to `fx-prefs.js`, which resolves two questions the OS conflates into
one: how much motion may run (`full` / `subtle` / `off`) and whether the app may make noise. The OS
`prefers-reduced-motion` setting still picks the *default* level, but an explicit choice in the
header's Effects control wins over it in both directions — the same rule `theme.js` applies to
`prefers-color-scheme`. The effective level is stamped on `<html>` as `data-fx` so `app.css` responds
without JS, and `subscribe()` lets a running shader downgrade in place rather than waiting for a
reload. `allowsHeavy()` is the tier the middle setting exists to decline: compute shaders, large
particle counts, the full-width analysis panel.

Nothing in the app is gated behind an effect. Every module's `init` returns false — no WebGL2, no
WebGPU, no context, effects off — and the page it belongs to renders exactly as it did before, because
in each case the information lives in the markup beside the canvas and the effect only illustrates it.

Rendering tier is chosen by what the effect actually needs, not for consistency: `fx-particles.js`
(WebGPU compute, tens of thousands of particles) → `fx-kiln.js` / `fx-practice-ribbon.js` /
`fx-mode-strip.js` / `fx-tuner.js` / `fx-background.js` / `fx-spectrum.js` (WebGL2 fragment shaders,
per-pixel fields and bloom) → `fx-hum-review.js` / `fx-streak.js` / `fx-cover.js` (2D canvas — a few
rectangles, a polyline, a hundred particles in a 30px box). A fourth GL context for a stroke would
cost a context and a fallback path to buy nothing. `fx-mode-strip.js` is the reason that matters: nine
cards get one context and a uniform array of measured rectangles rather than nine contexts.

The music-theory rule holds across all of it. `sfx.js` is *voiced in the detected mode* —
`ModalResultExtensions.EarconPitches` derives the scale from the shared `ScaleModes` table and hands
JS a list of MIDI numbers to sound; the module never derives a scale and its one chord is
tonic-fifth-octave so it states no third. `fx-mode-strip.js` reads each card's `--mode-color` off the
element instead of holding a colour table. `fx-practice-ribbon.js` and `practice-session.js` are given
the mode's pitch-class set with the exercise and only test membership — `ModeExerciseGrader` remains
the only thing that scores a take, and the ribbon draws what was heard rather than a verdict.
`fx-hum-review.js` draws the pitches `ModalMelodyGenerator` voiced and never infers a chord from them.
`fx-cover.js` is handed a hue, a degree count and a tempo, and knows nothing about what they mean.

Per-frame work never crosses into C#: `practice-session.js` and `live-session.js` push microphone
frames straight into `fx-practice-ribbon.js` and `fx-tuner.js`, and `hum-recorder.js` hands
`fx-hum-review.js` the buffer it already decoded for playback. Blazor only calls init/dispose and the
occasional discrete update, matching the `mixer.js` contract.

Cents are reported in exactly one place. The Practice page reports none, because its notes come from
the collector, which rounds to a semitone first. `fx-tuner.js` reads `detectPitch`'s fractional MIDI
before any rounding, which is why the tuner halo may show a deviation the grader never could — and it
is never sent anywhere or scored.

### Client conventions

- Heavy UI lives in plain JS modules, not Blazor: `canvas.js` (dual-lane visualization, pan/zoom, virtualized drawing) and `mixer.js` (Web Audio stem playback, synth note overlays, metronome clicks, Space/comma transport keys). `mixer.js` owns the transport clock and drives the canvas playhead directly — no per-frame Blazor renders. Blazor components only issue commands and receive discrete events.
- JS state is mirrored onto `data-*` attributes (`data-mixer-status`, `data-playhead`, …) precisely so Playwright tests can assert without reaching into module internals. Keep that contract when changing these modules.
- The app is an installable PWA with an Android share target, so a voice memo can be shared straight
  into PoMode. `service-worker.js` is **network-first, never cache-first** - `Program.cs` serves this
  app's unfingerprinted JS and CSS with `no-cache` precisely to stop a stale module being paired with
  the C# beside it, and a cache-first worker (including the Blazor PWA template's) would reintroduce
  that bug and make it survive a hard refresh. The share target POSTs to `/share-target`; the worker
  stashes the file in a cache and redirects to `/?shared=1`, where `Home.razor` claims it via
  `pwa.js`. The bytes come back from a separate `takeSharedBytes` call because Blazor only marshals a
  `Uint8Array` as a real byte array when it is the whole return value - nested in an object a 40 MB
  memo degrades to a JSON array of numbers. `.webmanifest` is mapped explicitly in `Program.cs`: a
  manifest served as `application/octet-stream` is ignored silently and the app simply stops being
  installable.
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
