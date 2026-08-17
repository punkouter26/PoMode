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

    let drawn = 0;
    if (state.model) {
        drawn += drawNotes(state, ctx, width, noteLaneHeight);
        drawn += drawChords(state, ctx, width, chordLaneTop, chordLaneHeight);
    }

    drawPlayhead(state, ctx, width, height);

    // View state is mirrored onto the element so it is inspectable in devtools and assertable from
    // the Playwright suite without reaching into module internals.
    const dataset = state.canvas.dataset;
    dataset.viewStart = state.viewStart.toFixed(3);
    dataset.viewEnd = state.viewEnd.toFixed(3);
    dataset.playhead = state.playhead.toFixed(3);
    dataset.drawn = String(drawn);
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
    for (const note of model.notes) {
        const endSec = note.startSec + note.durationSec;
        if (endSec < state.viewStart || note.startSec > state.viewEnd) {
            continue; // virtualized: off-screen notes cost nothing
        }

        const x = timeToX(state, note.startSec, width);
        const capsuleWidth = Math.max(timeToX(state, endSec, width) - x, 2);
        const top = (model.maxPitch - note.midiPitch) * rowHeight;

        ctx.fillStyle = colourForRole(state, note.role);
        roundedRect(ctx, x, top, capsuleWidth, capsuleHeight, Math.min(3, capsuleHeight / 2));
        ctx.fill();
        drawn++;

        if (showLabels && capsuleWidth >= MIN_LABEL_WIDTH_PX) {
            ctx.fillStyle = state.colours.text;
            ctx.fillText(`${note.pitchLabel} ${note.degreeLabel}`, x + 3, top + (capsuleHeight / 2));
        }
    }
    return drawn;
}

function colourForRole(state, role) {
    return state.colours.roles[role] || state.colours.roles.Outside;
}

function drawChords(state, ctx, width, laneTop, laneHeight) {
    const model = state.model;
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'top';

    let drawn = 0;
    for (let index = 0; index < model.chords.length; index++) {
        const chord = model.chords[index];
        if (chord.endSec < state.viewStart || chord.startSec > state.viewEnd) {
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
        if (blockWidth >= 60) {
            ctx.fillStyle = selected ? state.colours.lane : state.colours.muted;
            const tag = chord.modeTag ? `m${chord.measureNumber} · ${chord.modeTag}` : `m${chord.measureNumber}`;
            ctx.fillText(tag, x + 5, laneTop + 20);
        }
    }
    return drawn;
}

function drawPlayhead(state, ctx, width, height) {
    if (state.playhead < state.viewStart || state.playhead > state.viewEnd) {
        return;
    }
    const x = Math.round(timeToX(state, state.playhead, width)) + 0.5;
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
    if (!drag || drag.moved > DRAG_THRESHOLD_PX || !state.dotNet) {
        return; // a drag is a pan, not a seek
    }
    state.dotNet.invokeMethodAsync('OnCanvasSeek', xToTime(state, event.clientX));
}

// ---- exports ----

export function init(canvas, dotNetRef) {
    dispose(canvas);

    const state = {
        canvas,
        context: canvas.getContext('2d'),
        dotNet: dotNetRef,
        model: null,
        viewStart: 0,
        viewEnd: FALLBACK_DURATION_SECONDS,
        playhead: 0,
        selection: null,
        colours: readColours(canvas),
        frame: null,
        drag: null,
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

    states.set(canvas, state);
    invalidate(state);
}

export function setModel(canvas, payload) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.model = payload;
    state.viewStart = 0;
    state.viewEnd = totalSeconds(state);
    clampView(state);
    invalidate(state);
}

export function setPlayhead(canvas, seconds) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.playhead = seconds;
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
    states.delete(canvas);
}
