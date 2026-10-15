# Deliberately not here

Each of these existed and was removed, with the reason, so nobody rebuilds one by accident:

- **`ExecutionTier.Cloud`** (since returned, for one thing only) — an execution tier with zero implementations, threaded through the
  planner's ranking, the user-selectable predicate, the pipeline, the tier badge and four paragraphs
  of this file. There was never a paid executor to rank. `EffectiveRank` is now local model → browser
  → classic model-less DSP → placeholder, which is the order the code actually has. It came back,
  appended, when the optional Azure OpenAI interpreter was built: it tiers that writer and nothing in
  the pipeline, whose ranking still has no cloud rank. See ai-cost.md.
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
- **The modal HUD card, and every other second statement of one fact** — key, mode, confidence,
  scale and tempo appeared in the header *and* a HUD card under the player; purity five times on
  the Mode Lab; the scale three times. Each fact is now stated once: the key badge row is the
  headline, `WindowInspector` reads out only the clicked window, the Mode Lab has one purity readout
  and its chord pills sit on the roll's header. The Mode Lab's Stop button went with it (Play/Pause,
  plus any card or roll restarts from bar one), and its WAV/MIDI links share one `DownloadMenu`.
  Key / mode / chords / tempo are one `BackingControls` row on both the Mode Lab and Practice (tempo
  50–180 on both), and the singer's read is one `SingerCard` on both the analysis page and Practice.
- **GPU inference through DirectML** — measured on this project's Snapdragon X (Adreno X1-85):
  HTDemucs does not fit the integrated GPU's memory, RMVPE ran slower than on the CPU (0.84 s vs
  0.53 s a chunk), Beat This! the same, ChordMini faster by 50 ms. And the DirectML package stops at
  onnxruntime 1.24, five versions behind. Revisit on a machine with a discrete GPU, through Windows ML.
- **Running pitch and chord detection side by side** — both take seconds after separation's minutes,
  and a concurrent stage would persist over the browser handshake's `AwaitingClient`.
- **A long Ollama keep-alive** — thirty minutes left three 3–4 GB models resident and HTDemucs failed
  to allocate on a 16 GB machine.
- **Nine of eleven `fx-*` modules and the OpenTelemetry export** — decoration, and instruments with
  no collector configured anywhere. (The export has since come back as the Azure Monitor distro: the
  collector existed all along, the App Service carried its connection string, and the export now signs
  in with the managed identity. Platform/Telemetry.cs.) `js/shell/prefs.js` survives because it still governs motion and
  sound; which executor really ran is still recorded, in `StageHistory`, where the UI can show it.
  The effects that came back afterwards (see "Effects that carry data") are held to a rule these
  failed: each one draws something the analysis measured.

- **The NVIDIA probe on `/diag`** (`NvmlInterop`, `GpuReport`) - it reported free VRAM, CUDA and
  DirectML for GPU inference this project measured and dropped.
- **`playbackLevel`, `prefs.allowsHeavy` / `intensity`, `sfx.cheer` / `doubleTap` / `hasPalette`** -
  exports left behind by the deleted effect modules, read by nothing.

- **A Library page beside Analyze** - two destinations for one job: getting audio in and finding it
  again. The list is now `LibraryList.razor`, drawn under the intake card on the home page, which the
  header calls Library. `/library` still routes there, and an empty library draws nothing because the
  intake card is already the invitation. It pages at 8 rows, not 15, so the page stays short.

- **The `/beat` page** - a beat cut to a tempo typed on its own page, auditioned on its own clock.
  The drum loop is a strip in the Mode Lab, cut to the lab's tempo and played in step with its chords
  and melody; a second place to cut the same loop would be a second tempo to keep in agreement.

- **The server-side ffmpeg fallback** (`FfmpegTranscoder`) - it converted an MP4 that a browser
  without AAC had sent as recorded, but only where a developer had ffmpeg on the PATH: nothing
  installed it, so on Azure it never ran. The same shape as the removed yt-dlp ingest. An undecodable
  MP4 is now refused with a sentence pointing at Chrome or Edge.
- **The analysis-model choosers on the home page, the writer dropdown in the ask box, and the
  effects, sound and notification rows in the ⋯ menu** - moved, not lost: every optional choice is on
  the Settings page at its best value, so the pages people use carry only what they act on.
- **The keyboard-shortcuts modal** - the list is a section of Settings; Ctrl+K is the way to anywhere.
- **The Library's key and mode dropdowns** - one search box reads "dorian 120" as two filters and
  shows them back as chips.
- **Running a beat through the analysis** - a beat is kept, not analysed (architecture.md, Resumable
  uploads).

## The cloud cost audit (`ai-cost-audit.md`)

It compared cloud stem-separation providers (Replicate, LALAL.AI) and a "copilot", and recommended
keeping an execution order of `Local → fake → Replicate → LALAL`. It was also the one document
`CLAUDE.md`'s first working rule tells a reader to open before planning anything.

None of it was ever built. There has never been a Cloud-tier executor in this repository, "copilot"
appears nowhere in the source, and `ExecutionTier.Cloud` — the enum value that whole ordering hung
on — has now been deleted as well. A cost audit of spend that cannot occur is worse than no document,
because it sends the next reader looking for a tier to extend rather than for the one that exists.

The costs that are real are all local and none of them bill: CPU time for HTDemucs (stem separation)
and Basic Pitch (melody), and whatever model Ollama has installed for the written interpretation. A
paid tier has since been added (the optional Azure OpenAI interpreter), and its audit is in
ai-cost.md, written against the code that is in the repo.
