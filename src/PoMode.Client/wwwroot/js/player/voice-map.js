// The singer's voice on a keyboard: the six classical ranges stacked above the keys, the stretch
// where the singing actually sits laid across them, its centre marked, and the strongest note's key
// lit — with a button (in the component) that plays that note, so "your strongest note is A3" is
// something you hear as well as read.
//
// All of it is the server's: the keyboard's span, every range, the percentile notes, the labels.
// This module lays MIDI numbers out as keys. Which keys are black is keyboard geometry, not theory.

import * as prefs from '../shell/prefs.js';
import { bus, lead } from './voices.js';

const states = new Map();

const BLACK = new Set([1, 3, 6, 8, 10]);
const ROW_PX = 16;
const ROW_GAP_PX = 2;
const KEYS_PX = 44;

let audio = null;

function readColours(canvas) {
    const style = getComputedStyle(canvas);
    const read = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    return {
        fg: read('--pm-fg', '#1a1a1a'),
        muted: read('--pm-fg-muted', '#4c4c57'),
        border: read('--pm-border', '#d9d9e0'),
        surface: read('--pm-surface-alt', '#ececf1'),
        white: read('--pm-bg', '#ffffff'),
        black: read('--pm-fg', '#1a1a1a'),
        key: read('--pm-key', '#6750a4'),
        keySoft: read('--pm-key-soft', 'rgba(103,80,164,0.14)'),
        keyGlow: read('--pm-key-glow', 'rgba(103,80,164,0.28)'),
        best: read('--pm-ok-edge', '#16a34a'),
    };
}

/// x position and width of every key, white keys side by side and black keys straddling the line
/// between their neighbours.
function keyLayout(map, width) {
    let whites = 0;
    for (let midi = map.fromMidi; midi <= map.toMidi; midi++) {
        if (!BLACK.has(midi % 12)) {
            whites++;
        }
    }
    const whiteWidth = width / Math.max(whites, 1);
    const keys = new Map();
    let index = 0;
    for (let midi = map.fromMidi; midi <= map.toMidi; midi++) {
        if (BLACK.has(midi % 12)) {
            keys.set(midi, { x: (index * whiteWidth) - (whiteWidth * 0.3), w: whiteWidth * 0.6, black: true });
        } else {
            keys.set(midi, { x: index * whiteWidth, w: whiteWidth, black: false });
            index++;
        }
    }
    return keys;
}

const centreOf = key => key.x + (key.w / 2);

function draw(state) {
    const { canvas, map } = state;
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(canvas.clientWidth, 1);
    // The CSS height is sized for six ranges (VoiceMap.razor.css); the drawing fits whatever it gets.
    const height = Math.max(canvas.clientHeight, 1);
    const rowsHeight = map.types.length * (ROW_PX + ROW_GAP_PX);
    if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
        canvas.width = Math.round(width * ratio);
        canvas.height = Math.round(height * ratio);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const colours = state.colours;
    const keys = keyLayout(map, width);
    const left = midi => keys.get(midi)?.x ?? 0;
    const right = midi => { const key = keys.get(midi); return key ? key.x + key.w : width; };

    // One row per voice type.
    ctx.font = '12px system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    map.types.forEach((type, row) => {
        const y = row * (ROW_PX + ROW_GAP_PX);
        const x = left(type.lowMidi);
        const w = right(type.highMidi) - x;
        ctx.fillStyle = type.matched ? colours.keySoft : colours.surface;
        ctx.fillRect(x, y, w, ROW_PX);
        if (type.matched) {
            ctx.strokeStyle = colours.key;
            ctx.lineWidth = 1.5;
            ctx.strokeRect(x + 0.75, y + 0.75, w - 1.5, ROW_PX - 1.5);
        }
        ctx.fillStyle = type.matched ? colours.fg : colours.muted;
        ctx.font = `${type.matched ? '700 ' : ''}12px system-ui, sans-serif`;
        ctx.fillText(type.label, x + 5, y + (ROW_PX / 2));
    });

    // The singing's stretch, washed over the ranges (translucent, so each row stays readable
    // through it) and carried on down over the keys below.
    const low = left(map.lowMidi);
    const high = right(map.highMidi);
    ctx.fillStyle = colours.keyGlow;
    ctx.fillRect(low, 0, high - low, rowsHeight);

    // The keyboard: whites, then blacks on top.
    const top = rowsHeight + 2;
    const pressed = state.pressed;
    const now = performance.now();
    const glow = pressed && now < pressed.until ? (pressed.until - now) / 900 : 0;
    for (const black of [false, true]) {
        for (const [midi, key] of keys) {
            if (key.black !== black) {
                continue;
            }
            const inRange = midi >= map.lowMidi && midi <= map.highMidi;
            const best = midi === state.strongest;
            let fill = black ? colours.black : colours.white;
            if (best) {
                fill = colours.best;
            }
            ctx.fillStyle = fill;
            const keyHeight = black ? KEYS_PX * 0.6 : KEYS_PX;
            // The pressed key dips a couple of pixels while it sounds.
            const dip = best && glow > 0 ? 2 * glow : 0;
            ctx.fillRect(key.x + 0.5, top + dip, key.w - 1, keyHeight - dip);
            if (inRange && !best) {
                ctx.fillStyle = colours.keySoft;
                ctx.fillRect(key.x + 0.5, top, key.w - 1, keyHeight);
            }
            if (!black) {
                ctx.strokeStyle = colours.border;
                ctx.lineWidth = 1;
                ctx.strokeRect(key.x + 0.5, top + 0.5, key.w - 1, keyHeight - 1);
            }
            if (best && glow > 0) {
                ctx.globalAlpha = 0.5 * glow;
                ctx.fillStyle = colours.best;
                ctx.fillRect(key.x - 4, top - 4, key.w + 8, keyHeight + 8);
                ctx.globalAlpha = 1;
            }
        }
    }

    // Where the singing is centred: a line through the ranges and the keys.
    const median = keys.get(map.medianMidi);
    if (median) {
        ctx.strokeStyle = colours.key;
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(centreOf(median), 0);
        ctx.lineTo(centreOf(median), top + KEYS_PX);
        ctx.stroke();
    }

    // Labels under the keys. On a narrow keyboard they collide, so they are placed by importance —
    // the strongest note, then the centre, then the ends of the singing, then the octave Cs — and
    // any that would overlap one already placed is left out rather than shrunk.
    ctx.font = '12px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const rank = midi => (midi === state.strongest ? 0 : midi === map.medianMidi ? 1
        : midi === map.lowMidi || midi === map.highMidi ? 2 : 3);
    const placed = [];
    for (const label of [...map.labels].sort((a, b) => rank(a.midi) - rank(b.midi))) {
        const key = keys.get(label.midi);
        if (!key) {
            continue;
        }
        const half = ctx.measureText(label.label).width / 2;
        const x = Math.min(Math.max(centreOf(key), half), width - half);
        if (placed.some(([from, to]) => x - half < to + 4 && x + half > from - 4)) {
            continue;
        }
        placed.push([x - half, x + half]);
        ctx.fillStyle = rank(label.midi) <= 1 ? colours.fg : colours.muted;
        ctx.fillText(label.label, x, top + KEYS_PX + 4);
    }
    ctx.textAlign = 'start';

    if (glow > 0) {
        state.frame = requestAnimationFrame(() => draw(state));
    }
}

/// Draws `map` (a VoiceMapDto); `strongest` is the MIDI number of the strongest note, or null.
export function init(canvas, map, strongest) {
    dispose(canvas);
    const state = { canvas, map, strongest: strongest ?? null, colours: readColours(canvas), pressed: null, frame: null };
    state.redraw = () => {
        state.colours = readColours(canvas);
        draw(state);
    };
    state.resize = new ResizeObserver(() => draw(state));
    state.resize.observe(canvas);
    state.scheme = window.matchMedia('(prefers-color-scheme: dark)');
    state.scheme.addEventListener('change', state.redraw);
    state.themeObserver = new MutationObserver(state.redraw);
    state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    states.set(canvas, state);
    canvas.dataset.low = String(map.lowMidi);
    canvas.dataset.high = String(map.highMidi);
    draw(state);
}

/// Plays one note in the lead voice (the synth closest to a sung tone) and presses its key.
export function play(canvas, midi) {
    try {
        const Ctor = window.AudioContext ?? window.webkitAudioContext;
        if (!audio && Ctor) {
            const context = new Ctor();
            audio = { context, input: bus(context, context.destination, 0.25) };
        }
        if (audio) {
            if (audio.context.state === 'suspended') {
                audio.context.resume().catch(() => { });
            }
            lead(audio.context, audio.input, midi, audio.context.currentTime + 0.03, 1.2, 0.5);
        }
    } catch {
        // No audio here; the key still lights.
    }
    const state = states.get(canvas);
    if (state) {
        state.pressed = { midi, until: performance.now() + (prefs.allowsMotion() ? 900 : 0) };
        canvas.dataset.played = String(midi);
        cancelAnimationFrame(state.frame);
        draw(state);
    }
}

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    cancelAnimationFrame(state.frame);
    state.resize.disconnect();
    state.scheme.removeEventListener('change', state.redraw);
    state.themeObserver.disconnect();
    states.delete(canvas);
}
