// The analysis wait, filled in with what the pipeline has actually produced so far.
//
// An analysis takes minutes, and each stage leaves an artifact behind the moment it finishes. This
// draws those artifacts as they land, one layer per stage, so the progress card shows the result
// assembling rather than a spinner:
//
//   from the start       the upload's waveform, in the muted colour
//   Separating done      the separated vocal's waveform replaces it, in the key colour
//   PitchTracking done   the transcribed notes, as plain capsules
//   ChordDetecting done  the chord blocks along the bottom, with their symbols
//
// Every layer is a real artifact (the /peaks sketch, notes.json, chords.json), fetched once when the
// SignalR push says its stage completed — never polled. Nothing is coloured by role, because note
// roles do not exist until the modal analysis has run; the finished page's reveal takes it from
// there. Each layer sweeps in left to right when it arrives, unless effects are off.

import * as prefs from '../shell/prefs.js';

const states = new Map();

const SWEEP_MS = 900;
const CHORD_LANE_PX = 22;

function readColours(canvas) {
    const style = getComputedStyle(canvas);
    const read = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    return {
        lane: read('--pm-lane-bg', '#fbfbfd'),
        border: read('--pm-border', '#d9d9e0'),
        muted: read('--pm-fg-muted', '#4c4c57'),
        text: read('--pm-fg', '#1a1a1a'),
        key: read('--pm-key', '#6750a4'),
        note: read('--pm-accent', '#6750a4'),
        chord: read('--pm-chord-block', '#e4e4ed'),
    };
}

function invalidate(state) {
    if (state.frame === null) {
        state.frame = requestAnimationFrame(() => {
            state.frame = null;
            draw(state);
        });
    }
}

/// 0..1 share of the width a layer that arrived at `at` has swept across.
function sweep(state, at) {
    if (!state.animate || at === 0) {
        return 1;
    }
    return Math.min(1, (performance.now() - at) / SWEEP_MS);
}

function draw(state) {
    const canvas = state.canvas;
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(canvas.clientWidth, 1);
    const height = Math.max(canvas.clientHeight, 1);
    if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
        canvas.width = Math.round(width * ratio);
        canvas.height = Math.round(height * ratio);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    const colours = state.colours;
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = colours.lane;
    ctx.fillRect(0, 0, width, height);

    const duration = state.duration;
    const hasChords = state.chords.length > 0;
    const bodyHeight = height - (hasChords ? CHORD_LANE_PX + 4 : 0);
    const x = seconds => (duration > 0 ? (seconds / duration) * width : 0);
    let animating = false;

    const clipTo = share => {
        ctx.save();
        ctx.beginPath();
        ctx.rect(0, 0, width * share, height);
        ctx.clip();
    };

    if (state.peaks) {
        const share = sweep(state, state.peaksAt);
        animating ||= share < 1;
        clipTo(share);
        const columns = state.peaks.length / 2;
        const middle = bodyHeight / 2;
        const amp = bodyHeight * 0.46;
        ctx.globalAlpha = state.source === 'vocals' ? 0.35 : 0.22;
        ctx.fillStyle = state.source === 'vocals' ? colours.key : colours.muted;
        ctx.beginPath();
        for (let column = 0; column < columns; column++) {
            const left = (column / columns) * width;
            const low = state.peaks[column * 2];
            const high = state.peaks[(column * 2) + 1];
            ctx.rect(left, middle - (high * amp), Math.max(width / columns, 1), Math.max((high - low) * amp, 1));
        }
        ctx.fill();
        ctx.globalAlpha = 1;
        ctx.restore();
    }

    if (state.notes.length > 0 && duration > 0) {
        const share = sweep(state, state.notesAt);
        animating ||= share < 1;
        clipTo(share);
        const span = Math.max(state.maxPitch - state.minPitch + 1, 12);
        const row = (bodyHeight - 8) / span;
        ctx.fillStyle = colours.note;
        for (const note of state.notes) {
            const left = x(note.startSec);
            const top = 4 + ((state.maxPitch - note.midiPitch) * row);
            ctx.fillRect(left, top, Math.max(x(note.startSec + note.durationSec) - left, 1.5), Math.max(row - 1, 2));
        }
        ctx.restore();
    }

    if (hasChords && duration > 0) {
        const share = sweep(state, state.chordsAt);
        animating ||= share < 1;
        clipTo(share);
        const top = height - CHORD_LANE_PX;
        ctx.font = '12px system-ui, sans-serif';
        ctx.textBaseline = 'middle';
        for (const chord of state.chords) {
            const left = x(chord.startSec);
            const blockWidth = Math.max(x(chord.endSec) - left, 2);
            ctx.fillStyle = colours.chord;
            ctx.fillRect(left + 1, top, Math.max(blockWidth - 2, 1), CHORD_LANE_PX);
            if (blockWidth >= 28) {
                ctx.fillStyle = colours.text;
                ctx.fillText(chord.symbol, left + 5, top + (CHORD_LANE_PX / 2));
            }
        }
        ctx.restore();
    }

    ctx.strokeStyle = colours.border;
    ctx.lineWidth = 1;
    ctx.strokeRect(0.5, 0.5, width - 1, height - 1);

    if (animating) {
        invalidate(state);
    }
}

async function fetchJson(url) {
    try {
        const response = await fetch(url);
        return response.ok ? await response.json() : null;
    } catch {
        return null;
    }
}

/// `dotNet` hears which waveform is on screen ('mix' or 'vocals'), so the caption can say so.
export function init(canvas, dotNet) {
    dispose(canvas);
    const state = {
        canvas,
        dotNet: dotNet ?? null,
        vocalsAsked: false,
        colours: readColours(canvas),
        animate: prefs.allowsMotion(),
        frame: null,
        jobId: null,
        duration: 0,
        peaks: null,
        source: null,
        peaksAt: 0,
        notes: [],
        notesAt: 0,
        minPitch: 60,
        maxPitch: 72,
        chords: [],
        chordsAt: 0,
        pending: new Set(),
    };
    state.resize = new ResizeObserver(() => invalidate(state));
    state.resize.observe(canvas);
    state.onTheme = () => {
        state.colours = readColours(canvas);
        invalidate(state);
    };
    state.scheme = window.matchMedia('(prefers-color-scheme: dark)');
    state.scheme.addEventListener('change', state.onTheme);
    state.themeObserver = new MutationObserver(state.onTheme);
    state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    states.set(canvas, state);
    invalidate(state);
}

/// Fetches whatever the completed stages have made available and not yet been drawn. Called on each
/// status push; every layer is fetched once (the waveform twice: the upload's, then the vocal's).
export async function refresh(canvas, jobId, completedStages) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.jobId !== jobId) {
        Object.assign(state, { jobId, peaks: null, source: null, vocalsAsked: false, notes: [], chords: [], duration: 0 });
    }
    const completed = new Set(completedStages ?? []);
    const base = `api/analysis/${jobId}`;
    const once = async (key, work) => {
        if (state.pending.has(key)) {
            return;
        }
        state.pending.add(key);
        try {
            await work();
        } finally {
            state.pending.delete(key);
        }
    };

    // Asked for once more after separation: the answer is the vocal stem when one was written, and
    // the same upload sketch when the separator wrote none (a dry take, a failed model).
    const askVocals = completed.has('Separating') && !state.vocalsAsked;
    const jobs = [];
    if (!state.peaks || askVocals) {
        state.vocalsAsked ||= askVocals;
        jobs.push(once('peaks', async () => {
            const peaks = await fetchJson(`${base}/peaks`);
            // Only a change of source redraws: the upload's sketch never replaces the vocal's, and
            // the same sketch fetched twice must not sweep in again.
            if (peaks && state.jobId === jobId && peaks.source !== state.source && state.source !== 'vocals') {
                state.peaks = peaks.peaks;
                state.source = peaks.source;
                state.duration = Math.max(state.duration, peaks.durationSec);
                state.peaksAt = performance.now();
                canvas.dataset.waveSource = peaks.source;
                state.dotNet?.invokeMethodAsync('OnPreviewSource', peaks.source);
            }
        }));
    }
    if (completed.has('PitchTracking') && state.notes.length === 0) {
        jobs.push(once('notes', async () => {
            const notes = await fetchJson(`${base}/notes`);
            if (Array.isArray(notes) && notes.length > 0 && state.jobId === jobId) {
                state.notes = notes;
                state.minPitch = Math.min(...notes.map(note => note.midiPitch));
                state.maxPitch = Math.max(...notes.map(note => note.midiPitch));
                state.duration = Math.max(state.duration, ...notes.map(note => note.startSec + note.durationSec));
                state.notesAt = performance.now();
            }
            canvas.dataset.noteCount = String(state.notes.length);
        }));
    }
    if (completed.has('ChordDetecting') && state.chords.length === 0) {
        jobs.push(once('chords', async () => {
            const chords = await fetchJson(`${base}/chords`);
            if (Array.isArray(chords) && chords.length > 0 && state.jobId === jobId) {
                state.chords = chords;
                state.duration = Math.max(state.duration, ...chords.map(chord => chord.endSec));
                state.chordsAt = performance.now();
            }
            canvas.dataset.chordCount = String(state.chords.length);
        }));
    }
    await Promise.all(jobs);
    invalidate(state);
}

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.frame !== null) {
        cancelAnimationFrame(state.frame);
    }
    state.resize.disconnect();
    state.scheme.removeEventListener('change', state.onTheme);
    state.themeObserver.disconnect();
    states.delete(canvas);
}
