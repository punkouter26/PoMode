// The language model built into the browser (Chrome's Prompt API, Gemini Nano; Edge exposes the same
// LanguageModel global behind a flag). The interpretation tier that costs nothing and needs no server:
// the page builds the same prompt the server sends Ollama, this runs it, and the page checks the
// figures. No music here - the prompt arrives finished and the reply goes back as text.

const LANGUAGES = { expectedInputs: [{ type: 'text', languages: ['en'] }], expectedOutputs: [{ type: 'text', languages: ['en'] }] };

/** 'available', 'downloadable', 'downloading' or 'unavailable'. */
export async function availability() {
    if (!('LanguageModel' in self)) {
        return 'unavailable';
    }
    try {
        return await self.LanguageModel.availability(LANGUAGES);
    } catch {
        return 'unavailable';
    }
}

/**
 * Runs one prompt, handing each piece of the reply to sink.onChunk as it is written. Resolves when
 * the reply is complete. A 'downloadable' model downloads on the first call, which is why this is
 * only ever reached from a click.
 */
export async function reply(system, messages, schema, sink) {
    const session = await self.LanguageModel.create({
        ...LANGUAGES,
        initialPrompts: [{ role: 'system', content: system }, ...messages.slice(0, -1)],
    });
    try {
        const stream = session.promptStreaming(messages.at(-1).content, { responseConstraint: JSON.parse(schema) });
        // Current builds stream deltas; early ones repeated the whole text so far. Tell them apart by
        // whether a chunk extends everything already seen.
        let seen = '';
        for await (const chunk of stream) {
            const delta = seen.length > 0 && chunk.startsWith(seen) ? chunk.slice(seen.length) : chunk;
            seen = delta === chunk ? seen + chunk : chunk;
            if (delta) {
                await sink.invokeMethodAsync('OnBrowserChunk', delta);
            }
        }
    } finally {
        session.destroy();
    }
}
