// The JS side of spec §5's "specified once, implemented twice" contract. This is a direct port of
// PoMode.API/Features/PitchTracking/BasicPitchDecoder.cs — same thresholds, same emit rules, same
// ordering. Both implementations are asserted against the same fixture file
// (tests/TestCommon/basic-pitch-decoder.fixture.json); change one side and the other's test fails
// naming the note. Plain ES module: no npm, no bundler.

/**
 * Converts Basic Pitch's raw onset/frame posterior arrays into discrete note events.
 * @param {number[][]} onsets  [frame][pitchBin] onset posteriors
 * @param {number[][]} frames  [frame][pitchBin] frame (note-activation) posteriors
 * @param {number} framesPerSecond
 * @param {number} minMidi  MIDI pitch of bin 0
 * @returns {{midiPitch: number, startSec: number, durationSec: number, velocity: number}[]}
 */
export function decodeNotes(
    onsets,
    frames,
    framesPerSecond,
    minMidi,
    onsetThreshold = 0.5,
    frameThreshold = 0.3,
    minDurationSec = 0.058) {
    const frameCount = onsets.length;
    const pitchCount = frameCount > 0 ? onsets[0].length : 0;
    const frameDuration = 1.0 / framesPerSecond;
    const notes = [];

    const emit = (bin, startFrame, endFrame, sumEnergy, count) => {
        const durationSec = (endFrame - startFrame) * frameDuration;
        if (durationSec < minDurationSec) {
            return;
        }
        const meanEnergy = count > 0 ? sumEnergy / count : 0;
        // C#'s (int) cast truncates toward zero; Math.trunc is the same operation.
        const velocity = Math.min(127, Math.max(1, Math.trunc(meanEnergy * 127)));
        notes.push({
            midiPitch: bin + minMidi,
            startSec: startFrame * frameDuration,
            durationSec,
            velocity,
        });
    };

    for (let bin = 0; bin < pitchCount; bin++) {
        let start = null;
        let sumEnergy = 0;
        let count = 0;

        for (let f = 0; f < frameCount; f++) {
            const onsetFires = onsets[f][bin] >= onsetThreshold;

            if (start !== null && onsetFires) {
                emit(bin, start, f, sumEnergy, count);
                start = null;
                sumEnergy = 0;
                count = 0;
            }

            if (start === null && onsetFires) {
                start = f;
            }

            if (start !== null) {
                if (frames[f][bin] >= frameThreshold) {
                    sumEnergy += frames[f][bin];
                    count++;
                } else {
                    emit(bin, start, f, sumEnergy, count);
                    start = null;
                    sumEnergy = 0;
                    count = 0;
                }
            }
        }

        if (start !== null) {
            emit(bin, start, frameCount, sumEnergy, count);
        }
    }

    // Same ordering as the C# decoder: by start time, then by pitch. Array.prototype.sort is
    // stable per the ECMAScript spec, matching LINQ's stable OrderBy/ThenBy.
    notes.sort((a, b) => a.startSec - b.startSec || a.midiPitch - b.midiPitch);
    return notes;
}
