// Ctrl+K (Cmd+K on a Mac) opens the command palette from anywhere in the app. The palette itself is
// a Blazor component; this only turns the key into a call, and keeps the browser's own Ctrl+K (the
// address bar's search) from firing underneath it.

let handler = null;

export function listen(dotNet) {
    stop();
    handler = (event) => {
        if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
            event.preventDefault();
            dotNet.invokeMethodAsync('OpenPalette').catch(() => { });
        }
    };
    window.addEventListener('keydown', handler);
}

export function stop() {
    if (handler) {
        window.removeEventListener('keydown', handler);
        handler = null;
    }
}
