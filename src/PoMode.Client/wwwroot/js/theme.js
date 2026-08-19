// Manual theme override with system default. The choice persists in localStorage under `pm-theme`
// and is applied as `data-theme` on <html>; "system" removes the attribute so the CSS
// prefers-color-scheme rules take over. Tiny and synchronous by design — call init() before first
// paint to avoid a theme flash.

const STORAGE_KEY = 'pm-theme';
const MODES = ['system', 'light', 'dark'];

/// Reads the persisted choice, defaulting to "system" for absent or unrecognised values.
function stored() {
    let value = null;
    try {
        value = localStorage.getItem(STORAGE_KEY);
    } catch {
        // Storage can be unavailable (privacy modes); fall through to system.
    }
    return value === 'light' || value === 'dark' ? value : 'system';
}

/// Stamps (or removes, for system) the data-theme attribute on the root element, and swaps the
/// Radzen base stylesheets to match — they load under prefers-color-scheme media queries, which
/// only follow the OS, so an explicit override must rewrite their media attributes too.
function apply(mode) {
    const light = document.getElementById('radzen-light');
    const dark = document.getElementById('radzen-dark');
    if (mode === 'system') {
        delete document.documentElement.dataset.theme;
        if (light) light.media = '(prefers-color-scheme: light)';
        if (dark) dark.media = '(prefers-color-scheme: dark)';
    } else {
        document.documentElement.dataset.theme = mode;
        if (light) light.media = mode === 'light' ? 'all' : 'not all';
        if (dark) dark.media = mode === 'dark' ? 'all' : 'not all';
    }
}

/// Applies the persisted mode and returns it ("system" | "light" | "dark").
export function init() {
    const mode = stored();
    apply(mode);
    return mode;
}

/// Advances system → light → dark → system, persists, applies, and returns the new mode.
export function cycle() {
    const next = MODES[(MODES.indexOf(stored()) + 1) % MODES.length];
    try {
        if (next === 'system') {
            localStorage.removeItem(STORAGE_KEY);
        } else {
            localStorage.setItem(STORAGE_KEY, next);
        }
    } catch {
        // Persisting failed; the mode still applies for this page's lifetime.
    }
    apply(next);
    return next;
}

/// Tints the chrome with the detected key: --pm-key-hue (0-360) feeds the card accents, the
/// reactive background, and the spectrum wall. The hue value comes from server-decided data.
export function setKeyHue(hue) {
    document.documentElement.style.setProperty('--pm-key-hue', String(hue));
}
