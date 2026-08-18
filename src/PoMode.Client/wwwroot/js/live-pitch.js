// Live microphone pitch tracking shared by the karaoke overlay (mixer.js) and the Live page.
// Signal processing only — like the Basic Pitch port, deciding WHAT frequency is sounding is
// allowed here; deciding what it MEANS musically stays server-side.

const MIN_FREQ = 60;
const MAX_FREQ = 1000;
const RMS_GATE = 0.01;
const CLARITY_GATE = 0.5;

/// Normalized-autocorrelation pitch detection over one mono frame. Returns a fractional MIDI
/// number, or null for silence and unpitched input.
export function detectPitch(samples, sampleRate) {
    let energy = 0;
    for (let i = 0; i < samples.length; i++) {
        energy += samples[i] * samples[i];
    }
    if (Math.sqrt(energy / samples.length) < RMS_GATE) {
        return null;
    }

    const minLag = Math.floor(sampleRate / MAX_FREQ);
    const maxLag = Math.min(Math.floor(sampleRate / MIN_FREQ), samples.length - 2);
    if (maxLag <= minLag) {
        return null;
    }

    let bestLag = -1;
    let bestScore = 0;
    const scores = new Float32Array(maxLag + 1);
    for (let lag = minLag; lag <= maxLag; lag++) {
        let sum = 0;
        let lagEnergy = 0;
        const terms = samples.length - lag;
        for (let i = 0; i < terms; i++) {
            sum += samples[i] * samples[i + lag];
            lagEnergy += samples[i + lag] * samples[i + lag];
        }
        const score = lagEnergy > 0 ? sum / Math.sqrt(energy * lagEnergy) : 0;
        scores[lag] = score;
        if (score > bestScore) {
            bestScore = score;
            bestLag = lag;
        }
    }
    if (bestLag < 0 || bestScore < CLARITY_GATE) {
        return null;
    }

    // Parabolic refinement around the winning lag for sub-bin precision.
    let lag = bestLag;
    if (bestLag > minLag && bestLag < maxLag) {
        const left = scores[bestLag - 1];
        const mid = scores[bestLag];
        const right = scores[bestLag + 1];
        const denom = left - (2 * mid) + right;
        if (Math.abs(denom) > 1e-12) {
            lag += Math.min(Math.max(0.5 * (left - right) / denom, -1), 1);
        }
    }

    const freq = sampleRate / lag;
    return 69 + (12 * Math.log2(freq / 440));
}

/// Opens the microphone and calls onFrame(samples, sampleRate, contextTimeSec) per audio block.
/// Returns { stop } — always call stop, it releases the mic. `context` may be an existing
/// AudioContext (the mixer shares its own) or omitted to create one.
export async function openMicrophone(onFrame, context) {
    const stream = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: false },
    });
    const ownsContext = !context;
    const ctx = context ?? new (window.AudioContext || window.webkitAudioContext)();
    if (ctx.state === 'suspended') {
        await ctx.resume();
    }
    const source = ctx.createMediaStreamSource(stream);
    const processor = ctx.createScriptProcessor(2048, 1, 1);
    // A muted sink: ScriptProcessor only fires while connected to the destination.
    const sink = ctx.createGain();
    sink.gain.value = 0;

    processor.onaudioprocess = event => {
        const samples = event.inputBuffer.getChannelData(0);
        onFrame(samples, ctx.sampleRate, ctx.currentTime);
    };
    source.connect(processor);
    processor.connect(sink);
    sink.connect(ctx.destination);

    return {
        stop() {
            processor.onaudioprocess = null;
            source.disconnect();
            processor.disconnect();
            sink.disconnect();
            for (const track of stream.getTracks()) {
                track.stop();
            }
            if (ownsContext) {
                ctx.close();
            }
        },
    };
}

/// Turns a stream of (midi|null, timeSec) samples into discrete note events for the server:
/// a note opens when a rounded pitch holds, extends while it continues, and closes on a pitch
/// change or a gap. Velocity is fixed — a mic level says nothing reliable about intent.
export function createNoteCollector() {
    const MIN_NOTE_SEC = 0.09;
    const GAP_SEC = 0.15;
    const notes = [];
    let open = null;

    const close = endSec => {
        if (open && (endSec - open.startSec) >= MIN_NOTE_SEC) {
            notes.push({
                midiPitch: open.midiPitch,
                startSec: open.startSec,
                durationSec: endSec - open.startSec,
                velocity: 90,
            });
        }
        open = null;
    };

    return {
        push(midi, timeSec) {
            if (midi === null) {
                if (open && (timeSec - open.lastSeen) > GAP_SEC) {
                    close(open.lastSeen);
                }
                return;
            }
            const rounded = Math.round(midi);
            if (open && open.midiPitch === rounded) {
                open.lastSeen = timeSec;
                return;
            }
            if (open) {
                close(timeSec);
            }
            open = { midiPitch: rounded, startSec: timeSec, lastSeen: timeSec };
        },
        /// All finished notes plus the currently open one, times rebased to start at zero.
        snapshot(nowSec) {
            const all = [...notes];
            if (open && (Math.max(nowSec, open.lastSeen) - open.startSec) >= MIN_NOTE_SEC) {
                all.push({
                    midiPitch: open.midiPitch,
                    startSec: open.startSec,
                    durationSec: Math.max(nowSec, open.lastSeen) - open.startSec,
                    velocity: 90,
                });
            }
            if (all.length === 0) {
                return [];
            }
            const base = all[0].startSec;
            return all.map(note => ({ ...note, startSec: note.startSec - base }));
        },
        reset() {
            notes.length = 0;
            open = null;
        },
    };
}
