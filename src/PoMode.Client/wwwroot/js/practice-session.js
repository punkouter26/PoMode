// Practice page: hear the phrase, then sing it back against a click while the browser transcribes.
//
// A fourth capture path, and the reason it is not one of the other three is timing. audio-recorder.js
// and live-session.js hand the server audio or notes whose zero is "when the singer started";
// hum-recorder.js trims its take so zero is bar one of a loop. This one needs something none of them
// produce: notes timed from a downbeat the SERVER chose, because the grading compares each sung onset
// against a target onset the server issued. A take rebased onto its own first note would score a
// singer who came in two beats late as perfectly in time.
//
// Everything musical stays server-side as usual. This module detects what frequency is sounding
// (live-pitch.js, shared with the Live page) and when; whether that was the right note is decided by
// ModeExerciseGrader.

import { detectPitch, openMicrophone, createNoteCollector } from './live-pitch.js';
import { scheduleBeats } from './click-track.js';
import * as player from './modal-player.js';
import * as ribbon from './fx-practice-ribbon.js';
import * as sfx from './sfx.js';

/// Beats of count-in before the phrase starts. Four is a bar in the only metre the exercises use,
/// and it is what a singer expects.
const COUNT_IN_BEATS = 4;

/// Kept clicking for a beat past the end of the phrase, so the final note has a pulse to be sung
/// against rather than trailing off into silence.
const TAIL_BEATS = 1;

/// How long after the phrase ends the microphone stays open. A note begun on the last beat is still
/// being sung when the phrase is over, and cutting the recording there would truncate it.
const TAIL_SECONDS = 1.2;

let mic = null;
let collector = null;
let phraseStartTime = null;
let lastFrameTime = 0;
let attemptTimer = null;

/// Pitch classes of the mode being drilled, exactly as the server sent them with the exercise, and
/// the MIDI pitches of its target notes. Held here only so the live cue can be sounded without a
/// round trip; neither is interpreted. Whether the take was any good is ModeExerciseGrader's answer.
let modeClasses = null;
let targetPitches = null;

/// The pitch class most recently cued, so a held note ticks once rather than sixty times a second.
let lastCued = -1;

/// A quiet real-time nudge: a soft tick when the sung note is one the phrase asked for, a duller
/// one when it is outside the mode's set. Deliberately not a verdict — it fires on the pitch class
/// alone, says nothing about timing or octave, and is silent when there is no set to compare
/// against. The page still shows only what the server scored.
function cue(midi) {
    if (midi === null || !modeClasses) {
        lastCued = -1;
        return;
    }
    const rounded = Math.round(midi);
    const pitchClass = ((rounded % 12) + 12) % 12;
    if (pitchClass === lastCued) {
        return;
    }
    lastCued = pitchClass;
    if (!modeClasses.has(pitchClass)) {
        sfx.outside();
    } else if (targetPitches && targetPitches.has(rounded)) {
        sfx.land(1);
    } else {
        sfx.land(0.4);
    }
}

/// Records what the current exercise is made of, for the cue above. Called by the page when it loads
/// a phrase; passing null silences the cue entirely.
export function setExercise(pitchClasses, targetNotes) {
    modeClasses = Array.isArray(pitchClasses) && pitchClasses.length > 0
        ? new Set(pitchClasses.map((pc) => ((pc % 12) + 12) % 12))
        : null;
    targetPitches = Array.isArray(targetNotes) && targetNotes.length > 0
        ? new Set(targetNotes.map((n) => n.midiPitch ?? n.MidiPitch).filter(Number.isFinite))
        : null;
    lastCued = -1;
}

/// Mirrored onto the document, matching the mixer.js / modal-player.js / hum-recorder.js contract so
/// Playwright asserts on attributes rather than reaching into module internals.
function publishState(state) {
    try {
        if (!document.body) return;
        document.body.dataset.practice = state;
        document.body.dataset.practiceNotes = collector ? String(collector.snapshotFrom(phraseStartTime ?? 0, lastFrameTime).length) : '0';
    } catch { }
}

/// Plays the target phrase through the Mode Lab's flute voice. No backing: the exercise is about one
/// line, and harmony under it would tell the ear the answer.
export async function playPhrase(notes, durationSec) {
    const ctx = player.getSharedContext();
    if (ctx && ctx.state === 'suspended') {
        await ctx.resume().catch(() => { });
    }
    player.setLooping(false);
    // stop() first: the player keeps a pause offset between calls, so a phrase played after a
    // paused Mode Lab loop would otherwise start partway through itself.
    player.stop();
    player.play(notes || [], [], durationSec, null);
    publishState('demo');
    return true;
}

export function stopPlayback() {
    try { player.stop(); } catch { }
    publishState(mic ? 'recording' : 'idle');
}

/// Opens the microphone and counts in.
///
/// Resolves true once the count-in has finished and the phrase window has begun, so the caller can
/// show "sing now" at the moment it is actually true — then awaits `waitForPhrase` for the rest. The
/// microphone is opened BEFORE the count-in rather than after it: getUserMedia can take a noticeable
/// moment the first time, and opening it on the downbeat would swallow the first note of every first
/// attempt.
export async function start(bpm, durationSec) {
    cancel();
    ribbon.clearLive();
    lastCued = -1;

    const ctx = player.getSharedContext();
    if (!ctx) return false;
    if (ctx.state === 'suspended') {
        await ctx.resume().catch(() => { });
    }

    collector = createNoteCollector();
    lastFrameTime = 0;
    try {
        mic = await openMicrophone((samples, sampleRate, contextTime) => {
            lastFrameTime = contextTime;
            const midi = detectPitch(samples, sampleRate);
            collector.push(midi, contextTime);
            // The plot and the earcons hang off the same frame the grader's notes come from, so what
            // is drawn and what is scored can never be two different readings of the take. Both are
            // no-ops when the user has the effects off.
            // Negative until the count-in is over, which is what keeps the plot and the cue silent
            // while the singer is still waiting for the downbeat.
            const elapsed = phraseStartTime === null ? -1 : contextTime - phraseStartTime;
            if (elapsed >= 0) {
                ribbon.pushSample(elapsed, midi);
                cue(midi);
            }
        }, ctx);
    } catch {
        collector = null;
        publishState('denied');
        return false;
    }

    // Scheduled from a small lead so the first click is comfortably in the future when the audio
    // thread next looks; the returned instant is the phrase's own zero.
    const countInStart = ctx.currentTime + 0.15;
    phraseStartTime = scheduleBeats(ctx, countInStart, bpm, COUNT_IN_BEATS);

    const secondsPerBeat = 60.0 / (bpm > 0 ? bpm : 100.0);
    const phraseBeats = Math.ceil(durationSec / secondsPerBeat) + TAIL_BEATS;
    scheduleBeats(ctx, phraseStartTime, bpm, phraseBeats);

    publishState('counting');
    await wait((phraseStartTime - ctx.currentTime) * 1000);
    publishState('recording');
    return true;
}

/// Resolves when the phrase window (plus its tail) is over. Separate from `start` so the page can
/// put "sing now" on screen the instant recording begins rather than only after it ends.
///
/// Timed off the audio clock rather than from now: a slow render between the two calls must not
/// shorten the window the singer is being graded over.
export function waitForPhrase(durationSec) {
    const ctx = player.getSharedContext();
    const endsAt = (phraseStartTime ?? ctx.currentTime) + durationSec + TAIL_SECONDS;
    return new Promise((resolve) => {
        attemptTimer = setTimeout(() => {
            attemptTimer = null;
            resolve(true);
        }, Math.max(0, (endsAt - ctx.currentTime) * 1000));
    });
}

/// Closes the microphone and returns what was sung, timed from the phrase's downbeat. Notes sung
/// during the count-in fall before zero and are dropped by snapshotFrom.
export function finish() {
    ribbon.settle();
    sfx.thud();
    const origin = phraseStartTime ?? 0;
    const notes = collector ? collector.snapshotFrom(origin, Math.max(lastFrameTime, origin)) : [];
    teardown();
    publishState('idle');
    return notes;
}

/// Abandons an attempt without grading it.
export function cancel() {
    teardown();
    publishState('idle');
}

function teardown() {
    if (attemptTimer !== null) {
        clearTimeout(attemptTimer);
        attemptTimer = null;
    }
    if (mic) {
        // Stopping the tracks is what turns the browser's recording indicator off.
        try { mic.stop(); } catch { }
        mic = null;
    }
    collector = null;
    phraseStartTime = null;
}

export function dispose() {
    cancel();
    stopPlayback();
}

function wait(milliseconds) {
    return new Promise((resolve) => setTimeout(resolve, Math.max(0, milliseconds)));
}

// ---- Streak, kept in this browser only ----
//
// Deliberately not server state. A streak is a per-viewer convenience — the same class of thing as
// the theme override — and this app has no user store to hang one off. Persisting it server-side
// would also make it the only claim in the app that is about the person rather than the music.
// localStorage can be absent or throw (private windows, blocked site data), so every access is
// guarded and the page renders correctly with no record at all.

const STREAK_KEY = 'pm-practice';

/// A passing score. Deliberately below "sang it perfectly": the streak is there to reward turning
/// up, and a bar only a good day clears is one nobody keeps.
const PASS_SCORE = 70;

export function loadStreak() {
    try {
        const raw = localStorage.getItem(STREAK_KEY);
        if (!raw) return { streak: 0, best: 0, attempts: 0 };
        const parsed = JSON.parse(raw);
        return {
            streak: Number(parsed.streak) || 0,
            best: Number(parsed.best) || 0,
            attempts: Number(parsed.attempts) || 0,
        };
    } catch {
        return { streak: 0, best: 0, attempts: 0 };
    }
}

/// Folds one graded attempt into the record and returns the new one.
export function recordResult(score) {
    const current = loadStreak();
    const next = {
        streak: score >= PASS_SCORE ? current.streak + 1 : 0,
        best: Math.max(current.best, score),
        attempts: current.attempts + 1,
    };
    try {
        localStorage.setItem(STREAK_KEY, JSON.stringify(next));
    } catch {
        // A browser that will not store it still gets the number for this session.
    }
    try {
        if (document.body) {
            document.body.dataset.practiceStreak = String(next.streak);
        }
    } catch { }
    return next;
}
