// One place to ask "how much effect is this user willing to see and hear".
//
// Before this module every FX module answered that question for itself by reading
// prefers-reduced-motion directly, which had two problems: a user who wanted the shaders calmer but
// not gone had to change an OS setting to get it, and a user who wanted the visuals but not the UI
// sounds had no control at all. Both are now explicit, persisted, and independent:
//
//   level  'full' | 'subtle' | 'off'   how much decorative motion may run
//   muted  boolean                     whether synthesized UI sounds may play
//
// The OS preference is still honoured — it decides the DEFAULT level when the user has expressed no
// opinion — but an explicit choice wins over it in both directions, the same rule theme.js applies
// to prefers-color-scheme. That matters: "reduce motion" is set for many reasons, and a user who
// turns the effects back on here has answered the question more directly than the OS setting did.
//
// The effective level is stamped on <html> as data-fx so CSS can respond without JS, and every
// subscriber is notified on change so running shaders can downgrade or stop in place rather than
// waiting for a reload.

const LEVEL_KEY = 'pm-fx';
const MUTE_KEY = 'pm-fx-muted';
const LEVELS = ['full', 'subtle', 'off'];

/// Callbacks registered by the effect modules. A Set so a module that subscribes twice (a component
/// re-initialised without disposing) still only gets called once.
const listeners = new Set();

let reduceQuery = null;

function motionQuery() {
    if (!reduceQuery) {
        try {
            reduceQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
            // The OS preference can change while the page is open; when it does it only moves the
            // default, so a user with an explicit choice sees nothing happen.
            reduceQuery.addEventListener('change', () => {
                if (storedLevel() === null) {
                    apply();
                }
            });
        } catch {
            return null;
        }
    }
    return reduceQuery;
}

/// True when the OS asks for reduced motion. Treated as "unknown, assume no" if matchMedia is
/// unavailable, because guessing "reduce" would silently strip the app for everyone on that browser.
export function systemReducedMotion() {
    const query = motionQuery();
    return query ? query.matches : false;
}

/// The user's explicit level, or null when they have never chosen one.
function storedLevel() {
    try {
        const raw = localStorage.getItem(LEVEL_KEY);
        return LEVELS.includes(raw) ? raw : null;
    } catch {
        // Private windows and blocked site data land here; treat it as "no opinion recorded".
        return null;
    }
}

/// The level actually in force: the explicit choice if there is one, otherwise off under reduced
/// motion and full otherwise.
export function level() {
    return storedLevel() ?? (systemReducedMotion() ? 'off' : 'full');
}

/// True when the user has picked a level themselves, which is what lets the settings UI show
/// "following your system setting" honestly rather than claiming a choice they never made.
export function isExplicit() {
    return storedLevel() !== null;
}

export function muted() {
    try {
        return localStorage.getItem(MUTE_KEY) === '1';
    } catch {
        return false;
    }
}

/// Decorative motion of any kind. The floor below which an effect should render one static frame
/// (or nothing) instead of animating.
export function allowsMotion() {
    return level() !== 'off';
}

/// The expensive tier: compute shaders, large particle counts, per-frame physics. 'subtle' keeps
/// the cheap motion and drops these, which is the whole reason the middle setting exists.
export function allowsHeavy() {
    return level() === 'full';
}

/// Synthesized UI sounds. Tied to the level as well as the mute flag: someone who has turned the
/// effects off is not asking for chimes either.
export function allowsSound() {
    return !muted() && level() !== 'off';
}

/// Multiplier for effect amplitude — particle counts, glow radius, shake distance. Modules that can
/// scale continuously should prefer this over branching on the level name.
export function intensity() {
    switch (level()) {
        case 'off': return 0;
        case 'subtle': return 0.45;
        default: return 1;
    }
}

/// Stamps the effective state on <html> and tells every subscriber. Stamping both the level and the
/// mute flag means CSS can hide a sound-only affordance too, without a second lookup.
function apply() {
    try {
        document.documentElement.dataset.fx = level();
        document.documentElement.dataset.fxMuted = muted() ? '1' : '0';
    } catch {
        // No document (a worker importing this for the constants) — nothing to stamp.
    }
    for (const listener of listeners) {
        try {
            listener(level());
        } catch {
            // A throwing subscriber must not stop the others from downgrading.
        }
    }
}

/// Applies the persisted state and returns it. Call once, early.
export function init() {
    apply();
    return snapshot();
}

export function snapshot() {
    return { level: level(), muted: muted(), systemReduced: systemReducedMotion(), explicit: isExplicit() };
}

/// Records an explicit level. Passing 'system' clears the choice and hands the decision back to the
/// OS preference, which is the only way to get out of having an opinion once you have one.
export function setLevel(next) {
    try {
        if (next === 'system' || !LEVELS.includes(next)) {
            localStorage.removeItem(LEVEL_KEY);
        } else {
            localStorage.setItem(LEVEL_KEY, next);
        }
    } catch {
        // Not persisted; still applies for this page's lifetime via the stamp below.
    }
    apply();
    return snapshot();
}

export function setMuted(value) {
    try {
        localStorage.setItem(MUTE_KEY, value ? '1' : '0');
    } catch {
        // As above.
    }
    apply();
    return snapshot();
}

/// Registers a callback fired whenever the effective level changes. Returns an unsubscribe function;
/// effect modules should call it from dispose() so a torn-down canvas is not kept alive by the Set.
export function subscribe(callback) {
    if (typeof callback !== 'function') {
        return () => { };
    }
    listeners.add(callback);
    return () => listeners.delete(callback);
}
