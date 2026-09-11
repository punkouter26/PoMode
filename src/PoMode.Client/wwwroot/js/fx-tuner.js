// The Live page's tuner halo: a ring whose radius is the pitch you are singing and whose steadiness
// is how in tune you are.
//
// The Live page was the thinnest surface in the app — one button, a note count, and a canvas that
// only filled in every five seconds when the server answered. Between those answers there was no
// feedback at all, which is the wrong way round for the one page you use with your voice. This gives
// the intervening seconds something to work against: the ring locks and brightens as the note
// centres, and smears into a wobbling, colour-fringed band as it drifts. You can see you are sharp
// at the moment you are sharp, rather than five seconds later in a note roll.
//
// A note on cents. The Practice page deliberately reports nothing in cents, because its notes come
// from the collector, which rounds to the nearest semitone before anything is posted — a cents
// figure there would be arithmetic performed on a rounding. Here the fractional MIDI number comes
// straight out of detectPitch before any rounding happens, so the deviation is real. It is also
// never sent anywhere or scored: it drives a picture, and the server still decides what was sung.
//
// WebGL2 with a 2D fallback, both idle to nothing when the microphone is closed.

import * as prefs from './fx-prefs.js';

let state = null;

const MAX_DPR = 2;
const FRAME_MS = 1000 / 60;

/// Lowest and highest pitch the ring spans, in MIDI. Roughly E2 to C6 — wider than any one singer,
/// so the ring never pins to an edge, and fixed rather than adaptive because a scale that rescaled
/// itself would make the same note sit in a different place from one take to the next.
const MIN_MIDI = 40;
const MAX_MIDI = 84;

/// Cents inside which a note counts as centred, for the lock. Ten is tighter than most ears and
/// looser than a strobe tuner: hitting it should feel like an achievement and not an accident.
const LOCK_CENTS = 10;

/// Seconds of silence after which the halo fades out. Long enough to ride through the gap between
/// two phrases without the display collapsing every time the singer breathes.
const HOLD_SECONDS = 0.4;

const VERTEX_SOURCE = `#version 300 es
out vec2 vUv;
void main() {
    vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = pos;
    gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}`;

const FRAGMENT_SOURCE = `#version 300 es
precision mediump float;
in vec2 vUv;
uniform vec2 uRes;
uniform float uTime;
uniform float uPitch;    // 0..1 across the tuner's range
uniform float uDetune;   // -1..1, cents from the nearest semitone over the lock threshold
uniform float uLock;     // 0..1, how centred the note is
uniform float uPresence; // 0..1, faded out by silence
uniform vec3 uInColor;
uniform vec3 uSharpColor;
out vec4 outColor;

/// Aspect-corrected distance from the centre, so the ring is a circle on any strip.
float radiusAt(vec2 uv) {
    vec2 p = uv - 0.5;
    p.x *= uRes.x / max(uRes.y, 1.0);
    return length(p) * 2.0;
}

void main() {
    float r = radiusAt(vUv);
    vec2 p = vUv - 0.5;
    float angle = atan(p.y, p.x);

    // The ring's resting radius is the pitch. Low notes are a tight core, high ones a wide halo —
    // which makes an octave jump unmistakable even out of the corner of your eye.
    float target = 0.12 + (uPitch * 0.78);

    // Out of tune, the ring stops being a circle: it ripples, and the further off the note is the
    // deeper and faster the ripple. This is the whole mechanism — "in tune" is "the wobble stops".
    float wobbleAmount = abs(uDetune) * 0.055;
    float wobble = sin((angle * 6.0) + (uTime * (3.0 + abs(uDetune) * 9.0))) * wobbleAmount;
    float ringRadius = target + wobble;

    float d = abs(r - ringRadius);

    // Chromatic fringing, again proportional to the error: the three channels sample slightly
    // different radii, so a badly out-of-tune note visibly separates into colour and a centred one
    // resolves to a single clean line.
    float split = abs(uDetune) * 0.012;
    float core = smoothstep(0.020, 0.002, d);
    float coreR = smoothstep(0.020, 0.002, abs(r - (ringRadius + split)));
    float coreB = smoothstep(0.020, 0.002, abs(r - (ringRadius - split)));

    // Sharp reads warm, flat reads the same warm colour — the direction is on screen as a number in
    // the page, and colouring them differently would imply one is worse than the other.
    vec3 tint = mix(uSharpColor, uInColor, uLock);
    vec3 rgb = tint * core;
    rgb.r = max(rgb.r, tint.r * coreR);
    rgb.b = max(rgb.b, tint.b * coreB);

    // Bloom around the ring, and a bright flare that only appears once the note is genuinely locked.
    float glow = exp(-d * 9.0) * (0.16 + (uLock * 0.35));
    float flare = uLock * uLock * exp(-d * 30.0) * 0.55;
    rgb += tint * (glow + flare);

    // A faint graticule of semitone marks, so the ring has something to be measured against rather
    // than floating in an empty field.
    float ticks = smoothstep(0.972, 1.0, cos(r * 88.0)) * 0.05;
    rgb += vec3(ticks);

    float alpha = clamp(max(max(core, max(coreR, coreB)), glow + flare) + ticks, 0.0, 1.0);
    outColor = vec4(rgb * uPresence, alpha * uPresence);
}`;

function parseColour(raw, fallback) {
    const text = (raw || '').trim();
    const hex = /^#([0-9a-f]{6})$/i.exec(text);
    if (hex) {
        const n = parseInt(hex[1], 16);
        return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    }
    return fallback;
}

function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        gl.deleteShader(shader);
        return null;
    }
    return shader;
}

function createPipeline(canvas) {
    let gl;
    try {
        gl = canvas.getContext('webgl2', { alpha: true, antialias: false, premultipliedAlpha: false });
    } catch {
        return null;
    }
    if (!gl) {
        return null;
    }
    const vertex = compile(gl, gl.VERTEX_SHADER, VERTEX_SOURCE);
    const fragment = compile(gl, gl.FRAGMENT_SHADER, FRAGMENT_SOURCE);
    if (!vertex || !fragment) {
        return null;
    }
    const program = gl.createProgram();
    gl.attachShader(program, vertex);
    gl.attachShader(program, fragment);
    gl.linkProgram(program);
    gl.deleteShader(vertex);
    gl.deleteShader(fragment);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        gl.deleteProgram(program);
        return null;
    }
    gl.useProgram(program);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    return {
        gl,
        program,
        uniforms: {
            res: gl.getUniformLocation(program, 'uRes'),
            time: gl.getUniformLocation(program, 'uTime'),
            pitch: gl.getUniformLocation(program, 'uPitch'),
            detune: gl.getUniformLocation(program, 'uDetune'),
            lock: gl.getUniformLocation(program, 'uLock'),
            presence: gl.getUniformLocation(program, 'uPresence'),
            inColor: gl.getUniformLocation(program, 'uInColor'),
            sharpColor: gl.getUniformLocation(program, 'uSharpColor'),
        },
    };
}

function resize(s) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(s.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(s.canvas.clientHeight * dpr), 1);
    if (s.canvas.width !== width || s.canvas.height !== height) {
        s.canvas.width = width;
        s.canvas.height = height;
        s.gl?.viewport(0, 0, width, height);
    }
}

function frame(s, now) {
    s.raf = requestAnimationFrame((next) => frame(s, next));
    if (now - s.lastFrame < FRAME_MS) {
        return;
    }
    const dt = Math.min((now - s.lastFrame) / 1000, 0.1);
    s.lastFrame = now;

    // Presence decays on its own rather than being switched off, so a breath between phrases dims
    // the halo instead of extinguishing it.
    const heard = (now - s.lastHeard) / 1000 < HOLD_SECONDS;
    s.presence += ((heard ? 1 : 0) - s.presence) * Math.min(dt * (heard ? 9 : 3), 1);

    // The pitch itself is eased hard: raw frame-to-frame pitch is jumpy enough that an unsmoothed
    // ring would read as noise rather than as a note being held.
    s.pitch += (s.targetPitch - s.pitch) * Math.min(dt * 12, 1);
    s.detune += (s.targetDetune - s.detune) * Math.min(dt * 10, 1);
    s.lock += (s.targetLock - s.lock) * Math.min(dt * 8, 1);

    resize(s);

    if (s.gl) {
        const { gl, uniforms } = s;
        gl.useProgram(s.program);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.uniform2f(uniforms.res, s.canvas.width, s.canvas.height);
        gl.uniform1f(uniforms.time, (now - s.start) / 1000);
        gl.uniform1f(uniforms.pitch, s.pitch);
        gl.uniform1f(uniforms.detune, s.detune);
        gl.uniform1f(uniforms.lock, s.lock);
        gl.uniform1f(uniforms.presence, s.presence);
        gl.uniform3fv(uniforms.inColor, s.colours.inTune);
        gl.uniform3fv(uniforms.sharpColor, s.colours.offTune);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    } else {
        render2d(s);
    }

    // Idle to a stop once the halo has fully faded; the page restarts it on the next heard note.
    if (s.presence < 0.01 && !heard) {
        cancelAnimationFrame(s.raf);
        s.raf = null;
    }
}

/// The fallback: the same ring as a stroked circle, wobble and all, without the bloom or the
/// chromatic split. Enough to tune against, which is the point of it.
function render2d(s) {
    const ctx = s.ctx2d;
    if (!ctx) {
        return;
    }
    const { width, height } = s.canvas;
    ctx.clearRect(0, 0, width, height);
    if (s.presence < 0.01) {
        return;
    }
    const scale = Math.min(width, height) / 2;
    const radius = (0.12 + (s.pitch * 0.78)) * scale;
    const wobble = Math.abs(s.detune) * 0.055 * scale;
    const colour = s.lock > 0.5 ? s.colours.inTune : s.colours.offTune;
    ctx.strokeStyle = `rgba(${Math.round(colour[0] * 255)},${Math.round(colour[1] * 255)},${Math.round(colour[2] * 255)},${s.presence})`;
    ctx.lineWidth = 2 + (s.lock * 2);
    ctx.beginPath();
    for (let i = 0; i <= 72; i++) {
        const angle = (i / 72) * Math.PI * 2;
        const r = radius + (Math.sin((angle * 6) + ((performance.now() / 1000) * 4)) * wobble);
        const x = (width / 2) + (Math.cos(angle) * r);
        const y = (height / 2) + (Math.sin(angle) * r);
        if (i === 0) {
            ctx.moveTo(x, y);
        } else {
            ctx.lineTo(x, y);
        }
    }
    ctx.closePath();
    ctx.stroke();
}

export function init(canvas) {
    dispose();
    if (!canvas || typeof canvas.getContext !== 'function' || !prefs.allowsMotion()) {
        return false;
    }
    const pipeline = createPipeline(canvas);
    const style = getComputedStyle(canvas);
    const next = {
        canvas,
        gl: pipeline?.gl ?? null,
        program: pipeline?.program ?? null,
        uniforms: pipeline?.uniforms ?? null,
        ctx2d: pipeline ? null : canvas.getContext('2d'),
        colours: {
            inTune: parseColour(style.getPropertyValue('--pm-note-in-mode').trim(), [0.2, 0.83, 0.6]),
            offTune: parseColour(style.getPropertyValue('--pm-note-characteristic').trim(), [0.85, 0.55, 0.15]),
        },
        pitch: 0.5,
        targetPitch: 0.5,
        detune: 0,
        targetDetune: 0,
        lock: 0,
        targetLock: 0,
        presence: 0,
        lastHeard: -1e9,
        start: performance.now(),
        lastFrame: 0,
        raf: null,
        resizeObserver: null,
        unsubscribe: null,
    };
    if (!next.gl && !next.ctx2d) {
        return false;
    }
    state = next;
    resize(state);
    state.resizeObserver = new ResizeObserver(() => resize(state));
    state.resizeObserver.observe(canvas);
    state.unsubscribe = prefs.subscribe(() => {
        if (!prefs.allowsMotion()) {
            dispose();
        }
    });
    canvas.dataset.tuner = state.gl ? 'webgl2' : '2d';
    return true;
}

/// Reports one heard pitch as a fractional MIDI number, or null for silence.
///
/// Called from live-session.js's microphone callback at the audio frame rate, which is why the page
/// never sees it: per-frame work does not cross into C# anywhere in this codebase.
export function push(midi) {
    if (!state) {
        return;
    }
    if (midi === null || !Number.isFinite(midi)) {
        return;
    }
    const clamped = Math.max(MIN_MIDI, Math.min(midi, MAX_MIDI));
    state.targetPitch = (clamped - MIN_MIDI) / (MAX_MIDI - MIN_MIDI);

    const cents = (midi - Math.round(midi)) * 100;
    // Normalised against the lock threshold rather than against 50 cents: the interesting range is
    // the last few cents before the note centres, and scaling to a semitone would leave all of it
    // squeezed into the bottom tenth of the effect.
    state.targetDetune = Math.max(-1, Math.min(cents / (LOCK_CENTS * 2.5), 1));
    state.targetLock = Math.max(0, 1 - (Math.abs(cents) / LOCK_CENTS));
    state.lastHeard = performance.now();

    if (state.raf === null) {
        state.lastFrame = 0;
        state.raf = requestAnimationFrame((now) => frame(state, now));
    }
}

/// Lets the halo fade out — the microphone closed, not merely a pause between phrases.
export function idle() {
    if (state) {
        state.lastHeard = -1e9;
        state.targetLock = 0;
    }
}

export function dispose() {
    if (!state) {
        return;
    }
    if (state.raf !== null) {
        cancelAnimationFrame(state.raf);
    }
    state.resizeObserver?.disconnect();
    state.unsubscribe?.();
    if (state.gl) {
        state.gl.deleteProgram(state.program);
    }
    try {
        delete state.canvas.dataset.tuner;
    } catch {
        // Already detached.
    }
    state = null;
}
