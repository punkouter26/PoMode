// Analysis canvas (spec §7): the song's sections along the very top, a tempo line under them, note
// capsules in the middle, chord blocks below. The section and tempo lanes share the other two lanes'
// time axis, which is the whole reason they are drawn here rather than as separate charts above the
// canvas — pan and zoom keep all four aligned.
//
// Every musical decision — which colour class a note belongs to, its labels, its measure number —
// was already made server-side by VisualizationBuilder and arrives in the payload. This module only
// maps numbers to pixels. Keep it that way: no music theory in here.
//
// Drawing is virtualized (only items intersecting the visible time range are drawn) and coalesced
// into one requestAnimationFrame callback, so panning a five-minute track stays smooth.

import * as prefs from '../shell/prefs.js';

const states = new Map();

/// Role name -> CSS custom property. Names match the C# NoteRole enum, sent as strings so the
/// mapping cannot silently break if the enum is ever reordered.
const ROLE_VARIABLES = {
    ChordTone: '--pm-note-chord-tone',
    InMode: '--pm-note-in-mode',
    Characteristic: '--pm-note-characteristic',
    Outside: '--pm-note-outside',
};

const NOTE_LANE_FRACTION = 0.68;
/// Fixed pixels, not a fraction: the tempo line needs a readable strip rather than a share of the
/// box, so a short canvas shrinks the note lane instead of squashing this into nothing. Capped
/// against the canvas height below so it can never take over a very short one.
const TEMPO_LANE_PX = 38;
/// The section ribbon: tall enough for one line of 12px caption, the app's text floor.
const SECTION_LANE_PX = 22;
// The app's text floor (--pm-text-xs) holds on the canvas too: a label that does not fit at 12px is
// dropped, not shrunk. Note labels need a row at least MIN_LABEL_ROW_HEIGHT_PX tall to be drawn.
const LABEL_FONT = '12px system-ui, sans-serif';
/// The server names each band's colour as a theme token; anything else is ignored rather than read.
const SECTION_TOKEN = /^--pm-[a-z-]+$/;
const TEMPO_LANE_PAD_PX = 7;
const LANE_GAP_PX = 6;
const MIN_LABEL_WIDTH_PX = 38;
const MIN_LABEL_ROW_HEIGHT_PX = 13;
const MIN_VIEW_SECONDS = 0.5;
const DRAG_THRESHOLD_PX = 4;
const FALLBACK_DURATION_SECONDS = 10;

/// The result reveal's timing, in seconds. Each note waits for its turn by how far across the view
/// it sits, so the transcription arrives left to right as it was sung; chords land a beat behind the
/// notes. With `ink` a colour front then sweeps the same way (see reveal()).
const REVEAL = {
    noteSpread: 0.45,
    noteRise: 0.6,
    chordDelay: 0.25,
    chordSpread: 0.45,
    chordDrop: 0.4,
    inkStart: 1.0,
    inkSweep: 1.1,
};

function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
}

function readColours(canvas) {
    const style = getComputedStyle(canvas);
    const read = (name, fallback) => style.getPropertyValue(name).trim() || fallback;

    const roles = {};
    for (const [role, variable] of Object.entries(ROLE_VARIABLES)) {
        roles[role] = read(variable, '#888888');
    }
    return {
        roles,
        lane: read('--pm-lane-bg', '#f5f5f7'),
        border: read('--pm-border', '#d9d9e0'),
        text: read('--pm-fg', '#1a1a1a'),
        muted: read('--pm-fg-muted', '#55555f'),
        chordBlock: read('--pm-chord-block', '#e2e8f0'),
        chordSelected: read('--pm-accent', '#6750a4'),
        key: read('--pm-key', '#6750a4'),
        playhead: read('--pm-playhead', '#e11d48'),
        tempoLine: read('--pm-tempo-line', '#818cf8'),
        // Amber, matching the tempo chart in the stats panel, so a change reads the same everywhere.
        tempoChange: read('--pm-tempo-change', '#fbbf24'),
    };
}

/// Resolves each section's colour token against the live theme. Called on a new model and on every
/// theme flip, never per frame — getComputedStyle is not free.
function resolveSectionColours(state) {
    const style = getComputedStyle(state.canvas);
    const sections = state.model && state.model.sections ? state.model.sections : [];
    state.sectionColours = sections.map(section => (SECTION_TOKEN.test(section.colourToken)
        ? style.getPropertyValue(section.colourToken).trim()
        : '') || state.colours.muted);
}

function totalSeconds(state) {
    const duration = state.model ? state.model.durationSec : 0;
    return duration > 0 ? duration : FALLBACK_DURATION_SECONDS;
}

function clampView(state) {
    const total = totalSeconds(state);
    let span = clamp(state.viewEnd - state.viewStart, MIN_VIEW_SECONDS, total);
    let start = clamp(state.viewStart, 0, Math.max(total - span, 0));
    state.viewStart = start;
    state.viewEnd = start + span;
}

function invalidate(state) {
    if (state.frame !== null) {
        return;
    }
    state.frame = requestAnimationFrame(() => {
        state.frame = null;
        draw(state);
    });
}

/// Index of the first item whose startSec is at or after `threshold`; items are sorted by startSec.
function firstIndexFrom(items, threshold) {
    let low = 0;
    let high = items.length;
    while (low < high) {
        const mid = (low + high) >> 1;
        if (items[mid].startSec < threshold) {
            low = mid + 1;
        } else {
            high = mid;
        }
    }
    return low;
}

function timeToX(state, seconds, width) {
    const span = state.viewEnd - state.viewStart;
    return span <= 0 ? 0 : ((seconds - state.viewStart) / span) * width;
}

function xToTime(state, clientX) {
    const rect = state.canvas.getBoundingClientRect();
    if (rect.width <= 0) {
        return state.viewStart;
    }
    const fraction = clamp((clientX - rect.left) / rect.width, 0, 1);
    return state.viewStart + (fraction * (state.viewEnd - state.viewStart));
}

/// Matches the backing store to the CSS box and the device pixel ratio. Returns the CSS-pixel size,
/// which is what every layout calculation below uses.
function syncSize(state) {
    const canvas = state.canvas;
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(canvas.clientWidth, 1);
    const height = Math.max(canvas.clientHeight, 1);
    const backingWidth = Math.round(width * ratio);
    const backingHeight = Math.round(height * ratio);
    if (canvas.width !== backingWidth || canvas.height !== backingHeight) {
        canvas.width = backingWidth;
        canvas.height = backingHeight;
    }
    state.context.setTransform(ratio, 0, 0, ratio, 0, 0);
    return { width, height };
}

function draw(state) {
    const { width, height } = syncSize(state);
    const ctx = state.context;
    const colours = state.colours;

    const now = performance.now();
    const dt = state.lastFrameAt > 0 ? Math.min((now - state.lastFrameAt) / 1000, 0.1) : 0;
    state.lastFrameAt = now;

    // Eased auto-follow: glide the view toward the follow target a fraction per frame.
    if (state.followTarget !== null) {
        const span = state.viewEnd - state.viewStart;
        const delta = state.followTarget - state.viewStart;
        if (Math.abs(delta) < 0.01) {
            state.viewStart = state.followTarget;
            state.followTarget = null;
        } else {
            state.viewStart += delta * Math.min(1, dt * 6);
        }
        state.viewEnd = state.viewStart + span;
        clampView(state);
    }

    ctx.clearRect(0, 0, width, height);

    // Each strip only exists when there is something to draw in it, so a song with no sections and
    // no tempo map keeps the whole box for notes and chords exactly as before.
    const sections = state.model && state.model.sections ? state.model.sections : [];
    const sectionLaneHeight = sections.length > 0 ? SECTION_LANE_PX : 0;
    state.sectionLaneHeight = sectionLaneHeight;
    const tempoTop = sectionLaneHeight > 0 ? sectionLaneHeight + LANE_GAP_PX : 0;

    const tempoPoints = state.model && state.model.tempo ? state.model.tempo : [];
    const hasTempo = tempoPoints.length > 1;
    const tempoLaneHeight = hasTempo ? Math.min(TEMPO_LANE_PX, height * 0.25) : 0;
    const bodyTop = tempoTop + (hasTempo ? tempoLaneHeight + LANE_GAP_PX : 0);
    const bodyHeight = Math.max(height - bodyTop, 1);

    const noteLaneHeight = Math.max((bodyHeight - LANE_GAP_PX) * NOTE_LANE_FRACTION, 1);
    const chordLaneTop = noteLaneHeight + LANE_GAP_PX;
    const chordLaneHeight = Math.max(bodyHeight - chordLaneTop, 1);

    if (sectionLaneHeight > 0) {
        drawSections(state, ctx, width, sectionLaneHeight, sections);
    }
    if (hasTempo) {
        ctx.save();
        ctx.translate(0, tempoTop);
        drawTempo(state, ctx, width, tempoLaneHeight, tempoPoints);
        ctx.restore();
    }

    // The note and chord lanes are still drawn in their own coordinates and shifted as a block, so
    // reserving the strip above needed no change to any of the drawing below.
    ctx.save();
    ctx.translate(0, bodyTop);

    ctx.fillStyle = colours.lane;
    ctx.fillRect(0, 0, width, noteLaneHeight);
    ctx.fillRect(0, chordLaneTop, width, chordLaneHeight);

    ctx.strokeStyle = colours.border;
    ctx.lineWidth = 1;
    ctx.strokeRect(0.5, 0.5, width - 1, noteLaneHeight - 1);
    ctx.strokeRect(0.5, chordLaneTop + 0.5, width - 1, chordLaneHeight - 1);

    drawWaveform(state, ctx, width, noteLaneHeight);

    const reveal = revealClock(state, now, width);
    let drawn = 0;
    if (state.model) {
        if (state.drop) {
            updateDrop(state, dt, width, noteLaneHeight);
            drawDrop(state, ctx);
            drawn += state.drop.bodies.length;
        } else {
            drawn += drawNotes(state, ctx, width, noteLaneHeight, reveal);
        }
        drawn += drawChords(state, ctx, width, chordLaneTop, chordLaneHeight, reveal);
        if (reveal && reveal.ink) {
            drawInkFront(state, ctx, reveal.inkX, noteLaneHeight + LANE_GAP_PX + chordLaneHeight);
        }
        updateParticles(state, dt, noteLaneHeight);
        drawParticles(state, ctx, width, noteLaneHeight);
    }

    ctx.restore();

    // Full height on purpose: one playhead crossing all three lanes ties the tempo reading to the
    // moment it belongs to.
    drawPlayhead(state, ctx, width, height);
    updateChip(state, reveal !== null);

    // Animations in flight (view glide, live particles, the end-of-song drop, the reveal) need the
    // next frame.
    if (state.followTarget !== null || state.particles.length > 0 || state.drop || state.reveal) {
        invalidate(state);
    }

    // View state is mirrored onto the element so it is inspectable in devtools and assertable from
    // the Playwright suite without reaching into module internals.
    const dataset = state.canvas.dataset;
    const viewStartText = state.viewStart.toFixed(3);
    if (state.viewStartText !== viewStartText) {
        state.viewStartText = viewStartText;
        dataset.viewStart = viewStartText;
    }
    const viewEndText = state.viewEnd.toFixed(3);
    if (state.viewEndText !== viewEndText) {
        state.viewEndText = viewEndText;
        dataset.viewEnd = viewEndText;
    }
    dataset.playhead = state.playhead.toFixed(3);
    const drawnText = String(drawn);
    if (state.drawnText !== drawnText) {
        state.drawnText = drawnText;
        dataset.drawn = drawnText;
    }
    // The opening note's server-decided pitch label. Published because what the first note is called
    // is the whole question when a recording arrives off-tune, and it is otherwise only visible as
    // painted pixels. The label is decided by VisualizationBuilder; this only mirrors it.
    const firstNote = state.model?.notes?.length > 0 ? (state.model.notes[0].pitchLabel ?? '') : '';
    if (state.firstNoteText !== firstNote) {
        state.firstNoteText = firstNote;
        dataset.firstNote = firstNote;
    }
    dataset.painted = '1';
}

/// The section ribbon: one band per section, tinted with the colour the server named for its mode,
/// captioned with the server's own label when it fits, the bare letter when only that fits, and
/// nothing when neither does. A narrow band loses its words rather than shrinking them.
function drawSections(state, ctx, width, laneHeight, sections) {
    const colours = state.colours;
    ctx.save();
    ctx.beginPath();
    ctx.rect(0, 0, width, laneHeight);
    ctx.clip();
    ctx.font = LABEL_FONT;
    ctx.textBaseline = 'middle';

    for (let index = 0; index < sections.length; index++) {
        const section = sections[index];
        if (section.endSec < state.viewStart || section.startSec > state.viewEnd) {
            continue;
        }
        const x = timeToX(state, section.startSec, width);
        const bandWidth = Math.max(timeToX(state, section.endSec, width) - x, 1);
        const colour = state.sectionColours[index] || colours.muted;

        ctx.fillStyle = colour;
        ctx.globalAlpha = 0.22;
        ctx.fillRect(x, 0, bandWidth, laneHeight);
        ctx.globalAlpha = 1;
        // A solid foot on each band, so the section's colour survives the low-alpha fill in both themes.
        ctx.fillRect(x, laneHeight - 3, bandWidth, 3);
        ctx.fillStyle = colours.border;
        ctx.fillRect(Math.round(x), 0, 1, laneHeight);

        // Captions stay on the visible part of a band, so a long section panned half off-screen
        // still says what it is.
        const left = Math.max(x, 0) + 6;
        const room = Math.min(x + bandWidth, width) - left - 4;
        const caption = ctx.measureText(section.label).width <= room ? section.label
            : ctx.measureText(section.letter).width <= room ? section.letter
            : '';
        if (caption) {
            ctx.fillStyle = colours.text;
            ctx.fillText(caption, left, (laneHeight - 3) / 2);
        }
    }
    ctx.restore();
}

/// The section covering `seconds`, or null. A song has a handful of sections, so a scan is fine.
function sectionAt(state, seconds) {
    const sections = state.model && state.model.sections ? state.model.sections : [];
    return sections.find(section => seconds >= section.startSec && seconds < section.endSec) || null;
}

/// The tempo line: higher is faster, on the same time axis as the notes below it.
///
/// Drawn as a step, not a smooth curve. Each measure carries one measured tempo that holds until the
/// next downbeat, so interpolating between them would draw a gradual accelerando the analysis never
/// claimed. The vertical jumps sit exactly where the tempo changed, which is the thing worth seeing.
///
/// The vertical scale spans only this song's own tempo range. An absolute 0-200 scale would flatten
/// a performance drifting 96-104 BPM into a straight line and hide every real change.
function drawTempo(state, ctx, width, laneHeight, points) {
    const colours = state.colours;

    ctx.fillStyle = colours.lane;
    ctx.fillRect(0, 0, width, laneHeight);
    ctx.strokeStyle = colours.border;
    ctx.lineWidth = 1;
    ctx.strokeRect(0.5, 0.5, width - 1, laneHeight - 1);

    let min = Infinity;
    let max = -Infinity;
    for (const point of points) {
        min = Math.min(min, point.bpm);
        max = Math.max(max, point.bpm);
    }

    const usable = Math.max(laneHeight - (TEMPO_LANE_PAD_PX * 2), 1);
    const range = max - min;
    // A steady song has no range to scale against. Centring it draws the flat line it has earned,
    // instead of dividing by zero or magnifying rounding noise into a mountain range.
    const yFor = (bpm) => (range < 0.05
        ? TEMPO_LANE_PAD_PX + (usable / 2)
        : TEMPO_LANE_PAD_PX + (usable * (1 - ((bpm - min) / range))));

    const span = Math.max(state.viewEnd - state.viewStart, 1e-6);
    const xFor = (seconds) => ((seconds - state.viewStart) / span) * width;
    const songEnd = state.model.durationSec > 0
        ? state.model.durationSec
        : points[points.length - 1].startSec;

    ctx.save();
    ctx.beginPath();
    ctx.rect(1, 1, width - 2, laneHeight - 2);
    ctx.clip();

    ctx.beginPath();
    for (let i = 0; i < points.length; i++) {
        const y = yFor(points[i].bpm);
        const from = xFor(points[i].startSec);
        const to = xFor(i + 1 < points.length ? points[i + 1].startSec : songEnd);
        if (i === 0) {
            ctx.moveTo(from, y);
        } else {
            ctx.lineTo(from, y); // the vertical step into this measure's tempo
        }
        ctx.lineTo(to, y);
    }
    ctx.strokeStyle = colours.tempoLine;
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.stroke();

    // A dot on each measure that changed — the moments the step line exists to show.
    ctx.fillStyle = colours.tempoChange;
    for (const point of points) {
        if (!point.changed) {
            continue;
        }
        const x = xFor(point.startSec);
        if (x < -4 || x > width + 4) {
            continue;
        }
        ctx.beginPath();
        ctx.arc(x, yFor(point.bpm), 3, 0, Math.PI * 2);
        ctx.fill();
    }
    ctx.restore();

    // The two ends of the scale, so the line's height means something without a full axis.
    ctx.fillStyle = colours.muted;
    ctx.font = LABEL_FONT;
    ctx.textBaseline = 'top';
    ctx.fillText(String(Math.round(max)), 5, 3);
    ctx.textBaseline = 'bottom';
    ctx.fillText(Math.round(min) + ' BPM', 5, laneHeight - 3);
}

/// Where the reveal is, or null when none is running (and the moment it finishes, marks it done).
/// `inkX` is the colour front's pixel position; notes left of it are in their role colours.
function revealClock(state, now, width) {
    const reveal = state.reveal;
    if (!reveal) {
        return null;
    }
    const t = (now - reveal.start) / 1000;
    const end = reveal.ink ? REVEAL.inkStart + REVEAL.inkSweep : REVEAL.noteSpread + REVEAL.noteRise + 0.05;
    if (t >= end) {
        state.reveal = null;
        state.canvas.dataset.reveal = 'done';
        return null;
    }
    const inkX = reveal.ink ? clamp((t - REVEAL.inkStart) / REVEAL.inkSweep, 0, 1) * width : width;
    return { t, ink: reveal.ink, inkX };
}

/// 0..1 progress of one item whose turn comes `delay` seconds in and lasts `span`.
function revealProgress(t, delay, span) {
    return clamp((t - delay) / span, 0, 1);
}

/// A lightly underdamped spring from 0 to 1: it overshoots its row a little and settles, which is
/// what makes the notes read as landing rather than fading in.
function spring(progress) {
    return progress >= 1 ? 1 : 1 - (Math.exp(-5 * progress) * Math.cos(9 * progress));
}

/// The ink front: a soft band of the key colour where the verdict is being painted on.
function drawInkFront(state, ctx, x, height) {
    if (x <= 0 || x >= Math.max(state.canvas.clientWidth, 1)) {
        return;
    }
    const band = 18;
    const gradient = ctx.createLinearGradient(x - band, 0, x + 2, 0);
    gradient.addColorStop(0, 'rgba(0,0,0,0)');
    gradient.addColorStop(1, state.colours.key);
    ctx.globalAlpha = 0.45;
    ctx.fillStyle = gradient;
    ctx.fillRect(x - band, 0, band + 2, height);
    ctx.globalAlpha = 1;
}

function drawNotes(state, ctx, width, laneHeight, reveal) {
    const model = state.model;
    const pitchSpan = Math.max(model.maxPitch - model.minPitch + 1, 1);
    const rowHeight = laneHeight / pitchSpan;
    const capsuleHeight = Math.max(rowHeight - 1, 3);
    const showLabels = rowHeight >= MIN_LABEL_ROW_HEIGHT_PX && !reveal;
    // How loud the vocal stem is right now, when the mixer meters it: the sounding note's halo grows
    // and brightens with it, so the glow follows the singer rather than a clock.
    const voice = state.reducedMotion ? 0 : (state.stems.vocal ?? 0);
    const middle = (laneHeight - capsuleHeight) / 2;

    if (showLabels) {
        ctx.font = LABEL_FONT;
        ctx.textBaseline = 'middle';
    }

    let drawn = 0;
    const first = firstIndexFrom(model.notes, state.viewStart - state.maxNoteDuration);
    for (let index = first; index < model.notes.length; index++) {
        const note = model.notes[index];
        if (note.startSec > state.viewEnd) {
            break; // virtualized: off-screen notes cost nothing
        }
        const endSec = note.startSec + note.durationSec;
        if (endSec < state.viewStart) {
            continue;
        }

        const x = timeToX(state, note.startSec, width);
        const capsuleWidth = Math.max(timeToX(state, endSec, width) - x, 2);
        let top = (model.maxPitch - note.midiPitch) * rowHeight;

        // While the "why this mode?" readout is open, the notes it cites stay at full strength and
        // everything else steps back. Which notes those are was decided server-side (note.evidence).
        const faded = state.highlightEvidence && !note.evidence;
        let alpha = faded ? 0.3 : 1;
        let fill = colourForRole(state, note.role);
        if (reveal) {
            // Every note rises from the lane's middle to its own row, in the order it was sung.
            const progress = revealProgress(reveal.t, REVEAL.noteSpread * clamp(x / width, 0, 1), REVEAL.noteRise);
            top = middle + ((top - middle) * spring(progress));
            alpha *= Math.min(1, progress * 3);
            // Until the ink front reaches it, a take's note is just a sung pitch, not yet a verdict.
            if (reveal.ink && x > reveal.inkX) {
                fill = state.colours.muted;
            }
        }

        // Halo when the playhead is on this note, OR when the vocal overlay is on and the note
        // has already played (so the user sees the strip of synth notes they just heard).
        const sounding = state.playhead >= note.startSec && state.playhead < endSec;
        const haloed = sounding
            || (state.overlay.vocal && note.startSec <= state.playhead && state.playhead - note.startSec < 0.8);
        ctx.fillStyle = fill;
        if (haloed && !reveal) {
            // A soft halo behind the sounding note: same colour, low alpha, slightly larger — and
            // larger and brighter again with the vocal stem's level while the note is sounding.
            const lift = sounding ? voice : 0;
            const pad = 2 + (lift * 5);
            ctx.globalAlpha = 0.28 + (lift * 0.4);
            roundedRect(ctx, x - pad, top - pad, capsuleWidth + (pad * 2), capsuleHeight + (pad * 2), Math.min(5 + pad, (capsuleHeight + (pad * 2)) / 2));
            ctx.fill();
        }
        ctx.globalAlpha = alpha;
        roundedRect(ctx, x, top, capsuleWidth, capsuleHeight, Math.min(3, capsuleHeight / 2));
        ctx.fill();
        ctx.globalAlpha = 1;
        if (state.highlightEvidence && note.evidence) {
            ctx.strokeStyle = state.colours.text;
            ctx.lineWidth = 2;
            roundedRect(ctx, x - 1.5, top - 1.5, capsuleWidth + 3, capsuleHeight + 3, Math.min(4, (capsuleHeight + 3) / 2));
            ctx.stroke();
        }
        drawn++;

        if (showLabels && capsuleWidth >= MIN_LABEL_WIDTH_PX && !faded) {
            ctx.fillStyle = state.colours.text;
            ctx.fillText(note.label, x + 3, top + (capsuleHeight / 2));
        }
    }
    return drawn;
}

function colourForRole(state, role) {
    return state.colours.roles[role] || state.colours.roles.Outside;
}

/// Spawns particle bursts for notes the playhead just crossed, and ages the pool. Particles live
/// in time/pitch space so panning and zooming move them with the notes. Capped hard so a dense
/// passage cannot drop the frame rate; skipped entirely under prefers-reduced-motion.
function updateParticles(state, dt, laneHeight) {
    if (state.reducedMotion) {
        state.lastPlayhead = state.playhead;
        return;
    }
    const model = state.model;
    const advanced = state.playhead - state.lastPlayhead;
    if (advanced > 0 && advanced < 0.5 && state.particles.length < 200) {
        const first = firstIndexFrom(model.notes, state.lastPlayhead);
        for (let index = first; index < model.notes.length; index++) {
            const note = model.notes[index];
            if (note.startSec > state.playhead) {
                break;
            }
            for (let i = 0; i < 5 && state.particles.length < 240; i++) {
                state.particles.push({
                    t: note.startSec,
                    midi: note.midiPitch,
                    vt: 0.06 * ((i % 3) - 1),
                    vMidi: 2.2 * (((i * 37) % 100) / 100 - 0.5),
                    age: 0,
                    life: 0.55,
                    role: note.role,
                });
            }
        }

        // Chord-change bursts: a taller column pop when the playhead crosses a chord boundary,
        // spread across the lane's pitch range so the change reads at any zoom.
        const firstChord = firstIndexFrom(model.chords, state.lastPlayhead);
        for (let index = firstChord; index < model.chords.length; index++) {
            const chord = model.chords[index];
            if (chord.startSec > state.playhead) {
                break;
            }
            for (let i = 0; i < 10 && state.particles.length < 240; i++) {
                state.particles.push({
                    t: chord.startSec,
                    midi: model.minPitch + ((model.maxPitch - model.minPitch) * (i / 9)),
                    vt: 0.04 * ((i % 3) - 1),
                    vMidi: 3 * (((i * 53) % 100) / 100 - 0.5),
                    age: 0,
                    life: 0.7,
                    role: 'ChordTone',
                });
            }
        }
    }
    state.lastPlayhead = state.playhead;

    for (let i = state.particles.length - 1; i >= 0; i--) {
        const particle = state.particles[i];
        particle.age += dt;
        if (particle.age >= particle.life) {
            state.particles.splice(i, 1);
        }
    }
}

function drawParticles(state, ctx, width, laneHeight) {
    if (state.particles.length === 0) {
        return;
    }
    const model = state.model;
    const pitchSpan = Math.max(model.maxPitch - model.minPitch + 1, 1);
    const rowHeight = laneHeight / pitchSpan;

    for (const particle of state.particles) {
        const progress = particle.age / particle.life;
        const t = particle.t + (particle.vt * particle.age);
        const midi = particle.midi + (particle.vMidi * particle.age);
        if (t < state.viewStart || t > state.viewEnd) {
            continue;
        }
        const x = timeToX(state, t, width);
        const y = ((model.maxPitch - midi) + 0.5) * rowHeight;
        if (y < 0 || y > laneHeight) {
            continue;
        }
        ctx.globalAlpha = (1 - progress) * 0.7;
        ctx.fillStyle = colourForRole(state, particle.role);
        ctx.beginPath();
        ctx.arc(x, y, 2.2 * (1 - (progress * 0.5)), 0, Math.PI * 2);
        ctx.fill();
    }
    ctx.globalAlpha = 1;
}

function drawChords(state, ctx, width, laneTop, laneHeight, reveal) {
    const model = state.model;
    ctx.font = LABEL_FONT;
    ctx.textBaseline = 'top';
    // The backing stem's level brightens the chord under the playhead, the way the vocal's lifts
    // the sounding note: the block pulses with the band that is playing it.
    const band = state.reducedMotion ? 0 : (state.stems.backing ?? 0);

    let drawn = 0;
    const first = firstIndexFrom(model.chords, state.viewStart - state.maxChordDuration);
    for (let index = first; index < model.chords.length; index++) {
        const chord = model.chords[index];
        if (chord.startSec > state.viewEnd) {
            break;
        }
        if (chord.endSec < state.viewStart) {
            continue;
        }

        const x = timeToX(state, chord.startSec, width);
        const blockWidth = Math.max(timeToX(state, chord.endSec, width) - x, 2);
        const selected = index === state.selection;

        // In the reveal each block drops in from above, bar by bar behind the notes.
        const progress = reveal
            ? revealProgress(reveal.t, REVEAL.chordDelay + (REVEAL.chordSpread * clamp(x / width, 0, 1)), REVEAL.chordDrop)
            : 1;
        const blockHeight = (laneHeight - 6) * spring(progress);
        ctx.globalAlpha = Math.min(1, progress * 2);
        ctx.fillStyle = selected ? state.colours.chordSelected : state.colours.chordBlock;
        roundedRect(ctx, x + 1, laneTop + 3, Math.max(blockWidth - 2, 1), Math.max(blockHeight, 1), 3);
        ctx.fill();
        if (!selected && band > 0.02 && state.playhead >= chord.startSec && state.playhead < chord.endSec) {
            ctx.globalAlpha = band * 0.35;
            ctx.fillStyle = state.colours.chordSelected;
            ctx.fill();
        }
        ctx.globalAlpha = 1;
        drawn++;

        if (blockWidth < 26 || progress < 1) {
            continue;
        }
        ctx.fillStyle = selected ? state.colours.lane : state.colours.text;
        ctx.fillText(chord.symbol, x + 5, laneTop + 6);
        if (blockWidth >= 90) {
            ctx.fillStyle = selected ? state.colours.lane : state.colours.muted;
            const tag = chord.modeTag ? `m${chord.measureNumber} · ${chord.modeTag}` : `m${chord.measureNumber}`;
            ctx.fillText(tag, x + 5, laneTop + 22);
        }
    }
    return drawn;
}

/// The real vocal waveform behind the note capsules, mirrored around the lane's middle at low
/// alpha — pure context, never louder than the notes. Peaks come from mixer.js after stem decode.
function drawWaveform(state, ctx, width, laneHeight) {
    const wave = state.waveform;
    if (!wave || wave.peaks.length === 0) {
        return;
    }
    const middle = laneHeight / 2;
    const amp = laneHeight * 0.46;
    const columns = wave.peaks.length / 2;
    const secondsPerColumn = wave.durationSec / columns;
    const first = Math.max(0, Math.floor(state.viewStart / secondsPerColumn));
    const last = Math.min(columns - 1, Math.ceil(state.viewEnd / secondsPerColumn));

    ctx.globalAlpha = 0.25;
    ctx.fillStyle = state.colours.chordSelected;
    ctx.beginPath();
    for (let column = first; column <= last; column++) {
        const x = timeToX(state, column * secondsPerColumn, width);
        const columnWidth = Math.max(timeToX(state, (column + 1) * secondsPerColumn, width) - x, 1);
        const low = wave.peaks[column * 2];
        const high = wave.peaks[(column * 2) + 1];
        ctx.rect(x, middle - (high * amp), columnWidth, Math.max((high - low) * amp, 1));
    }
    ctx.fill();
    ctx.globalAlpha = 1;
}

/// The glass chip over the canvas: the chord, the sung note and the section at the playhead, all as
/// the server labelled them. Written straight into the DOM from here, so following the playhead costs
/// no Blazor render, and only when one of the three actually changes. Hidden during the reveal and
/// wherever there is nothing under the playhead.
function updateChip(state, revealing) {
    const chip = state.chip;
    if (!chip) {
        return;
    }
    const model = state.model;
    const t = state.playhead;
    let chord = null;
    let note = null;
    if (model && !revealing) {
        const chordIndex = firstIndexFrom(model.chords, t + 1e-6) - 1;
        if (chordIndex >= 0 && model.chords[chordIndex].endSec > t) {
            chord = model.chords[chordIndex];
        }
        for (let index = firstIndexFrom(model.notes, t - state.maxNoteDuration); index < model.notes.length; index++) {
            const candidate = model.notes[index];
            if (candidate.startSec > t) {
                break;
            }
            if (candidate.startSec + candidate.durationSec > t) {
                note = candidate;
            }
        }
    }
    const section = model && !revealing ? sectionAt(state, t) : null;
    const key = `${chord?.symbol ?? ''}|${note?.label ?? ''}|${section?.label ?? ''}`;
    if (key === state.chipKey) {
        return;
    }
    state.chipKey = key;
    const set = (part, text) => {
        const element = chip.querySelector(`[data-part="${part}"]`);
        if (element) {
            element.textContent = text;
            element.hidden = text === '';
        }
    };
    set('chord', chord?.symbol ?? '');
    set('note', note?.label ?? '');
    set('section', section?.label ?? '');
    chip.dataset.nowChord = chord?.symbol ?? '';
    chip.dataset.nowNote = note?.pitchLabel ?? '';
    chip.dataset.empty = chord || note || section ? '0' : '1';
}

/// End-of-song easter egg: the visible note capsules become falling, bouncing bodies for a few
/// seconds, then fade. Purely decorative; any seek, play, or new model clears it instantly.
function updateDrop(state, dt, width, laneHeight) {
    const drop = state.drop;
    drop.age += dt;
    for (const body of drop.bodies) {
        body.vy += 900 * dt; // gravity, px/s²
        body.y += body.vy * dt;
        body.x += body.vx * dt;
        body.spin += body.vSpin * dt;
        const floor = laneHeight - body.h;
        if (body.y > floor) {
            body.y = floor;
            body.vy = -body.vy * 0.45; // lossy bounce settles quickly
            body.vx *= 0.8;
        }
    }
    if (drop.age > 4.5) {
        state.drop = null; // fully faded; normal drawing resumes
    }
}

function drawDrop(state, ctx) {
    const drop = state.drop;
    const fade = clamp(1 - ((drop.age - 3) / 1.5), 0, 1);
    ctx.globalAlpha = fade;
    for (const body of drop.bodies) {
        ctx.save();
        ctx.translate(body.x + (body.w / 2), body.y + (body.h / 2));
        ctx.rotate(body.spin);
        ctx.fillStyle = colourForRole(state, body.role);
        roundedRect(ctx, -body.w / 2, -body.h / 2, body.w, body.h, Math.min(3, body.h / 2));
        ctx.fill();
        ctx.restore();
    }
    ctx.globalAlpha = 1;
}

function drawPlayhead(state, ctx, width, height) {
    if (state.playhead < state.viewStart || state.playhead > state.viewEnd) {
        return;
    }
    const x = Math.round(timeToX(state, state.playhead, width)) + 0.5;

    // Motion trail: a short gradient wake behind the line, brighter when the music is louder.
    // Skipped under reduced motion along with the rest of the decoration.
    if (!state.reducedMotion && state.level > 0.01) {
        const trailWidth = 14 + (state.level * 60);
        const gradient = ctx.createLinearGradient(x - trailWidth, 0, x, 0);
        gradient.addColorStop(0, 'rgba(0,0,0,0)');
        gradient.addColorStop(1, state.colours.playhead);
        ctx.globalAlpha = 0.10 + (state.level * 0.25);
        ctx.fillStyle = gradient;
        ctx.fillRect(x - trailWidth, 0, trailWidth, height);
        ctx.globalAlpha = 1;
    }

    ctx.strokeStyle = state.colours.playhead;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
}

function roundedRect(ctx, x, y, width, height, radius) {
    const r = Math.max(Math.min(radius, width / 2, height / 2), 0);
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + width, y, x + width, y + height, r);
    ctx.arcTo(x + width, y + height, x, y + height, r);
    ctx.arcTo(x, y + height, x, y, r);
    ctx.arcTo(x, y, x + width, y, r);
    ctx.closePath();
}

// ---- interaction ----

function onWheel(state, event) {
    event.preventDefault();
    state.followTarget = null;
    state.followSuspendedUntil = performance.now() + 4000;
    const rect = state.canvas.getBoundingClientRect();
    const fraction = rect.width <= 0 ? 0.5 : clamp((event.clientX - rect.left) / rect.width, 0, 1);
    const anchor = state.viewStart + (fraction * (state.viewEnd - state.viewStart));
    // Negative deltaY (scroll up) shrinks the span, i.e. zooms in, anchored under the cursor.
    const span = (state.viewEnd - state.viewStart) * Math.exp(event.deltaY * 0.0015);
    const clamped = clamp(span, MIN_VIEW_SECONDS, totalSeconds(state));
    state.viewStart = anchor - (fraction * clamped);
    state.viewEnd = state.viewStart + clamped;
    clampView(state);
    invalidate(state);
}

function onPointerDown(state, event) {
    state.drag = { pointerId: event.pointerId, startX: event.clientX, lastX: event.clientX, moved: 0 };
    // The user is taking the wheel: stop auto-follow fighting their pan for a few seconds.
    state.followTarget = null;
    state.followSuspendedUntil = performance.now() + 4000;
    state.canvas.setPointerCapture(event.pointerId);
}

function onPointerMove(state, event) {
    const drag = state.drag;
    if (!drag || drag.pointerId !== event.pointerId) {
        return;
    }
    const rect = state.canvas.getBoundingClientRect();
    const deltaX = event.clientX - drag.lastX;
    drag.lastX = event.clientX;
    drag.moved += Math.abs(deltaX);
    if (rect.width <= 0) {
        return;
    }
    const secondsPerPixel = (state.viewEnd - state.viewStart) / rect.width;
    state.viewStart -= deltaX * secondsPerPixel;
    state.viewEnd -= deltaX * secondsPerPixel;
    clampView(state);
    invalidate(state);
}

function onPointerUp(state, event) {
    const drag = state.drag;
    state.drag = null;
    if (state.canvas.hasPointerCapture(event.pointerId)) {
        state.canvas.releasePointerCapture(event.pointerId);
    }
    // pointercancel means the browser took the gesture (touch-action pan-y vertical scroll) —
    // never a click, so never a seek. A real drag is a pan, not a seek, either.
    if (event.type === 'pointercancel' || !drag || drag.moved > DRAG_THRESHOLD_PX || !state.dotNet) {
        return;
    }
    // A click on the section ribbon jumps to the start of that section rather than to the exact
    // spot under the pointer: the band is a "go to the chorus" control, not a scrubber.
    const rect = state.canvas.getBoundingClientRect();
    const clickedTime = xToTime(state, event.clientX);
    const section = event.clientY - rect.top < state.sectionLaneHeight ? sectionAt(state, clickedTime) : null;
    const seekTime = section ? section.startSec : clickedTime;
    state.playhead = seekTime;
    invalidate(state);
    state.dotNet.invokeMethodAsync('OnCanvasSeek', seekTime);
}

// ---- exports ----

/// `chip` is the optional glass readout element laid over the canvas (see updateChip).
export function init(canvas, dotNetRef, chip) {
    dispose(canvas);

    const state = {
        canvas,
        context: canvas.getContext('2d'),
        dotNet: dotNetRef,
        chip: chip ?? null,
        chipKey: null,
        reveal: null,
        stems: {},
        model: null,
        maxNoteDuration: 0,
        maxChordDuration: 0,
        viewStart: 0,
        viewEnd: FALLBACK_DURATION_SECONDS,
        playhead: 0,
        selection: null,
        particles: [],
        waveform: null,
        level: 0,
        drop: null,
        lastPlayhead: 0,
        lastFrameAt: 0,
        followTarget: null,
        followSuspendedUntil: 0,
        reducedMotion: !prefs.allowsMotion(),
        overlay: { vocal: true, backing: false },
        colours: readColours(canvas),
        sectionColours: [],
        sectionLaneHeight: 0,
        highlightEvidence: false,
        frame: null,
        drag: null,
        viewStartText: null,
        viewEndText: null,
        drawnText: null,
    };

    state.handlers = {
        wheel: event => onWheel(state, event),
        pointerdown: event => onPointerDown(state, event),
        pointermove: event => onPointerMove(state, event),
        pointerup: event => onPointerUp(state, event),
        pointercancel: event => onPointerUp(state, event),
    };
    canvas.addEventListener('wheel', state.handlers.wheel, { passive: false });
    canvas.addEventListener('pointerdown', state.handlers.pointerdown);
    canvas.addEventListener('pointermove', state.handlers.pointermove);
    canvas.addEventListener('pointerup', state.handlers.pointerup);
    canvas.addEventListener('pointercancel', state.handlers.pointercancel);

    state.resizeObserver = new ResizeObserver(() => invalidate(state));
    state.resizeObserver.observe(canvas);

    // Re-read the palette when the OS theme flips so the canvas follows light/dark like the rest of the UI.
    state.scheme = window.matchMedia('(prefers-color-scheme: dark)');
    state.onSchemeChange = () => {
        state.colours = readColours(canvas);
        resolveSectionColours(state);
        invalidate(state);
    };
    state.scheme.addEventListener('change', state.onSchemeChange);

    // The manual theme toggle flips data-theme on <html>; re-read the palette when it does.
    state.themeObserver = new MutationObserver(state.onSchemeChange);
    state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    states.set(canvas, state);
    invalidate(state);
}

export function setModel(canvas, payload) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.model = payload;
    state.maxNoteDuration = 0;
    state.maxChordDuration = 0;
    state.drop = null;
    state.reveal = null;
    state.chipKey = null;
    state.waveform = null; // a new job's waveform arrives from mixer.js after its stems decode
    if (payload) {
        for (const note of payload.notes) {
            note.label = `${note.pitchLabel} ${note.degreeLabel}`;
            state.maxNoteDuration = Math.max(state.maxNoteDuration, note.durationSec);
        }
        for (const chord of payload.chords) {
            state.maxChordDuration = Math.max(state.maxChordDuration, chord.endSec - chord.startSec);
        }
    }
    resolveSectionColours(state);

    // Published once per model rather than per frame: which sections exist and how many notes carry
    // the mode's evidence are facts about the payload, not about the view.
    const sections = payload && payload.sections ? payload.sections : [];
    canvas.dataset.sectionCount = String(sections.length);
    canvas.dataset.sectionLetters = sections.map(section => section.letter).join('');
    canvas.dataset.evidenceNotes = String(payload ? payload.notes.filter(note => note.evidence).length : 0);

    state.viewStart = 0;
    state.viewEnd = totalSeconds(state);
    clampView(state);
    invalidate(state);
}

/// `stems` carries the mixer's per-stem levels ({ vocal, backing }, each 0..1) when it meters them;
/// the sounding note and chord glow with them. Absent, both glows hold still.
export function setPlayhead(canvas, seconds, level = 0, stems = null) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.playhead = seconds;
    state.level = level; // drives the playhead's motion trail; 0 when the caller has no analyser
    state.stems = stems ?? {};

    // Auto-follow: when the playhead drifts out of the comfortable middle of the view, ease the
    // view so the playhead sits at 30% — unless the user panned recently (their view wins).
    const span = state.viewEnd - state.viewStart;
    if (span > 0 && performance.now() > state.followSuspendedUntil) {
        const position = (seconds - state.viewStart) / span;
        if (position > 0.78 || position < 0) {
            const target = clamp(seconds - (span * 0.3), 0, Math.max(totalSeconds(state) - span, 0));
            state.followTarget = state.reducedMotion ? null : target;
            if (state.reducedMotion) {
                state.viewStart = target;
                state.viewEnd = target + span;
            }
        }
    }
    invalidate(state);
}

/// Plays the result in: every note rises from the lane's middle to its own row in the order it was
/// sung, and the chord blocks drop in behind them. With `ink` (a hum take, whose melody is the
/// user's own) the notes land uncoloured and a front in the key colour then sweeps across painting
/// each one with the role the analysis gave it — the verdict arriving over the take. Everything drawn
/// is the payload; only its arrival is animated. Skipped outright when effects are off. Mirrored to
/// data-reveal ('running', 'inking', then 'done').
export function reveal(canvas, ink) {
    const state = states.get(canvas);
    if (!state || !state.model) {
        return;
    }
    if (state.reducedMotion || state.model.notes.length === 0) {
        canvas.dataset.reveal = 'done';
        return;
    }
    state.reveal = { start: performance.now(), ink: !!ink };
    canvas.dataset.reveal = ink ? 'inking' : 'running';
    invalidate(state);
}

export function setSelection(canvas, index) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.selection = index;
    invalidate(state);
}

/// Rings the notes the server marked as evidence for the song's mode and fades the rest. Mirrored
/// to data-evidence-highlight so a browser test can assert the state without reading pixels.
export function setEvidenceHighlight(canvas, enabled) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.highlightEvidence = !!enabled;
    canvas.dataset.evidenceHighlight = state.highlightEvidence ? '1' : '0';
    invalidate(state);
}

/// Toggle a note-overlay (visual only — the audio layer is owned by mixer.js). When `vocal` is true,
/// notes near the playhead get a soft halo so the user sees "what you're hearing right now".
export function setOverlay(canvas, source, enabled) {
    const state = states.get(canvas);
    if (!state || !(source in state.overlay)) {
        return;
    }
    state.overlay[source] = enabled;
    invalidate(state);
}

/// Hands the canvas the decoded vocal waveform as [min,max] peak pairs. Called by mixer.js once
/// the stems are decoded; null/empty clears the lane.
export function setWaveform(canvas, peaks, durationSec) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.waveform = peaks && peaks.length > 0 && durationSec > 0 ? { peaks, durationSec } : null;
    invalidate(state);
}

/// End-of-song physics drop: the currently visible note capsules fall and bounce, then fade.
/// No-op under reduced motion or without a model.
export function dropNotes(canvas) {
    const state = states.get(canvas);
    if (!state || !state.model || state.reducedMotion || state.drop) {
        return;
    }
    const width = Math.max(state.canvas.clientWidth, 1);
    const laneHeight = Math.max((Math.max(state.canvas.clientHeight, 1) - LANE_GAP_PX) * NOTE_LANE_FRACTION, 1);
    const model = state.model;
    const pitchSpan = Math.max(model.maxPitch - model.minPitch + 1, 1);
    const rowHeight = laneHeight / pitchSpan;

    const bodies = [];
    const first = firstIndexFrom(model.notes, state.viewStart - state.maxNoteDuration);
    for (let index = first; index < model.notes.length && bodies.length < 250; index++) {
        const note = model.notes[index];
        if (note.startSec > state.viewEnd) {
            break;
        }
        const x = timeToX(state, note.startSec, width);
        const w = Math.max(timeToX(state, note.startSec + note.durationSec, width) - x, 2);
        bodies.push({
            x,
            y: (model.maxPitch - note.midiPitch) * rowHeight,
            w,
            h: Math.max(rowHeight - 1, 3),
            vx: ((index % 7) - 3) * 12,
            vy: -40 - ((index % 5) * 25),
            spin: 0,
            vSpin: ((index % 9) - 4) * 0.4,
            role: note.role,
        });
    }
    if (bodies.length > 0) {
        state.drop = { bodies, age: 0 };
        invalidate(state);
    }
}

/// Clears transient effects (drop bodies, particles) — playback and seeks call this so the lane
/// snaps back to the truthful picture instantly. A reveal is left to finish: it draws the true
/// picture throughout, and the mixer's first seek lands while it may still be running.
export function resetFx(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.drop = null;
    state.particles = [];
    invalidate(state);
}

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.frame !== null) {
        cancelAnimationFrame(state.frame);
    }
    for (const [name, handler] of Object.entries(state.handlers)) {
        canvas.removeEventListener(name, handler);
    }
    state.resizeObserver.disconnect();
    state.scheme.removeEventListener('change', state.onSchemeChange);
    state.themeObserver.disconnect();
    states.delete(canvas);
}
