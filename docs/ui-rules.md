# Look, layout and effects

## Look and layout

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
  `display: none` at any width, so the destinations fit a 320px phone on one line without a
  horizontal scroll strip hiding the last of them. Diagnostics is not one of them: it is an
  operator's page and lives in the ⋯ overflow menu.
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
- **Buttons are `RadzenButton`**, sentence case, and Radzen's own colours (`--rz-primary`,
  `--rz-on-primary`, danger, notifications, placeholder) are mapped onto the `--pm-*` tokens once in
  `app.css`. Left to the Material theme, dark mode put white on pale lavender at 2.65:1. One filled
  Primary per group (the action); everything else `Light`; a toggle that is on is Primary +
  `Variant.Flat` + `Shade.Lighter`. An either-or choice is a `RadzenSelectBar`, not a row of buttons.
- **Every page has an `<h1>`** — visible where the page has a title, `.pm-sr-only` where the page is
  its own heading (Library, Mode Lab, Practice, the analysis page). Section labels are `<h2>`.
- **Targets are at least 24px tall**; text-sized toggles get padding, not a smaller font. Canvas labels
  hold the 12px floor too (`canvas.js` `LABEL_FONT`) and are dropped when they do not fit.
- **Late content reserves its room.** The stats skeleton, the mixer's section and loading slot, and
  Practice's plot hold their height before they fill, so nothing already on screen moves.
- A `string` component parameter needs `@` to bind a field: `ProgressionId="_progressionId"` passes
  the literal text. It kept the Chords dropdown blank on both backing pages.

- **Which buttons are not `RadzenButton`, on purpose:** the header's icon buttons and the rows of its
  overflow menu (a menu, `role="menuitem"`), the text-sized disclosure toggles ("Why this mode?"), and
  "+ New Track", which sits beside a download *link* and has to match it. Everything else is Radzen.
- **Touch gets 44px.** Under `@media (pointer: coarse)` full-size buttons, disclosures, the header
  icons and the menu rows are at least 2.75rem tall; small buttons in dense rows keep their own phone
  layout. The 24px floor is for a pointer.
- **Late or streamed text is announced.** Queue position and the active stage are `role="status"`,
  finished answers land in a `role="log"`, errors are `role="alert"`. A draft being streamed is not
  live, or every fragment would be read out.

## Effects that carry data

The background shader, particle field, spectrum bars, page transitions and confetti were deleted as
decoration. What replaced them follows one rule: an effect animates a fact from an artifact, never a
mood. Each is gated on `prefs.js` (skipped or drawn still when effects are off) and mirrors its state
onto `data-*` for Playwright.

- **Result reveal** (`canvas.js` `reveal()`, `AnalysisCanvas Reveal`): notes spring from the lane's
  middle to their rows in the order they were sung, chords drop in behind; about a second,
  `data-reveal` goes running → done. For a hum take (`Ink`) the notes land uncoloured and a key-coloured
  front paints each with its role - the verdict arriving over the singer's own melody.
- **Live wait** (`JobProgress`, `player/wait-preview.js`): each stage's artifact drawn as it lands -
  the upload's waveform, then the vocal's, then notes.json, then chords.json - fetched once per
  SignalR push, never polled. `GET /api/analysis/{id}/peaks` is a streamed 512-slice min/max sketch
  (`AudioDecoder.TryReadPeaks`) of `vocals.wav` once it exists, the upload before that.
- **Stem-driven glow** (`mixer.js`): the vocals and instrumental stems are metered before their gains,
  so the sounding note's halo follows the singer and the chord under the playhead pulses with the band,
  whichever stem is audible. With the metronome on, a phone buzzes on each downbeat (`sfx.tap`).
- **Now-playing chip**: frosted glass over the canvas naming the chord, sung note and section at the
  playhead, written straight into the DOM by `canvas.js` only when one changes.
- **Home-note view** (Mode Lab, `player/mode-gravity.js`): the mode's notes as spring-held bodies on a
  chromatic clock around its home note; a card change moves the same bodies to a new home. Bodies
  light while `modal-player.js` sounds their pitch, and tapping one auditions it.
- **Voices** (`player/voices.js`): lead, Karplus-Strong pluck and FM electric piano, plus one generated
  room. The mixer's overlays, the Mode Lab's room, the voice map and the tonal UI cues all use them.

- **Count-up** (`canvas.js` `countUp`): during the reveal the confidence and tempo in the headline
  run up from zero to the measured figure. The text node is written in place, so Blazor's later
  updates (the live tempo) still reach it.
- **Placed auditions**: a note tapped in the home-note view is panned to where its body sits.
- **Onset bursts** (`canvas.js`): sized by the note's length, largest for the notes the mode's verdict
  rests on, so the burst is the note's weight in the analysis.
- **Chroma heat** (`chroma-layer.js`): the band's pitch-class energy under the chord labels, tonic at
  the bottom; drawn once per song (WebGPU where available), never per frame.
- **Tension ribbon**: the server's per-window share of singing off the chord, glowing where the
  playhead is.
- **Section wash**: the analysis card's inset glow eases to the playing section's mode colour.
- **Drum hit flashes** (`drum-loop.js`): each marker glows as its hit sounds.
- **Practice comet**: the voice's leading edge during a take, in the playhead's colour, not the
  agree/pass pair, so it marks where the voice is and never how it sits.
- **Song tiles** (`SongTile.razor`): a row's analysis as a picture - mode hue, tempo stripes, key angle.
- **The key morph**: opening a song from the library moves its key into the headline (View
  Transitions). The one page transition, and it carries the key.

## Layout additions

- **Settings** (`/settings`) holds every optional choice, each at its best value; the menu keeps only
  places to go. Ctrl+K opens the palette.
- **`.pm-label`** is the one small-caps caption (section titles, figure labels); a component adds only
  what differs (a colour, a weight).
- **Fluid type**: `--pm-text-sm`, `-md`, `-lg`, `-xl` are `clamp()`s that never go under the 12px floor.
- **Drawers**: explanations that would push the canvas down open beside it (Radzen side dialogs).
- **Phone dock**: Play/Pause and the key dock at the foot of a phone's screen once the mixer's own
  transport has scrolled away, and only then.
- **Sticky transport**: the Mode Lab's transport sticks under the header (`--pm-header-height`).

Two documents are deliberately taller than a phone: the finished analysis page and the Mode Lab.
Both are content that exists to be read and compared, and the alternative to scrolling them is
hiding part of them. Every other route fits 390×844 and 1440×900 with no scrolling in either axis.
