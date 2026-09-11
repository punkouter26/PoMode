// A WebGPU compute-shader particle field that drifts on the music, drawn behind the spectrum strip.
//
// This is the one effect in the app that genuinely needs more than WebGL2. Tens of thousands of
// particles, each integrated every frame against the live spectrum, is a compute problem: on the
// GPU it is one dispatch and one instanced draw, while the same thing done per-particle in JS would
// spend the whole frame budget in a loop and the same thing done as a WebGL2 transform-feedback pass
// would be considerably more code for the same result.
//
// So it is strictly additive. Every browser without WebGPU — which today is most of them — gets
// exactly what it got before: fx-spectrum.js's WebGL2 bars, or its 2D fallback under that. `init`
// returns false and the caller carries on. Nothing in the app is gated behind this file, and no
// information is carried by it: the bars above it hold the actual spectrum, and this is the wash
// behind them.
//
// Held to `prefs.allowsHeavy()` — this is the definition of the expensive tier the middle effects
// setting exists to decline.

import { frequencyData, masterLevel } from './mixer.js';
import * as prefs from './fx-prefs.js';

const states = new Map();

/// Particles at full intensity. Chosen so one workgroup dispatch stays small and the whole buffer
/// fits comfortably in a few megabytes: 48k x 32 bytes is 1.5 MB.
const MAX_PARTICLES = 49152;

const WORKGROUP_SIZE = 64;

/// Spectrum bands fed to the simulation. Fewer than the analyser's bins on purpose — a particle
/// needs the shape of the sound, not its resolution, and 32 floats is one tidy uniform upload.
const BANDS = 32;

const MAX_DPR = 2;
const DEFAULT_HUE = 262;
const COLOUR_REFRESH_MS = 2000;

/// Uniform block: time, dt, level, hue, resolution (2), padding (2) = 8 floats, then 32 band
/// energies. std140-ish alignment is satisfied by keeping everything f32 and 16-byte aligned.
const UNIFORM_FLOATS = 8 + BANDS;

const SHADER = /* wgsl */ `
struct Particle {
    pos  : vec2<f32>,
    vel  : vec2<f32>,
    life : f32,
    seed : f32,
    band : f32,
    pad  : f32,
};

struct Uniforms {
    time  : f32,
    dt    : f32,
    level : f32,
    hue   : f32,
    res   : vec2<f32>,
    pad   : vec2<f32>,
    bands : array<vec4<f32>, ${BANDS / 4}>,
};

// The same buffer, bound twice with different access. WebGPU forbids a read_write storage buffer
// in a vertex stage, so the simulation gets the writable view and the draw gets a read-only one;
// one buffer, two bindings, no copy between them.
@group(0) @binding(0) var<storage, read_write> particles : array<Particle>;
@group(0) @binding(1) var<uniform> u : Uniforms;
@group(0) @binding(2) var<storage, read> particlesRead : array<Particle>;

fn bandAt(index : u32) -> f32 {
    let i = min(index, ${BANDS - 1}u);
    return u.bands[i / 4u][i % 4u];
}

fn hash(n : f32) -> f32 {
    return fract(sin(n * 127.1) * 43758.5453);
}

fn hueToRgb(deg : f32) -> vec3<f32> {
    let h = deg / 60.0;
    let k = (h + vec3<f32>(0.0, 4.0, 2.0)) % 6.0;
    return clamp(abs(k - 3.0) - 1.0, vec3<f32>(0.0), vec3<f32>(1.0));
}

/// Puts a particle back at the bottom of the field, on the band it will be driven by. Respawning
/// rather than allocating is the whole reason the buffer is a fixed size.
fn respawn(p : ptr<function, Particle>, index : u32, time : f32) {
    let r1 = hash(f32(index) + time * 0.017);
    let r2 = hash(f32(index) * 1.37 + time * 0.031);
    (*p).pos = vec2<f32>(r1, -0.04 - (r2 * 0.08));
    (*p).vel = vec2<f32>((r2 - 0.5) * 0.02, 0.02 + (r1 * 0.05));
    (*p).life = 0.0;
    (*p).seed = r2;
    (*p).band = floor(r1 * ${BANDS}.0);
}

@compute @workgroup_size(${WORKGROUP_SIZE})
fn simulate(@builtin(global_invocation_id) gid : vec3<u32>) {
    let index = gid.x;
    if (index >= arrayLength(&particles)) {
        return;
    }
    var p = particles[index];

    // A never-initialised buffer arrives as zeroes; treating a zero seed as "unborn" seeds the whole
    // field on the first frame without a separate initialisation pass.
    if (p.seed == 0.0) {
        respawn(&p, index, u.time);
        particles[index] = p;
        return;
    }

    let energy = bandAt(u32(p.band));

    // The band pushes its own column of particles upward, so a bass hit lifts the left of the field
    // and a cymbal lifts the right — the motion is legibly the music rather than generic drift.
    p.vel.y = p.vel.y + ((0.015 + (energy * 0.36)) * u.dt * 3.0);
    // Lateral wander, so columns bloom outward instead of rising as rigid stripes.
    p.vel.x = p.vel.x + (sin((u.time * 0.7) + (p.seed * 12.0) + (p.pos.y * 5.0)) * 0.012 * u.dt * 3.0);
    p.vel = p.vel * 0.985;
    p.pos = p.pos + (p.vel * u.dt);
    p.life = p.life + (u.dt * (0.28 + (0.22 * p.seed)));

    if (p.life >= 1.0 || p.pos.y > 1.15) {
        respawn(&p, index, u.time);
    }
    particles[index] = p;
}

struct VertexOut {
    @builtin(position) position : vec4<f32>,
    @location(0) uv : vec2<f32>,
    @location(1) colour : vec4<f32>,
};

@vertex
fn vertexMain(@builtin(vertex_index) vertex : u32,
              @builtin(instance_index) instance : u32) -> VertexOut {
    // Six vertices per instance form a quad; built here rather than from a vertex buffer so the
    // draw needs no geometry at all.
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(-1.0, -1.0), vec2<f32>(1.0, -1.0), vec2<f32>(-1.0, 1.0),
        vec2<f32>(-1.0, 1.0),  vec2<f32>(1.0, -1.0), vec2<f32>(1.0, 1.0),
    );
    let corner = corners[vertex];
    let p = particlesRead[instance];

    let energy = bandAt(u32(p.band));
    // Fade in over the first fifth of life and out over the rest, so nothing pops into or out of
    // existence at the edges of the strip.
    let fade = smoothstep(0.0, 0.2, p.life) * (1.0 - smoothstep(0.35, 1.0, p.life));
    let size = (0.006 + (energy * 0.010) + (p.seed * 0.004)) * (0.6 + (u.level * 0.9));

    // Aspect-corrected so a particle is round on a wide strip.
    let aspect = u.res.x / max(u.res.y, 1.0);
    let offset = vec2<f32>(corner.x * size / aspect, corner.y * size);
    let clip = vec2<f32>((p.pos.x * 2.0) - 1.0, (p.pos.y * 2.0) - 1.0) + offset;

    var out : VertexOut;
    out.position = vec4<f32>(clip, 0.0, 1.0);
    out.uv = corner;
    // Hotter bands shade toward the key's hue; quiet ones stay near-white, which keeps the field
    // readable as one wash rather than a rainbow.
    let tint = mix(vec3<f32>(0.72, 0.74, 0.85), hueToRgb(u.hue), 0.35 + (energy * 0.55));
    out.colour = vec4<f32>(tint, fade * (0.16 + (energy * 0.5)));
    return out;
}

@fragment
fn fragmentMain(in : VertexOut) -> @location(0) vec4<f32> {
    // Soft round falloff; the quad's corners disappear entirely.
    let d = length(in.uv);
    let alpha = in.colour.a * smoothstep(1.0, 0.0, d);
    return vec4<f32>(in.colour.rgb * alpha, alpha);
}
`;

function readHue() {
    const raw = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--pm-key-hue'));
    return Number.isFinite(raw) ? raw : DEFAULT_HUE;
}

function resize(state) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(state.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(state.canvas.clientHeight * dpr), 1);
    if (state.canvas.width !== width || state.canvas.height !== height) {
        state.canvas.width = width;
        state.canvas.height = height;
    }
}

/// Folds the analyser's bins down to BANDS averages. Averaged rather than sampled: a single bin is
/// noisy enough that particles driven by it would jitter rather than surge.
function fillBands(state, bins) {
    const bands = state.bands;
    if (!bins) {
        for (let i = 0; i < BANDS; i++) {
            bands[i] *= 0.90; // ease to rest when playback stops rather than dropping dead
        }
        return;
    }
    const per = Math.max(Math.floor(bins.length / BANDS), 1);
    for (let band = 0; band < BANDS; band++) {
        let sum = 0;
        const from = band * per;
        for (let i = 0; i < per; i++) {
            sum += bins[from + i] ?? 0;
        }
        const value = (sum / per) / 255;
        // Asymmetric smoothing: rises fast so a transient is felt, falls slowly so the field
        // billows rather than flickers.
        bands[band] = value > bands[band] ? value : bands[band] + ((value - bands[band]) * 0.18);
    }
}

function frame(state, now) {
    state.raf = requestAnimationFrame((next) => frame(state, next));

    const dt = Math.min((now - state.last) / 1000, 0.05);
    state.last = now;

    const bins = frequencyData();
    fillBands(state, bins);
    const level = masterLevel();

    // Nothing playing and nothing left moving: stop. A slow poll restarts the loop, the same idle
    // contract fx-spectrum.js uses.
    if (!bins && level < 0.001) {
        state.idleFrames++;
        if (state.idleFrames > 90) {
            cancelAnimationFrame(state.raf);
            state.raf = null;
            state.idleTimer = setTimeout(() => wake(state), 250);
            return;
        }
    } else {
        state.idleFrames = 0;
    }

    if (now - state.lastHueRead > COLOUR_REFRESH_MS) {
        state.lastHueRead = now;
        state.hue = readHue();
    }

    resize(state);

    const u = state.uniformData;
    u[0] = (now - state.start) / 1000;
    u[1] = dt;
    u[2] = level;
    u[3] = state.hue;
    u[4] = state.canvas.width;
    u[5] = state.canvas.height;
    u.set(state.bands, 8);
    state.device.queue.writeBuffer(state.uniformBuffer, 0, u);

    const encoder = state.device.createCommandEncoder();

    const compute = encoder.beginComputePass();
    compute.setPipeline(state.computePipeline);
    compute.setBindGroup(0, state.bindGroup);
    compute.dispatchWorkgroups(Math.ceil(state.count / WORKGROUP_SIZE));
    compute.end();

    const view = state.context.getCurrentTexture().createView();
    const pass = encoder.beginRenderPass({
        colorAttachments: [{
            view,
            clearValue: { r: 0, g: 0, b: 0, a: 0 },
            loadOp: 'clear',
            storeOp: 'store',
        }],
    });
    pass.setPipeline(state.renderPipeline);
    pass.setBindGroup(0, state.bindGroup);
    pass.draw(6, state.count);
    pass.end();

    state.device.queue.submit([encoder.finish()]);
}

function wake(state) {
    state.idleTimer = null;
    if (frequencyData() || masterLevel() > 0.001) {
        state.idleFrames = 0;
        state.last = performance.now();
        state.raf = requestAnimationFrame((now) => frame(state, now));
    } else {
        state.idleTimer = setTimeout(() => wake(state), 250);
    }
}

/// Starts the field on `canvas`.
///
/// Async because acquiring a WebGPU device is: the caller awaits a boolean, and false means nothing
/// was created and nothing needs cleaning up. Every failure path — no navigator.gpu, no adapter, a
/// shader that will not compile, a lost device — lands on false rather than throwing, because this
/// is decoration on a page that must still play audio.
export async function init(canvas) {
    if (!canvas || states.has(canvas)) {
        return false;
    }
    if (!prefs.allowsHeavy() || !navigator.gpu) {
        return false;
    }

    let device;
    let context;
    let format;
    try {
        const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'low-power' });
        if (!adapter) {
            return false;
        }
        device = await adapter.requestDevice();
        context = canvas.getContext('webgpu');
        if (!context) {
            return false;
        }
        format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device, format, alphaMode: 'premultiplied' });
    } catch {
        return false;
    }

    try {
        const module = device.createShaderModule({ code: SHADER });
        // Surfaced as a rejected promise on some implementations and a message list on others;
        // either way a shader that did not compile must not reach a dispatch.
        const info = await module.getCompilationInfo?.();
        if (info?.messages?.some((m) => m.type === 'error')) {
            device.destroy?.();
            return false;
        }

        const count = Math.max(WORKGROUP_SIZE, Math.round(MAX_PARTICLES * prefs.intensity()));
        const particleBuffer = device.createBuffer({
            size: count * 32,
            usage: GPUBufferUsage.STORAGE,
        });
        const uniformBuffer = device.createBuffer({
            size: UNIFORM_FLOATS * 4,
            usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
        });

        const layout = device.createBindGroupLayout({
            entries: [
                {
                    binding: 0,
                    visibility: GPUShaderStage.COMPUTE,
                    buffer: { type: 'storage' },
                },
                {
                    binding: 1,
                    visibility: GPUShaderStage.COMPUTE | GPUShaderStage.VERTEX,
                    buffer: { type: 'uniform' },
                },
                {
                    binding: 2,
                    visibility: GPUShaderStage.VERTEX,
                    buffer: { type: 'read-only-storage' },
                },
            ],
        });
        const pipelineLayout = device.createPipelineLayout({ bindGroupLayouts: [layout] });

        const computePipeline = device.createComputePipeline({
            layout: pipelineLayout,
            compute: { module, entryPoint: 'simulate' },
        });
        const renderPipeline = device.createRenderPipeline({
            layout: pipelineLayout,
            vertex: { module, entryPoint: 'vertexMain' },
            fragment: {
                module,
                entryPoint: 'fragmentMain',
                targets: [{
                    format,
                    // Premultiplied additive: particles accumulate into a glow instead of occluding
                    // each other, which is the whole look.
                    blend: {
                        color: { srcFactor: 'one', dstFactor: 'one', operation: 'add' },
                        alpha: { srcFactor: 'one', dstFactor: 'one', operation: 'add' },
                    },
                }],
            },
            primitive: { topology: 'triangle-list' },
        });

        const bindGroup = device.createBindGroup({
            layout,
            entries: [
                { binding: 0, resource: { buffer: particleBuffer } },
                { binding: 1, resource: { buffer: uniformBuffer } },
                { binding: 2, resource: { buffer: particleBuffer } },
            ],
        });

        const state = {
            canvas,
            device,
            context,
            count,
            computePipeline,
            renderPipeline,
            bindGroup,
            particleBuffer,
            uniformBuffer,
            uniformData: new Float32Array(UNIFORM_FLOATS),
            bands: new Float32Array(BANDS),
            hue: readHue(),
            lastHueRead: performance.now(),
            start: performance.now(),
            last: performance.now(),
            idleFrames: 0,
            idleTimer: null,
            raf: null,
            unsubscribe: null,
        };
        states.set(canvas, state);

        // A lost device (driver reset, tab backgrounded on some platforms) must tear the field down
        // rather than leave a loop submitting to a dead queue every frame.
        device.lost?.then(() => dispose(canvas)).catch(() => { });

        state.unsubscribe = prefs.subscribe(() => {
            if (!prefs.allowsHeavy()) {
                dispose(canvas);
            }
        });

        resize(state);
        state.raf = requestAnimationFrame((now) => frame(state, now));
        canvas.dataset.particles = 'webgpu';
        return true;
    } catch {
        try {
            device.destroy?.();
        } catch {
            // Nothing more to do; the caller is about to give up on this canvas anyway.
        }
        return false;
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
    if (state.idleTimer !== null) {
        clearTimeout(state.idleTimer);
    }
    state.unsubscribe?.();
    try {
        state.particleBuffer.destroy();
        state.uniformBuffer.destroy();
        state.device.destroy?.();
    } catch {
        // Already lost.
    }
    try {
        delete canvas.dataset.particles;
    } catch {
        // Already detached.
    }
    states.delete(canvas);
}
