// Web Audio acoustic synthesizer for the Mode Lab:
// - Acoustic Grand Piano for the chord progression (hammer percussive attack, detuned unison strings, soundboard filter decay)
// - Concert Flute for the melody (pure sine/triangle fundamental with breath air noise chiff)
// - Real-time mode morphing, loop scheduling, and 7-mode comparative tour playback.

let audioCtx = null;
let masterGain = null;
let isPlaying = false;
let isLooping = true;
let loopDuration = 0;
let playbackStartTime = 0;
let pauseOffset = 0;
let activeVoices = [];
let animFrameId = null;
let onPlayheadCallback = null;
let dotNetRef = null;

let playCount = 0;

let currentMelodyNotes = [];
let currentBackingNotes = [];
let lastScheduledTime = 0;

// Mirror player state onto the document, matching the mixer.js/canvas.js contract: Playwright
// asserts on these attributes instead of reaching into module internals. modalPlays counts actual
// play() calls, which is what distinguishes a genuine restart from a silent melody swap.
function publishState() {
    try {
        const el = document.body;
        if (!el) return;
        el.dataset.modalPlayer = isPlaying ? 'playing' : 'stopped';
        el.dataset.modalPlays = String(playCount);
        el.dataset.modalLoopSec = (loopDuration || 0).toFixed(3);
        el.dataset.modalBackingCount = String(currentBackingNotes.length);
    } catch { }
}

function clamp(val, min, max) {
    return Math.max(min, Math.min(max, val));
}

function ensureContext() {
    if (!audioCtx) {
        const Ctor = window.AudioContext ?? window.webkitAudioContext;
        audioCtx = new Ctor();
        masterGain = audioCtx.createGain();
        masterGain.gain.value = 1.0;
        masterGain.connect(audioCtx.destination);
    }
    if (audioCtx.state === 'suspended') {
        audioCtx.resume().catch(() => { });
    }
    return audioCtx;
}

function midiToFreq(midiPitch) {
    return 440 * Math.pow(2, (midiPitch - 69) / 12);
}

/// Concert Flute Synthesizer: simulates a wooden/silver concert flute
/// with pure sine fundamental, gentle overtone, and breath chiff noise.
function playFluteMelodyNote(ctx, note, startTime) {
    const freq = midiToFreq(note.midiPitch);
    const duration = Math.max(0.08, note.durationSec);
    const velocity = (note.velocity ?? 95) / 127;
    const level = 0.28 * velocity;

    // 1. Pure Sine Fundamental + Subtle Octave Triangle (Clean stable pitch, no vibrato)
    const osc1 = ctx.createOscillator();
    const osc2 = ctx.createOscillator();
    osc1.type = 'sine';
    osc2.type = 'triangle';
    osc1.frequency.setValueAtTime(freq, startTime);
    osc2.frequency.setValueAtTime(freq * 2, startTime);

    const osc2Gain = ctx.createGain();
    osc2Gain.gain.value = 0.15;
    osc2.connect(osc2Gain);

    // 2. Breath Air Noise (Chiff onset + gentle air stream)
    let breathSource = null;
    let breathGain = null;
    let breathFilter = null;
    try {
        const noiseSec = Math.min(duration + 0.05, 1.2);
        const noiseBuffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate * noiseSec), ctx.sampleRate);
        const data = noiseBuffer.getChannelData(0);
        let b0 = 0, b1 = 0, b2 = 0;
        for (let i = 0; i < data.length; i++) {
            const white = Math.random() * 2 - 1;
            b0 = 0.99 * b0 + white * 0.05;
            b1 = 0.95 * b1 + white * 0.08;
            b2 = 0.85 * b2 + white * 0.15;
            data[i] = (b0 + b1 + b2) * 0.35;
        }
        breathSource = ctx.createBufferSource();
        breathSource.buffer = noiseBuffer;

        breathFilter = ctx.createBiquadFilter();
        breathFilter.type = 'bandpass';
        breathFilter.frequency.value = Math.min(freq * 1.5, 4500);
        breathFilter.Q.value = 3.5;

        breathGain = ctx.createGain();
        // Chiff burst in first 35ms then settle to soft breath
        breathGain.gain.setValueAtTime(0.0001, startTime);
        breathGain.gain.exponentialRampToValueAtTime(level * 0.35, startTime + 0.02);
        breathGain.gain.exponentialRampToValueAtTime(level * 0.08, startTime + 0.06);
        breathGain.gain.setValueAtTime(level * 0.08, startTime + duration - 0.02);
        breathGain.gain.exponentialRampToValueAtTime(0.0001, startTime + duration + 0.04);

        breathSource.connect(breathFilter);
        breathFilter.connect(breathGain);
    } catch { }

    // 4. Main Flute Body Filter
    const bodyFilter = ctx.createBiquadFilter();
    bodyFilter.type = 'lowpass';
    bodyFilter.frequency.value = Math.min(freq * 4, 6000);
    bodyFilter.Q.value = 0.8;

    // 5. Flute Breath Envelope (Smooth vocal/woodwind onset)
    const env = ctx.createGain();
    const attack = 0.035; // Soft airy embouchure attack
    env.gain.setValueAtTime(0.0001, startTime);
    env.gain.exponentialRampToValueAtTime(level, startTime + attack);
    env.gain.exponentialRampToValueAtTime(level * 0.88, startTime + attack + 0.12);
    env.gain.setValueAtTime(level * 0.88, startTime + duration - 0.03);
    env.gain.exponentialRampToValueAtTime(0.0001, startTime + duration + 0.06);

    osc1.connect(bodyFilter);
    osc2Gain.connect(bodyFilter);
    bodyFilter.connect(env);
    if (breathGain) breathGain.connect(env);

    if (masterGain) {
        env.connect(masterGain);
    } else {
        env.connect(ctx.destination);
    }

    osc1.start(startTime);
    osc2.start(startTime);
    if (breathSource) breathSource.start(startTime);

    const stopTime = startTime + duration + 0.1;
    osc1.stop(stopTime);
    osc2.stop(stopTime);
    if (breathSource) breathSource.stop(stopTime);

    osc1.onended = () => {
        env.disconnect();
        bodyFilter.disconnect();
    };

    // The breath noise belongs here too, or pause/stop leaves it hissing after the tone is cut.
    activeVoices.push(osc1, osc2);
    if (breathSource) activeVoices.push(breathSource);
}

/// Acoustic Grand Piano Synthesizer: simulates hammer strike percussive transient,
/// multiple detuned unison strings, soundboard resonance, and natural piano decay.
function playPianoChordNote(ctx, note, startTime) {
    const freq = midiToFreq(note.midiPitch);
    const duration = Math.max(0.12, note.durationSec);
    const velocity = (note.velocity ?? 75) / 127;
    const level = 0.16 * velocity;

    // 1. Dual detuned string oscillators (Piano unison beating)
    const osc1 = ctx.createOscillator();
    const osc2 = ctx.createOscillator();
    const oscHammer = ctx.createOscillator();

    osc1.type = 'triangle';
    osc2.type = 'sawtooth';
    oscHammer.type = 'sine';

    osc1.frequency.setValueAtTime(freq, startTime);
    osc2.frequency.setValueAtTime(freq * 1.0015, startTime); // Subtle detune for string chorus
    oscHammer.frequency.setValueAtTime(freq * 2.01, startTime); // Felt hammer impact transient

    const oscMix = ctx.createGain();
    oscMix.gain.value = 0.7;

    const hammerGain = ctx.createGain();
    hammerGain.gain.setValueAtTime(level * 0.6, startTime);
    hammerGain.gain.exponentialRampToValueAtTime(0.0001, startTime + 0.035); // Fast hammer thud

    // 2. Piano Soundboard Filter Envelope (Bright felt attack decaying into warm wood)
    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    const initCutoff = Math.min(3200 + (velocity * 1800), 7500);
    filter.frequency.setValueAtTime(initCutoff, startTime);
    filter.frequency.exponentialRampToValueAtTime(650, startTime + Math.min(0.4, duration * 0.5));
    filter.Q.value = 1.0;

    // 3. Piano Amplitude Envelope (Immediate percussive strike + exponential string decay)
    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, startTime);
    env.gain.linearRampToValueAtTime(level, startTime + 0.004); // 4ms percussive attack
    env.gain.exponentialRampToValueAtTime(level * 0.65, startTime + 0.15); // Initial decay
    env.gain.exponentialRampToValueAtTime(level * 0.40, startTime + Math.min(duration * 0.6, 1.2)); // Ringing sustain
    env.gain.setValueAtTime(level * 0.40, startTime + duration - 0.04);
    env.gain.exponentialRampToValueAtTime(0.0001, startTime + duration + 0.12); // Damper release

    osc1.connect(oscMix);
    osc2.connect(oscMix);
    oscMix.connect(filter);
    oscHammer.connect(hammerGain);
    hammerGain.connect(filter);
    filter.connect(env);

    // Subtle stereo width across piano keyboard
    if (ctx.createStereoPanner) {
        const pan = ctx.createStereoPanner();
        pan.pan.value = clamp((note.midiPitch - 60) / 36, -0.6, 0.6);
        env.connect(pan);
        if (masterGain) {
            pan.connect(masterGain);
        } else {
            pan.connect(ctx.destination);
        }
    } else {
        if (masterGain) {
            env.connect(masterGain);
        } else {
            env.connect(ctx.destination);
        }
    }

    osc1.start(startTime);
    osc2.start(startTime);
    oscHammer.start(startTime);

    const stopTime = startTime + duration + 0.18;
    osc1.stop(stopTime);
    osc2.stop(stopTime);
    oscHammer.stop(startTime + 0.06);

    osc1.onended = () => {
        env.disconnect();
        filter.disconnect();
        oscMix.disconnect();
    };

    activeVoices.push(osc1, osc2, oscHammer);
}

function stopActiveVoices() {
    for (const v of activeVoices) {
        try {
            v.stop();
            v.disconnect();
        } catch { }
    }
    activeVoices = [];
}

function scheduleWindow(ctx, fromLoopSec, toLoopSec, audioBaseTime) {
    // Acoustic Piano chord accompaniment
    for (const n of currentBackingNotes) {
        if (n.startSec >= fromLoopSec && n.startSec < toLoopSec) {
            const when = audioBaseTime + (n.startSec - fromLoopSec);
            if (when >= ctx.currentTime - 0.02) {
                playPianoChordNote(ctx, n, when);
            }
        }
    }

    // Concert Flute melody lead
    for (const n of currentMelodyNotes) {
        if (n.startSec >= fromLoopSec && n.startSec < toLoopSec) {
            const when = audioBaseTime + (n.startSec - fromLoopSec);
            if (when >= ctx.currentTime - 0.02) {
                playFluteMelodyNote(ctx, n, when);
            }
        }
    }
}

function tick() {
    if (!isPlaying || !audioCtx) return;

    const ctx = audioCtx;
    const now = ctx.currentTime;
    const elapsed = (now - playbackStartTime);
    const loopPos = loopDuration > 0 ? (elapsed % loopDuration) : 0;

    if (!isLooping && elapsed >= loopDuration) {
        stop();
        // Tell Blazor the take ended. Without this its _isPlaying stays true forever, and every
        // later Sample click is swallowed by the "already playing" guard.
        if (dotNetRef) {
            try { dotNetRef.invokeMethodAsync('OnPlaybackEnded'); } catch { }
        }
        return;
    }

    if (onPlayheadCallback) {
        onPlayheadCallback(loopPos);
    }

    // Lookahead scheduling (0.22s)
    const lookahead = 0.22;
    const schedEnd = (now + lookahead - playbackStartTime) % loopDuration;

    if (schedEnd > lastScheduledTime) {
        scheduleWindow(ctx, lastScheduledTime, schedEnd, now + (lastScheduledTime - loopPos));
    } else if (schedEnd < lastScheduledTime) {
        // Wrapped around loop
        scheduleWindow(ctx, lastScheduledTime, loopDuration, now + (lastScheduledTime - loopPos));
        scheduleWindow(ctx, 0, schedEnd, now + (loopDuration - loopPos));
    }
    lastScheduledTime = schedEnd;

    animFrameId = requestAnimationFrame(tick);
}

export function play(melodyNotes, backingNotes, totalDuration, dotNetHelper) {
    const ctx = ensureContext();
    if (!ctx) return;

    if (ctx.state === 'suspended') {
        ctx.resume();
    }

    stopActiveVoices();
    currentMelodyNotes = melodyNotes || [];
    currentBackingNotes = backingNotes || [];
    loopDuration = totalDuration || 8.0;

    if (dotNetHelper) {
        dotNetRef = dotNetHelper;
        onPlayheadCallback = (pos) => {
            try {
                dotNetHelper.invokeMethodAsync('OnPlayheadUpdated', pos);
            } catch { }
        };
    }

    playbackStartTime = ctx.currentTime - pauseOffset;
    lastScheduledTime = pauseOffset % loopDuration;
    isPlaying = true;

    // Schedule initial immediate block
    const initialLook = Math.min(loopDuration, 0.4);
    scheduleWindow(ctx, lastScheduledTime, (lastScheduledTime + initialLook) % loopDuration, ctx.currentTime);
    lastScheduledTime = (lastScheduledTime + initialLook) % loopDuration;

    if (animFrameId) cancelAnimationFrame(animFrameId);
    animFrameId = requestAnimationFrame(tick);
    playCount++;
    publishState();
}

export function updateMelody(melodyNotes, backingNotes, totalDuration) {
    currentMelodyNotes = melodyNotes || [];
    // The piano chords and the loop length belong to the same arrangement as the lead. Swapping
    // only the melody leaves the previous mode's harmony playing underneath it.
    if (backingNotes) currentBackingNotes = backingNotes;
    if (totalDuration > 0) {
        loopDuration = totalDuration;
        lastScheduledTime = lastScheduledTime % loopDuration;
    }
    publishState();
}

export function pause() {
    if (!isPlaying || !audioCtx) return;
    isPlaying = false;
    pauseOffset = (audioCtx.currentTime - playbackStartTime) % (loopDuration || 1);
    stopActiveVoices();
    if (animFrameId) cancelAnimationFrame(animFrameId);
    publishState();
}

export function stop() {
    isPlaying = false;
    pauseOffset = 0;
    stopActiveVoices();
    if (animFrameId) cancelAnimationFrame(animFrameId);
    publishState();
    if (onPlayheadCallback) onPlayheadCallback(0);
}

export function seek(positionSec) {
    pauseOffset = Math.max(0, positionSec);
    if (isPlaying && audioCtx) {
        stopActiveVoices();
        playbackStartTime = audioCtx.currentTime - pauseOffset;
        lastScheduledTime = pauseOffset % loopDuration;
    } else if (onPlayheadCallback) {
        onPlayheadCallback(pauseOffset);
    }
}

export function setLooping(loop) {
    isLooping = loop;
}

export function dispose() {
    stop();
    onPlayheadCallback = null;
    dotNetRef = null;
}
