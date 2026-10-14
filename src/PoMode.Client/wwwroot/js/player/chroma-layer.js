// Turns the band's chroma (GET /api/analysis/{id}/chroma: 12 pitch-class energies per frame, row 0
// the song's tonic) into one image of the whole song, which canvas.js slices to the visible time
// range and lays under the chord blocks. Rendered once per song, never per frame.
//
// On a browser with WebGPU the image is drawn by a fragment shader: the energies go up as a 12-row
// texture, sampled with linear filtering so frames and pitch classes blend smoothly, and coloured
// along a ramp from the theme's lane colour to its accent. Without WebGPU the same ramp is computed
// in JS and the browser's own image smoothing does the blending. Both answer an image source that
// drawImage accepts, so canvas.js does not know which ran.

const HEIGHT = 96;          // 8 px a pitch class: enough for the shader's blend to read as a field
const MAX_WIDTH = 4096;     // the widest texture every WebGPU adapter guarantees

/// Fetches and renders the chroma for a job. Answers { image, durationSec } or null.
export async function load(jobId, colours) {
    let chroma;
    try {
        const response = await fetch(`api/analysis/${jobId}/chroma`);
        if (!response.ok) {
            return null;
        }
        chroma = await response.json();
    } catch {
        return null;
    }
    const frames = chroma.frames;
    if (!frames) {
        return null;
    }
    const energy = Uint8Array.from(atob(chroma.energy), (c) => c.charCodeAt(0));
    const low = parse(colours.lane);
    const high = parse(colours.accent);
    const image = (await renderGpu(energy, frames, low, high)) ?? renderCpu(energy, frames, low, high);
    return { image, durationSec: frames / chroma.framesPerSecond };
}

function parse(colour) {
    const probe = document.createElement('canvas').getContext('2d');
    probe.fillStyle = colour || '#888';
    const hex = probe.fillStyle;
    if (hex.startsWith('#')) {
        return [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
    }
    const parts = hex.match(/[\d.]+/g)?.map(Number) ?? [136, 136, 136];
    return parts.slice(0, 3).map((value) => value / 255);
}

/// The CPU path: one pixel per frame and pitch class, rows flipped so the tonic is at the bottom.
function renderCpu(energy, frames, low, high) {
    const canvas = document.createElement('canvas');
    canvas.width = frames;
    canvas.height = 12;
    const ctx = canvas.getContext('2d');
    const pixels = ctx.createImageData(frames, 12);
    for (let f = 0; f < frames; f++) {
        for (let row = 0; row < 12; row++) {
            const t = ramp(energy[f * 12 + row] / 255);
            const at = ((11 - row) * frames + f) * 4;
            pixels.data[at] = 255 * (low[0] + (high[0] - low[0]) * t);
            pixels.data[at + 1] = 255 * (low[1] + (high[1] - low[1]) * t);
            pixels.data[at + 2] = 255 * (low[2] + (high[2] - low[2]) * t);
            pixels.data[at + 3] = 255 * t;
        }
    }
    ctx.putImageData(pixels, 0, 0);
    return canvas;
}

/// Weak energy is mostly leakage from neighbours; the ramp keeps it near the ground colour.
function ramp(value) {
    return Math.min(1, Math.max(0, (value - 0.25) / 0.75)) ** 1.5;
}

const SHADER = /* wgsl */ `
struct Out { @builtin(position) position: vec4f, @location(0) uv: vec2f };

@vertex fn vs(@builtin(vertex_index) i: u32) -> Out {
    var corners = array<vec2f, 6>(vec2f(-1, -1), vec2f(1, -1), vec2f(-1, 1), vec2f(-1, 1), vec2f(1, -1), vec2f(1, 1));
    var out: Out;
    out.position = vec4f(corners[i], 0, 1);
    out.uv = vec2f((corners[i].x + 1) * 0.5, (1 - corners[i].y) * 0.5);
    return out;
}

@group(0) @binding(0) var energy: texture_2d<f32>;
@group(0) @binding(1) var linearSampler: sampler;
@group(0) @binding(2) var<uniform> colours: array<vec4f, 2>;

@fragment fn fs(input: Out) -> @location(0) vec4f {
    // Row 0 of the texture is the tonic; the image puts it at the bottom.
    let value = textureSample(energy, linearSampler, vec2f(input.uv.x, 1.0 - input.uv.y)).r;
    let t = pow(clamp((value - 0.25) / 0.75, 0.0, 1.0), 1.5);
    return vec4f(mix(colours[0].rgb, colours[1].rgb, t) * t, t);
}`;

async function renderGpu(energy, frames, low, high) {
    if (!navigator.gpu) {
        return null;
    }
    try {
        const adapter = await navigator.gpu.requestAdapter();
        if (!adapter) {
            return null;
        }
        const device = await adapter.requestDevice();
        const width = Math.min(MAX_WIDTH, Math.max(frames * 2, 64));
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = HEIGHT;
        const context = canvas.getContext('webgpu');
        const format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device, format, alphaMode: 'premultiplied' });

        // Frames across, pitch classes down; a texture wider than the limit is decimated first.
        const columns = Math.min(frames, MAX_WIDTH);
        const rows = new Uint8Array(columns * 12);
        for (let c = 0; c < columns; c++) {
            const f = Math.floor(c * frames / columns);
            for (let row = 0; row < 12; row++) {
                rows[row * columns + c] = energy[f * 12 + row];
            }
        }
        const bytesPerRow = Math.ceil(columns / 256) * 256;
        const padded = new Uint8Array(bytesPerRow * 12);
        for (let row = 0; row < 12; row++) {
            padded.set(rows.subarray(row * columns, (row + 1) * columns), row * bytesPerRow);
        }
        const texture = device.createTexture({
            size: [columns, 12], format: 'r8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
        });
        device.queue.writeTexture({ texture }, padded, { bytesPerRow }, [columns, 12]);

        const uniform = device.createBuffer({ size: 32, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        device.queue.writeBuffer(uniform, 0, new Float32Array([...low, 1, ...high, 1]));

        const module = device.createShaderModule({ code: SHADER });
        const pipeline = device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs' },
            fragment: { module, entryPoint: 'fs', targets: [{ format }] },
            primitive: { topology: 'triangle-list' },
        });
        const bindings = device.createBindGroup({
            layout: pipeline.getBindGroupLayout(0),
            entries: [
                { binding: 0, resource: texture.createView() },
                { binding: 1, resource: device.createSampler({ magFilter: 'linear', minFilter: 'linear' }) },
                { binding: 2, resource: { buffer: uniform } },
            ],
        });
        const encoder = device.createCommandEncoder();
        const pass = encoder.beginRenderPass({
            colorAttachments: [{
                view: context.getCurrentTexture().createView(),
                clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store',
            }],
        });
        pass.setPipeline(pipeline);
        pass.setBindGroup(0, bindings);
        pass.draw(6);
        pass.end();
        device.queue.submit([encoder.finish()]);
        await device.queue.onSubmittedWorkDone();

        // Copied onto a plain 2D canvas: a WebGPU canvas's contents are only guaranteed until the
        // next frame, and canvas.js draws from this image on every frame for the life of the page.
        const copy = document.createElement('canvas');
        copy.width = width;
        copy.height = HEIGHT;
        copy.getContext('2d').drawImage(canvas, 0, 0);
        texture.destroy();
        uniform.destroy();
        device.destroy();
        return copy;
    } catch {
        return null;
    }
}
