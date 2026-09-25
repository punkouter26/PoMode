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

- `tests/PoMode.Integration` includes `ModelAccuracyReportTests`: it renders a known-truth sample MP3, plus a voice-like stem with vibrato and bleed and two drum grooves with a known bar grid, races every free pitch/chord/beat executor against them, and rewrites `test-reports/model-accuracy.html`. It is a reporting tool, not a test, so it is opt-in: `POMODE_MODEL_REPORT=1 dotnet test tests/PoMode.Integration`. Otherwise it skips.

- First E2EUI run: install browsers with `pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium`.
- API reference UI: `/scalar`. Health: `/health`, `/health/live`, `/health/ready`. Diagnostics: `/diag`.

## Working rules

These override any default instinct. Where one contradicts a habit, the rule wins.

### Orientation

- **Read `docs/` first.** The root `docs/` folder carries background that is not derivable from the
  code. Skim it before planning work, so a decision already made and written down is not
  re-litigated from scratch — and so a feature that was deliberately removed is not rebuilt.
- **Test suites are capped**: 100 Unit, 50 Integration, 25 E2EAPI, 25 E2EUI. A new test that would
  breach a cap has to earn its place against an existing one.

### Verifying a change

- **Build, restart, then check the restart actually worked.** After a code change: `dotnet build`,
  then restart `PoMode.API` and confirm it came up — `/health/ready` answering is the check, not the
  absence of a crash in the first second. Kill any running instance before building; it locks the DLLs.
- **Run only the tests that cover what you changed**, with `--filter`. Never the whole suite, and
  never all four suites — a full run costs minutes on every edit and tells you almost nothing about a
  two-line change. `dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~HumTakeSeeder"` is
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

Each uploaded song becomes a job that runs 4 stages in `AnalysisPipeline`: **Separating → PitchTracking → ChordDetecting → ModalAnalysis**. Every stage has multiple executors registered in `Program.cs` behind seams (`IStemSeparator`, `IPitchTracker`, `IChordRecognizer`), each tagged with an `ExecutionTier` (Local ONNX model, or ClientDelegated browser inference). `ExecutionPlanner.EffectiveRank` fixes the selection order: local model → browser → classic model-less DSP (`IsClassicFallback`: `YinPitchTracker`, `ViterbiChordRecognizer`) → Fake placeholder; within a rank, DI registration order breaks ties, so register new executors *after* the one that should stay the default. `AnalysisPipeline.RunWithFallbackAsync` falls through the same order when an executor fails and records who actually ran in `StageHistory`. If any Fake executor ran, the client shows the "USING MOCK DATA" banner.

**Pitch default is RMVPE, on the numbers.** `RmvpePitchTracker` (RMVPE, MIT, 361 MB, vocal-only, reads `vocals.wav`) is registered *before* `OnnxPitchTracker` (Basic Pitch) so it wins their shared local-model rank. Accuracy report, note F1 (exact pitch, onset ±0.25 s): sine melody RMVPE 0.88 / Basic Pitch 1.00; voice-like line with vibrato and 18 dB pad bleed RMVPE 1.00 / Basic Pitch 0.31 (44 notes for 8 — it transcribes the bleed); mean 0.94 vs 0.65. On a real full mix RMVPE gives 1.1 notes/s in D2–B4, Basic Pitch 4.6 notes/s down to E1. RMVPE is deliberately not an `IFileTranscriber`, so the backing stem is still transcribed by Basic Pitch. YIN scores 1.00 on both synthetic stems (clean monophonic tones are its best case) but stays a classic fallback; the report's default-is-the-winner check excludes classic fallbacks when the default is a model, and says so.

**Beats: `IBeatTracker`** (not a planned stage; runs best-effort inside ChordDetecting, walked by `EffectiveRank` with fall-through, writer recorded in `BeatGridDto.Tracker`). `BeatThisBeatTracker` (Beat This!, CPJKU, MIT, 83 MB + mel filterbank) → `DspBeatTracker` (`TempoEstimator`, classic, hears no downbeats). Beat This writes `BeatGridDto.Downbeats` and a tempo map built bar by bar from them, and `ModalAnalysisEngine` numbers measures from those downbeats when present (4/4 from t=0 otherwise). Report, beat F / downbeat F at ±70 ms: 104 BPM groove 0.96/0.94 vs DSP 0.96/0.00; 143 BPM groove 0.99/0.96 vs DSP 0.65/0.67 (DSP halved the tempo). The chord recognizers still segment on their own `TempoEstimator` grid.

Model files come from `ModelCatalog` (URL pinned to a commit, SHA-256, licence noted) and download at runtime into `Models:RootPath`; nothing is committed. The neural front ends use `AudioDecoder.ResampleBandLimited`, not the linear `Resample`, because their top mel bands sit at the new Nyquist.

Users can pin an executor per stage: `GET /api/analysis/executors` feeds one dropdown per stage on the home page (Fake placeholders are filtered out — never user-selectable), the pick rides on upload query params (`stemSeparator`/`pitchTracker`/`chordRecognizer`), and the planner honours it only if it is available. A stage with one real option renders as plain text rather than a disabled control, which would read as broken rather than as "no choice needed".

Jobs are restart-safe: `JobStore` persists `job.json` plus artifacts (`notes.json`, `notes-backing.json`, `chords.json`, `beats.json`, `result.json`, stem WAVs) in a per-job folder under a per-job semaphore, mirroring everything to Azure Blob (Azurite locally). `JobRecoveryService` re-enqueues incomplete jobs on boot; `JobCleanupService` purges old ones. Stage progress is pushed over SignalR only — never polled, never written per-tick.

**Tier 2 (client-delegated)**: the browser probes onnxruntime-web support (`pitch-worker.js`), uploads declare `clientCanInfer=true`, and when a job reaches `AwaitingClient` the browser runs the model and POSTs validated notes back to `/api/analysis/{jobId}/client-result`.

### Resumable uploads (tus)

Every file the home page analyses — picked, recorded, or shared in from another app — arrives over
tus (`Features/Uploads`, `tusdotnet` server, `tus-js-client` 4.3.1 vendored as
`js/upload/tus.min.js`). A voice memo is up to 100 MB over mobile data, and a single POST that drops
at 60 MB restarted from zero; now `js/upload/resumable-upload.js` asks the server how far it got and
sends the rest, in 8 MB chunks, retrying through offline spells. A picked `File` also resumes across a
reload (tus fingerprint in localStorage); a recording or shared Blob resumes within the session only.

- `/api/uploads` is the tus endpoint: auth required, 100 MB cap checked at creation (deferred length
  refused), partial files in `{jobs root}-uploads` — a *sibling* of the jobs root because both purge
  sweeps treat every folder under it as a job. Sliding 24 h expiry, swept hourly by
  `ResumableUploadCleanupService`. Local to the instance, never mirrored; scale-out would need
  affinity. Upload ids carry a hash of the owner (`OwnerScopedFileIdProvider`), so someone else's id
  answers 404 on every HEAD/PATCH/DELETE. Not rate-limited: every chunk is a request.
- `POST /api/analysis/uploads/{id}` (with the old `clientCanInfer` / executor query params) sniffs the
  header with `AudioFormatValidator`, hands the file to `AnalysisIntake.StartAsync` and returns the same
  `JobStatusDto` the multipart upload did; 409 while bytes are missing. It is idempotent per upload (a
  hand-off marker maps upload → job), so a finalize whose response was lost can be retried. It carries
  the upload rate limit and `QueueCapacityFilter`; a refusal leaves the upload in place to retry.
- The multipart `POST /api/analysis` is **gone**, not kept beside this. `POST /api/modal-melodies/hum`
  stays multipart, and that is not a second way to do the same thing: it is a different request (the
  backing parameters and the `HumTakeSeeder` hook ride with it), and its body is a take recorded
  seconds ago that the page still holds in memory, so there is nothing to resume.
- API tests upload through `UploadClientExtensions.UploadAudioAsync` (tus creation-with-upload, then
  finalize) in `AuthedFactory.cs`.

### Push notifications

An analysis takes minutes and SignalR only reaches an open tab, so a finished job also sends a Web
Push (`Features/Push`, `Lib.Net.Http.WebPush`) to its owner's opted-in browsers. The pipeline calls
`IJobOutcomeNotifier` once per run on Complete or Failed (not Cancelled), after the terminal state is
saved, best-effort. `WebPushOutcomeNotifier` words the title and body server-side ("'song.mp3' is
ready" / "D Dorian, 96 BPM. Tap to open the analysis.", omitting any figure the job lacks; a failure
never puts exception text on a lock screen), links to `/?job={id}`, uses the job id as the push Topic,
and deletes a subscription the push service answers 404/410 for.

- Keys: `PoMode:Push:VapidPublicKey` (appsettings or Key Vault) plus `PoMode--Push--VapidPrivateKey`
  in Key Vault. Push is on only when both are present and well-formed (`PushSettings`). Development
  without them generates a throwaway pair and logs that subscriptions will not survive a restart;
  other environments without them simply have push off, and `/api/push` says so.
- `GET /api/push`, `POST` / `DELETE /api/push/subscriptions` — all signed-in only. Endpoints are
  allow-listed to the real push services (FCM, Mozilla, WNS, Apple): the server POSTs to whatever a
  subscription names, so an open list is an SSRF hole.
- `PushSubscriptionStore`: one JSON file per owner (hashed name) in `{jobs root}-push`, mirrored to the
  job blob container under `push-subscriptions/`, restored from there when the local copy is gone.
- Client: the header overflow menu's "Notify me when analysis finishes" (`js/shell/push.js`,
  `data-push-state` on the item and on `<body>`). Hidden when the server has no keys or the browser
  has no PushManager. Notification permission is requested only by a click on that item, never on
  load. A subscribed browser re-sends its subscription on every visit, which re-registers it under
  whoever is now signed in and resubscribes it if the server's key changed; signing out first drops
  it from the account being left.

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

### Hum takes (the shared recording path)

`/practice` and nothing else drives this now. The Mode Lab used to carry a second copy of the same
flow — its own record button, its own four-state machine, its own element ids — and two pages
answering "sing over these chords" differently is the expensive kind of choice. The studio rolls and
plays progressions; the one exercise lives on its own page.

`hum-recorder.js` is the second capture path beside `audio-recorder.js`, and the only one that
*wants* the browser's echo cancellation, because it is the only one recording while the page plays
audio out of the speakers. It shares `modal-player.js`'s AudioContext so the two clocks are
comparable, and trims the take so sample zero is bar one — the count-in and the mic-open gap come off
the front.

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

### The singer's voice

`GET /api/analysis/{id}/voice` answers what a singer asks about their own voice: which voice type's
range it sits in, and the note it does best. `/api/modal-melodies/voice` carries the same read for the
caller, pooled over the same hum takes as their range and gated by the same minimum.

The type is judged by range only — the share of notes inside each classical range (E2–E4 bass up to
C4–C6 soprano), then the type whose usual centre is nearest the median. Those centres sit about two
semitones below the middle of each range, because singers live in the lower middle of their voice;
matching the arithmetic middle read every baritone as a bass. Within 1.5 semitones of two centres the
answer is "between baritone and tenor", because a pitch track cannot honestly split them finer. Every
read carries a sentence saying a teacher would also listen to tone and register changes, which pitch
cannot hear, and labels name a range, never the person.

"Best note" is not the most frequent one. notes.json keeps whole semitones, so `IntonationMeter` goes
back to the audio and reads YIN's continuous pitch inside each note (`PitchesInside`, middle 60% only —
the scoop into a note and the fall off it are expression, not tuning). A pitch's score is time held,
discounted by distance from true pitch and by wobble while held; the sentence only claims the
superlatives it actually won. That measurement costs a second or two per song, so unlike `/stats` it is
cached as `intonation.json`, keyed on note count and tuning reference. A song is graded against its own
measured tuning (the band sets the pitch); a hum take against A=440, the backing it was sung over —
correcting a take for its own offset would forgive exactly the flatness a singer wants to hear about.
The demo declines outright: its melody is a synthesized flute, not a voice.

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

### Why this mode, and where the sections are

Both ride on `/visual`, derived on every request and never stored, same ruling as the rest of that
payload: one fetch, and the canvas never has to join two responses on a time axis.

`ModeEvidenceBuilder` answers "why Dorian?" by comparing the primary mode with its one-note
neighbours. The neighbours are *computed* - two seven-note modes whose interval sets differ by exactly
one note - which produces the brightness chain without a second hand-written table; the headline is
whichever contrast `ScaleModes.CharacteristicIntervals` names first, so the badge, the note colour and
the sentence all point at the same degree. For each contrast it measures sung time and chord count on
the mode's tone and on the rival's, calls the rival ruled out when its tone carries at most a fifth of
the weight, and names the strongest argument *for* a neighbour as counter-evidence. Degrees are spelled
against the major scale, so Locrian's tritone reads ♭5 and Lydian's reads ♯4. Same wording rule as
`SongFingerprint`: a weak figure is dropped, not hedged; a tone never sounded is stated as never
sounded, because that is a fact about the song. Pentatonics have no one-note neighbour, so their
readout is the notes they leave out, and only when the line really keeps to five notes. The notes
flagged `VisualNote.Evidence` are exactly the tones the sentences cite - ringing a note the text never
mentions sends the reader looking for an explanation that is not there - and `VisualWindow.Evidence`
gives the HUD one line when a single window settles the headline question on its own. The client's
"Why this mode?" disclosure sits beside the key badge; opening it rings those notes on the canvas
(`data-evidence-highlight`) and fades the rest.

`SongSectionBuilder` (its own slice, `Features/SongStructure`) finds the verse/chorus shape from the
chord track alone: a root-weighted chroma per bar, a cosine self-similarity matrix, a Foote checkerboard
slid down its diagonal, peaks at least four bars apart, then segments lettered by matching their overall
chord content (adjacent same letters merge - a boundary between A and A is a peak the harmony did not
bear out). Bars come from the tempo map, else the beat grid, else fixed four-beat bars at the analysed
tempo. Each section's mode is the overlap- and confidence-weighted vote of the modal windows under it,
named only past 60% of the vote with 30% coverage; the caption adds the mode's own chord (Dorian's IV,
Mixolydian's ♭VII) only when that chord actually sounds in the section. A song that comes out as one
section gets no sections: a ribbon reading "A" end to end says nothing. The ribbon's colour is a token
name (`--pm-mode-{name}`, `--pm-fg-muted` otherwise) resolved against the live theme in `canvas.js`;
clicking a band seeks to its start, and `data-section-count` / `data-section-letters` mirror it.

### Practice (sing over the chords)

`/practice` is the other half of the app's purpose: the analyzer tells you a song is Dorian, this asks
what happens when *you* sing. It issues a chord progression, records whatever melody you invent over
it, and hands the take to the analyzer to say which mode that melody turned out to be in.

Deliberately unscored, and that is the design rather than an omission. The page issues no target
melody, so there is nothing to be right or wrong against; and a live "that note is outside the mode"
cue would be worse than useless here, because it would steer the singer toward the mode the chords
were built from before the analysis has had its say. The answer is the analysis, and it arrives after
the take. This replaced an earlier scored drill (`ModeExerciseBuilder` / `ModeExerciseGrader`, a
`/api/practice` group and a localStorage streak); all of it was deleted rather than left switched off.

There is no Practice slice on the server. The page is a second, narrower door onto the Mode Lab's
hum path: `ModalMelodyGenerator` voices the backing, `POST /api/modal-melodies/hum` takes the
recording, and `HumTakeSeeder` seeds the progression as the job's chord track. The backing travels as
the `ModalMelodyRequest` that generated it rather than as a copy of its notes, which is what lets the
server regenerate the exact progression and put the chords the singer actually heard under their
melody. The Mode Lab is a studio where this is one strip among many; this page is the one exercise on
its own, and both go through the same endpoint so there is no second path to keep in step.

`/generate` returns a melody as well as a backing and the page ignores it: playing the server's melody
would hand the singer the answer to the question the analyzer is about to be asked. Picking a mode
swaps in its own signature cadence through the shared `ProgressionCatalog.SignatureFor`, matching the
Mode Lab's "Match to mode" default — a mode is a tonal centre, and rooting the chords on the parent
key would put the wrong note under the first thing the singer hears.

The page reads the caller's own takes back through two reads in the same `ModalMelodies` group, both
served by `HumTakeHistory` and derived on demand like `/stats`. `GET /api/modal-melodies/takes` lists
earlier takes over the same backing and tallies what the analyzer found in one server-worded sentence
("2 takes over Dorian Groove (i - IV - i - IV) in Dorian: 1 came out Dorian, 1 Mixolydian"). It is a
mirror, not a grade: no percentage, no best take, no streak, and the backing's own mode gets no
special place in the tally. Takes are matched on mode, progression and purity (purity rotates the
loop's opening chord), never on key or tempo — transposing asks the same modal question in another
register, and "Fit to my voice" exists to change the key. Matching needs `TakeOrigin.Backing`, the
request the loop was generated from, which the seeder now stores; takes from before it are unmatched.
`GET /api/modal-melodies/voice?mode=` pools the notes of the caller's last 20 finished takes (over any
chords) into a 10th–90th-percentile range, and refuses to claim one below 3 usable takes and 40 notes,
saying how many more takes it needs instead. Its fit puts the mode's home note half an octave under
the median sung note, and returns the *parent key* — the field `ModalMelodyRequest.TonicPitchClass`
already carries — so the client applies it without any transposing of its own. The dropdown is
labelled "Key" for that reason: Dorian in D is E Dorian.

Capture is `hum-recorder.js`, not a fourth path: this records while chords play out of the speakers,
which is the one case that *wants* the browser's echo cancellation, and it already shares
`modal-player.js`'s AudioContext so the take can be trimmed to bar one. `fx-hum-review.js` serves both
moments — `prepareLive` draws the chord bands and fills the sung line in as it arrives, then `prepare`
replaces it with an offline pitch track once the take is decoded, because that pass reads the whole
take at a steadier hop than a capture callback manages. Its consonance tick is silenced while a take
is running, for the same reason the page carries no score.

### First-run demo

A new user would otherwise face an empty library and a multi-minute wait. `Features/Demo` gives every
library one finished analysis instead. `DemoTemplateService` builds a **template** once at startup:
`DemoSong` synthesizes about 45 s of F Lydian (the `lydian-space` vamp, fixed seeds) with
`ModalMelodyGenerator` + `ModalWavSynthesizer`, and queues it through `AnalysisIntake` under the fixed
id `DemoSong.TemplateJobId`, owned by `system:demo-template`. `JobState.IsServerOwned` (the `system:`
prefix, which no sign-in can mint) keeps it out of every library and out of both purge sweeps. Because
it is an ordinary job, an interrupted build is re-enqueued by `JobRecoveryService`; a finished one is
found on the next start and left alone; a Failed or Cancelled one is rebuilt.

The seed hook hands the job what the server wrote: melody and piano rendered separately as the two
stems, the chord track and the tempo, with Separating and ChordDetecting marked provided as
`DemoScore` (`JobState.MarkProvided`, shared with `HumTakeSeeder`). Estimating them measured the
estimators instead: the vocal separator dropped most of the synthesized flute and the chord recognizer
heard chords that were never played. Melody transcription and the modal analysis run for real.
Not D Dorian, the obvious first pick: the analyzer puts the Dorian i–IV vamp's tonic on G even from the
generator's exact notes (as it does for the Mixolydian, Aeolian and Locrian signatures). A first-run
example has to be one it gets right.

Users get a **copy**, never a run: `JobStore.CopyAsync` copies the artifact files, rewrites `job.json`
via `JobState.CopyAs` (new id, owner, `CreatedAt`), and mirrors the copy to blob. `DemoLibrary` seeds
from `GET /api/library`, the one place an empty library is actually observed. Guest creation would
miss Microsoft sign-ins and anyone who arrived while the template was building. An explicit endpoint
would put the "is it empty" decision in the client. Seeding happens on the first empty read once the
template is Complete. A ledger (`demo-seeded.txt` in the jobs root, mirrored to blob as
`_demo/demo-seeded.txt`) records each owner, so a purged or deleted demo never comes back. Until the
template is ready the library is simply demo-less. The copy carries `TakeOrigin.Demo`, worded
server-side ("Demo · synthesized in F Lydian so you can see a finished analysis"). The Library badges
it, and the Home intake card shows "See a finished example" (`data-demo-job`) while the demo is the
library's only row. `Demo:Enabled` (default true) is off in both test fixtures, like
`RateLimits:Enabled`.

### What is deliberately not here

Each of these existed and was removed, with the reason, so nobody rebuilds one by accident:

- **`ExecutionTier.Cloud`** — an execution tier with zero implementations, threaded through the
  planner's ranking, the user-selectable predicate, the pipeline, the tier badge and four paragraphs
  of this file. There was never a paid executor to rank. `EffectiveRank` is now local model → browser
  → classic model-less DSP → placeholder, which is the order the code actually has.
- **A second opinion from MusicBrainz / AcousticBrainz** — 700 server lines and 26 unit cases against
  a community dataset frozen since 2022, where a miss was the common case.
- **URL ingest (yt-dlp)** — the server shelled out to a binary that no Bicep file, CI step or
  appsettings ever installed, so the feature was dead on every deployed instance.
- **Batch upload, MusicXML export, chord-chart export, the share-card PNG, the 3D mode landscape**
  — each a second way to do something the app already did once. The landscape alone cost 691KB of
  three.js, the largest tracked file in the repository, for one button.
- **The `/live` page** — a third microphone path, analysing with no harmony underneath. Practice
  answers the same question against real chords.
- **Basic / Advanced views, and the Library's Table / Wall views** — one page rendered two ways is
  two layouts to keep truthful. The analysis page keeps the compact one; the library keeps the table.
- **Karaoke scoring, the tonic drone, tap tempo and BPM nudge in the mixer** — advanced-only
  controls behind a disclosure inside a view that no longer exists.
- **Nine of eleven `fx-*` modules and the OpenTelemetry export** — decoration, and instruments with
  no collector configured anywhere. `js/shell/prefs.js` survives because it still governs motion and
  sound; which executor really ran is still recorded, in `StageHistory`, where the UI can show it.

### Operational guards

`PoRateLimits` limits the three things that cost real resources - queueing an analysis, running a
language model, calling a catalogue - and deliberately nothing else: reading a finished job's
artifacts is what the canvas does dozens of times while a user pans a timeline, and a blanket limit
would throttle the interactive part of the app to protect the expensive part. Partitioned by
signed-in user, falling back to remote address, and `UseRateLimiter` runs *after* `UseAuthentication`
so that partition is available. The policies are always registered even when limiting is off (a
disabled policy becomes a no-op limiter), because a missing named policy is a startup exception and
making that reachable from a config flag is how a test setting takes production down. Both test
fixtures set `RateLimits:Enabled=false`.

`QueueCapacityFilter` is a different guard, not a redundant one: the rate limit bounds how fast one
client may ask, this bounds how much the server has agreed to do. Without it `JobQueue`'s bounded
channel makes an over-quota finalize *hang* - holding a request until a worker frees a slot - where a
503 with a Retry-After is the honest answer.

Which executor really ran is recorded in `StageHistory`, rewritten by `CompleteStageAsync` after any
fallback has taken over - so the history names the executor that did the work rather than the one
that was planned. That matters because a local model failing on every job is invisible from outside:
the DSP fallback answers and the page renders normally. The job status carries it, the client shows
it, and `/diag` reports which guards are on as booleans only.

### Look and layout

`app.css` holds every colour, size and spacing value the app is allowed to use, defined three times
over — light on bare `:root`, dark under `prefers-color-scheme`, dark again under
`[data-theme="dark"]` so the header toggle wins in both directions. A page stylesheet states a raw
hex only for a chart or canvas *fill*, never for text or a border. The reason is not tidiness: the
Mode Lab was written against a dark ground and shipped light-theme text at 1.2:1, invisible in the
theme that is the OS default on most machines. Status colours come in triples
(`--pm-ok` / `--pm-ok-bg` / `--pm-ok-edge`, and the same for `danger`, `caution`, `info`, `hot`,
`violet`), and each foreground is picked to clear 4.5:1 against its own tinted ground *in that
theme* — which is exactly what one hardcoded hex cannot do. The seven modes get
`--pm-mode-{name}` for the same reason: those are card titles, not decoration.

- **`--pm-text-xs` (12px) is the floor.** Nothing renders text smaller. A label that does not fit
  gets shortened, wrapped or dropped — not shrunk. The header's nav does this literally: every
  destination carries a long and a short label (`.nav-wide` / `.nav-narrow`), one of which is
  `display: none` at any width, so six destinations fit a 320px phone on one line without a
  horizontal scroll strip hiding the last of them.
- **Never clip to make something fit.** `overflow: hidden` on a layout container, `white-space:
  nowrap` on a phrase, and a viewport-height box are all ways of hiding content while appearing to
  lay it out; the Mode Lab did all three and hid 885px of its own controls on a phone. Wrap, reflow,
  or let the page be taller.
- **One breakpoint, 640px** for page chrome (720px where the Mode Lab's four-region layout needs the
  extra room). Every page has one. Fixed pixel widths and `flex-wrap: nowrap` do not survive it, and
  a flex or grid item that holds a control needs `min-width: 0` — the default minimum is the item's
  content, and a dropdown's content is its longest option.
- **`.pm-panel`** is the surface for something that must read as a Radzen card but is not one — the
  Mode Lab's strips. Same ground, border and corner as `.rz-card`, no specular or noise layer.
- Chooser controls are Radzen (`RadzenDropDown`, `RadzenSlider`); a native `<select>` beside one
  reads as a different application. `role="tablist"` is only for a real tablist: a pair of buttons
  that swaps a rendering is `role="group"` with `aria-pressed`.
- Popovers close on Escape and on a click outside, and take focus when they open, which is what
  makes the Escape handler reachable at all.

Two documents are deliberately taller than a phone: the finished analysis page and the Mode Lab.
Both are content that exists to be read and compared, and the alternative to scrolling them is
hiding part of them. Every other route fits 390×844 and 1440×900 with no scrolling in either axis.

### Client conventions

- `wwwroot/js` is grouped by what a module is for, not by what it is made of: `player/` (canvas,
  mixer, modal-player, click-track, take-plot), `capture/` (the recorders, live-pitch, wav),
  `infer/` (the browser-tier pitch worker and its decoder), `upload/` (the vendored tus client and
  the resumable uploader) and `shell/` (theme, pwa, push, prefs, sfx).
  Imports are relative, so a module that moves folders has to fix its own siblings.
- Heavy UI lives in plain JS modules, not Blazor: `player/canvas.js` (dual-lane visualization, pan/zoom, virtualized drawing) and `player/mixer.js` (Web Audio stem playback, synth note overlays, metronome clicks, Space/comma transport keys). `mixer.js` owns the transport clock and drives the canvas playhead directly — no per-frame Blazor renders. Blazor components only issue commands and receive discrete events.
- JS state is mirrored onto `data-*` attributes (`data-mixer-status`, `data-playhead`, …) precisely so Playwright tests can assert without reaching into module internals. Keep that contract when changing these modules.
- The app is an installable PWA with an Android share target, so a voice memo can be shared straight
  into PoMode. `service-worker.js` is **network-first, never cache-first** - `Program.cs` serves this
  app's unfingerprinted JS and CSS with `no-cache` precisely to stop a stale module being paired with
  the C# beside it, and a cache-first worker (including the Blazor PWA template's) would reintroduce
  that bug and make it survive a hard refresh. Adding push did not change that: the worker's
  `push` / `notificationclick` handlers only show a server-worded notification and focus or open
  its link, and every non-GET (all of tus included) still passes straight through. The share target
  POSTs to `/share-target`; the worker stashes the file in a cache and redirects to `/?shared=1`,
  where `Home.razor` claims it via `shell/pwa.js`. The file stays a Blob and reaches the uploader as a
  JS object reference (`takeSharedFile`), so a 40 MB memo never crosses into the WASM heap. `pwa.js`
  registers the worker immediately if `load` has already fired - it is reached through a dynamic
  import that can settle after it, and a late listener left the app with no worker at all.
  `.webmanifest` is mapped explicitly in `Program.cs`: a manifest served as `application/octet-stream`
  is ignored silently and the app simply stops being installable.
- Musical decisions (note colours, labels, measure numbers) are made server-side in `VisualizationBuilder`; `canvas.js` only maps numbers to pixels. Keep music theory out of JS. Same rule for audio: the mixer's chord-pad layer plays notes voiced server-side by `ChordPadBuilder` (served as `/api/analysis/{id}/notes-chords`, derived from chords.json, not stored) — mixer.js treats them as just another note list ('vocal'/'backing'/'chords').

### Infrastructure notes

- `SecretsBootstrap` wires Key Vault via `DefaultAzureCredential` with an env-var fallback (logged as a warning). Never add connection strings or appsettings secrets. The VAPID private key is `PoMode--Push--VapidPrivateKey` in `kv-poshared`; nothing in the app writes it.
- Auth lives in `Features/Auth`. `PoAuth` issues one session cookie from two doors: **guest** (every
  environment, Production included — the client mints one on first visit so the app works before
  anyone signs in) and **Microsoft** (Microsoft.Identity.Web, personal + work accounts, on only when
  `PoMode:AzureAd:ClientId` and `ClientSecret` are set; the secret is `PoMode--AzureAd--ClientSecret`
  in `kv-poshared`). It reads that prefixed section, never `AzureAd`, because the shared vault holds
  another app's unprefixed `AzureAd--*` secrets. Signing in with Microsoft moves the guest's jobs
  across (`JobStore.ReassignOwnerAsync`); a guest is never offered "sign out", since that session
  *is* their library. `FakeAuthHandler` (`X-Fake-User` / `X-Fake-Roles`) is registered only in
  Development and Test, and still throws if constructed in Production.
- `PoUser.IdOf` is the one owner key (`guest:…`, `ms:…`, `test:…`), stamped as the `po_uid` claim by
  every sign-in path. Jobs carry `OwnerId`; the library lists only the caller's, delete answers 404
  for someone else's, and the rate limiter partitions by it (never by display name — every guest is
  "Guest nnnn"). Per-job reads stay open to the unguessable id, as before. `MainLayout` renders no
  page until `SessionState.EnsureAsync` has a session, so no first-render API call lands ownerless.
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
