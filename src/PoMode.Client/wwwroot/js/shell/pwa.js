// Installability and the share target's page-side half.
//
// Registering the worker is done from here rather than inline in index.html so it can be kept off
// the critical path: the app boots first, the worker registers after, and a failure to register is
// invisible rather than fatal. A service worker only ever makes this app installable and readable
// offline — every analysis still needs the server — so it must never be able to stop the page.

let installPrompt = null;

export function register() {
    if (!('serviceWorker' in navigator)) {
        return false;
    }
    // After load: registration competes with the WASM download for bandwidth otherwise, and the
    // runtime is what the user is actually waiting for.
    const registerWorker = () => navigator.serviceWorker.register('/service-worker.js').catch(() => {
        // An unregistered worker costs offline support, the share target and push, and nothing else.
    });
    // index.html reaches this through a dynamic import, which can settle after `load` has already
    // fired — and a listener added then never runs, which left the app with no worker at all.
    if (document.readyState === 'complete') {
        registerWorker();
    } else {
        window.addEventListener('load', registerWorker, { once: true });
    }
    return true;
}

/// Captures the browser's install prompt so the app can offer it at a sensible moment instead of
/// whenever Chrome decides. Returns nothing; `canInstall` reports whether one was captured.
export function watchInstall(dotNetHelper) {
    window.addEventListener('beforeinstallprompt', (event) => {
        // Without this the browser shows its own mini-infobar, and the app has no say in when.
        event.preventDefault();
        installPrompt = event;
        publish();
        if (dotNetHelper) {
            try { dotNetHelper.invokeMethodAsync('OnInstallAvailable'); } catch { }
        }
    });
    window.addEventListener('appinstalled', () => {
        installPrompt = null;
        publish();
    });
    publish();
}

export function canInstall() {
    return installPrompt !== null;
}

/// Shows the captured prompt. Resolves true only if the user actually accepted.
export async function promptInstall() {
    if (!installPrompt) {
        return false;
    }
    const prompt = installPrompt;
    // A captured prompt is single-use: holding on to it after it has been shown gives a button that
    // silently does nothing the second time.
    installPrompt = null;
    publish();
    try {
        prompt.prompt();
        const choice = await prompt.userChoice;
        return choice && choice.outcome === 'accepted';
    } catch {
        return false;
    }
}

/// True when the app is running as an installed app rather than in a browser tab.
export function isStandalone() {
    try {
        return window.matchMedia('(display-mode: standalone)').matches
            || window.navigator.standalone === true;
    } catch {
        return false;
    }
}

/// Must match the SHARE_CACHE name in service-worker.js — the two halves of the handoff.
const SHARE_CACHE = 'pomode-v1-share';

/// The shared file, held between the two calls below.
let sharedFile = null;

/// Claims the file another app shared into PoMode and returns its name, or null if there is none.
///
/// Read once and then deleted: the cache entry is a handoff, not storage, and leaving it there would
/// make the next visit to `/?shared=1` re-upload a file the user already analysed.
///
/// The file itself stays on this side as a Blob and goes from `takeSharedFile` straight to the
/// resumable uploader as an object reference. Its bytes never cross into .NET: a 40 MB memo copied
/// into the WASM heap and back out again would cost memory a phone does not have to spare.
export async function takeSharedName() {
    sharedFile = null;
    if (!('caches' in window)) {
        return null;
    }
    try {
        const cache = await caches.open(SHARE_CACHE);
        const response = await cache.match('/shared-file');
        if (!response) {
            return null;
        }
        const name = decodeURIComponent(response.headers.get('X-Shared-Name') || 'shared-audio');
        sharedFile = await response.blob();
        await cache.delete('/shared-file');
        return name;
    } catch {
        return null;
    }
}

/// The file claimed by the last `takeSharedName` (an empty Blob if none), cleared as it is handed
/// over so a re-render cannot upload the same file twice.
export function takeSharedFile() {
    const file = sharedFile ?? new Blob([]);
    sharedFile = null;
    return file;
}

/// Mirrored onto the document like the other modules, so Playwright can assert on install state
/// without reaching into this module.
function publish() {
    try {
        if (!document.body) return;
        document.body.dataset.pwaInstallable = installPrompt ? 'yes' : 'no';
        document.body.dataset.pwaStandalone = isStandalone() ? 'yes' : 'no';
    } catch { }
}
