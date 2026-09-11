// The Practice streak flame: a small particle fire that burns higher the longer the run.
//
// Deliberately a 2D canvas and not a shader. It occupies about thirty pixels square, the particle
// count tops out in the low hundreds, and additive blending over that area costs nothing measurable —
// a WebGL context and its shader compile would be more expensive than the effect. The WebGPU and
// WebGL2 paths elsewhere in the app exist because those effects are genuinely large; this one is not,
// and matching them for consistency's sake would be cargo cult.
//
// The flame is the one place on the Practice page that rewards rather than reports, which is why it
// is driven by the streak — a number this browser keeps for itself — rather than by any score the
// server issued. It burns hotter at longer runs and goes out the moment one is broken.

import * as prefs from './fx-prefs.js';

const states = new Map();

/// Below this the page shows a glyph instead; the flame never starts.
const MIN_STREAK = 3;

/// Where the fire stops growing. Past it the streak number carries the story on its own — an
/// ever-taller flame would just overflow its corner of the header.
const MAX_STREAK = 12;

const MAX_DPR = 2;

/// Particles emitted per frame at full heat. Scaled by the streak and again by the effect level, so
/// 'subtle' gets a candle and 'full' gets a fire.
const SPAWN_PER_FRAME = 2.4;

/// Flame gradient, coolest last — a particle walks down this as it ages, so the base is white-hot
/// and the tips are deep orange going transparent.
const HEAT = [
    [255, 246, 214],
    [255, 205, 108],
    [252, 148, 46],
    [214, 78, 24],
];

function colourAt(t) {
    const scaled = Math.max(0, Math.min(0.999, t)) * (HEAT.length - 1);
    const index = Math.floor(scaled);
    const f = scaled - index;
    const a = HEAT[index];
    const b = HEAT[Math.min(index + 1, HEAT.length - 1)];
    return [
        Math.round(a[0] + ((b[0] - a[0]) * f)),
        Math.round(a[1] + ((b[1] - a[1]) * f)),
        Math.round(a[2] + ((b[2] - a[2]) * f)),
    ];
}

function resize(state) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(state.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(state.canvas.clientHeight * dpr), 1);
    if (state.canvas.width !== width || state.canvas.height !== height) {
        state.canvas.width = width;
        state.canvas.height = height;
    }
    state.dpr = dpr;
}

/// 0..1: how far along the streak scale this run sits. Drives spawn rate, rise speed and lifetime
/// together so a hotter flame is taller and busier rather than only denser.
function heat(state) {
    const span = MAX_STREAK - MIN_STREAK;
    return Math.max(0, Math.min((state.streak - MIN_STREAK) / span, 1));
}

function spawn(state) {
    const { width, height } = state.canvas;
    const h = heat(state);
    // A narrow base that widens slightly with heat; the sideways drift does the rest of the shape.
    const spread = width * (0.14 + (h * 0.08));
    state.particles.push({
        x: (width / 2) + ((Math.random() - 0.5) * spread * 2),
        y: height * 0.94,
        vx: (Math.random() - 0.5) * width * 0.012,
        vy: -height * (0.010 + (Math.random() * 0.008) + (h * 0.009)),
        life: 0,
        span: 0.5 + (Math.random() * 0.35) + (h * 0.3),
        size: (width * 0.10) * (0.6 + (Math.random() * 0.7)),
    });
}

function frame(state, now) {
    state.raf = requestAnimationFrame((next) => frame(state, next));

    const dt = Math.min((now - state.last) / 1000, 0.05);
    state.last = now;
    const ctx = state.ctx;
    const { width, height } = state.canvas;
    ctx.clearRect(0, 0, width, height);

    const h = heat(state);
    const burning = state.streak >= MIN_STREAK;

    if (burning) {
        // Fractional spawn rate carried between frames, so a low rate still emits steadily instead
        // of rounding down to nothing every frame.
        state.pending += SPAWN_PER_FRAME * (0.45 + (h * 0.55)) * prefs.intensity() * (dt * 60);
        while (state.pending >= 1) {
            state.pending -= 1;
            spawn(state);
        }
    }

    // Additive, so overlapping particles read as a hotter core rather than a stack of discs.
    ctx.globalCompositeOperation = 'lighter';
    for (let i = state.particles.length - 1; i >= 0; i--) {
        const p = state.particles[i];
        p.life += dt;
        if (p.life >= p.span) {
            state.particles.splice(i, 1);
            continue;
        }
        const t = p.life / p.span;
        p.x += p.vx * dt * 60;
        p.y += p.vy * dt * 60;
        // Buoyancy: a particle accelerates upward as it cools and thins, which is what gives the
        // flame its taper instead of a straight column.
        p.vy *= 1.012;
        p.vx *= 0.97;

        const [r, g, b] = colourAt(t);
        const alpha = (1 - t) * (1 - t) * 0.85;
        const radius = p.size * (1 - (t * 0.45));
        const gradient = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, radius);
        gradient.addColorStop(0, `rgba(${r},${g},${b},${alpha})`);
        gradient.addColorStop(1, `rgba(${r},${g},${b},0)`);
        ctx.fillStyle = gradient;
        ctx.beginPath();
        ctx.arc(p.x, p.y, radius, 0, Math.PI * 2);
        ctx.fill();
    }
    ctx.globalCompositeOperation = 'source-over';

    // Once the streak is broken the loop runs on only until the last particle has died, then stops
    // for good rather than spinning at 60 fps over an empty canvas.
    if (!burning && state.particles.length === 0) {
        cancelAnimationFrame(state.raf);
        state.raf = null;
        ctx.clearRect(0, 0, width, height);
    }
}

/// Sets the current streak, starting, intensifying or extinguishing the flame to match. Safe to call
/// on every render — the common case is that the number has not changed and nothing happens.
export function setStreak(canvas, streak) {
    if (!canvas) {
        return;
    }
    const value = Number(streak) || 0;
    let state = states.get(canvas);

    if (!state) {
        if (value < MIN_STREAK || !prefs.allowsMotion()) {
            return;
        }
        const ctx = canvas.getContext('2d');
        if (!ctx) {
            return;
        }
        state = {
            canvas,
            ctx,
            dpr: 1,
            streak: value,
            particles: [],
            pending: 0,
            last: performance.now(),
            raf: null,
            resizeObserver: null,
            unsubscribe: null,
        };
        states.set(canvas, state);
        resize(state);
        state.resizeObserver = new ResizeObserver(() => resize(state));
        state.resizeObserver.observe(canvas);
        state.unsubscribe = prefs.subscribe(() => {
            if (!prefs.allowsMotion()) {
                dispose(canvas);
            }
        });
        state.raf = requestAnimationFrame((now) => frame(state, now));
        canvas.dataset.streakFlame = 'on';
        return;
    }

    state.streak = value;
    canvas.dataset.streakFlame = value >= MIN_STREAK ? 'on' : 'out';
    // Restarts a loop that stopped itself when the fire last went out.
    if (value >= MIN_STREAK && state.raf === null) {
        state.last = performance.now();
        state.raf = requestAnimationFrame((now) => frame(state, now));
    }
}

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.raf !== null) {
        cancelAnimationFrame(state.raf);
    }
    state.resizeObserver?.disconnect();
    state.unsubscribe?.();
    states.delete(canvas);
}
