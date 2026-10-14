// The phone's docked player: once the mixer's own transport has scrolled out of view on the long
// analysis page, a slim bar at the foot of the screen carries Play/Pause and the key, so reading the
// statistics further down never means scrolling back up to stop the music. The bar is only shown
// while the real transport is out of view, so the two are never on screen together.

const observers = new WeakMap();

export function watch(transport, dock) {
    unwatch(transport);
    if (!transport || !dock || typeof IntersectionObserver === 'undefined') {
        return;
    }
    const observer = new IntersectionObserver(([entry]) => {
        dock.dataset.docked = entry.isIntersecting ? '0' : '1';
    }, { threshold: 0 });
    observer.observe(transport);
    observers.set(transport, observer);
}

export function unwatch(transport) {
    observers.get(transport)?.disconnect();
    observers.delete(transport);
}
