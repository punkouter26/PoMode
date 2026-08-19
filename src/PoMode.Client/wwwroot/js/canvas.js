// Dual-lane analysis canvas (spec §7): note capsules on top, chord blocks below.
//
// Every musical decision — which colour class a note belongs to, its labels, its measure number —
// was already made server-side by VisualizationBuilder and arrives in the payload. This module only
// maps numbers to pixels. Keep it that way: no music theory in here.
//
// Drawing is virtualized (only items intersecting the visible time range are drawn) and coalesced
// into one requestAnimationFrame callback, so panning a five-minute track stays smooth.

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
const LANE_GAP_PX = 6;
const MIN_LABEL_WIDTH_PX = 38;
const MIN_LABEL_ROW_HEIGHT_PX = 9;
const MIN_VIEW_SECONDS = 0.5;
const DRAG_THRESHOLD_PX = 4;
const FALLBACK_DURATION_SECONDS = 10;

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
        playhead: read('--pm-playhead', '#e11d48'),
    };
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

    const noteLaneHeight = Math.max((height - LANE_GAP_PX) * NOTE_LANE_FRACTION, 1);
    const chordLaneTop = noteLaneHeight + LANE_GAP_PX;
    const chordLaneHeight = Math.max(height - chordLaneTop, 1);

    ctx.fillStyle = colours.lane;
    ctx.fillRect(0, 0, width, noteLaneHeight);
    ctx.fillRect(0, chordLaneTop, width, chordLaneHeight);

    ctx.strokeStyle = colours.border;
    ctx.lineWidth = 1;
    ctx.strokeRect(0.5, 0.5, width - 1, noteLaneHeight - 1);
    ctx.strokeRect(0.5, chordLaneTop + 0.5, width - 1, chordLaneHeight - 1);

    drawWaveform(state, ctx, width, noteLaneHeight);

    let drawn = 0;
    if (state.model) {
        if (state.drop) {
            updateDrop(state, dt, width, noteLaneHeight);
            drawDrop(state, ctx);
            drawn += state.drop.bodies.length;
        } else {
            drawn += drawNotes(state, ctx, width, noteLaneHeight);
        }
        drawn += drawChords(state, ctx, width, chordLaneTop, chordLaneHeight);
        drawLivePitch(state, ctx, width, noteLaneHeight);
        updateParticles(state, dt, noteLaneHeight);
        drawParticles(state, ctx, width, noteLaneHeight);
    }

    drawPlayhead(state, ctx, width, height);

    // Animations in flight (view glide, live particles, the end-of-song drop) need the next frame.
    if (state.followTarget !== null || state.particles.length > 0 || state.drop) {
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
    dataset.painted = '1';
}

function drawNotes(state, ctx, width, laneHeight) {
    const model = state.model;
    const pitchSpan = Math.max(model.maxPitch - model.minPitch + 1, 1);
    const rowHeight = laneHeight / pitchSpan;
    const capsuleHeight = Math.max(rowHeight - 1, 3);
    const showLabels = rowHeight >= MIN_LABEL_ROW_HEIGHT_PX;

    if (showLabels) {
        ctx.font = '10px system-ui, sans-serif';
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
        const top = (model.maxPitch - note.midiPitch) * rowHeight;

        // Halo when the playhead is on this note, OR when the vocal overlay is on and the note
        // has already played (so the user sees the strip of synth notes they just heard).
        const haloed = (state.playhead >= note.startSec && state.playhead < endSec)
            || (state.overlay.vocal && note.startSec <= state.playhead && state.playhead - note.startSec < 0.8);
        ctx.fillStyle = colourForRole(state, note.role);
        if (haloed) {
            // A soft halo behind the sounding note: same colour, low alpha, slightly larger.
            ctx.globalAlpha = 0.28;
            roundedRect(ctx, x - 2, top - 2, capsuleWidth + 4, capsuleHeight + 4, Math.min(5, (capsuleHeight + 4) / 2));
            ctx.fill();
            ctx.globalAlpha = 1;
        }
        roundedRect(ctx, x, top, capsuleWidth, capsuleHeight, Math.min(3, capsuleHeight / 2));
        ctx.fill();
        drawn++;

        if (showLabels && capsuleWidth >= MIN_LABEL_WIDTH_PX) {
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

/// The karaoke overlay: the singer's own pitch as small dots over the note lane. Pure pixels —
/// whether the singing is RIGHT is judged elsewhere; this just shows where the voice sits.
function drawLivePitch(state, ctx, width, laneHeight) {
    if (state.livePitch.length === 0) {
        return;
    }
    const model = state.model;
    const pitchSpan = Math.max(model.maxPitch - model.minPitch + 1, 1);
    const rowHeight = laneHeight / pitchSpan;

    ctx.fillStyle = state.colours.chordSelected;
    for (const point of state.livePitch) {
        if (point.t < state.viewStart || point.t > state.viewEnd) {
            continue;
        }
        const x = timeToX(state, point.t, width);
        const y = ((model.maxPitch - point.midi) + 0.5) * rowHeight;
        if (y < 0 || y > laneHeight) {
            continue;
        }
        ctx.beginPath();
        ctx.arc(x, y, Math.max(Math.min(rowHeight * 0.35, 3), 1.5), 0, Math.PI * 2);
        ctx.fill();
    }
}

function drawChords(state, ctx, width, laneTop, laneHeight) {
    const model = state.model;
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'top';

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

        ctx.fillStyle = selected ? state.colours.chordSelected : state.colours.chordBlock;
        roundedRect(ctx, x + 1, laneTop + 3, Math.max(blockWidth - 2, 1), laneHeight - 6, 3);
        ctx.fill();
        drawn++;

        if (blockWidth < 26) {
            continue;
        }
        ctx.fillStyle = selected ? state.colours.lane : state.colours.text;
        ctx.fillText(chord.symbol, x + 5, laneTop + 6);
        if (blockWidth >= 90) {
            ctx.fillStyle = selected ? state.colours.lane : state.colours.muted;
            const tag = chord.modeTag ? `m${chord.measureNumber} · ${chord.modeTag}` : `m${chord.measureNumber}`;
            ctx.fillText(tag, x + 5, laneTop + 20);
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
    const seekTime = xToTime(state, event.clientX);
    state.playhead = seekTime;
    invalidate(state);
    state.dotNet.invokeMethodAsync('OnCanvasSeek', seekTime);
}

// ---- exports ----

export function init(canvas, dotNetRef) {
    dispose(canvas);

    const state = {
        canvas,
        context: canvas.getContext('2d'),
        dotNet: dotNetRef,
        model: null,
        maxNoteDuration: 0,
        maxChordDuration: 0,
        viewStart: 0,
        viewEnd: FALLBACK_DURATION_SECONDS,
        playhead: 0,
        selection: null,
        livePitch: [],
        particles: [],
        waveform: null,
        level: 0,
        drop: null,
        lastPlayhead: 0,
        lastFrameAt: 0,
        followTarget: null,
        followSuspendedUntil: 0,
        reducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches,
        overlay: { vocal: true, backing: false },
        colours: readColours(canvas),
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
    state.viewStart = 0;
    state.viewEnd = totalSeconds(state);
    clampView(state);
    invalidate(state);
}

export function setPlayhead(canvas, seconds, level = 0) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.playhead = seconds;
    state.level = level; // drives the playhead's motion trail; 0 when the caller has no analyser

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

/// Appends one sung-pitch sample (song time, fractional MIDI). The trace is capped so an hour of
/// karaoke cannot grow memory without bound.
export function addLivePitch(canvas, seconds, midi) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.livePitch.push({ t: seconds, midi });
    if (state.livePitch.length > 4000) {
        state.livePitch.splice(0, state.livePitch.length - 4000);
    }
    invalidate(state);
}

export function clearLivePitch(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.livePitch = [];
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
/// snaps back to the truthful picture instantly.
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
