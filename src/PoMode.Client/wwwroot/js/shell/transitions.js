// The one page transition left, and it carries a fact: opening a song from the library, its key
// ("F Lydian" in the row) moves to become the analysis page's headline key badge, so the eye follows
// the measurement from the list into the reading of it. Built on the View Transitions API, which
// snapshots the two states and morphs one named element into the other; nothing else moves.
//
// Only where the browser has the API and the effects level allows motion; otherwise the link is an
// ordinary link. The row's key carries the name only for the duration of the click, because a name
// must be unique on the page and every row has a key.

import { allowsMotion } from './prefs.js';

const NAME_CLASS = 'pm-morph-key';
const WAIT_MS = 1500;

let installed = false;

export function install() {
    if (installed || typeof document.startViewTransition !== 'function') {
        return;
    }
    installed = true;
    // Capture phase, ahead of Blazor's own link handling, so this click becomes one navigation.
    document.addEventListener('click', (event) => {
        const link = event.target instanceof Element ? event.target.closest('a[data-morph]') : null;
        if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey
            || event.shiftKey || event.altKey || !allowsMotion() || !window.Blazor?.navigateTo) {
            return;
        }
        const row = link.closest('tr, .compare-card');
        const key = row?.querySelector('[data-morph-key]');
        if (!key) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        key.classList.add(NAME_CLASS);
        const url = link.getAttribute('href');
        document.startViewTransition(async () => {
            key.classList.remove(NAME_CLASS);
            window.Blazor.navigateTo(url);
            await headlineRendered();
        });
    }, true);
}

/// Resolves once the analysis page's headline key is in the DOM, or after a moment regardless: a
/// slow load should cut over, not freeze the old page.
function headlineRendered() {
    return new Promise((resolve) => {
        const started = performance.now();
        const check = () => {
            if (document.querySelector(`[data-morph-target].${NAME_CLASS}`) || performance.now() - started > WAIT_MS) {
                resolve();
            } else {
                requestAnimationFrame(check);
            }
        };
        check();
    });
}
