// The Mode Lab's nine cards, given nine visual identities.
//
// The strip is the conceptual heart of the app — the same seven notes, nine ways of deciding which
// one is home — and the cards were flat rectangles distinguished by a border colour. This lays a
// glow behind them: each card gets a cell tinted with its own mode colour, the selected card burns
// brighter and breathes with whatever the player is actually sounding, and a change of selection
// morphs between the two rather than cutting.
//
// One WebGL2 context for the whole strip, not one per card. Seven or nine contexts on a page that
// already runs the ambient background, the spectrum strip, the analysis canvas and sometimes a
// three.js landscape would be crowding the browser's context limit for pure decoration, and losing
// a context is a far worse outcome than not having the effect. So the cells are passed to a single
// shader as measured rectangles, which also means a strip that wraps onto two rows on a phone draws
// correctly with no extra work.
//
// No colour table lives here. Each card already carries its mode's colour as the `--mode-color`
// custom property in ModeLab.razor.css, and this reads it off the element — the same rule canvas.js
// follows, and the reason a retheme cannot leave the glow behind.

import * as prefs from './fx-prefs.js';
import { playbackLevel } from './modal-player.js';

const states = new Map();

/// Cells the shader can draw. Nine is every card the strip can hold — seven diatonic modes plus the
/// two pentatonics — so the array never needs resizing.
const MAX_CELLS = 9;

const FRAME_MS = 1000 / 30;
const MAX_DPR = 2;

/// How long a selection change takes to travel. Slow enough to read as a morph, short enough not to
/// lag behind someone auditioning modes quickly.
const MORPH_SECONDS = 0.45;

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
uniform float uLevel;                 // 0..1, what the player is sounding right now
uniform int uCount;
uniform vec4 uRects[${MAX_CELLS}];    // x, y, w, h in pixels, y down from the strip's top
uniform vec3 uColors[${MAX_CELLS}];
uniform float uWeights[${MAX_CELLS}]; // 0..1 selection weight, eased by the CPU into a morph
out vec4 outColor;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

/// Signed distance to a rounded rectangle, negative inside. The glow is built from this so it hugs
/// the card's actual corner radius rather than being a blurred box behind a rounded one.
float roundedBox(vec2 p, vec2 half, float radius) {
    vec2 d = abs(p) - half + radius;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - radius;
}

void main() {
    // Pixel space with y running down, matching getBoundingClientRect so the rects need no flipping
    // on the way in.
    vec2 px = vec2(vUv.x * uRes.x, (1.0 - vUv.y) * uRes.y);

    vec3 rgb = vec3(0.0);
    float alpha = 0.0;

    for (int i = 0; i < ${MAX_CELLS}; i++) {
        if (i >= uCount) {
            break;
        }
        vec4 rect = uRects[i];
        vec2 half = rect.zw * 0.5;
        vec2 centre = rect.xy + half;
        vec2 local = px - centre;

        float weight = uWeights[i];
        float d = roundedBox(local, half, min(min(half.x, half.y), 14.0));

        // Breathing. The selected card pulses with the audio; the others drift slowly on their own
        // phase so the strip is never a row of synchronised blinking lights.
        float phase = float(i) * 1.9;
        float idle = 0.5 + (0.5 * sin((uTime * 0.6) + phase));
        float pulse = mix(idle * 0.35, 0.55 + (uLevel * 1.5), weight);

        // Outside the card: a halo that reaches a little past the border.
        float halo = exp(max(d, 0.0) * -0.055) * (0.18 + (weight * 0.55)) * (0.55 + (pulse * 0.75));

        // Inside: a soft wash with grain, strongest near the bottom edge so the card looks lit from
        // under rather than uniformly filled, which would just read as a background colour.
        float inside = smoothstep(0.0, -18.0, d);
        float grain = noise(vec2(px.x * 0.02, (px.y * 0.02) - (uTime * 0.25)));
        float lift = smoothstep(1.0, -0.2, (local.y / max(half.y, 1.0)));
        float wash = inside * (0.05 + (weight * 0.20)) * (0.55 + (grain * 0.55)) * (0.4 + lift * 0.8) * (0.6 + pulse * 0.6);

        // A travelling sheen across the selected card only — the one piece of motion that says
        // "this is the mode you are listening to" without moving the card itself.
        float sweep = 0.0;
        if (weight > 0.01) {
            float along = (local.x / max(half.x, 1.0)) * 0.5 + 0.5;
            float head = fract(uTime * 0.22);
            sweep = exp(-abs(along - head) * 9.0) * inside * weight * 0.22;
        }

        float amount = halo + wash + sweep;
        rgb += uColors[i] * amount;
        alpha = max(alpha, amount);
    }

    outColor = vec4(rgb, clamp(alpha, 0.0, 1.0));
}`;

/// Parses the `--mode-color` the card's stylesheet already carries. Falls back to a neutral rather
/// than to a guessed hue: a mode whose colour could not be read should look unremarkable, not wrong.
function parseColour(raw) {
    const text = (raw || '').trim();
    let match = /^#([0-9a-f]{6})$/i.exec(text);
    if (match) {
        const n = parseInt(match[1], 16);
        return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    }
    match = /^#([0-9a-f]{3})$/i.exec(text);
    if (match) {
        return match[1].split('').map((c) => parseInt(c + c, 16) / 255);
    }
    match = /^rgba?\(([^)]+)\)$/i.exec(text);
    if (match) {
        const parts = match[1].split(/[\s,/]+/).filter(Boolean).map(Number);
        if (parts.length >= 3 && parts.every(Number.isFinite)) {
            return [parts[0] / 255, parts[1] / 255, parts[2] / 255];
        }
    }
    return [0.45, 0.47, 0.55];
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
            level: gl.getUniformLocation(program, 'uLevel'),
            count: gl.getUniformLocation(program, 'uCount'),
            rects: gl.getUniformLocation(program, 'uRects'),
            colors: gl.getUniformLocation(program, 'uColors'),
            weights: gl.getUniformLocation(program, 'uWeights'),
        },
    };
}

/// Re-measures every card against the strip. Called on resize, on layout changes and whenever the
/// selection moves, because a selected card can change size and a stale rect would leave the glow
/// visibly behind the border it is supposed to be hugging.
function measure(state) {
    const cards = state.container.querySelectorAll('.compact-mode-card');
    const host = state.container.getBoundingClientRect();
    const dpr = state.dpr;
    let count = 0;
    for (const card of cards) {
        if (count >= MAX_CELLS) {
            break;
        }
        const rect = card.getBoundingClientRect();
        const base = count * 4;
        state.rects[base] = (rect.left - host.left) * dpr;
        state.rects[base + 1] = (rect.top - host.top) * dpr;
        state.rects[base + 2] = rect.width * dpr;
        state.rects[base + 3] = rect.height * dpr;

        const colour = parseColour(getComputedStyle(card).getPropertyValue('--mode-color'));
        state.colors[count * 3] = colour[0];
        state.colors[count * 3 + 1] = colour[1];
        state.colors[count * 3 + 2] = colour[2];

        // The page owns which card is selected; this only reads the class it already sets.
        state.targets[count] = card.classList.contains('selected') ? 1 : 0;
        count++;
    }
    state.count = count;
}

function resize(state) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    state.dpr = dpr;
    const width = Math.max(Math.round(state.container.clientWidth * dpr), 1);
    const height = Math.max(Math.round(state.container.clientHeight * dpr), 1);
    if (state.canvas.width !== width || state.canvas.height !== height) {
        state.canvas.width = width;
        state.canvas.height = height;
        state.gl.viewport(0, 0, width, height);
    }
}

function frame(state, now) {
    state.raf = requestAnimationFrame((next) => frame(state, next));
    if (now - state.lastFrame < FRAME_MS) {
        return;
    }
    const dt = Math.min((now - state.lastFrame) / 1000, 0.2);
    state.lastFrame = now;

    if (state.remeasure) {
        state.remeasure = false;
        resize(state);
        measure(state);
    }

    // Selection weights ease toward their targets, which is the morph: the old card's glow recedes
    // while the new one's rises, so the strip never blinks between two states.
    const step = dt / MORPH_SECONDS;
    let moving = false;
    for (let i = 0; i < state.count; i++) {
        const target = state.targets[i];
        if (state.weights[i] < target) {
            state.weights[i] = Math.min(state.weights[i] + step, target);
            moving = true;
        } else if (state.weights[i] > target) {
            state.weights[i] = Math.max(state.weights[i] - step, target);
            moving = true;
        }
    }

    // Smoothed so a percussive attack swells the glow rather than strobing it.
    const level = playbackLevel();
    state.level += (level - state.level) * (level > state.level ? 0.5 : 0.08);

    const { gl, uniforms } = state;
    gl.useProgram(state.program);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.uniform2f(uniforms.res, state.canvas.width, state.canvas.height);
    gl.uniform1f(uniforms.time, (now - state.start) / 1000);
    gl.uniform1f(uniforms.level, Math.min(state.level * 2.2, 1));
    gl.uniform1i(uniforms.count, state.count);
    gl.uniform4fv(uniforms.rects, state.rects);
    gl.uniform3fv(uniforms.colors, state.colors);
    gl.uniform1fv(uniforms.weights, state.weights);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    // Unused today, but kept because the next thing anyone adds here will want to know whether the
    // strip is still settling; asserting it in a test is cheaper than re-deriving it.
    state.canvas.dataset.stripSettling = moving ? '1' : '0';
}

/// Starts the strip glow.
///
/// Both elements come from the page: `canvas` is the layer to draw on and `container` is the element
/// whose `.compact-mode-card` children get measured. The canvas is authored in the Razor markup
/// rather than created here on purpose — Blazor diffs the strip's children by index when the mode
/// list re-renders, and a node this module had spliced in would be an unaccounted-for child sitting
/// in the middle of that.
export function init(canvas, container) {
    if (!canvas || !container || states.has(container)) {
        return false;
    }
    if (!prefs.allowsMotion()) {
        return false;
    }
    const pipeline = createPipeline(canvas);
    if (!pipeline) {
        return false;
    }

    const state = {
        container,
        canvas,
        gl: pipeline.gl,
        program: pipeline.program,
        uniforms: pipeline.uniforms,
        dpr: 1,
        count: 0,
        rects: new Float32Array(MAX_CELLS * 4),
        colors: new Float32Array(MAX_CELLS * 3),
        weights: new Float32Array(MAX_CELLS),
        targets: new Float32Array(MAX_CELLS),
        level: 0,
        start: performance.now(),
        lastFrame: 0,
        remeasure: true,
        raf: null,
        resizeObserver: null,
        unsubscribe: null,
    };
    states.set(container, state);

    resize(state);
    measure(state);
    // The first frame starts at the current selection rather than fading up to it: a page that has
    // just loaded should look settled, not like it is mid-animation.
    state.weights.set(state.targets);

    state.resizeObserver = new ResizeObserver(() => {
        state.remeasure = true;
    });
    state.resizeObserver.observe(container);
    state.unsubscribe = prefs.subscribe(() => {
        if (!prefs.allowsMotion()) {
            dispose(container);
        }
    });
    state.raf = requestAnimationFrame((now) => frame(state, now));
    return true;
}

/// Re-reads which card is selected. Called from the page after a mode change, because the selection
/// lives in a CSS class Blazor rewrites and there is no event to observe it from.
export function refresh(container) {
    const state = states.get(container);
    if (state) {
        state.remeasure = true;
    }
}

export function dispose(container) {
    const state = states.get(container);
    if (!state) {
        return;
    }
    if (state.raf !== null) {
        cancelAnimationFrame(state.raf);
    }
    state.resizeObserver?.disconnect();
    state.unsubscribe?.();
    state.gl.deleteProgram(state.program);
    states.delete(container);
}
