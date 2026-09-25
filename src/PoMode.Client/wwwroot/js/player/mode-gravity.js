// The Mode Lab's home-note view: the mode's notes as bodies held by springs, the home note at the
// centre and every other note on a chromatic clock around it, placed by how many semitones it sits
// above home. It is the page's one lesson made physical: all seven modes share a note set, and what
// a mode changes is which note is home. Pick another card and the same bodies stay — the new home
// springs to the centre, the old one flies out to its place on the ring, and the ring's pattern of
// filled and empty slots rearranges into the new mode's interval shape.
//
// Which notes, which one is home and which is characteristic are handed over from C# (ScaleModes in
// PoMode.Shared); this module only knows pitch classes, intervals and flags. Bodies light up while
// the loop sounds their pitch (modal-player's soundingPitches), and tapping one plays it.
//
// A few dozen lines of spring physics rather than an engine: nine bodies at most, each pulled toward
// its target and damped a little under critical, so it overshoots and settles.

import * as prefs from '../shell/prefs.js';
import { soundingPitches, audition } from './modal-player.js';

const states = new Map();

const STIFFNESS = 90;
const DAMPING = 11;
const SETTLED = 0.2;
const IDLE_POLL_MS = 250;

function readColours(state) {
    const style = getComputedStyle(state.canvas);
    const read = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    state.colours = {
        fg: read('--pm-fg', '#1a1a1a'),
        bg: read('--pm-bg', '#ffffff'),
        muted: read('--pm-fg-muted', '#4c4c57'),
        border: read('--pm-border', '#d9d9e0'),
        inMode: read('--pm-note-in-mode', '#059669'),
        characteristic: read('--pm-note-characteristic', '#d97706'),
        home: (/^--pm-[a-z-]+$/.test(state.homeToken) ? read(state.homeToken, '') : '') || read('--pm-accent', '#6750a4'),
    };
}

function layout(state) {
    const width = Math.max(state.canvas.clientWidth, 1);
    const height = Math.max(state.canvas.clientHeight, 1);
    const size = Math.min(width, height);
    return { width, height, cx: width / 2, cy: height / 2, ring: size * 0.36, unit: size / 220 };
}

/// Where a body wants to be: the centre when it is home, otherwise its semitone's slot on the clock
/// with home at twelve o'clock.
function target(state, body) {
    const { cx, cy, ring, unit } = layout(state);
    if (body.home) {
        return { x: cx, y: cy, r: 24 * unit };
    }
    const angle = (-Math.PI / 2) + (body.interval * (Math.PI / 6));
    return { x: cx + (Math.cos(angle) * ring), y: cy + (Math.sin(angle) * ring), r: 13 * unit };
}

function step(state, dt) {
    let moving = false;
    for (const body of state.bodies.values()) {
        const goal = target(state, body);
        if (!state.animate) {
            Object.assign(body, { x: goal.x, y: goal.y, r: goal.r, vx: 0, vy: 0, vr: 0 });
        } else {
            for (const [p, v, t] of [['x', 'vx', goal.x], ['y', 'vy', goal.y], ['r', 'vr', goal.r]]) {
                body[v] += ((STIFFNESS * (t - body[p])) - (DAMPING * body[v])) * dt;
                body[p] += body[v] * dt;
            }
        }
        const alphaGoal = body.present ? 1 : 0;
        body.alpha += (alphaGoal - body.alpha) * Math.min(1, dt * 8);
        body.pulse = Math.max(0, body.pulse - (dt * 2.2));
        moving ||= Math.abs(body.vx) + Math.abs(body.vy) > SETTLED || Math.abs(body.alpha - alphaGoal) > 0.01
            || body.pulse > 0.01;
    }
    // Faded-out bodies (a seven-note mode's notes that a pentatonic leaves out) are dropped once gone.
    for (const [key, body] of state.bodies) {
        if (!body.present && body.alpha < 0.01) {
            state.bodies.delete(key);
        }
    }
    return moving;
}

function lightSounding(state) {
    for (const { midi, part } of soundingPitches()) {
        const body = state.bodies.get(((midi % 12) + 12) % 12);
        if (body && body.present) {
            body.pulse = Math.max(body.pulse, part === 'melody' ? 1 : 0.55);
        }
    }
}

function draw(state) {
    const canvas = state.canvas;
    const { width, height, cx, cy, ring, unit } = layout(state);
    const ratio = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
        canvas.width = Math.round(width * ratio);
        canvas.height = Math.round(height * ratio);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const colours = state.colours;

    // The clock: twelve slots, filled or not by the mode's shape.
    ctx.strokeStyle = colours.border;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(cx, cy, ring, 0, Math.PI * 2);
    ctx.stroke();
    ctx.fillStyle = colours.border;
    for (let slot = 0; slot < 12; slot++) {
        const angle = (-Math.PI / 2) + (slot * (Math.PI / 6));
        ctx.beginPath();
        ctx.arc(cx + (Math.cos(angle) * ring), cy + (Math.sin(angle) * ring), 2.5 * unit, 0, Math.PI * 2);
        ctx.fill();
    }

    const home = [...state.bodies.values()].find(body => body.home && body.present);
    if (home) {
        // Tethers: each note held to home, which is the relationship a mode is made of.
        ctx.strokeStyle = colours.home;
        ctx.lineWidth = 1.5;
        for (const body of state.bodies.values()) {
            if (body === home) {
                continue;
            }
            ctx.globalAlpha = 0.28 * body.alpha;
            ctx.beginPath();
            ctx.moveTo(home.x, home.y);
            ctx.lineTo(body.x, body.y);
            ctx.stroke();
        }
        ctx.globalAlpha = 1;
    }

    // Draw home last so it sits on top as it flies in to the centre.
    const ordered = [...state.bodies.values()].sort((a, b) => Number(a.home) - Number(b.home));
    for (const body of ordered) {
        const fill = body.home ? colours.home : body.characteristic ? colours.characteristic : colours.inMode;
        if (body.pulse > 0.01) {
            ctx.globalAlpha = 0.35 * body.pulse * body.alpha;
            ctx.fillStyle = fill;
            ctx.beginPath();
            ctx.arc(body.x, body.y, body.r * (1.25 + (0.6 * body.pulse)), 0, Math.PI * 2);
            ctx.fill();
        }
        ctx.globalAlpha = body.alpha;
        ctx.fillStyle = fill;
        ctx.beginPath();
        ctx.arc(body.x, body.y, Math.max(body.r, 1), 0, Math.PI * 2);
        ctx.fill();

        // Home's name sits inside its disc; the mode colours are text colours against the page, so
        // the page colour on top of them clears the same contrast. Ring notes are labelled outside,
        // away from the centre, in the page's own text colour.
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        if (body.home) {
            ctx.font = `700 ${Math.round(15 * unit)}px system-ui, sans-serif`;
            ctx.fillStyle = colours.bg;
            ctx.fillText(body.label, body.x, body.y);
        } else {
            const dx = body.x - cx;
            const dy = body.y - cy;
            const length = Math.hypot(dx, dy) || 1;
            const offset = body.r + (11 * unit);
            ctx.font = `${Math.max(12, Math.round(12 * unit))}px system-ui, sans-serif`;
            ctx.fillStyle = colours.fg;
            ctx.fillText(body.label, body.x + ((dx / length) * offset), body.y + ((dy / length) * offset));
        }
        ctx.globalAlpha = 1;
    }
}

function loop(state, now) {
    state.frame = null;
    const dt = state.last > 0 ? Math.min((now - state.last) / 1000, 1 / 30) : 1 / 60;
    state.last = now;
    const playing = document.body?.dataset.modalPlayer === 'playing';
    if (playing) {
        lightSounding(state);
    }
    const moving = step(state, dt);
    draw(state);
    state.canvas.dataset.settled = moving ? '0' : '1';
    if (moving || playing) {
        state.frame = requestAnimationFrame(next => loop(state, next));
    } else {
        // At rest nothing is drawn; a slow check notices the loop starting so the bodies can light.
        state.last = 0;
        state.idle = setTimeout(() => wake(state), IDLE_POLL_MS);
    }
}

function wake(state) {
    clearTimeout(state.idle);
    state.idle = null;
    if (state.frame === null) {
        state.frame = requestAnimationFrame(now => loop(state, now));
    }
}

function onPointerDown(state, event) {
    const rect = state.canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    for (const body of state.bodies.values()) {
        if (body.present && Math.hypot(body.x - x, body.y - y) <= Math.max(body.r, 16)) {
            // Middle-register pitch of this pitch class, C4 to B4.
            audition(60 + body.pitchClass);
            body.pulse = 1;
            wake(state);
            return;
        }
    }
}

export function init(canvas) {
    dispose(canvas);
    const state = {
        canvas,
        bodies: new Map(),
        homeToken: '',
        colours: null,
        animate: prefs.allowsMotion(),
        frame: null,
        idle: null,
        last: 0,
    };
    readColours(state);
    state.onPointerDown = event => onPointerDown(state, event);
    canvas.addEventListener('pointerdown', state.onPointerDown);
    state.resize = new ResizeObserver(() => wake(state));
    state.resize.observe(canvas);
    state.onTheme = () => {
        readColours(state);
        wake(state);
    };
    state.scheme = window.matchMedia('(prefers-color-scheme: dark)');
    state.scheme.addEventListener('change', state.onTheme);
    state.themeObserver = new MutationObserver(state.onTheme);
    state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    states.set(canvas, state);
}

/// The mode on screen, as bodies: { pitchClass, label, interval, home, characteristic }.
/// `homeToken` is the CSS custom property naming the mode's colour. Bodies are matched by pitch
/// class, so a note shared with the previous mode moves rather than being replaced.
export function setMode(canvas, bodies, homeToken) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    state.homeToken = homeToken ?? '';
    readColours(state);
    const { cx, cy } = layout(state);
    const incoming = new Set();
    for (const next of bodies) {
        incoming.add(next.pitchClass);
        const body = state.bodies.get(next.pitchClass)
            ?? { x: cx, y: cy, r: 0, vx: 0, vy: 0, vr: 0, alpha: 0, pulse: 0 };
        Object.assign(body, next, { present: true });
        state.bodies.set(next.pitchClass, body);
    }
    for (const [pitchClass, body] of state.bodies) {
        if (!incoming.has(pitchClass)) {
            body.present = false;
            body.home = false;
        }
    }
    const home = bodies.find(body => body.home);
    canvas.dataset.home = home ? home.label : '';
    canvas.dataset.bodyCount = String(bodies.length);
    wake(state);
}

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.frame !== null) {
        cancelAnimationFrame(state.frame);
    }
    clearTimeout(state.idle);
    canvas.removeEventListener('pointerdown', state.onPointerDown);
    state.resize.disconnect();
    state.scheme.removeEventListener('change', state.onTheme);
    state.themeObserver.disconnect();
    states.delete(canvas);
}
