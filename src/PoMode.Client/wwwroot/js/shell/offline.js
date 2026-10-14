// "Keep offline" for one analysed song. The service worker already keeps every page and reading it
// has fetched (network-first, the cache only a fallback), but it skips the stem audio, which is tens
// of megabytes nobody asked to store. Keeping a song offline fetches its readings and its stems into
// a cache of their own, which the worker's fallback finds like any other; forgetting it deletes them.
//
// Same version prefix as service-worker.js, so a deploy that bumps it clears these too rather than
// leaving audio for a build that no longer exists.

const CACHE = 'pomode-v1-saved';

const READINGS = ['', '/result', '/visual', '/stats', '/notes', '/notes-backing', '/notes-chords', '/chords',
    '/beats', '/tempo-map', '/voice', '/structure', '/peaks', '/lyrics'];
const STEMS = ['/stems/mix', '/stems/vocals', '/stems/instrumental'];

function urlsFor(jobId) {
    return [...READINGS, ...STEMS].map((path) => `/api/analysis/${jobId}${path}`);
}

/// Fetches and stores everything the analysis page reads for this job. Answers how many megabytes
/// were stored. A reading the job does not have (no sections, no lyrics) is simply skipped.
export async function save(jobId) {
    const cache = await caches.open(CACHE);
    let bytes = 0;
    await Promise.all(urlsFor(jobId).map(async (url) => {
        try {
            const response = await fetch(url, { credentials: 'same-origin' });
            if (response.ok) {
                const copy = response.clone();
                bytes += (await copy.arrayBuffer()).byteLength;
                await cache.put(url, response);
            }
        } catch {
            // Offline already, or one reading refused: keep what did arrive.
        }
    }));
    // The page itself, so a cold start offline can open this song's link.
    try {
        await cache.add(`/?job=${jobId}`);
    } catch { }
    return Math.round(bytes / 1048576 * 10) / 10;
}

export async function forget(jobId) {
    const cache = await caches.open(CACHE);
    await Promise.all([...urlsFor(jobId), `/?job=${jobId}`].map((url) => cache.delete(url)));
}

/// The ids kept offline, read from the stored status responses.
export async function savedIds() {
    try {
        const cache = await caches.open(CACHE);
        const keys = await cache.keys();
        return keys
            .map((request) => new URL(request.url).pathname.match(/^\/api\/analysis\/([0-9a-f]{32})$/))
            .filter(Boolean)
            .map((match) => match[1]);
    } catch {
        return [];
    }
}

export async function isSaved(jobId) {
    return (await savedIds()).includes(jobId);
}
