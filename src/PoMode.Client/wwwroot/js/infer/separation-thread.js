// The browser separation tier's worker: runs UVR's MDX-Net Voc_FT over a stereo mix off the main
// thread, so a minute of FFTs and model runs never freezes the page. Receives { left, right, backend }
// at 44.1 kHz, posts { progress } as chunks finish, then { left, right } (the vocal stem) or { error }.
// The runtime and the model come from our own origin, pinned and verified like every model.

import { separateVocals, MDX } from './mdx.js';

self.onmessage = async ({ data: { left, right, backend } }) => {
    try {
        const module = await import('/web-runtime/ort.all.bundle.min.mjs');
        const ort = module.default ?? module;
        ort.env.wasm.wasmPaths = '/web-runtime/';

        const response = await fetch('/web-runtime/UVR-MDX-NET-Voc_FT.onnx');
        if (!response.ok) {
            throw new Error(`model fetch failed: ${response.status}`);
        }
        const session = await ort.InferenceSession.create(new Uint8Array(await response.arrayBuffer()), {
            // WASM stays in the list as the in-place fallback for an adapter that fails session init.
            executionProviders: backend === 'webgpu' ? ['webgpu', 'wasm'] : ['wasm'],
        });
        const input = session.inputNames[0];
        const output = session.outputNames[0];

        const vocals = await separateVocals(left, right, async spec => {
            const results = await session.run({ [input]: new ort.Tensor('float32', spec, [1, 4, MDX.dimF, MDX.dimT]) });
            return results[output].data;
        }, progress => self.postMessage({ progress }));

        self.postMessage({ left: vocals.left, right: vocals.right }, [vocals.left.buffer, vocals.right.buffer]);
    } catch (error) {
        self.postMessage({ error: String(error?.message ?? error) });
    }
};
