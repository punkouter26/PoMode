// PoMode's service worker: an offline shell, a share target, and nothing clever.
//
// NETWORK-FIRST, deliberately, and this is the most important decision in the file. Program.cs
// serves this app's own scripts and styles with `no-cache` precisely because they carry no
// fingerprint in their URL, and a browser left to its own heuristics will keep serving a copy from
// before the last deploy — which is how a page ends up calling into a JS module that no longer
// matches the C# beside it. A cache-first worker, including the one the Blazor PWA template ships,
// would reintroduce that bug and make it survive a hard refresh. So the network is always asked
// first and the cache is only ever a fallback for a request that failed.
//
// What that buys is the honest version of "offline": everything you have already loaded still
// opens, and everything you have not still needs the network. What it costs is that the first
// offline visit after a deploy may find an asset missing. That is the right trade for a tool whose
// analysis lives on the server anyway.

const VERSION = 'pomode-v1';
const SHELL_CACHE = `${VERSION}-shell`;
const SHARE_CACHE = `${VERSION}-share`;

/// Enough to boot the SPA shell offline. Everything else is cached as it is fetched.
const SHELL = [
    '/',
    '/css/app.css',
    '/manifest.webmanifest',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
];

self.addEventListener('install', (event) => {
    event.waitUntil((async () => {
        const cache = await caches.open(SHELL_CACHE);
        // addAll rejects the whole install if any one entry 404s, which would leave the app with no
        // worker at all over a single renamed file. Each is added on its own instead.
        await Promise.all(SHELL.map((url) => cache.add(url).catch(() => { })));
        await self.skipWaiting();
    })());
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(names
            .filter((name) => !name.startsWith(VERSION))
            .map((name) => caches.delete(name)));
        await self.clients.claim();
    })());
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    const url = new URL(request.url);

    // A file shared into the app from another app (a voice memo, a track in a file manager). The
    // POST is answered here rather than by the server: the page needs the bytes, not a round trip,
    // and handing them straight to the upload endpoint would skip the executor picks the home page
    // collects. See share-target.js.
    if (request.method === 'POST' && url.pathname === '/share-target' && url.origin === self.location.origin) {
        event.respondWith(receiveShare(request));
        return;
    }

    // Everything that changes the server is network-only. A cached POST is not a fallback, it is a
    // lie about work that never happened.
    if (request.method !== 'GET' || url.origin !== self.location.origin) {
        return;
    }

    event.respondWith(networkFirst(request));
});

/// Network, then cache. A successful response is stored on the way past so it is there next time
/// the network is not.
async function networkFirst(request) {
    const url = new URL(request.url);
    try {
        const response = await fetch(request);
        if (response.ok && isCacheable(url)) {
            const copy = response.clone();
            caches.open(SHELL_CACHE).then((cache) => cache.put(request, copy)).catch(() => { });
        }
        return response;
    } catch {
        const cached = await caches.match(request);
        if (cached) {
            return cached;
        }
        // A navigation to any route falls back to the shell, because this is a single-page app and
        // the shell is what renders every route once it boots.
        if (request.mode === 'navigate') {
            const shell = await caches.match('/');
            if (shell) {
                return shell;
            }
        }
        return new Response('PoMode is offline and has no stored copy of that.', {
            status: 503,
            headers: { 'Content-Type': 'text/plain' },
        });
    }
}

/// What is worth keeping. Stem audio and the SignalR hub are excluded: a stem WAV is tens of
/// megabytes of something only useful while the mixer is already open, and a hub negotiation
/// replayed from cache would hand the client a dead connection id.
function isCacheable(url) {
    if (url.pathname.startsWith('/hubs/')) {
        return false;
    }
    if (url.pathname.includes('/stems/')) {
        return false;
    }
    return true;
}

/// Stashes a shared file where the page can pick it up, then redirects to the page.
///
/// A redirect rather than a response body because the share target is a navigation: the browser is
/// opening the app, and what it needs back is somewhere to land. The file goes into its own cache
/// under a fixed key, which share-target.js reads once and then deletes.
async function receiveShare(request) {
    try {
        const form = await request.formData();
        const file = form.get('file');
        if (file && file.size > 0) {
            const cache = await caches.open(SHARE_CACHE);
            await cache.put('/shared-file', new Response(file, {
                headers: {
                    'Content-Type': file.type || 'application/octet-stream',
                    // The name does not survive a Response on its own, and the analyzer uses it for
                    // the library row and for the catalogue lookup's search query.
                    'X-Shared-Name': encodeURIComponent(file.name || 'shared-audio'),
                },
            }));
            return Response.redirect('/?shared=1', 303);
        }
    } catch {
        // Fall through: a share that could not be read is better answered by the ordinary page than
        // by an error the sharing app will render as a crash.
    }
    return Response.redirect('/', 303);
}
