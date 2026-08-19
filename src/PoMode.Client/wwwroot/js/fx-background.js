// Audio-reactive ambient background. Preferred path: a WebGL2 fragment shader — layered value
// noise tinted with the detected key's hue, breathing with the mixer's master level. Fallback
// (no WebGL2): the original 2D drifting radial gradients. Both paths share one lifecycle: ~30 fps
// cap, freeze in silence, one static wash under prefers-reduced-motion. Purely decorative — the
// canvas draws behind the UI and carries no information.
//
// Colour comes from CSS custom properties (--pm-key-hue, --pm-bg) read at draw time but cached for
// ~2 s, so theme flips and key changes are picked up without a getComputedStyle per frame.

import { masterLevel } from './mixer.js';

const states = new Map();

const FRAME_MS = 1000 / 30;
const COLOUR_REFRESH_MS = 2000;
const DEFAULT_HUE = 262;
const MAX_DPR = 2;

/// The 2D-fallback drifting light blobs. Angular speeds are incommensurate so the pattern never loops.
const BLOBS = [
    { radius: 0.85, orbitX: 0.32, orbitY: 0.22, speed: 0.011, phase: 0.0, hueShift: 0, alpha: 1.0 },
    { radius: 0.7, orbitX: 0.28, orbitY: 0.3, speed: -0.017, phase: 2.1, hueShift: 38, alpha: 0.8 },
    { radius: 0.6, orbitX: 0.35, orbitY: 0.18, speed: 0.023, phase: 4.4, hueShift: -30, alpha: 0.65 },
];

const VERTEX_SHADER = `#version 300 es
void main() {
    // One oversized triangle covers the viewport with no vertex buffer at all.
    vec2 corners[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
    gl_Position = vec4(corners[gl_VertexID], 0.0, 1.0);
}`;

const FRAGMENT_SHADER = `#version 300 es
precision mediump float;
uniform float uTime;
uniform float uLevel;
uniform float uHue;   // degrees, the detected key
uniform vec3 uBg;     // theme background, 0..1
uniform vec2 uRes;
out vec4 fragColor;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
        mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x),
        f.y);
}

vec3 hueToRgb(float hueDegrees) {
    float h = hueDegrees / 60.0;
    vec3 k = mod(h + vec3(0.0, 4.0, 2.0), 6.0);
    return clamp(abs(k - 3.0) - 1.0, 0.0, 1.0);
}

void main() {
    vec2 uv = gl_FragCoord.xy / uRes;
    uv.x *= uRes.x / max(uRes.y, 1.0);
    float t = uTime * 0.03;

    // Two octaves of drifting noise; a third, faster one only fades in with the music level.
    float n = noise(uv * 1.8 + vec2(t, -t * 0.7)) * 0.55
        + noise(uv * 4.2 - vec2(t * 0.6, t)) * 0.3
        + noise(uv * 9.0 + vec2(t * 1.7, t * 0.4)) * 0.15 * uLevel;

    float glow = (0.045 + uLevel * 0.32) * smoothstep(0.35, 0.95, n);
    vec3 accent = mix(vec3(0.45), hueToRgb(uHue), 0.65);
    fragColor = vec4(uBg + accent * glow, 1.0);
}`;

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

/// "#rrggbb" → [r, g, b] in 0..1; anything unparseable comes back near-black.
function hexToRgb(hex) {
    const match = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
    if (!match) {
        return [0.07, 0.07, 0.09];
    }
    const value = parseInt(match[1], 16);
    return [((value >> 16) & 255) / 255, ((value >> 8) & 255) / 255, (value & 255) / 255];
}

/// Matches the backing store to the CSS box, DPR-capped so a 4K hidpi page stays cheap.
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

/// Tries to build the WebGL2 pipeline; null on any failure so init falls back to 2D.
function createGlPipeline(canvas) {
    try {
        const gl = canvas.getContext('webgl2', { alpha: false, antialias: false });
        if (!gl) {
            return null;
        }
        const compile = (type, source) => {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
                throw new Error(gl.getShaderInfoLog(shader) ?? 'shader compile failed');
            }
            return shader;
        };
        const program = gl.createProgram();
        gl.attachShader(program, compile(gl.VERTEX_SHADER, VERTEX_SHADER));
        gl.attachShader(program, compile(gl.FRAGMENT_SHADER, FRAGMENT_SHADER));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            return null;
        }
        gl.useProgram(program);
        return {
            gl,
            uniforms: {
                time: gl.getUniformLocation(program, 'uTime'),
                level: gl.getUniformLocation(program, 'uLevel'),
                hue: gl.getUniformLocation(program, 'uHue'),
                bg: gl.getUniformLocation(program, 'uBg'),
                res: gl.getUniformLocation(program, 'uRes'),
            },
        };
    } catch {
        return null;
    }
}

function drawShader(state, level, timeSeconds) {
    const { gl, uniforms } = state;
    const [r, g, b] = hexToRgb(state.colours.background);
    gl.uniform1f(uniforms.time, timeSeconds);
    gl.uniform1f(uniforms.level, level);
    gl.uniform1f(uniforms.hue, state.colours.hue);
    gl.uniform3f(uniforms.bg, r, g, b);
    gl.uniform2f(uniforms.res, state.canvas.width, state.canvas.height);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
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

function drawFrame(state, level, timeSeconds) {
    if (state.gl) {
        drawShader(state, level, timeSeconds);
    } else {
        drawBlobs(state, level, timeSeconds);
    }
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

        drawFrame(state, state.level, now / 1000);

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
    const pipeline = createGlPipeline(canvas);
    const state = {
        canvas,
        gl: pipeline?.gl ?? null,
        uniforms: pipeline?.uniforms ?? null,
        // getContext('2d') after a successful webgl2 context would return null; only the fallback asks.
        ctx: pipeline ? null : canvas.getContext('2d'),
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
        drawFrame(state, 0.15, 0);
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
