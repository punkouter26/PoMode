// View Transitions for route changes.
//
// The automatic cross-document form (`@view-transition { navigation: auto }`) does nothing here:
// this is a single-page app and the document never navigates. So the transition has to be driven
// around Blazor's own render, which is what this module does — it intercepts clicks on internal
// links in the capture phase, hands the navigation back to Blazor through a .NET callback, and holds
// the transition open until the new page has actually painted.
//
// Everything about it is optional. No View Transition API, effects turned off, a modified click, an
// external link, a download, a new tab — all fall straight through to the router untouched, which is
// the behaviour the app had before this file existed. That matters more than the animation: a page
// transition that can swallow a navigation is worse than no page transition.

import * as prefs from './fx-prefs.js';

let listener = null;
let dotNet = null;

/// Two frames, which is what it takes for a Blazor re-render to be on screen: the first fires after
/// the callback returns, the second after the render that callback scheduled has been painted.
function nextPaint() {
    return new Promise((resolve) => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
    });
}

/// The in-app path for a click, or null when this click is not ours to take. Deliberately
/// conservative — every uncertain case returns null and lets the browser and router do what they
/// already do correctly.
function internalHref(event) {
    if (event.defaultPrevented || event.button !== 0) {
        return null;
    }
    // A modified click means "open somewhere else", which is never a same-page transition.
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
        return null;
    }
    const anchor = event.target?.closest?.('a[href]');
    if (!anchor || anchor.hasAttribute('download') || anchor.target === '_blank') {
        return null;
    }
    // data-no-transition opts a link out — used by anything that navigates to a page which is about
    // to start its own animation, where a cross-fade on top would just be noise.
    if (anchor.dataset.noTransition !== undefined) {
        return null;
    }
    let url;
    try {
        url = new URL(anchor.href, document.baseURI);
    } catch {
        return null;
    }
    if (url.origin !== window.location.origin) {
        return null;
    }
    // A pure fragment jump is scroll, not navigation.
    if (url.pathname === window.location.pathname && url.search === window.location.search && url.hash) {
        return null;
    }
    if (url.href === window.location.href) {
        return null;
    }
    return url.href;
}

/// Starts intercepting. `dotNetRef` must expose a JSInvokable `NavigateTo(string)`.
export function init(dotNetRef) {
    dispose();
    if (typeof document.startViewTransition !== 'function') {
        // No API: leave the router entirely alone rather than installing a listener that only ever
        // declines. Nothing here is worth the cost of inspecting every click on the page.
        return false;
    }
    dotNet = dotNetRef;
    listener = (event) => {
        // Checked per click, not at init: the user can change the setting without a reload.
        if (!prefs.allowsMotion()) {
            return;
        }
        const href = internalHref(event);
        if (!href || !dotNet) {
            return;
        }
        event.preventDefault();
        try {
            document.startViewTransition(async () => {
                try {
                    await dotNet.invokeMethodAsync('NavigateTo', href);
                } catch {
                    // The router never ran — a disposed reference, a torn-down circuit. The click was
                    // already consumed by preventDefault, so leaving it here would strand the user on
                    // the page they asked to leave; a full document load is slower than the router but
                    // it always works.
                    window.location.assign(href);
                    return;
                }
                await nextPaint();
            });
        } catch {
            // If starting the transition throws, the navigation must still happen — a broken
            // animation is not a reason to strand the user on the page they clicked away from.
            dotNet.invokeMethodAsync('NavigateTo', href).catch(() => { });
        }
    };
    document.addEventListener('click', listener, true);
    return true;
}

export function dispose() {
    if (listener) {
        document.removeEventListener('click', listener, true);
        listener = null;
    }
    dotNet = null;
}
