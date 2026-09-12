// The count-in / metronome click, shared by the Mode Lab hum recorder and the Practice page.
//
// Pitched at 1050 Hz and 1600 Hz, which is not an aesthetic choice: live-pitch.js only looks for
// fundamentals between 60 and 1000 Hz, so a click sounding while the microphone is open cannot be
// transcribed as a sung note. That is what lets the Practice page keep clicking all the way through
// a take instead of falling silent the moment it starts listening.

/// One click on `ctx`'s clock at `when` (an absolute currentTime instant).
export function click(ctx, when, isDownbeat) {
    const osc = ctx.createOscillator();
    osc.type = 'square';
    osc.frequency.setValueAtTime(isDownbeat ? 1600 : 1050, when);

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, when);
    env.gain.exponentialRampToValueAtTime(isDownbeat ? 0.22 : 0.13, when + 0.004);
    env.gain.exponentialRampToValueAtTime(0.0001, when + 0.07);

    osc.connect(env);
    env.connect(ctx.destination);
    osc.start(when);
    osc.stop(when + 0.09);
    osc.onended = () => { try { env.disconnect(); } catch { } };
}

/// Schedules `beats` clicks at `bpm` starting at `startTime`, first one accented. Returns the
/// instant the beat AFTER the last click falls on — which is where whatever follows the count-in
/// begins, so the caller never has to redo the arithmetic.
export function scheduleBeats(ctx, startTime, bpm, beats) {
    const secondsPerBeat = 60.0 / (bpm > 0 ? bpm : 100.0);
    for (let i = 0; i < beats; i++) {
        click(ctx, startTime + (i * secondsPerBeat), i === 0);
    }
    return startTime + (beats * secondsPerBeat);
}
