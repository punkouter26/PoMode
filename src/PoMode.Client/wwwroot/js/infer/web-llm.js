// A language model run in the page through WebLLM (MLC) on WebGPU, for a browser that has a GPU but
// not Chrome's built-in Prompt API (Edge, Firefox, Safari). Same contract as browser-llm.js: the
// page builds the prompt, this runs it and streams the reply back, the page checks the figures.
//
// The model is Qwen2.5 1.5B Instruct, 4-bit: about a gigabyte, downloaded once into the browser's
// cache on the first click (never on load) with the progress shown, then reused. Its reply is held
// to the same JSON schema as every other interpreter through WebLLM's grammar-constrained decoding.

const WEB_LLM = 'https://cdn.jsdelivr.net/npm/@mlc-ai/web-llm@0.2.85/+esm';
const MODEL = 'Qwen2.5-1.5B-Instruct-q4f16_1-MLC';

let engine = null;
let library = null;

async function load() {
    library ??= await import(WEB_LLM);
    return library;
}

/** 'available' (downloaded), 'downloadable' (WebGPU, not yet downloaded) or 'unavailable'. */
export async function availability() {
    if (!navigator.gpu) {
        return 'unavailable';
    }
    try {
        if (!(await navigator.gpu.requestAdapter())) {
            return 'unavailable';
        }
        const webllm = await load();
        return (await webllm.hasModelInCache(MODEL)) ? 'available' : 'downloadable';
    } catch {
        return 'unavailable';
    }
}

/** Runs one prompt, handing each piece of the reply to sink.OnBrowserChunk as it is written. */
export async function reply(system, messages, schema, sink) {
    const turns = typeof messages === 'string' ? JSON.parse(messages) : messages;
    const webllm = await load();
    engine ??= await webllm.CreateMLCEngine(MODEL, {
        initProgressCallback: (report) => {
            sink.invokeMethodAsync('OnBrowserDownload', Math.min(99, Math.round((report.progress ?? 0) * 100))).catch(() => { });
        },
    });
    sink.invokeMethodAsync('OnBrowserDownload', 100).catch(() => { });
    const stream = await engine.chat.completions.create({
        messages: [{ role: 'system', content: system }, ...turns],
        stream: true,
        temperature: 0.4,
        max_tokens: 1536,
        response_format: { type: 'json_object', schema },
    });
    for await (const part of stream) {
        const delta = part.choices?.[0]?.delta?.content;
        if (delta) {
            await sink.invokeMethodAsync('OnBrowserChunk', delta);
        }
    }
}
