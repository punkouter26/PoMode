// Practice: the phrase you were asked to sing, and the line you actually sang, on one pitch/time
// plot that fills in live while you sing it.
//
// What this is NOT is a grader. Every number on the Practice page comes from ModeExerciseGrader on
// the server — that is the whole arrangement, and a browser that scored the take on the side would
// be a second opinion nobody asked for and nobody could reconcile. This draws what was heard against
// what was asked, and stops there. The one classification it makes is "is this pitch class in the
// set the server sent with the exercise", which is a lookup against server-supplied data in exactly
// the sense canvas.js maps a server-decided note role to a colour.
//
// Rendering is a single fullscreen-triangle fragment shader fed by two 512x1 RGBA8 textures — the
// target phrase and the live take — so the whole plot including its glow is one draw call per frame,
// and appending a sung sample is a one-pixel texSubImage2D rather than a geometry rebuild. Without
// WebGL2 a plain 2D-canvas renderer draws the same two lines without the bloom.
//
// The live samples arrive from practice-session.js directly rather than through Blazor: this runs at
// the microphone's frame rate and the codebase's rule is that per-frame work never crosses into C#.

import * as prefs from './fx-prefs.js';

/// Horizontal resolution of the plot in samples. 512 columns across a phrase of a few seconds is
/// finer than the pitch detector's own frame rate, so the limit on detail is the microphone, not this.
const COLUMNS = 512;

const MAX_DPR = 2;

/// Semitones of headroom above and below the phrase, so the target line never sits on the edge and a
/// singer who overshoots by a step is still on screen.
const PITCH_PADDING = 4;

/// Floor for the drawn pitch span. A characteristic-leap drill spans a few semitones, and plotting
/// three notes over the full height would turn a clean take into a wild-looking scribble.
const MIN_PITCH_SPAN = 14;

/// The one live plot on the page. A module-level singleton rather than a Map keyed by canvas,
/// because practice-session.js pushes samples from its microphone callback and threading a canvas
/// reference through the audio path to reach a Map would buy nothing.
let state = null;

const VERTEX_SOURCE = `#version 300 es
out vec2 vUv;
void main() {
    // Fullscreen triangle from gl_VertexID — no buffers needed, same trick as fx-spectrum.js.
    vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = pos;
    gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}`;

const FRAGMENT_SOURCE = `#version 300 es
precision mediump float;
in vec2 vUv;
uniform sampler2D uTarget;   // R = pitch 0..1, G = 1 where a target note sounds
uniform sampler2D uLive;     // R = pitch 0..1, G = 1 where a pitch was heard, B = 1 when in the mode
uniform vec3 uTargetColor;
uniform vec3 uInColor;       // heard, inside the mode
uniform vec3 uOutColor;      // heard, outside it
uniform vec2 uRes;
uniform float uProgress;     // 0..1, how far through the phrase we are
uniform float uLiveAlpha;    // fades the sung line in, so a fresh attempt does not pop
out vec4 outColor;

/// Vertical extent of a trace within one column, widened across the neighbouring taps.
///
/// Sampling a single column and thresholding |v - pitch| draws a dotted line wherever the melody
/// moves faster than one column per pixel — which on a characteristic leap is most of the
/// interesting part. Taking the min and max pitch over a few taps and filling between them draws
/// the connecting stroke instead, which is what a leap actually looks like.
vec2 spanAt(sampler2D tex, float u, float du) {
    float lo = 2.0;
    float hi = -1.0;
    for (int i = -2; i <= 2; i++) {
        vec2 s = texture(tex, vec2(u + (float(i) * du), 0.5)).rg;
        if (s.g > 0.5) {
            lo = min(lo, s.r);
            hi = max(hi, s.r);
        }
    }
    return vec2(lo, hi);
}

/// Distance from v to the filled interval, zero inside it.
float distanceToSpan(vec2 span, float v) {
    if (span.y < 0.0) {
        return 1e9;
    }
    return max(max(span.x - v, v - span.y), 0.0);
}

void main() {
    float du = 1.0 / ${COLUMNS}.0;
    float v = vUv.y;

    vec3 rgb = vec3(0.0);
    float alpha = 0.0;

    // The target phrase: a soft wide ghost with a brighter core, drawn under everything. Thicker
    // than the sung line on purpose — it is the thing being aimed at, not a competing reading.
    float dTarget = distanceToSpan(spanAt(uTarget, vUv.x, du), v);
    float targetGhost = exp(-dTarget * 46.0) * 0.5;
    float targetCore = smoothstep(0.012, 0.004, dTarget);
    float target = clamp(targetGhost + (targetCore * 0.55), 0.0, 1.0);
    rgb += uTargetColor * target;
    alpha = max(alpha, target * 0.75);

    // The sung line, drawn only up to the playhead so it appears to be written in real time.
    if (vUv.x <= uProgress) {
        vec2 liveSpan = spanAt(uLive, vUv.x, du);
        float dLive = distanceToSpan(liveSpan, v);
        // The in/out colour is read from the same column the trace came from, so a single stray
        // note is marked where it happened rather than tinting the whole line.
        float inMode = texture(uLive, vec2(vUv.x, 0.5)).b;
        vec3 liveColor = mix(uOutColor, uInColor, step(0.5, inMode));
        float glow = exp(-dLive * 70.0) * 0.65;
        float core = smoothstep(0.010, 0.003, dLive);
        float live = clamp(glow + core, 0.0, 1.0) * uLiveAlpha;
        rgb = mix(rgb, liveColor, clamp(live * 1.4, 0.0, 1.0));
        alpha = max(alpha, live);
    }

    // The playhead: a thin bright edge with a short trailing wash, so the eye has something to
    // follow while nothing is being sung.
    float head = abs(vUv.x - uProgress);
    float headLine = smoothstep(2.5 / uRes.x, 0.0, head);
    float headWash = exp(-head * 90.0) * 0.20 * step(vUv.x, uProgress);
    rgb += uInColor * (headLine * 0.9 + headWash);
    alpha = max(alpha, headLine * 0.85 + headWash);

    outColor = vec4(rgb, clamp(alpha, 0.0, 1.0));
}`;

function readColour(element, name, fallback) {
    const raw = getComputedStyle(element).getPropertyValue(name).trim();
    return parseColour(raw) ?? fallback;
}

/// Parses the hex and rgb() forms the theme tokens actually use into 0..1 RGB. Returns null on
/// anything else so the caller's fallback stands rather than a black line appearing.
function parseColour(raw) {
    if (!raw) {
        return null;
    }
    let match = /^#([0-9a-f]{6})$/i.exec(raw);
    if (match) {
        const n = parseInt(match[1], 16);
        return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    }
    match = /^#([0-9a-f]{3})$/i.exec(raw);
    if (match) {
        const [r, g, b] = match[1].split('').map((c) => parseInt(c + c, 16) / 255);
        return [r, g, b];
    }
    match = /^rgba?\(([^)]+)\)$/i.exec(raw);
    if (match) {
        const parts = match[1].split(/[\s,/]+/).filter(Boolean).map(Number);
        if (parts.length >= 3 && parts.every((p) => Number.isFinite(p))) {
            return [parts[0] / 255, parts[1] / 255, parts[2] / 255];
        }
    }
    return null;
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

function createGl(canvas) {
    const gl = canvas.getContext('webgl2', { alpha: true, antialias: false, premultipliedAlpha: false });
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

    const makeTexture = () => {
        const texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, COLUMNS, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        return texture;
    };

    gl.useProgram(program);
    const uniforms = {
        target: gl.getUniformLocation(program, 'uTarget'),
        live: gl.getUniformLocation(program, 'uLive'),
        targetColor: gl.getUniformLocation(program, 'uTargetColor'),
        inColor: gl.getUniformLocation(program, 'uInColor'),
        outColor: gl.getUniformLocation(program, 'uOutColor'),
        res: gl.getUniformLocation(program, 'uRes'),
        progress: gl.getUniformLocation(program, 'uProgress'),
        liveAlpha: gl.getUniformLocation(program, 'uLiveAlpha'),
    };
    gl.uniform1i(uniforms.target, 0);
    gl.uniform1i(uniforms.live, 1);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

    return { gl, program, uniforms, targetTex: makeTexture(), liveTex: makeTexture() };
}

function resize(s) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(s.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(s.canvas.clientHeight * dpr), 1);
    if (s.canvas.width !== width || s.canvas.height !== height) {
        s.canvas.width = width;
        s.canvas.height = height;
    }
    if (s.gl) {
        s.gl.viewport(0, 0, width, height);
    }
}

/// Maps a MIDI pitch into the drawn 0..1 band, or null when it falls outside it — a sung note an
/// octave out lands off the plot, which is the honest picture and matches what the grader will say.
function normalise(s, midi) {
    const value = (midi - s.minMidi) / Math.max(s.maxMidi - s.minMidi, 1);
    return value >= 0 && value <= 1 ? value : null;
}

function columnFor(s, seconds) {
    if (!(s.duration > 0)) {
        return 0;
    }
    const column = Math.floor((seconds / s.duration) * COLUMNS);
    return Math.max(0, Math.min(COLUMNS - 1, column));
}

/// Rebuilds the target texture from the note list. Each note fills the columns it sounds through,
/// which is what gives rests their gaps: a column no note covers keeps presence 0 and is skipped by
/// the shader rather than being interpolated across.
function writeTargets(s, notes) {
    s.targetData.fill(0);
    for (const note of notes) {
        const pitch = normalise(s, note.midiPitch ?? note.MidiPitch);
        if (pitch === null) {
            continue;
        }
        const start = note.startSec ?? note.StartSec ?? 0;
        const duration = note.durationSec ?? note.DurationSec ?? 0;
        const from = columnFor(s, start);
        const to = columnFor(s, start + Math.max(duration, 0.02));
        for (let column = from; column <= to; column++) {
            const offset = column * 4;
            s.targetData[offset] = Math.round(pitch * 255);
            s.targetData[offset + 1] = 255;
        }
    }
    if (s.gl) {
        s.gl.bindTexture(s.gl.TEXTURE_2D, s.targetTex);
        s.gl.texSubImage2D(s.gl.TEXTURE_2D, 0, 0, 0, COLUMNS, 1, s.gl.RGBA, s.gl.UNSIGNED_BYTE, s.targetData);
    }
}

function draw(s, now) {
    s.raf = requestAnimationFrame((next) => draw(s, next));
    if (s.dirty || now - s.lastDraw > 200) {
        s.lastDraw = now;
        s.dirty = false;
        render(s);
    }
}

function render(s) {
    // Eased rather than stepped so the sung line arrives rather than blinking on, and so a phrase
    // reset visibly clears instead of swapping between two takes in one frame.
    s.liveAlpha += ((s.liveTarget - s.liveAlpha) * 0.18);

    if (s.gl) {
        const { gl, uniforms } = s;
        gl.useProgram(s.program);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, s.targetTex);
        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, s.liveTex);
        gl.uniform3fv(uniforms.targetColor, s.colours.target);
        gl.uniform3fv(uniforms.inColor, s.colours.inMode);
        gl.uniform3fv(uniforms.outColor, s.colours.outside);
        gl.uniform2f(uniforms.res, s.canvas.width, s.canvas.height);
        gl.uniform1f(uniforms.progress, s.progress);
        gl.uniform1f(uniforms.liveAlpha, s.liveAlpha);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        return;
    }
    render2d(s);
}

/// The no-WebGL2 path: the same two traces as plain strokes. No bloom, no playhead wash — the
/// information is identical, the polish is not, which is the right way round for a fallback.
function render2d(s) {
    const ctx = s.ctx2d;
    if (!ctx) {
        return;
    }
    const { width, height } = s.canvas;
    ctx.clearRect(0, 0, width, height);

    const strokeFrom = (data, colour, lineWidth, limitColumn) => {
        ctx.strokeStyle = colour;
        ctx.lineWidth = lineWidth;
        ctx.lineJoin = 'round';
        ctx.lineCap = 'round';
        ctx.beginPath();
        let drawing = false;
        for (let column = 0; column <= limitColumn; column++) {
            const offset = column * 4;
            if (data[offset + 1] < 128) {
                drawing = false;
                continue;
            }
            const x = (column / (COLUMNS - 1)) * width;
            const y = height - ((data[offset] / 255) * height);
            if (drawing) {
                ctx.lineTo(x, y);
            } else {
                ctx.moveTo(x, y);
                drawing = true;
            }
        }
        ctx.stroke();
    };

    const css = (rgb, alpha) =>
        `rgba(${Math.round(rgb[0] * 255)},${Math.round(rgb[1] * 255)},${Math.round(rgb[2] * 255)},${alpha})`;

    strokeFrom(s.targetData, css(s.colours.target, 0.55), Math.max(height * 0.05, 4), COLUMNS - 1);
    if (s.liveAlpha > 0.02) {
        strokeFrom(s.liveData, css(s.colours.inMode, s.liveAlpha), Math.max(height * 0.022, 2),
            columnFor(s, s.progress * s.duration));
    }
}

/// Starts the plot. Draws nothing and starts no loop when effects are off — the page keeps its
/// numbers, which are the part that matters.
export function init(canvas) {
    dispose();
    if (!canvas || typeof canvas.getContext !== 'function' || !prefs.allowsMotion()) {
        return false;
    }
    const pipeline = createGl(canvas);
    const next = {
        canvas,
        gl: pipeline?.gl ?? null,
        program: pipeline?.program ?? null,
        uniforms: pipeline?.uniforms ?? null,
        targetTex: pipeline?.targetTex ?? null,
        liveTex: pipeline?.liveTex ?? null,
        // getContext('2d') after a successful webgl2 context returns null; only the fallback asks.
        ctx2d: pipeline ? null : canvas.getContext('2d'),
        targetData: new Uint8Array(COLUMNS * 4),
        liveData: new Uint8Array(COLUMNS * 4),
        minMidi: 55,
        maxMidi: 79,
        duration: 1,
        progress: 0,
        liveAlpha: 0,
        liveTarget: 0,
        modeClasses: null,
        lastColumn: -1,
        colours: {
            target: readColour(canvas, '--pm-chord-block', [0.55, 0.55, 0.62]),
            inMode: readColour(canvas, '--pm-note-in-mode', [0.2, 0.83, 0.6]),
            outside: readColour(canvas, '--pm-note-outside', [0.97, 0.44, 0.44]),
        },
        dirty: true,
        lastDraw: 0,
        raf: null,
        resizeObserver: null,
        unsubscribe: null,
    };
    if (!next.gl && !next.ctx2d) {
        return false;
    }
    state = next;
    resize(state);
    state.resizeObserver = new ResizeObserver(() => {
        if (state) {
            resize(state);
            state.dirty = true;
        }
    });
    state.resizeObserver.observe(canvas);
    // A level change tears the plot down; the page re-inits it on the next attempt. Rebuilding here
    // would need the exercise back, which only the page has.
    state.unsubscribe = prefs.subscribe(() => {
        if (!prefs.allowsMotion()) {
            dispose();
        }
    });
    state.raf = requestAnimationFrame((now) => draw(state, now));
    canvas.dataset.ribbon = state.gl ? 'webgl2' : '2d';
    return true;
}

/// Loads the phrase to aim at.
///
/// `modePitchClasses` is the mode's own note set as the server sent it with the exercise. Passing it
/// in rather than deriving it here is the point: the browser is told which notes are inside, it does
/// not work it out.
export function setTargets(notes, durationSec, modePitchClasses) {
    if (!state) {
        return;
    }
    const list = Array.isArray(notes) ? notes : [];
    let min = Infinity;
    let max = -Infinity;
    for (const note of list) {
        const midi = note.midiPitch ?? note.MidiPitch;
        if (Number.isFinite(midi)) {
            min = Math.min(min, midi);
            max = Math.max(max, midi);
        }
    }
    if (!Number.isFinite(min)) {
        min = 60;
        max = 72;
    }
    min -= PITCH_PADDING;
    max += PITCH_PADDING;
    const short = MIN_PITCH_SPAN - (max - min);
    if (short > 0) {
        min -= short / 2;
        max += short / 2;
    }

    state.minMidi = min;
    state.maxMidi = max;
    state.duration = durationSec > 0 ? durationSec : 1;
    state.modeClasses = Array.isArray(modePitchClasses) && modePitchClasses.length > 0
        ? new Set(modePitchClasses.map((pc) => ((pc % 12) + 12) % 12))
        : null;
    writeTargets(state, list);
    clearLive();
}

/// Appends one heard pitch. Called from practice-session.js's microphone callback, so it must stay
/// cheap: it writes one pixel and flags a redraw, and does no allocation in the common case.
///
/// `seconds` is measured from the phrase's downbeat — the same origin the grader uses — so a singer
/// who comes in late is drawn late rather than being quietly shifted onto the target.
export function pushSample(seconds, midi) {
    if (!state || seconds < 0) {
        return;
    }
    const column = columnFor(state, seconds);
    state.progress = Math.max(state.progress, Math.min(seconds / state.duration, 1));

    if (Number.isFinite(midi) && midi > 0) {
        const pitch = normalise(state, midi);
        if (pitch !== null) {
            const offset = column * 4;
            state.liveData[offset] = Math.round(pitch * 255);
            state.liveData[offset + 1] = 255;
            // No mode set means no claim: everything draws in the neutral "inside" colour rather
            // than the browser guessing which notes were wrong.
            const inMode = !state.modeClasses
                || state.modeClasses.has(((Math.round(midi) % 12) + 12) % 12);
            state.liveData[offset + 2] = inMode ? 255 : 0;
            if (state.gl) {
                state.gl.bindTexture(state.gl.TEXTURE_2D, state.liveTex);
                state.gl.texSubImage2D(state.gl.TEXTURE_2D, 0, column, 0, 1, 1,
                    state.gl.RGBA, state.gl.UNSIGNED_BYTE, state.liveData.subarray(offset, offset + 4));
            }
            state.lastColumn = column;
        }
    }
    state.liveTarget = 1;
    state.dirty = true;
}

/// Moves the playhead without recording a pitch, so the line keeps advancing through a rest.
export function setProgress(seconds) {
    if (!state) {
        return;
    }
    state.progress = Math.max(0, Math.min(seconds / state.duration, 1));
    state.dirty = true;
}

/// Empties the sung line, ready for another attempt.
export function clearLive() {
    if (!state) {
        return;
    }
    state.liveData.fill(0);
    state.progress = 0;
    state.liveAlpha = 0;
    state.liveTarget = 0;
    state.lastColumn = -1;
    if (state.gl) {
        state.gl.bindTexture(state.gl.TEXTURE_2D, state.liveTex);
        state.gl.texSubImage2D(state.gl.TEXTURE_2D, 0, 0, 0, COLUMNS, 1,
            state.gl.RGBA, state.gl.UNSIGNED_BYTE, state.liveData);
    }
    state.dirty = true;
}

/// Holds the finished take on screen at full length, so the plot after grading shows the whole
/// attempt next to the whole phrase rather than stopping wherever the last note happened to land.
export function settle() {
    if (!state) {
        return;
    }
    state.progress = 1;
    state.liveTarget = 1;
    state.dirty = true;
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
        state.gl.deleteTexture(state.targetTex);
        state.gl.deleteTexture(state.liveTex);
        state.gl.deleteProgram(state.program);
    }
    try {
        delete state.canvas.dataset.ribbon;
    } catch {
        // The canvas may already be detached.
    }
    state = null;
}
