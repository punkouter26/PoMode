# Architecture

How PoMode is put together and why. `CLAUDE.md` holds the commands and working rules; this is the
background they point at.

One process: `PoMode.API` hosts the Blazor WASM client (`PoMode.Client`), the REST endpoints, a SignalR hub (`/hubs/analysis`), and the background analysis worker. `PoMode.Shared` holds DTOs and the source-generated `PoModeJsonContext`, plus — as the one deliberate carve-out from NET_RULES' "zero business logic" — pure, dependency-free lookup extensions over those DTOs that both API and Client need (e.g. `ModalResultExtensions.WindowIndexAt`, `TimelineSearch`, `ExecutorNames.Display` — the page-facing name for a recorded executor class name; `/diag` keeps the raw one), and the interpretation prompts with their reply reader and `GroundingCheck`, because the browser's built-in model is asked the same question as Ollama and checked the same way; anything with I/O, state, or musical judgment stays out.

## Analysis pipeline (the core)

Each uploaded song becomes a job that runs 4 stages in `AnalysisPipeline`: **Separating → PitchTracking → ChordDetecting → ModalAnalysis**. Every stage has multiple executors registered in `Program.cs` behind seams (`IStemSeparator`, `IPitchTracker`, `IChordRecognizer`), each tagged with an `ExecutionTier` (Local ONNX model, or ClientDelegated browser inference). `ExecutionPlanner.EffectiveRank` fixes the selection order: local model → browser → classic model-less DSP (`IsClassicFallback`: `YinPitchTracker`, `ViterbiChordRecognizer`) → Fake placeholder; within a rank, DI registration order breaks ties, so register new executors *after* the one that should stay the default. `AnalysisPipeline.RunWithFallbackAsync` falls through the same order when an executor fails and records who actually ran in `StageHistory`. If any Fake executor ran, the client shows the "USING MOCK DATA" banner.

**Pitch default is RMVPE, on the numbers.** `RmvpePitchTracker` (RMVPE, MIT, 361 MB, vocal-only, reads `vocals.wav`) is registered *before* `OnnxPitchTracker` (Basic Pitch) so it wins their shared local-model rank. Accuracy report, note F1 (exact pitch, onset ±0.25 s): sine melody RMVPE 0.88 / Basic Pitch 1.00; voice-like line with vibrato and 18 dB pad bleed RMVPE 1.00 / Basic Pitch 0.31 (44 notes for 8 — it transcribes the bleed); mean 0.94 vs 0.65. On a real full mix RMVPE gives 1.1 notes/s in D2–B4, Basic Pitch 4.6 notes/s down to E1. RMVPE is deliberately not an `IFileTranscriber`, so the backing stem is still transcribed by Basic Pitch. YIN scores 1.00 on both synthetic stems (clean monophonic tones are its best case) but stays a classic fallback; the report's default-is-the-winner check excludes classic fallbacks when the default is a model, and says so.

**Beats: `IBeatTracker`** (not a planned stage; runs best-effort inside ChordDetecting, walked by `EffectiveRank` with fall-through, writer recorded in `BeatGridDto.Tracker`). `BeatThisBeatTracker` (Beat This!, CPJKU, MIT, 83 MB + mel filterbank) → `DspBeatTracker` (`TempoEstimator`, classic, hears no downbeats). Beat This writes `BeatGridDto.Downbeats` and a tempo map built bar by bar from them, and `ModalAnalysisEngine` numbers measures from those downbeats when present (4/4 from t=0 otherwise). Report, beat F / downbeat F at ±70 ms: 104 BPM groove 0.96/0.94 vs DSP 0.96/0.00; 143 BPM groove 0.99/0.96 vs DSP 0.65/0.67 (DSP halved the tempo). The beat tracker runs *before* chord recognition, and its beats ride on `StageContext.Beats` so every recognizer cuts chord changes on the beats that were heard (`ChordSegmenter`'s `beats` overload) rather than on its own single-tempo `TempoEstimator` grid, which they fall back to only when no usable grid came out.

**Chords: ChordMini is the default, on the numbers.** `ChordMiniChordRecognizer` (ChordMini's ChordNet "2E1D", MIT, 17 MB, `musetric/chordmini-onnx`) is registered before `ChromaChordRecognizer` so it wins their shared local rank. The graph is the classifier only; its features are librosa 0.11's recursive CQT, computed here from the plan the model ships with (`cqt-plan.bin`: half-band resampler, per-octave sparse FFT basis), step for step as musetric's reference host does on the GPU — against `librosa.cqt` the port correlates at 0.9999997. Its 170 labels fold to the qualities `ChordPadBuilder` voices (sixths, sus and diminished sevenths to their nearest triad). Report, frame accuracy at triad level: plain triad pad under a sine melody ChordMini 100% / Chroma 100% / Viterbi 75%; the demo's F Lydian vamp, full mix with its flute melody, ChordMini 99% / Chroma 40% / Viterbi 0%.

Model files come from `ModelCatalog` (URL pinned to a commit, SHA-256, licence noted) and download at runtime into `Models:RootPath`; nothing is committed. The neural front ends use `AudioDecoder.ResampleBandLimited`, not the linear `Resample`, because their top mel bands sit at the new Nyquist.

Users can pin an executor per stage: `GET /api/analysis/executors` feeds one dropdown per stage on the home page, folded under an "Analysis models · Auto" disclosure (Fake placeholders are filtered out — never user-selectable), the pick rides on upload query params (`stemSeparator`/`pitchTracker`/`chordRecognizer`), and the planner honours it only if it is available. A stage with one real option renders as plain text rather than a disabled control, which would read as broken rather than as "no choice needed".

Jobs are restart-safe: `JobStore` persists `job.json` plus artifacts (`notes.json`, `notes-backing.json`, `chords.json`, `beats.json`, `result.json`, stem WAVs) in a per-job folder under a per-job semaphore, mirroring everything to Azure Blob (Azurite locally). `JobRecoveryService` re-enqueues incomplete jobs on boot; `JobCleanupService` purges old ones. Stage progress is pushed over SignalR only — never polled, never written per-tick.

**Tier 2 (client-delegated)**: the browser probes onnxruntime-web support (`pitch-worker.js`), uploads declare `clientCanInfer=true`, and when a job reaches `AwaitingClient` the browser runs the model and POSTs validated notes back to `/api/analysis/{jobId}/client-result`. The same handshake now covers separation, for the host that cannot run HTDemucs (Azure): `ClientDelegatedStemSeparator` parks, `js/infer/separation.js` fetches the upload, and a worker (`separation-thread.js` + `mdx.js`) runs UVR's MDX-Net Voc_FT (`ModelCatalog.MdxVocals`, UVR public pack, MIT with a credit request honoured in the executor's display name). `mdx.js` is a port of UVR's `demix` — torch STFT semantics at n_fft 7680 via a small mixed-radix FFT, UVR's chunk cross-fade, compensation 1.021 — validated in Node against HTDemucs on a real recording (vocal correlation 0.935). Only the vocals travel back (`POST client-stems`, 16-bit stereo 44.1 kHz, body limit set from the upload's length); the instrumental is mix minus vocals, derived server-side. A browser without WebGPU declines songs over 60 s (`DELETE client-stems`), so the stage falls through at once instead of holding the only worker, and the browser tells the two stages apart by whether `Separating` has completed. Clips under 30 s skip browser separation exactly as they skip HTDemucs, and the browser pitch worker then reads the mix, like the server-side trackers. The browser's files are served on Azure too: `ModelRegistry.EnsureAsync(servedToBrowser: true)` fetches and verifies them there, because the Azure rule is about running models on the server, and before this the hosted app had no browser tier at all.

## Resumable uploads (tus)

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

A recording, a shared file or a converted `.m4a` is a Blob that exists only in the page, so the
uploader keeps the one most recent unsent Blob in IndexedDB (`pm-uploads`) under a fingerprint of its
content (a SHA-256 prefix: every recording is "recording.wav", and two takes of one length used to
resume one onto the other's bytes). A beat left unsent is remembered as a beat. `Home.razor` asks for it once the page is idle (`unsentName` / `takeUnsent`, the same
reference hand-off as the share target) and the upload carries on from the byte the server last
confirmed. It is dropped on success and on a 4xx, which no retry would change.

A decoded file is written as stereo 44.1 kHz WAV when that fits the 100 MB cap, as mono when only
that fits, and is refused as too long past that - never as "too large", a size the picked file did
not have. The browser decides whether to decode from the file's declared type only (WAV and MP3 go up
as they are); `AudioFormatValidator` is the one judge of the bytes. Its MP3 sniff requires a non-zero
layer so raw AAC (ADTS, same sync bits) is not passed off as MP3, and an MP4 container that reaches it
undecoded is refused as `CannotConvert` with a sentence pointing at Chrome or Edge.

**Beats are kept, not analysed.** `?beat=true` on the hand-off routes to `AnalysisIntake.KeepBeatAsync`:
an ordinary library job (restart-safe, mirrored, listed) stamped `TakeOrigin.Beat` and Complete at
once, with no plan and nothing queued. Running separation, pitch and chords on a drum take held the
single worker for minutes and filled the library with "songs" nobody read. The library opens a beat in
the Mode Lab; it cannot be previewed, compared or re-run.

## Push notifications

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

## Mode Lab harmony

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

## Mode Lab layout

The page is a stack of strips, each its own component: the transport (sticky under the header, its
offset measured by `js/shell/layout.js` as `--pm-header-height`), the backing settings with a
"Describe it" box, `DrumStrip`, `ModeCards`, `GravityPanel` and the roll. A MIDI keyboard can play
along (`js/player/midi-in.js`, Web MIDI, notes only, asked for on a click): each key sounds through the
player's room and lights its body on the home-note view, and the readout counts notes outside the
mode. The interface's own sounds are voiced in the lab's mode (`ModeNames.Pitches` → `sfx.setPalette`),
as they are in the song's key on an analysis page.

## Hum takes (the shared recording path)

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

## The singer's voice

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

`VoiceProfileDto.Map` carries a keyboard drawing's worth of the same read - the span (whole Cs), the
10th/50th/90th-percentile notes, all six ranges with the matched type and leaning flagged, key labels -
and `VoiceMap.razor` draws it wherever the profile shows (the stats panel, Practice), with a button
that plays the strongest note in the lead synth voice.

"Best note" is not the most frequent one. notes.json keeps whole semitones, so `IntonationMeter` goes
back to the audio and reads YIN's continuous pitch inside each note (`PitchesInside`, middle 60% only —
the scoop into a note and the fall off it are expression, not tuning). A pitch's score is time held,
discounted by distance from true pitch and by wobble while held; the sentence only claims the
superlatives it actually won. That measurement costs a second or two per song, so unlike `/stats` it is
cached as `intonation.json`, keyed on note count and tuning reference. A song is graded against its own
measured tuning (the band sets the pitch); a hum take against A=440, the backing it was sung over —
correcting a take for its own offset would forgive exactly the flatness a singer wants to hear about.
The demo declines outright: its melody is a synthesized flute, not a voice.

`GET /api/modal-melodies/voice/song/{jobId}` puts the two reads together for an analysed song: its
comfortable span against the caller's own (`HumTakeHistory.SongFit`, both 10th to 90th percentile),
as the transposition that centres one on the other, folded into half an octave either way, with the
range it would then run spelled out. 404 when there is nothing honest to say (too few takes, too few
notes, or a job that is itself a take or the demo); the stats panel shows the sentence under the
singer's card and nothing otherwise.

## Song statistics and interpretation

`GET /api/analysis/{id}/stats` derives every melody/harmony statistic on demand from the stored
artifacts — `SongStatsBuilder` takes the `VisualizationPayload` (so note roles and pitch labels are
reused, never recomputed) plus `chords.json`, `result.json` and the optional `beats.json`. Nothing is
persisted, same ruling as `/visual`. `SongFingerprint` then writes the same numbers as one
plain-English paragraph; its rule is that a weak figure (unconfident mode, missing beat grid) is
*omitted*, never hedged.

`GET /api/analysis/{id}/interpretation?interpreter=` turns those statistics into prose behind the
`ISongInterpreter` seam: `OllamaSongInterpreter` (Local, whatever model Ollama has installed,
preferring `phi4-mini`, a five-for-five winner of the 2026-10-14 interpreter report; the constant's comment has the numbers) → `TemplateSongInterpreter`
(deterministic, always available, `IsClassicFallback`). There is no cloud interpreter.
`SongInterpreterSelector` falls through on failure exactly like `RunWithFallbackAsync`, and ranks by
answer quality rather than by `ExecutionPlanner.EffectiveRank`, because one small prompt is not the
cost question a pipeline stage poses. The prompt contains only measured numbers, no audio, title or
artist, so a model cannot report what it was never given.

One route per operation, two representations: `Accept: text/event-stream` gets server-sent
`InterpretationEvent`s (each field's text as it is written, a restart when an attempt is abandoned,
the checked result last) and anything else gets the finished JSON from the same events. The reply is
JSON constrained by a schema (Ollama `format`, the Prompt API's `responseConstraint`) — `{plain,
theory}` for the summary, `{inData, answer}` for a question — and `ReplyReader` reads it while it
streams. That replaced a `===FOR MUSICIANS===` delimiter and a `NOT IN THE DATA` first line, which
models mangled often enough to need tolerant parsers of their own. `GroundingCheck` then compares
every figure in the reply with the measurements it was shown (rounding to the written precision is
quoting; whole numbers up to 12 pass, the documented ceiling); a model that invents one is sent its
own reply with the figures named for one more attempt, then falls through. The summary is cached per
job and interpreter as `interpretation-{name}.json`, keyed on a hash of the whole prompt, so a
reload is a file read and a prompt change invalidates it; the GET carries the interpret rate limit
because a cache miss runs a model.

Every request about a song sends the same system message (`InterpretationPrompt.System`) and puts
the statistics first in the user message, the task after them: llama.cpp reuses the processed prefix
of its previous request, so after the first request only the tail is processed. Ollama requests set
`think: false` (reasoning models otherwise spend the whole output budget on `thinking`), `num_ctx`
8192 (Ollama drops the *front* of an overlong prompt, which is where the measurements are, and the
prompt token count is logged against it), and `num_predict` 1536 with `done_reason: length` treated as
a failure (llama3.2 looped on the theory half under the JSON constraint until the old five-minute
timeout). A probe loads the model in the background at the same `num_ctx` — warming at Ollama's
default made the first question reload it. Residency is left at Ollama's five-minute default on
purpose: see the list below.

`POST /api/analysis/{id}/interpretation/ask` turns that one-shot write-up into a conversation, over
the same seam and the same measurements. `QuestionPrompt` restates the full statistics block on
*every* turn ahead of the transcript, because a model asked to recall a figure from six messages back
approximates it, and an approximated statistic presented as measured is the failure the grounding
exists to prevent. What the question prompt adds is permission to decline: a summary can always be
written from the data but a question need not be answerable from it, so `inData` false is a
legitimate answer, and the client labels it rather than hiding the refusal.
`TemplateSongInterpreter` answers by routing the question to the measurement it is about (mode, tempo,
range, rhythm, harmony, motion, phrasing, tension) and declines anything else, which is honest and
also tells the reader a local model would get them further. The conversation is held client-side; the
server stores no transcript, same ruling as the statistics themselves. On the page the summary and
the questions are one transcript: "Summarize this song" is the first starter chip and its write-up
lands as the first turn, rather than a separate Interpret block with its own heading and controls.

The browser's built-in model (Chrome's Prompt API, `js/infer/browser-llm.js`) is a third interpreter
the page offers when `LanguageModel.availability()` says it can run, ranked where the planner ranks a
browser tier: after Ollama, ahead of the template, and the default only when it is ready and the
server has no model. The page builds the same prompt from the `SongStats` it holds and runs the same
`GroundingCheck`; a reply that fails it, or a model that errors, falls back to the server's default.

The question prompt also says what the question is: a listener's words, not instructions. One that
asks the model to drop the rules or write about something else is answered `inData` false.

## Why this mode, and where the sections are

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
gives the window inspector one line when a single window settles the headline question on its own. The
client's "Why this mode?" disclosure sits beside the key badge and is the page's one answer to that
question: `ModeWhyPanel` shows these sentences plus the mode-vote and scale-degree bars from `/stats`
(one fetch in `BasicView`, shared with the stats panel), and opening it rings the cited notes on the
canvas (`data-evidence-highlight`) and fades the rest.

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

"How were the sections found?" under the canvas opens the measurements themselves:
`GET /api/analysis/{id}/structure` serves `SongSectionBuilder.Analyse` - bar edges, the similarity
matrix as one byte per cell in base64, the novelty curve, the boundary bars - on its own route because
only that view reads it, and 404s wherever the ribbon is absent (no sections, or past 256 bars, where
the matrix grows with the square). `js/player/section-map.js` draws the matrix in one WebGL2 fragment
pass (2D canvas otherwise) with the checkerboard sliding down the diagonal once on open, weighted by
the server's own Gaussian taper, then parked on the bar line nearest the analysis canvas's
`data-playhead`; the novelty curve and section letters sit underneath on the same bar axis.

## Practice (sing over the chords)

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

## Drum loop (in the Mode Lab)

The Mode Lab's Drums strip takes a beat recorded without a click and plays it under the chords and
melody as exactly whole 4/4 bars at the lab's tempo, and saves that loop as a WAV. It has been both
places: a strip here, then a `/beat` page of its own with its own tempo and Play button, then back,
because a beat cut to a tempo nothing else is playing at answers no question the lab asks. There is
no importer and no recorder of its own for it. The Library's intake row is four buttons, "Choose a
beat", "Record a beat", "Choose a song", "Record a song"; the two recordings share
`audio-recorder.js` and one Stop button, and differ only in where the take goes. A beat goes up over
the same tus upload as a song, is kept as a library job without being analysed (`KeepBeatAsync`, see
Resumable uploads), and opens `/modes?beat={jobId}`. Picking a beat sets the lab's tempo: `drum-loop.js`
`playedBpm` reads the take's own tempo (the pulse read as whichever note value lands nearest 120),
the lab moves to the nearest whole BPM inside the slider's 50 to 180, the chords and melody are
regenerated there and the drums are cut to the same figure. The slider still changes it afterwards. The strip's dropdown lists the
library's finished jobs, beats first (the demo aside), so an earlier upload or recording can be picked too.

`player/drum-loop.js` fetches the job's original audio back (`stems/mix`), decodes it on
`modal-player.js`'s AudioContext and starts the loop from that player's own downbeat (`sync`), the
same shared-clock reason `hum-recorder.js` has: two clocks cannot be held in step. The strip is its own
component (`DrumStrip.razor`), which recuts whenever the lab's `Bpm` parameter moves; the lab calls its
`SyncAsync` after play, pause, stop and seek, so the three parts start, stop and jump together. A drum
loop whose bar count divides the progression's stays on its bar one; any other length drifts round
it as a polymeter, which is what was asked for by picking it. The first cut waits for the render
that gives the canvas its size. The canvas draws the loop on its bar and beat grid with a sync
marker where each hit was put, the loop's two ends, and while playing a playhead read off the audio
clock (`data-drum-loop-progress`).

`player/loop-cut.js` is the splice, pure arithmetic with no DOM. Nothing is time-stretched. The
recording is cut at its hits (`findOnsets`: a step up in a 10 ms level envelope, placed at the start
of its attack), each hit is given the sixteenth it was played on, and the slices are laid back down on
that grid at the target tempo (`buildLoop`). `sourceTempo` finds the pulse first - the period the gaps
between nearby hits are most nearly multiples of - and then reads it as a beat, eighth or sixteenth,
whichever puts the tempo within half an octave of the target, so the answer is the one that moves the
hits least. (Scoring tempos directly, beats weighted over subdivisions, read one real phone take three
different ways depending on the target.) `layOut` then counts each hit on from the one before with the
step length eased toward what the player is doing, because a take with no click drifts and a grid
ruled once across it slides off. The first hit is beat one. A last hit landing exactly on a bar line
is the closing "one" and does not add a bar. A slice longer than its slot is faded short, a shorter
one leaves silence, and the last slice's decay is folded over beat one so the seam does not click.
Straight sixteenths only: swing is squared off, and rises under 15% of the loudest stay inside the
slice before them - the first detector, hop-to-hop on a 1.3 ms envelope, heard 97 hits in a 31-hit take.

Bars follows what was played until it is typed in. The export is `drum-loop-{bpm}bpm-{bars}bar.wav`,
16-bit mono, the drums alone.

Analyze takes any audio the browser can decode, because phone voice memos are `.m4a`: the server's
rule is still WAV or MP3 by header, and `resumable-upload.js` (`asWavOrMp3`) decodes anything else
and uploads it as WAV. That applies to every way in, the share target and OneDrive included. The decode
is the browser's own, so a browser without AAC (an editor's built-in browser, some Linux Chromium
builds) cannot do it, and says so. The server-side ffmpeg fallback that used to cover that case is
gone (see removed.md).

Checked by `node tests/loop-cut.check.mjs` (a synthetic take at 93 BPM with jittered hits, spliced to
100), not by an E2EUI test: that suite is at its cap.

## First-run demo

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

## Operational guards

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

A waiting job is told how many will run before it. `JobQueue` keeps the ids in its channel in order
(a channel cannot be read without taking from it), the hub answers `Subscribe` with the caller's
position, and `AnalysisWorker` pushes `QueuePosition` to everyone still waiting each time it takes a
job. Pushed, never polled, like the stage progress; `JobProgress` shows it until the first stage starts.

Every response carries `X-Content-Type-Options: nosniff`, `Content-Security-Policy: frame-ancestors
'none'` and a referrer policy, and HSTS outside Development. There is deliberately no `script-src`:
it would have to allow the two inline scripts in `index.html`, the WASM runtime and the model
workers' blobs, and a wrong one fails silently in production.

## Client conventions

- `wwwroot/js` is grouped by what a module is for, not by what it is made of: `player/` (canvas,
  mixer, modal-player, click-track, take-plot, voices, section-map, wait-preview, mode-gravity,
  voice-map, drum-loop, loop-cut), `capture/` (the recorders, live-pitch, wav),
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

- `L` loops the section under the playhead (`mixer.js` `toggleLoop`, `data-loop-section`; the mixer's
  "Loop section" button is the same toggle). A seek out of the section ends the loop.
- What the browser remembers, all in localStorage and all optional, all through one typed helper
  (`Services/BrowserPrefs.cs`, JSON through its own source-generated context; anything unreadable reads
  as nothing remembered): the Library's search (`pm-library-filters`), Practice's backing
  (`pm-practice-backing`), the pinned analysis models (`pm-executor-pins`, kept only while the server
  still lists that executor) and the chosen writer (`pm-interpreter`). The theme, effect level, sound
  and spatial-audio flags stay in `theme.js` / `prefs.js`, which stamp them before first paint. The
  Mode Lab is not remembered: its settings move each other.

- The home page (`/`, and `/library` for old links) is the Library: the intake card, then
  `LibraryList` with the caller's songs, filters and the one-tick preview / two-tick comparison. A
  job being run or shown replaces both, as it replaced the intake card before.

## Settings, the palette and the menu

Every optional choice lives on `/settings`, each already at its best value: theme, effects, interface
sounds, spatial audio, the analysis-model pins, the written-interpretation writer, push notifications,
the keyboard shortcuts and the person's data. The pages people use carry only what they act on: the
home page names a pinned model in one line ("2 analysis models pinned · change") rather than showing
the choosers, and the analysis page's ask box has no writer dropdown. The header's ⋯ menu keeps only
places to go (account, Settings, "Go to…", Diagnostics, Install). `ThemeState` is shared by the header's
quick toggle and Settings so each shows what the other set.

Ctrl+K (`js/shell/palette.js` → `CommandPalette.razor`) opens a combobox over a listbox of pages,
settings and every finished song and beat in the library; each typed word must appear in the row.

**Your data** (`Features/Account`): `GET /api/account/export` zips every job folder the caller owns
(written to a temp file, streamed with DeleteOnClose); `DELETE /api/account/data` cancels and deletes
every job they own and forgets their push subscriptions. The demo ledger keeps their name, so an
emptied library stays empty.

## Library: one search box, tiles, likeness, offline

`LibraryQuery` reads the one search box word by word: a mode (or three letters of one), a key, a tempo
or a range, a kind (beats, takes, demo), else a word of the file name; each recognised filter is shown
back as a removable chip. `SongTile` draws each row's analysis as a picture (ground = main mode's hue,
stripes = tempo, angle = key), from facts the row already carries. The date column is the first to go
under 900px.

`GET /api/library/{id}/similar` (`SongSimilarity`) scores the caller's other analysed songs by the
cosine of their mode-time profiles, with tempo and a shared tonic as tie-breakers, and words the reason
claiming only what holds. Shown under a single-song preview.

"Keep offline" (`js/shell/offline.js`) fetches a song's readings and stems into the `pomode-v1-saved`
cache, which the network-first service worker's fallback finds like any other; the library badges kept
songs. Opening a song from the library morphs its key into the analysis headline (View Transitions,
`js/shell/transitions.js`), the one page transition, and it carries a fact.

## The analysis page, after the fact

- **Chroma under the chords** (`GET /{id}/chroma`, cached as `chroma.json`): the DSP chord reader's own
  `ChromaExtractor` over the instrumental stem, quantised to a byte per pitch class per frame, rows
  rotated so the tonic is at the bottom. `js/player/chroma-layer.js` renders the whole song once (WebGPU
  fragment shader where available, a 2D ramp otherwise) and `canvas.js` slices it to the view.
- **Tension ribbon**: `VisualWindow.Tension` is the share of sung time on non-chord tones, from the
  roles the notes are coloured by; drawn along the foot of the note lane, glowing where the playhead is.
- **Section wash**: the card takes a faint inset glow of the playing section's mode token, through the
  registered `--pm-section-tint` so the change is a browser-animated cross-fade.
- **Lyrics** (`Features/Lyrics`): Whisper base.en through Whisper.net on `vocals.wav`, after the job,
  written as `lyrics.json`; the now-playing chip shows the line being sung. Segments under 0.4 of
  Whisper's own probability and bracketed sound labels are dropped. Only from a separated vocal.
- **Drawers**: "Why this mode?" and "How were the sections found?" open as Radzen side dialogs, so the
  canvas stays where it was while reading why. The why drawer offers Essentia's major/minor key reading
  (`js/infer/key-check.js`, in the browser) worded against the mode by `KeyAgreement`. Essentia.js is
  AGPL-3.0 and is loaded from jsDelivr on that click only; anyone redistributing PoMode should weigh
  that licence before keeping the feature.
- **Phone dock**: once the mixer's transport scrolls away on a phone, a slim fixed bar carries
  Play/Pause and the key (`js/player/dock.js`, an IntersectionObserver), never both at once.
- **Try another model** (`JobRerun`, `POST /{id}/rerun?stage=&executor=`): a new job holding only what
  the stages before the chosen one produced (`JobStore.CopyAsync`'s include filter, so nothing later
  reaches the copy or its blob mirror), the chosen stage pinned. Ordinary uploads only.
- **Time left** (`EtaEstimator`, `GET /{id}/eta`): seconds per audio second for each executor, the
  median of this server's finished stages, times the song's length; null when any remaining executor
  has never been timed here. Fetched once per stage change.

## Aftercare: work done before anyone asks

`JobAftercare` (a background channel, one job at a time) runs after every finished job except beats and
the demo template: it writes the default interpreter's summary into its cache, then the lyrics. The
pipeline's single `IJobOutcomeNotifier` is `OutcomeFanOut`, which enqueues aftercare and sends the Web
Push.

## Interpretation tiers and the describer

Server order (`SongInterpreterSelector.Rank`): a local model (Ollama), then the optional cloud model
(`AzureOpenAiSongInterpreter`, `ExecutionTier.Cloud`, managed identity, no key; available only when
`PoMode:AzureOpenAI:Endpoint` and `:Deployment` are set, which the Bicep does when
`enableCloudInterpreter` is true), then the template. In the page: Chrome's Prompt API and WebLLM
(`js/infer/web-llm.js`, Qwen2.5 1.5B on WebGPU, about 1 GB, downloaded on first use), both through the
same prompt and `GroundingCheck`. While a model writes, the template's summary stands in as a "quick
read". `InterpreterStats` counts each writer's passes, grounding retries and fall-throughs for `/diag`.
The system prompt carries three worked examples of what the grounding check accepts and rejects.

The Mode Lab's "Describe it" box (`ModeLabDescriber`, `POST /api/modal-melodies/describe`) reads plain
rules first (darker/brighter along the brightness order, faster/slower, mode names, "in D" as the mode's
home note, styles, purity, "new melody") and only sends a phrase the rules find nothing in to a model,
held to a JSON schema of the five settings; it counts only what actually changes.

## Separation on the server, two ways

`MdxStemSeparator` runs UVR's MDX-Net Voc_FT on the CPU through `MdxDemix`, the C# twin of
`js/infer/mdx.js` (kept line for line). Registered after HTDemucs so HTDemucs stays the default; about
three times faster than real time on the reference machine against HTDemucs's minutes, and pickable in
Settings or through "Try another model".

## OneDrive

A Microsoft account can bring songs in from its OneDrive (`Features/OneDrive`): Graph with the person's
own delegated token, Files.Read asked for only when they choose OneDrive (`/auth/login/microsoft?
onedrive=true`, incremental consent). `GET /api/onedrive/audio` lists mp3/wav/m4a files;
`/items/{id}/content` streams one through the server, and the page hands the Blob to the ordinary
upload path. Tokens are cached in memory, so a restart costs one more consent round trip, which the
picker handles. The app registration is created by `scripts/setup-microsoft-signin.ps1`.

## Infrastructure notes

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
- Telemetry (`Platform/Telemetry.cs`): the Azure Monitor OpenTelemetry distro, on only where
  `APPLICATIONINSIGHTS_CONNECTION_STRING` is set (the Bicep sets it for the deployed app), signing in
  with the managed identity (`monitoring-role.bicep` grants Monitoring Metrics Publisher). `/diag`
  reports it as a boolean.
- E2EUI's `AppFixture` boots the real app and uploads real WAVs generated by `TestCommon`'s `TestAudio`; E2EAPI uses `AuthedFactory` (in-process, FakeAuth headers). `TestCommon` holds shared audio/fixture helpers, not an app fixture.
