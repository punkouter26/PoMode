// Audio-reactive ambient background: two or three large radial gradients drifting slowly across
// the canvas, their intensity breathing with the mixer's master level. Silence leaves the page
// nearly untouched; playback makes it glow gently. Purely decorative — the canvas draws behind the
// UI and carries no information, so under prefers-reduced-motion it degrades to one static wash.
//
// Colour comes from CSS custom properties (--pm-key-hue, --pm-bg) read at draw time but cached for
// ~2 s, so theme flips and key changes are picked up without a getComputedStyle per frame.

import { masterLevel } from './mixer.js';

const states = new Map();

const FRAME_MS = 1000 / 30;
const COLOUR_REFRESH_MS = 2000;
const DEFAULT_HUE = 262;
const MAX_DPR = 2;

/// The drifting light blobs. Angular speeds are incommensurate so the pattern never visibly loops.
const BLOBS = [
    { radius: 0.85, orbitX: 0.32, orbitY: 0.22, speed: 0.011, phase: 0.0, hueShift: 0, alpha: 1.0 },
    { radius: 0.7, orbitX: 0.28, orbitY: 0.3, speed: -0.017, phase: 2.1, hueShift: 38, alpha: 0.8 },
    { radius: 0.6, orbitX: 0.35, orbitY: 0.18, speed: 0.023, phase: 4.4, hueShift: -30, alpha: 0.65 },
];

function reducedMotion() {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/// Reads the palette from the root element: key hue (may be absent) and the theme background.
function readColours() {
    const style = getComputedStyle(document.documentElement);
    const rawHue = parseFloat(style.getPropertyValue('--pm-key-hue'));
    return {
        hue: Number.isFinite(rawHue) ? rawHue : DEFAULT_HUE,
        background: style.getPropertyValue('--pm-bg').trim() || '#111118',
    };
}

/// Matches the backing store to the CSS box, DPR-capped so a 4K hidpi page stays cheap.
function resize(state) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(state.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(state.canvas.clientHeight * dpr), 1);
    if (state.canvas.width !== width || state.canvas.height !== height) {
        state.canvas.width = width;
        state.canvas.height = height;
    }
}

function drawBlobs(state, level, timeSeconds) {
    const { canvas, ctx, colours } = state;
    const w = canvas.width;
    const h = canvas.height;

    ctx.fillStyle = colours.background;
    ctx.fillRect(0, 0, w, h);

    // Floor keeps the page from going dead flat in silence; playback adds the breathing on top.
    const intensity = 0.03 + level * 0.22;
    ctx.globalCompositeOperation = 'lighter';
    for (const blob of BLOBS) {
        const angle = timeSeconds * blob.speed * Math.PI * 2 + blob.phase;
        const cx = w * (0.5 + Math.cos(angle) * blob.orbitX);
        const cy = h * (0.5 + Math.sin(angle * 1.3) * blob.orbitY);
        const radius = Math.max(w, h) * blob.radius;
        const hue = colours.hue + blob.hueShift;
        const alpha = intensity * blob.alpha;

        const gradient = ctx.createRadialGradient(cx, cy, 0, cx, cy, radius);
        gradient.addColorStop(0, `hsla(${hue}, 60%, 55%, ${alpha})`);
        gradient.addColorStop(1, `hsla(${hue}, 60%, 55%, 0)`);
        ctx.fillStyle = gradient;
        ctx.fillRect(0, 0, w, h);
    }
    ctx.globalCompositeOperation = 'source-over';
}

/// How long after the last audible moment the animation keeps running before freezing.
const IDLE_AFTER_MS = 2500;

function frame(state, now) {
    if (!states.has(state.canvas)) {
        return;
    }

    // Skip work entirely in background tabs; rAF is already throttled there, but be explicit.
    // ~30 fps is plenty for gradients this slow and halves the fill cost.
    if (!document.hidden && now - state.lastFrame >= FRAME_MS) {
        state.lastFrame = now;

        if (now - state.lastColourRead > COLOUR_REFRESH_MS) {
            state.colours = readColours();
            state.lastColourRead = now;
        }

        resize(state);

        // Ease toward the live level so beats swell rather than flicker.
        const target = masterLevel();
        state.level = state.level * 0.92 + target * 0.08;
        if (target > 0.001) {
            state.activeUntil = now + IDLE_AFTER_MS;
        }

        drawBlobs(state, state.level, now / 1000);

        // Fully quiet for a while: freeze on this wash and poll cheaply until sound returns —
        // an animated background in silence is pure GPU waste (and the glass cards above would
        // recomposite their blur every frame too).
        if (now > state.activeUntil) {
            state.raf = null;
            state.poll = setTimeout(() => wake(state), 500);
            return;
        }
    }

    state.raf = requestAnimationFrame((next) => frame(state, next));
}

function wake(state) {
    if (!states.has(state.canvas)) {
        return;
    }
    if (masterLevel() > 0.001) {
        state.poll = null;
        state.activeUntil = performance.now() + IDLE_AFTER_MS;
        state.raf = requestAnimationFrame((now) => frame(state, now));
    } else {
        state.poll = setTimeout(() => wake(state), 500);
    }
}

/// Starts the ambient background on `canvas`. Under prefers-reduced-motion this draws one static,
/// very subtle wash and never animates.
export function init(canvas) {
    if (states.has(canvas)) {
        return;
    }
    const state = {
        canvas,
        ctx: canvas.getContext('2d'),
        colours: readColours(),
        level: 0,
        lastFrame: 0,
        lastColourRead: performance.now(),
        activeUntil: performance.now() + IDLE_AFTER_MS,
        raf: null,
        poll: null,
    };
    states.set(canvas, state);

    resize(state);
    if (reducedMotion()) {
        drawBlobs(state, 0.15, 0);
        return;
    }
    state.raf = requestAnimationFrame((now) => frame(state, now));
}

/// Stops the loop and forgets the canvas.
export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.raf !== null) {
        cancelAnimationFrame(state.raf);
    }
    if (state.poll !== null) {
        clearTimeout(state.poll);
    }
    states.delete(canvas);
}
