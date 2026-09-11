// The analysis wait, made into something worth watching.
//
// A run through the pipeline is the longest stretch of time the app asks anyone to sit still for,
// and until now it was a checklist and a spinner. This draws what each stage is actually doing, in
// its own idiom, and dissolves between them as the job advances:
//
//   Separating      one wash splitting into four bands — the stems coming apart
//   PitchTracking   scattered points condensing onto a single contour — a melody being found
//   ChordDetecting  a field crystallising into quantised blocks — harmony resolving into spans
//   ModalAnalysis   the whole picture tinting to the detected key and settling into rings
//
// It is decoration and it says so: nothing here is derived from the job's actual audio, notes or
// chords, because none of that exists yet while the stage is still running. It is an illustration of
// which stage is in progress, not a preview of its result — which is exactly why the stage names and
// durations stay on screen beside it.
//
// One fullscreen-triangle fragment shader, ~30 fps, WebGL2 only: without it the panel simply does not
// appear and the checklist above it carries the whole job on its own, which it always did.

import * as prefs from './fx-prefs.js';

const states = new Map();

const FRAME_MS = 1000 / 30;
const MAX_DPR = 2;
const DEFAULT_HUE = 262;
const COLOUR_REFRESH_MS = 2000;

/// Stage order, matching StageNames on the server. The index is the shader's `uStage`, and the
/// crossfade between two of them is what makes the panel read as one continuous process rather than
/// four unrelated animations.
const STAGES = ['separating', 'pitch', 'chords', 'modal'];

/// Seconds the dissolve between two stages takes. Long enough to see, short enough that a fast local
/// model does not spend its whole stage mid-transition.
const MORPH_SECONDS = 0.9;

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
uniform float uTime;
uniform float uStage;     // fractional: 1.5 is halfway from pitch-tracking to chord detection
uniform float uProgress;  // 0..1 through the whole job
uniform float uHue;       // the detected key, once there is one
uniform float uSettle;    // 1 while running, easing to 0 as a finished job calms down
uniform vec3 uBg;
uniform vec2 uRes;
out vec4 outColor;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

vec3 hueToRgb(float hueDegrees) {
    float h = hueDegrees / 60.0;
    vec3 k = mod(h + vec3(0.0, 4.0, 2.0), 6.0);
    return clamp(abs(k - 3.0) - 1.0, 0.0, 1.0);
}

/// Stage 0 — separation. A single band splits into four, the gaps widening with the stage's own
/// progress, each drifting at its own rate so they read as independent tracks rather than one
/// striped texture.
float layerSeparating(vec2 uv, float t) {
    float split = smoothstep(0.0, 0.75, t);
    float lanes = 4.0;
    float band = uv.y * lanes;
    float index = floor(band);
    float within = fract(band);
    // The gap opens as the split advances; at t=0 the lanes are flush and read as one wash.
    float gap = 0.06 + (split * 0.30);
    float body = smoothstep(gap, gap + 0.16, within) * smoothstep(1.0 - gap, 1.0 - gap - 0.16, within);
    float drift = sin((uv.x * 7.0) - (uTime * (0.7 + index * 0.35)) + (index * 2.1));
    return body * (0.35 + (0.35 * drift)) * (0.4 + (split * 0.6));
}

/// Stage 1 — pitch tracking. A field of scattered points is pulled onto one contour: every point
/// keeps its scattered position at t=0 and lerps onto the line as the stage runs, which is the
/// clearest picture of "a melody being extracted from everything else".
float layerPitch(vec2 uv, float t) {
    float pull = smoothstep(0.0, 0.85, t);
    float contour = 0.5 + (0.22 * sin(uv.x * 8.0 + uTime * 0.8)) + (0.08 * sin(uv.x * 19.0 - uTime * 1.3));

    float total = 0.0;
    for (int i = 0; i < 3; i++) {
        float fi = float(i);
        vec2 cell = vec2(46.0 + fi * 13.0, 26.0);
        vec2 id = floor(vec2(uv.x, uv.y) * cell);
        float scatter = hash(id + fi * 7.7);
        // Where this point sits before the melody is found, and where it ends up after.
        float loose = fract(scatter * 3.3 + (uTime * 0.05));
        float y = mix(loose, contour, pull);
        float d = abs(uv.y - y);
        float dot = exp(-d * (90.0 + pull * 130.0));
        // Points thin out as they converge — a found melody is one line, not a thousand samples.
        total += dot * mix(0.5, 0.16, pull) * step(0.35, scatter);
    }
    float line = exp(-abs(uv.y - contour) * 150.0) * pull;
    return clamp(total + line, 0.0, 1.0);
}

/// Stage 2 — chord detection. A smooth field quantises into blocks along the time axis, snapping to
/// wider and wider spans, which is what a chord track is: continuous harmony read as held regions.
float layerChords(vec2 uv, float t) {
    float crystal = smoothstep(0.0, 0.8, t);
    float spans = mix(26.0, 7.0, crystal);
    float column = floor(uv.x * spans);
    float held = hash(vec2(column, floor(uTime * 0.35)));
    // Block height stands in for a chord's weight; the smooth version underneath is what it
    // crystallised out of, and the mix between them is the stage's progress.
    float blockTop = 0.24 + (held * 0.5);
    float block = smoothstep(blockTop + 0.02, blockTop - 0.02, uv.y) * step(0.06, uv.y);
    float smoothField = smoothstep(0.62, 0.2, uv.y + (noise(vec2(uv.x * 5.0, uTime * 0.3)) * 0.28));
    float edge = smoothstep(0.012, 0.0, abs(fract(uv.x * spans) - 0.5) - 0.47) * crystal * 0.6;
    return mix(smoothField * 0.5, (block * 0.55) + edge, crystal);
}

/// Stage 3 — modal analysis. Rings settle around a centre and the whole field takes the key's hue;
/// this is the stage whose answer colours everything else in the app, so it is the one that looks
/// like the colour arriving.
float layerModal(vec2 uv, float t) {
    float settle = smoothstep(0.0, 0.9, t);
    vec2 p = uv - vec2(0.5, 0.5);
    p.x *= uRes.x / max(uRes.y, 1.0);
    float r = length(p);
    float rings = 0.5 + 0.5 * sin((r * mix(40.0, 13.0, settle)) - (uTime * 1.4));
    float falloff = exp(-r * 2.4);
    float sweep = smoothstep(0.02, 0.0, abs(fract((atan(p.y, p.x) / 6.2831853) + (uTime * 0.08)) - 0.5) - 0.48);
    return (rings * falloff * (0.35 + settle * 0.5)) + (sweep * falloff * 0.5 * settle);
}

/// Triangular weight around the current stage, so exactly two layers are ever being paid for.
float weightFor(float index) {
    return max(1.0 - abs(uStage - index), 0.0);
}

void main() {
    vec2 uv = vUv;
    // Within-stage progress. The job's overall progress is the only signal available — the stages do
    // not report their own — so the run is treated as four equal quarters and the current stage's
    // share of it is unpacked here. Right when the stages take similar times, approximate when they
    // do not, and it drives an illustration, so approximate is the correct amount of precision.
    float local = clamp((uProgress * 4.0) - floor(uStage), 0.0, 1.0);

    float value = 0.0;
    float w;
    w = weightFor(0.0); if (w > 0.0) { value += layerSeparating(uv, local) * w; }
    w = weightFor(1.0); if (w > 0.0) { value += layerPitch(uv, local) * w; }
    w = weightFor(2.0); if (w > 0.0) { value += layerChords(uv, local) * w; }
    w = weightFor(3.0); if (w > 0.0) { value += layerModal(uv, local) * w; }

    // The key's colour bleeds in as the modal stage approaches, which is the one honest piece of
    // information in the panel: by the time the picture is fully tinted, the key is known.
    float keyed = smoothstep(2.0, 3.0, uStage);
    vec3 neutral = vec3(0.55, 0.58, 0.72);
    vec3 tint = mix(neutral, hueToRgb(uHue), 0.35 + (keyed * 0.5));

    // A finished job stops performing: the pattern stays as a still, at a fraction of its intensity.
    value *= mix(0.22, 1.0, uSettle);

    vec3 rgb = uBg + (tint * value);
    // Vignette, so the panel sits in its card rather than ending at a hard rectangle.
    float vignette = smoothstep(1.1, 0.25, length((uv - 0.5) * vec2(1.25, 1.0)));
    outColor = vec4(mix(uBg, rgb, vignette), 1.0);
}`;

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
        gl = canvas.getContext('webgl2', { alpha: false, antialias: false });
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
    return {
        gl,
        program,
        uniforms: {
            time: gl.getUniformLocation(program, 'uTime'),
            stage: gl.getUniformLocation(program, 'uStage'),
            progress: gl.getUniformLocation(program, 'uProgress'),
            hue: gl.getUniformLocation(program, 'uHue'),
            settle: gl.getUniformLocation(program, 'uSettle'),
            bg: gl.getUniformLocation(program, 'uBg'),
            res: gl.getUniformLocation(program, 'uRes'),
        },
    };
}

/// Theme background and key hue, read from the root element's custom properties — the same source
/// fx-background.js uses, so the two never disagree about what colour the page is.
function readColours() {
    const style = getComputedStyle(document.documentElement);
    const rawHue = parseFloat(style.getPropertyValue('--pm-key-hue'));
    const hue = Number.isFinite(rawHue) ? rawHue : DEFAULT_HUE;

    const raw = style.getPropertyValue('--pm-bg').trim();
    let bg = [0.07, 0.07, 0.08];
    const hex = /^#([0-9a-f]{6})$/i.exec(raw);
    if (hex) {
        const n = parseInt(hex[1], 16);
        bg = [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    }
    return { hue, bg };
}

function resize(state) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(state.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(state.canvas.clientHeight * dpr), 1);
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

    if (now - state.lastColourRead > COLOUR_REFRESH_MS) {
        state.lastColourRead = now;
        state.colours = readColours();
    }

    // Eased toward the target rather than snapped, which is what produces the dissolve.
    const step = dt / MORPH_SECONDS;
    if (state.stage < state.targetStage) {
        state.stage = Math.min(state.stage + step, state.targetStage);
    } else if (state.stage > state.targetStage) {
        state.stage = Math.max(state.stage - step, state.targetStage);
    }
    state.settle += (state.targetSettle - state.settle) * Math.min(dt * 1.6, 1);

    resize(state);
    const { gl, uniforms } = state;
    gl.useProgram(state.program);
    gl.uniform1f(uniforms.time, (now - state.start) / 1000);
    gl.uniform1f(uniforms.stage, state.stage);
    gl.uniform1f(uniforms.progress, state.progress);
    gl.uniform1f(uniforms.hue, state.colours.hue);
    gl.uniform1f(uniforms.settle, state.settle);
    gl.uniform3fv(uniforms.bg, state.colours.bg);
    gl.uniform2f(uniforms.res, state.canvas.width, state.canvas.height);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    // A settled job is a still image; stop burning frames redrawing it.
    if (state.targetSettle === 0 && Math.abs(state.settle) < 0.01 && state.stage === state.targetStage) {
        cancelAnimationFrame(state.raf);
        state.raf = null;
    }
}

/// Starts the panel. Returns false when WebGL2 is unavailable or effects are off, in which case the
/// caller should leave the canvas hidden — there is no 2D fallback here on purpose: the checklist
/// beside it already carries every fact, and a flat approximation of this would be worse than none.
export function init(canvas) {
    if (!canvas || states.has(canvas)) {
        return false;
    }
    if (!prefs.allowsMotion() || !prefs.allowsHeavy()) {
        // 'subtle' opts out too. This is a full-width animated shader running for minutes at a
        // time; someone who asked for less motion did not mean "less, except during the long part".
        return false;
    }
    const pipeline = createPipeline(canvas);
    if (!pipeline) {
        return false;
    }
    const state = {
        canvas,
        gl: pipeline.gl,
        program: pipeline.program,
        uniforms: pipeline.uniforms,
        colours: readColours(),
        stage: 0,
        targetStage: 0,
        progress: 0,
        settle: 1,
        targetSettle: 1,
        start: performance.now(),
        lastFrame: 0,
        lastColourRead: performance.now(),
        raf: null,
        unsubscribe: null,
    };
    states.set(canvas, state);
    resize(state);
    state.unsubscribe = prefs.subscribe(() => {
        if (!prefs.allowsHeavy()) {
            dispose(canvas);
        }
    });
    state.raf = requestAnimationFrame((now) => frame(state, now));
    canvas.dataset.kiln = 'on';
    return true;
}

/// Tells the panel which stage is running and how far the job has got.
///
/// `stageKey` is one of the names in STAGES, or anything else for "not running" — a finished,
/// failed or cancelled job settles to a still rather than animating on under a result nobody is
/// waiting for.
export function setStage(canvas, stageKey, progress, running) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    const index = STAGES.indexOf(stageKey);
    if (index >= 0) {
        state.targetStage = index;
    }
    state.progress = Math.max(0, Math.min(Number(progress) || 0, 1));
    state.targetSettle = running ? 1 : 0;
    canvas.dataset.kilnStage = index >= 0 ? stageKey : 'idle';
    if (state.raf === null) {
        // Woken from the still it settled into — a job that resumes, or a new one starting in the
        // same panel.
        state.lastFrame = 0;
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
    state.unsubscribe?.();
    state.gl.deleteProgram(state.program);
    try {
        delete canvas.dataset.kiln;
        delete canvas.dataset.kilnStage;
    } catch {
        // Already detached.
    }
    states.delete(canvas);
}
