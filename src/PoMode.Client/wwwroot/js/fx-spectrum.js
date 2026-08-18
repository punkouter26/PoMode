// Frequency-bar visualizer strip with glow, fed by the mixer's analyser bins.
//
// Preferred path is WebGL2: the 128 byte bins are uploaded each frame as a 128x1 R8 texture and a
// single fullscreen-quad draw renders every bar plus an additive glow above each tip — one draw
// call, trivially 60 fps. Without WebGL2 a plain 2D-canvas bar renderer takes over.
//
// When nothing is playing the previous frame's bars ease toward zero, and once fully faded the
// animation loop stops; a slow setTimeout poll (~4/s) watches for playback to restart it. Under
// prefers-reduced-motion the strip renders nothing at all.

import { frequencyData } from './mixer.js';

const states = new Map();

const BAR_COUNT = 128;
const DEFAULT_HUE = 262;
const MAX_DPR = 2;
const COLOUR_REFRESH_MS = 2000;
const IDLE_POLL_MS = 250;
const FADE = 0.88;

const VERTEX_SOURCE = `#version 300 es
out vec2 vUv;
void main() {
    // Fullscreen triangle from gl_VertexID — no buffers needed.
    vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = pos;
    gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}`;

const FRAGMENT_SOURCE = `#version 300 es
precision mediump float;
in vec2 vUv;
uniform sampler2D uBins;
uniform vec3 uColor;
uniform float uBarCount;
out vec4 outColor;
void main() {
    float column = vUv.x * uBarCount;
    float value = texture(uBins, vec2((floor(column) + 0.5) / uBarCount, 0.5)).r;

    // Thin dark gap between bars.
    float within = fract(column);
    float bar = step(0.08, within) * step(within, 0.92);

    // Solid body below the tip, exponential glow bleeding upward above it.
    float body = step(vUv.y, value) * (0.55 + 0.45 * vUv.y / max(value, 0.001));
    float above = max(vUv.y - value, 0.0);
    float glow = exp(-above * 14.0) * step(value, vUv.y) * (0.25 + value * 0.6);

    float intensity = bar * (body + glow);
    outColor = vec4(uColor * intensity, intensity);
}`;

function reducedMotion() {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function readHue() {
    const raw = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--pm-key-hue'));
    return Number.isFinite(raw) ? raw : DEFAULT_HUE;
}

/// hsl -> [r, g, b] in 0..1, for the shader colour uniform and the 2D fill.
function hslToRgb(h, s, l) {
    const k = (n) => (n + h / 30) % 12;
    const a = s * Math.min(l, 1 - l);
    const channel = (n) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
    return [channel(0), channel(8), channel(4)];
}

function resize(state) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(state.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(state.canvas.clientHeight * dpr), 1);
    if (state.canvas.width !== width || state.canvas.height !== height) {
        state.canvas.width = width;
        state.canvas.height = height;
        if (state.gl) {
            state.gl.viewport(0, 0, width, height);
        }
    }
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

/// Builds the WebGL2 pipeline, or returns null so the caller falls back to 2D.
function initGl(state) {
    const gl = state.canvas.getContext('webgl2', { alpha: true, antialias: false });
    if (!gl) {
        return false;
    }
    const vertex = compile(gl, gl.VERTEX_SHADER, VERTEX_SOURCE);
    const fragment = compile(gl, gl.FRAGMENT_SHADER, FRAGMENT_SOURCE);
    if (!vertex || !fragment) {
        return false;
    }
    const program = gl.createProgram();
    gl.attachShader(program, vertex);
    gl.attachShader(program, fragment);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        return false;
    }
    gl.useProgram(program);

    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, BAR_COUNT, 1, 0, gl.RED, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

    gl.uniform1i(gl.getUniformLocation(program, 'uBins'), 0);
    gl.uniform1f(gl.getUniformLocation(program, 'uBarCount'), BAR_COUNT);

    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.clearColor(0, 0, 0, 0);

    state.gl = gl;
    state.program = program;
    state.texture = texture;
    state.colorLocation = gl.getUniformLocation(program, 'uColor');
    return true;
}

/// Folds the analyser's bins (whatever length the mixer's FFT produced) down to BAR_COUNT targets,
/// then eases the displayed bars toward them: instant attack, smoothed decay.
function updateBins(state, data) {
    const bins = state.bins;
    if (data) {
        const step = data.length / BAR_COUNT;
        for (let i = 0; i < BAR_COUNT; i++) {
            const target = data[Math.min(Math.floor(i * step), data.length - 1)] / 255;
            bins[i] = target > bins[i] ? target : bins[i] * 0.75 + target * 0.25;
        }
        return true;
    }
    let alive = false;
    for (let i = 0; i < BAR_COUNT; i++) {
        bins[i] *= FADE;
        if (bins[i] > 0.004) {
            alive = true;
        } else {
            bins[i] = 0;
        }
    }
    return alive;
}

function refreshColour(state, now) {
    if (now - state.lastColourRead > COLOUR_REFRESH_MS) {
        state.rgb = hslToRgb(readHue(), 0.75, 0.6);
        state.lastColourRead = now;
    }
}

function drawGl(state) {
    const gl = state.gl;
    for (let i = 0; i < BAR_COUNT; i++) {
        state.bytes[i] = Math.round(state.bins[i] * 255);
    }
    gl.bindTexture(gl.TEXTURE_2D, state.texture);
    gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, BAR_COUNT, 1, gl.RED, gl.UNSIGNED_BYTE, state.bytes);
    gl.uniform3f(state.colorLocation, state.rgb[0], state.rgb[1], state.rgb[2]);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
}

function draw2d(state) {
    const ctx = state.ctx2d;
    const w = state.canvas.width;
    const h = state.canvas.height;
    ctx.clearRect(0, 0, w, h);

    const [r, g, b] = state.rgb.map((c) => Math.round(c * 255));
    const barWidth = w / BAR_COUNT;
    const gap = Math.max(barWidth * 0.16, 0.5);
    for (let i = 0; i < BAR_COUNT; i++) {
        const value = state.bins[i];
        if (value <= 0) {
            continue;
        }
        const x = i * barWidth + gap / 2;
        const barHeight = value * h;
        ctx.fillStyle = `rgba(${r}, ${g}, ${b}, 0.85)`;
        ctx.fillRect(x, h - barHeight, barWidth - gap, barHeight);
        // Cheap glow: one translucent cap above the tip instead of per-bar gradients.
        const cap = Math.min(h * 0.08 * (0.4 + value), h - barHeight);
        if (cap > 0) {
            ctx.fillStyle = `rgba(${r}, ${g}, ${b}, ${0.25 * value})`;
            ctx.fillRect(x, h - barHeight - cap, barWidth - gap, cap);
        }
    }
}

/// One idle poll tick: restart the frame loop the moment the mixer produces bins again.
function idle(state) {
    state.idleTimer = setTimeout(() => {
        state.idleTimer = null;
        if (frequencyData() !== null) {
            state.raf = requestAnimationFrame((now) => frame(state, now));
        } else {
            idle(state);
        }
    }, IDLE_POLL_MS);
}

function frame(state, now) {
    state.raf = null;

    resize(state);
    refreshColour(state, now);

    const data = frequencyData();
    const alive = updateBins(state, data);

    if (state.gl) {
        drawGl(state);
    } else {
        draw2d(state);
    }

    if (data !== null || alive) {
        state.raf = requestAnimationFrame((next) => frame(state, next));
    } else {
        // Fully faded: stop burning frames and drop to a slow poll until playback returns.
        idle(state);
    }
}

/// Starts the spectrum strip on `canvas`. Renders nothing under prefers-reduced-motion.
export function init(canvas) {
    if (states.has(canvas)) {
        return;
    }
    const state = {
        canvas,
        gl: null,
        ctx2d: null,
        program: null,
        texture: null,
        colorLocation: null,
        bins: new Float32Array(BAR_COUNT),
        bytes: new Uint8Array(BAR_COUNT),
        rgb: hslToRgb(readHue(), 0.75, 0.6),
        lastColourRead: performance.now(),
        raf: null,
        idleTimer: null,
    };
    states.set(canvas, state);

    resize(state);
    if (reducedMotion()) {
        const ctx = canvas.getContext('2d');
        if (ctx) {
            ctx.clearRect(0, 0, canvas.width, canvas.height);
        }
        return;
    }

    let hasGl = false;
    try {
        hasGl = initGl(state);
    } catch {
        hasGl = false;
    }
    if (!hasGl) {
        state.gl = null;
        state.ctx2d = canvas.getContext('2d');
        if (!state.ctx2d) {
            states.delete(canvas);
            return;
        }
    }
    state.raf = requestAnimationFrame((now) => frame(state, now));
}

/// Stops frames and polls, releases GL resources, and forgets the canvas.
export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.raf !== null) {
        cancelAnimationFrame(state.raf);
    }
    if (state.idleTimer !== null) {
        clearTimeout(state.idleTimer);
    }
    if (state.gl) {
        state.gl.deleteTexture(state.texture);
        state.gl.deleteProgram(state.program);
    }
    states.delete(canvas);
}
