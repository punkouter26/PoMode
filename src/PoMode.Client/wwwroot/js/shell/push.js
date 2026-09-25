// "Notify me when analysis finishes": the opt-in behind the header menu's toggle.
//
// Permission is requested from `enable` and nowhere else, and `enable` runs only from a click on that
// item. A prompt on page load is how a site gets its permission denied for good, and browsers now
// silence or refuse a prompt that no gesture asked for.
//
// Every state this module reaches is returned to the layout and mirrored onto
// `document.body.dataset.pushState`: `unavailable` (this server sends no pushes, or this browser
// cannot receive them — the item is hidden), `blocked` (the person said no in the browser), `off`, `on`.

const API = '/api/push';

let config = null;

/// Works out the current state without asking the person anything. When this browser is already
/// subscribed the subscription is re-sent to the server on every visit: that is what re-registers it
/// under whoever is signed in now, and what replaces it when the server's key has changed (a new key
/// pair, or Development's throwaway one after a restart).
export async function init() {
    if (!supported()) {
        return publish('unavailable');
    }
    const { available, publicKey } = await loadConfig();
    if (!available) {
        return publish('unavailable');
    }
    if (Notification.permission === 'denied') {
        return publish('blocked');
    }
    try {
        const registration = await navigator.serviceWorker.getRegistration();
        const existing = registration ? await registration.pushManager.getSubscription() : null;
        if (!existing || Notification.permission !== 'granted') {
            return publish('off');
        }
        await save(await withKey(registration, existing, publicKey));
        return publish('on');
    } catch {
        return publish('off');
    }
}

/// Asks for permission (this is the click), subscribes, and tells the server.
export async function enable() {
    if (!supported()) {
        return publish('unavailable');
    }
    // Before any other await, while the click still counts as the reason for the prompt.
    const permission = await Notification.requestPermission();
    if (permission !== 'granted') {
        return publish(permission === 'denied' ? 'blocked' : 'off');
    }
    const { available, publicKey } = await loadConfig();
    if (!available) {
        return publish('unavailable');
    }
    const registration = await readyRegistration();
    if (!registration) {
        return publish('off');
    }
    const existing = await registration.pushManager.getSubscription();
    const subscription = existing
        ? await withKey(registration, existing, publicKey)
        : await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: decode(publicKey) });
    await save(subscription);
    return publish('on');
}

/// Stops notifications to this browser, on the server and in the browser both.
export async function disable() {
    const subscription = await currentSubscription();
    if (subscription) {
        await forgetOnServer(subscription.endpoint);
        await subscription.unsubscribe();
    }
    return publish('off');
}

/// Drops this browser from the signed-in account's notifications while keeping the browser's own
/// subscription. Called just before signing out: the account being left must not go on notifying a
/// browser someone else may now be using. The next visit re-registers it for whoever is signed in.
export async function forget() {
    const subscription = await currentSubscription();
    if (subscription) {
        await forgetOnServer(subscription.endpoint);
    }
}

function supported() {
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
}

async function loadConfig() {
    if (config) {
        return config;
    }
    try {
        const response = await fetch(API);
        config = response.ok ? await response.json() : { available: false };
    } catch {
        // Not cached: a failed fetch is not the server saying push is off.
        return { available: false };
    }
    return config;
}

async function currentSubscription() {
    try {
        const registration = await navigator.serviceWorker.getRegistration();
        return registration ? await registration.pushManager.getSubscription() : null;
    } catch {
        return null;
    }
}

/// The worker registers after the page loads (see pwa.js), so a click can come first. `ready` waits
/// for it, but never resolves at all if registration failed, hence the bound.
async function readyRegistration() {
    const timeout = new Promise((resolve) => setTimeout(() => resolve(null), 10000));
    return await Promise.race([navigator.serviceWorker.ready, timeout]);
}

/// The subscription as it stands if it was made with the server's current key, otherwise a fresh
/// one. A subscription is bound to the key it was made with, and pushes signed with any other are
/// refused by the push service.
async function withKey(registration, subscription, publicKey) {
    const expected = decode(publicKey);
    const actual = subscription.options?.applicationServerKey;
    if (actual && sameBytes(new Uint8Array(actual), expected)) {
        return subscription;
    }
    await subscription.unsubscribe();
    return await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: expected });
}

async function save(subscription) {
    const { endpoint, keys } = subscription.toJSON();
    const response = await fetch(`${API}/subscriptions`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ endpoint, p256dh: keys?.p256dh ?? '', auth: keys?.auth ?? '' }),
    });
    if (!response.ok) {
        throw new Error(`The server did not accept the subscription (${response.status}).`);
    }
}

async function forgetOnServer(endpoint) {
    try {
        await fetch(`${API}/subscriptions?endpoint=${encodeURIComponent(endpoint)}`, { method: 'DELETE' });
    } catch {
        // Offline: the push service reports the subscription gone once the browser drops it, and
        // the server deletes it then.
    }
}

function decode(base64Url) {
    const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(base64Url.length / 4) * 4, '=');
    return Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
}

function sameBytes(a, b) {
    return a.length === b.length && a.every((value, i) => value === b[i]);
}

function publish(state) {
    try {
        document.body.dataset.pushState = state;
    } catch { }
    return state;
}
