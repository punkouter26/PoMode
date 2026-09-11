// Procedural cover art for the library wall.
//
// Every song in the library has a key, a mode and a tempo, and the grid rendered all three as text
// in three columns. Those same three numbers make a perfectly good picture, and a picture is what a
// shelf of music is supposed to look like — so the wall view draws one cover per song from exactly
// the facts the analysis produced and nothing else.
//
// The art is a function of the analysis, not of the audio: same song, same reading, same cover,
// every time and on every machine. That determinism is the point — a cover that shuffled on each
// visit would be decoration, while one that does not becomes something you recognise a song by.
//
// Nothing here is a musical claim. The hue comes from the tonic's pitch class, the ring count from
// how many notes the mode has, the pulse from the tempo — all of them handed over by the page from
// server-decided values. This module does not know what a mode is; it knows how many arcs to draw.
//
// 2D canvas, one per cover, painted once. Covers are static images: there is no loop, no rAF and no
// GL context, and a wall of forty of them costs one paint each.

import * as prefs from './fx-prefs.js';

/// Deterministic 32-bit hash of the seed string, so a job id becomes a stable stream of numbers.
/// FNV-1a: short, no dependencies, and well enough distributed that two adjacent job ids do not
/// produce two near-identical covers.
function hashSeed(text) {
    let hash = 2166136261;
    const value = String(text ?? '');
    for (let i = 0; i < value.length; i++) {
        hash ^= value.charCodeAt(i);
        hash = Math.imul(hash, 16777619);
    }
    return hash >>> 0;
}

/// A small deterministic PRNG seeded from the hash above (mulberry32). Every random-looking choice
/// in a cover comes from one of these, which is what makes the whole thing reproducible.
function rng(seed) {
    let a = seed >>> 0;
    return () => {
        a = (a + 0x6d2b79f5) >>> 0;
        let t = Math.imul(a ^ (a >>> 15), 1 | a);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

/// Paints one cover.
///
/// `spec` carries only server-decided facts:
///   seed       stable string identifying the song (its job id)
///   hue        0-360, the tonic's pitch class mapped to the colour wheel by the page
///   degrees    how many notes the mode has — 7 for the diatonic modes, 5 for the pentatonics
///   tempo      BPM, or 0 when none was measured
///   settled    false when the analysis never finished, which the art states rather than hides
export function paint(canvas, spec) {
    if (!canvas || typeof canvas.getContext !== 'function') {
        return false;
    }
    const ctx = canvas.getContext('2d');
    if (!ctx) {
        return false;
    }

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const size = Math.max(Math.round(canvas.clientWidth * dpr), 1);
    canvas.width = size;
    canvas.height = size;

    const hue = Number.isFinite(spec?.hue) ? spec.hue : 262;
    const degrees = Math.max(3, Math.min(spec?.degrees || 7, 12));
    const tempo = Math.max(0, Math.min(spec?.tempo || 0, 220));
    const settled = spec?.settled !== false;
    const random = rng(hashSeed(spec?.seed));

    // Unfinished jobs get a desaturated cover. An analysis that never produced a key should not be
    // represented by a confident piece of art — the shelf would be claiming something it does not
    // have, which is the same failure the stats fingerprint avoids by omitting weak figures.
    const saturation = settled ? 62 : 10;

    // 1. Ground: a two-stop wash at the key's hue, angled by the seed so no two covers in a row
    //    share a direction.
    const angle = random() * Math.PI * 2;
    const gradient = ctx.createLinearGradient(
        size * (0.5 - Math.cos(angle) * 0.5), size * (0.5 - Math.sin(angle) * 0.5),
        size * (0.5 + Math.cos(angle) * 0.5), size * (0.5 + Math.sin(angle) * 0.5));
    gradient.addColorStop(0, `hsl(${hue} ${saturation}% ${settled ? 22 : 18}%)`);
    gradient.addColorStop(1, `hsl(${(hue + 42) % 360} ${saturation}% ${settled ? 46 : 24}%)`);
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, size, size);

    // 2. One arc per note in the mode, around a common centre. A pentatonic cover is visibly
    //    sparser than a diatonic one, which is the single most useful thing the picture can say at
    //    thumbnail size.
    const centre = { x: size * (0.34 + random() * 0.32), y: size * (0.34 + random() * 0.32) };
    ctx.lineCap = 'round';
    for (let i = 0; i < degrees; i++) {
        const t = i / degrees;
        const radius = size * (0.12 + (t * 0.34) + (random() * 0.04));
        const start = random() * Math.PI * 2;
        // Longer arcs low in the stack, shorter ones high, so the shape reads as a form rather than
        // as a set of equal rings.
        const sweep = (Math.PI * (0.35 + ((1 - t) * 1.15))) * (0.7 + random() * 0.5);
        ctx.beginPath();
        ctx.arc(centre.x, centre.y, radius, start, start + sweep);
        ctx.strokeStyle = `hsl(${(hue + (t * 60)) % 360} ${saturation + 16}% ${settled ? 72 : 42}% / ${0.24 + (1 - t) * 0.42})`;
        ctx.lineWidth = size * (0.006 + ((1 - t) * 0.016));
        ctx.stroke();
    }

    // 3. Tempo as a pulse: a row of ticks along the foot, one per ten BPM. Absent entirely when no
    //    tempo was measured, rather than drawn at some default — the same rule the fingerprint
    //    follows about omitting rather than hedging.
    if (tempo > 0) {
        const ticks = Math.round(tempo / 10);
        const margin = size * 0.1;
        const span = size - (margin * 2);
        ctx.fillStyle = `hsl(${hue} ${saturation + 20}% 88% / 0.55)`;
        for (let i = 0; i < ticks; i++) {
            const x = margin + ((i / Math.max(ticks - 1, 1)) * span);
            const height = size * (0.012 + ((i % 4 === 0) ? 0.022 : 0));
            ctx.fillRect(x, size - margin - height, Math.max(size * 0.005, 1), height);
        }
    }

    // 4. Grain, so a large cover does not band across the gradient. Same reasoning as the noise veil
    //    on the glass cards, done here with pixels because the canvas has no backdrop to filter.
    const grainCount = Math.round(size * size * 0.02);
    ctx.fillStyle = 'rgb(255 255 255 / 0.035)';
    for (let i = 0; i < grainCount; i++) {
        ctx.fillRect(random() * size, random() * size, 1, 1);
    }

    // 5. A vignette to seat the art inside its frame.
    const vignette = ctx.createRadialGradient(size / 2, size / 2, size * 0.25, size / 2, size / 2, size * 0.72);
    vignette.addColorStop(0, 'rgb(0 0 0 / 0)');
    vignette.addColorStop(1, 'rgb(0 0 0 / 0.42)');
    ctx.fillStyle = vignette;
    ctx.fillRect(0, 0, size, size);

    canvas.dataset.cover = settled ? 'painted' : 'unsettled';
    return true;
}

/// Paints every cover on the page that has not been painted yet.
///
/// Each canvas carries its own facts as data attributes, set by Blazor, so the wall can be
/// re-rendered, filtered or paged and this can be called again without the page having to track
/// which canvases are new. Already-painted covers are skipped, which is what keeps a re-render from
/// repainting forty canvases.
export function paintAll(root) {
    const host = root ?? document;
    const canvases = host.querySelectorAll('canvas.library-cover:not([data-cover])');
    let painted = 0;
    for (const canvas of canvases) {
        const ok = paint(canvas, {
            seed: canvas.dataset.seed,
            hue: parseFloat(canvas.dataset.hue),
            degrees: parseInt(canvas.dataset.degrees, 10),
            tempo: parseFloat(canvas.dataset.tempo),
            settled: canvas.dataset.settled !== 'false',
        });
        if (ok) {
            painted++;
        }
    }
    return painted;
}

/// Whether the wall may tilt its covers under the pointer. Read by the page rather than applied
/// here, because the tilt is a CSS transform on the card and belongs in the stylesheet with the
/// rest of the card's presentation.
export function allowsTilt() {
    return prefs.allowsHeavy();
}
