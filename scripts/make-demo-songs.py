#!/usr/bin/env python3
"""Generates the five demo songs in samples/.

Why generated rather than downloaded: the files are committed, so they have to be unambiguously
free to redistribute. Synthesising them settles that question outright, keeps them small, and lets
each one be written to exercise a different corner of the analysis pipeline — a different mode,
tempo, phrase length and rhythmic feel. Nothing here depends on a third-party host staying up.

The output is deterministic: same script, same bytes. Re-run only to change the songs.

    python scripts/make-demo-songs.py          # writes samples/*.wav, then MP3 if ffmpeg is present

Requires only the Python standard library. ffmpeg is optional; without it the WAVs are kept.
"""

import math
import os
import struct
import subprocess
import shutil
import sys

SAMPLE_RATE = 22050
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "samples")

# ---------------------------------------------------------------------------------------------
# Synthesis
# ---------------------------------------------------------------------------------------------

# A sung vowel is a fundamental plus a falling series of harmonics. Five is enough to read as a
# voice rather than a test tone, and few enough that pure-Python rendering stays quick.
VOICE_HARMONICS = [1.0, 0.50, 0.28, 0.14, 0.07]

# Real singers do not hold a dead-straight pitch. A slow, shallow vibrato makes the result sound
# sung — and, more usefully here, gives the pitch tracker the kind of signal it sees in practice.
VIBRATO_HZ = 5.2
VIBRATO_DEPTH = 0.004  # ±0.4% of the frequency


def midi_to_hz(midi):
    return 440.0 * (2.0 ** ((midi - 69) / 12.0))


def envelope(i, count, attack, release):
    """Linear attack/release over a flat sustain. Silent edges stop clicks at note boundaries."""
    if i < attack:
        return i / attack
    if i >= count - release:
        return max(0.0, (count - i) / release)
    return 1.0


def add_voice(buffer, midi, start_sec, duration_sec, amplitude):
    """A sung note: harmonics, vibrato, and a soft edge at each end."""
    start = int(start_sec * SAMPLE_RATE)
    count = int(duration_sec * SAMPLE_RATE)
    if count <= 0:
        return

    freq = midi_to_hz(midi)
    attack = max(1, int(0.025 * SAMPLE_RATE))
    release = max(1, int(0.070 * SAMPLE_RATE))
    phase = 0.0

    for i in range(count):
        index = start + i
        if index >= len(buffer):
            break
        t = i / SAMPLE_RATE
        # Frequency modulation has to accumulate in phase, not be applied per-sample to t, or the
        # vibrato turns into a buzz.
        bend = 1.0 + (VIBRATO_DEPTH * math.sin(2 * math.pi * VIBRATO_HZ * t))
        phase += 2 * math.pi * freq * bend / SAMPLE_RATE
        value = 0.0
        for harmonic, level in enumerate(VOICE_HARMONICS, start=1):
            value += level * math.sin(phase * harmonic)
        buffer[index] += value * amplitude * envelope(i, count, attack, release)


def add_pad(buffer, midi, start_sec, duration_sec, amplitude):
    """A held chord tone: fundamental plus an octave, slow in and out so it sits behind the voice."""
    start = int(start_sec * SAMPLE_RATE)
    count = int(duration_sec * SAMPLE_RATE)
    if count <= 0:
        return

    freq = midi_to_hz(midi)
    attack = max(1, int(0.12 * SAMPLE_RATE))
    release = max(1, int(0.25 * SAMPLE_RATE))

    for i in range(count):
        index = start + i
        if index >= len(buffer):
            break
        t = i / SAMPLE_RATE
        value = math.sin(2 * math.pi * freq * t) + (0.30 * math.sin(2 * math.pi * freq * 2 * t))
        buffer[index] += value * amplitude * envelope(i, count, attack, release)


def add_bass(buffer, midi, start_sec, duration_sec, amplitude):
    """A plucked root: near-sine with a decay, so it anchors the harmony without muddying it."""
    start = int(start_sec * SAMPLE_RATE)
    count = int(duration_sec * SAMPLE_RATE)
    if count <= 0:
        return

    freq = midi_to_hz(midi)
    for i in range(count):
        index = start + i
        if index >= len(buffer):
            break
        t = i / SAMPLE_RATE
        decay = math.exp(-2.2 * t)
        value = math.sin(2 * math.pi * freq * t) + (0.18 * math.sin(2 * math.pi * freq * 2 * t))
        buffer[index] += value * amplitude * decay


def write_wav(path, buffer):
    """Normalises to a fixed headroom so every demo lands at a comparable level."""
    peak = max((abs(sample) for sample in buffer), default=1.0) or 1.0
    scale = 0.89 / peak
    pcm = b"".join(struct.pack("<h", int(max(-1.0, min(1.0, sample * scale)) * 32767))
                   for sample in buffer)

    with open(path, "wb") as handle:
        handle.write(b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVE")
        handle.write(b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, SAMPLE_RATE,
                                           SAMPLE_RATE * 2, 2, 16))
        handle.write(b"data" + struct.pack("<I", len(pcm)))
        handle.write(pcm)


# ---------------------------------------------------------------------------------------------
# Songs
# ---------------------------------------------------------------------------------------------

TRIADS = {
    "maj": [0, 4, 7],
    "min": [0, 3, 7],
}

# The gap between these two is the whole trick, and it is narrower than it looks. Too loud a pad
# and Basic Pitch transcribes the accompaniment as melody. Too quiet and the chroma is all melody:
# at 0.11 the chord recogniser emitted one chord per quarter note, tracking the tune instead of the
# harmony, which left every modal window holding a single pitch class and the whole song reported
# as "mode unclear". The pad also sits an octave below the melody so the two do not compete.
# These levels were tuned by running the real pipeline over the output and reading the windows.
VOICE_LEVEL = 0.80
PAD_LEVEL = 0.11


def render(song):
    """Turns one song description into a mono float buffer."""
    beat = 60.0 / song["bpm"]
    total_beats = song["bars"] * 4
    length = int(((total_beats * beat) + 1.5) * SAMPLE_RATE)
    buffer = [0.0] * length

    # Harmony, kept deliberately faint.
    #
    # These clips are short, so the pipeline skips stem separation (the fast path) and the pitch
    # tracker reads the full mix. At ordinary backing levels it transcribes the accompaniment as
    # melody: the first cut of these songs had a bass line, and Basic Pitch duly reported MIDI 36,
    # 41, 43 and 45 as vocal notes, which turned a stepwise tune into "67% leaps". So there is no
    # bass at all, and the pad sits far enough down that it colours the chroma for chord detection
    # without ever crossing the pitch tracker's threshold.
    for bar, (root, quality) in enumerate(song["chords"] * (song["bars"] // len(song["chords"]))):
        start = bar * 4 * beat
        for interval in TRIADS[quality]:
            add_pad(buffer, root + interval, start, (4 * beat) - 0.05, PAD_LEVEL)

    # Melody: the part every statistic in the app is measured from, and by far the loudest thing
    # in the mix so that it is what gets transcribed.
    for midi, start_beat, length_beats in song["melody"]:
        add_voice(buffer, midi, start_beat * beat, length_beats * beat * 0.92, VOICE_LEVEL)

    return buffer


def scale_line(root, degrees, pattern):
    """(degree index, start beat, length) -> absolute MIDI notes over a scale."""
    return [(root + degrees[step % len(degrees)] + (12 * (step // len(degrees))), start, length)
            for step, start, length in pattern]


# Each song targets a different part of the analysis: a plain major key, a slow minor ballad with
# real rests between phrases, a Dorian tune that actually sings its natural 6th, a syncopated one
# that lands off the beat, and a sparse pentatonic one with long notes and long gaps.

C4, D4, E4, F4, G4, A4, B4, C5 = 60, 62, 64, 65, 67, 69, 71, 72
A3, B3 = 57, 59
D5, E5 = 74, 76

SONGS = [
    {
        "file": "01-sunrise-c-major",
        "title": "Sunrise (C major, 96 BPM)",
        "bpm": 96,
        "bars": 8,
        "chords": [(48, "maj"), (55, "maj"), (57, "min"), (48, "maj")],  # C G Am C
        "melody": [
            # Scale motion, not arpeggios. A four-note run covers four pitch classes, which clears
            # the modal engine's evidence bar while keeping the line genuinely stepwise — the first
            # cut used arpeggios for the same coverage and reported 74% leaps on a "gentle" tune.
            (C4, 0, 1), (D4, 1, 1), (E4, 2, 1), (F4, 3, 1),
            (G4, 4, 1), (F4, 5, 1), (E4, 6, 1), (D4, 7, 1),
            (E4, 8, 1), (F4, 9, 1), (G4, 10, 1), (A4, 11, 1),
            (G4, 12, 1), (F4, 13, 1), (E4, 14, 1), (D4, 15, 1),
            (E4, 16, 1), (F4, 17, 1), (G4, 18, 1), (A4, 19, 1),
            (B4, 20, 1), (A4, 21, 1), (G4, 22, 1), (F4, 23, 1),
            (E4, 24, 1), (F4, 25, 1), (G4, 26, 1), (E4, 27, 1),
            (D4, 28, 1), (E4, 29, 1), (D4, 30, 1), (C4, 31, 1),
        ],
    },
    {
        "file": "02-blue-room-a-minor",
        "title": "Blue Room (A minor, 76 BPM)",
        "bpm": 76,
        "bars": 8,
        "chords": [(57, "min"), (53, "maj"), (48, "maj"), (57, "min")],  # Am F C Am
        "melody": [
            # A beat is 0.79 s here, so the one-beat rest at the end of every bar clears the half
            # second the phrase detector splits on. Eight phrases, one per bar.
            (A4, 0, 1), (B4, 1, 1), (C5, 2, 1),
            (B4, 4, 1), (A4, 5, 1), (G4, 6, 1),
            (E4, 8, 1), (F4, 9, 1), (G4, 10, 1),
            (A4, 12, 1), (G4, 13, 1), (E4, 14, 1),
            (C5, 16, 1), (B4, 17, 1), (A4, 18, 1),
            (G4, 20, 1), (F4, 21, 1), (E4, 22, 1),
            (D4, 24, 1), (E4, 25, 1), (F4, 26, 1),
            (G4, 28, 1), (B4, 29, 1), (A4, 30, 2),
        ],
    },
    {
        "file": "03-dorian-walk-d-dorian",
        "title": "Dorian Walk (D Dorian, 112 BPM)",
        "bpm": 112,
        "bars": 8,
        # Three bars of Dm to one of G. D Dorian and A Aeolian contain the same seven notes, so the
        # only thing that separates them is which note the music treats as home: an earlier cut split
        # the bars evenly and the tonic detector duly called it A Aeolian. The G bar still supplies
        # the B natural that makes this Dorian rather than D Aeolian.
        "chords": [(50, "min"), (55, "maj"), (50, "min"), (50, "min")],
        "melody": [
            (D4, 0, 1), (E4, 1, 1), (F4, 2, 1), (G4, 3, 1),
            (A4, 4, 1), (B4, 5, 1), (A4, 6, 1), (G4, 7, 1),
            (F4, 8, 1), (E4, 9, 1), (D4, 10, 1), (F4, 11, 1),
            (A4, 12, 1), (G4, 13, 1), (F4, 14, 1), (D4, 15, 1),
            (D4, 16, 1), (E4, 17, 1), (F4, 18, 1), (G4, 19, 1),
            (A4, 20, 1), (B4, 21, 1), (C5, 22, 1), (B4, 23, 1),
            (A4, 24, 1), (G4, 25, 1), (F4, 26, 1), (E4, 27, 1),
            (F4, 28, 1), (E4, 29, 1), (D4, 30, 2),
        ],
    },
    {
        "file": "04-skip-along-g-major",
        "title": "Skip Along (G major, 132 BPM)",
        "bpm": 132,
        "bars": 8,
        "chords": [(55, "maj"), (50, "maj"), (52, "min"), (55, "maj")],  # G D Em G
        "melody": [
            # Continuous quavers: every beat carries an onset so the tempo estimator cannot halve
            # the pulse (an earlier cut with gaps on beats 2 and 4 was read as 66 BPM), and the
            # quavers between them are what put half the onsets on the off-beat.
            (G4, 0, 0.5), (B4, 0.5, 0.5), (A4, 1, 0.5), (D5, 1.5, 0.5),
            (B4, 2, 0.5), (G4, 2.5, 0.5), (A4, 3, 0.5), (F4 + 1, 3.5, 0.5),
            (D4, 4, 0.5), (F4 + 1, 4.5, 0.5), (A4, 5, 0.5), (D5, 5.5, 0.5),
            (A4, 6, 0.5), (F4 + 1, 6.5, 0.5), (E4, 7, 0.5), (D4, 7.5, 0.5),
            (E4, 8, 0.5), (G4, 8.5, 0.5), (B4, 9, 0.5), (E5, 9.5, 0.5),
            (B4, 10, 0.5), (G4, 10.5, 0.5), (A4, 11, 0.5), (B4, 11.5, 0.5),
            (G4, 12, 0.5), (A4, 12.5, 0.5), (B4, 13, 0.5), (D5, 13.5, 0.5),
            (B4, 14, 0.5), (A4, 14.5, 0.5), (G4, 15, 1),
            (G4, 16, 0.5), (B4, 16.5, 0.5), (A4, 17, 0.5), (D5, 17.5, 0.5),
            (B4, 18, 0.5), (G4, 18.5, 0.5), (A4, 19, 0.5), (F4 + 1, 19.5, 0.5),
            (D4, 20, 0.5), (F4 + 1, 20.5, 0.5), (A4, 21, 0.5), (D5, 21.5, 0.5),
            (A4, 22, 0.5), (F4 + 1, 22.5, 0.5), (E4, 23, 0.5), (D4, 23.5, 0.5),
            (E4, 24, 0.5), (G4, 24.5, 0.5), (B4, 25, 0.5), (E5, 25.5, 0.5),
            (B4, 26, 0.5), (G4, 26.5, 0.5), (A4, 27, 0.5), (B4, 27.5, 0.5),
            (G4, 28, 0.5), (A4, 28.5, 0.5), (B4, 29, 0.5), (D5, 29.5, 0.5),
            (B4, 30, 0.5), (A4, 30.5, 0.5), (G4, 31, 1),
        ],
    },
    {
        "file": "05-quiet-hymn-e-pentatonic",
        "title": "Quiet Hymn (E minor pentatonic, 68 BPM)",
        "bpm": 68,
        "bars": 8,
        "chords": [(52, "min"), (52, "min"), (50, "maj"), (52, "min")],  # Em Em D Em
        "melody": [
            # Only the five pentatonic degrees (E G A B D), three per bar so each chord span still
            # has evidence, and a full beat of rest between every phrase.
            (E4, 0, 1), (G4, 1, 1), (B4, 2, 1),
            (A4, 4, 1), (G4, 5, 1), (E4, 6, 1),
            (D5, 8, 1), (B4, 9, 1), (A4, 10, 1),
            (B4, 12, 1), (A4, 13, 1), (G4, 14, 1),
            (E4, 16, 1), (G4, 17, 1), (A4, 18, 1),
            (B4, 20, 1), (D5, 21, 1), (A4, 22, 1),
            (A4, 24, 1), (G4, 25, 1), (E4, 26, 1),
            (G4, 28, 1), (A4, 29, 1), (E4, 30, 2),
        ],
    },
]


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        print("ffmpeg not found — keeping WAV output only.", file=sys.stderr)

    for song in SONGS:
        wav_path = os.path.join(OUT_DIR, song["file"] + ".wav")
        print(f"rendering {song['title']} ...", flush=True)
        write_wav(wav_path, render(song))

        if ffmpeg:
            mp3_path = os.path.join(OUT_DIR, song["file"] + ".mp3")
            subprocess.run(
                [ffmpeg, "-y", "-loglevel", "error", "-i", wav_path,
                 "-codec:a", "libmp3lame", "-b:a", "128k", mp3_path],
                check=True)
            os.remove(wav_path)
            size = os.path.getsize(mp3_path) / 1024
            print(f"  -> {os.path.basename(mp3_path)}  {size:.0f} KB")
        else:
            size = os.path.getsize(wav_path) / 1024
            print(f"  -> {os.path.basename(wav_path)}  {size:.0f} KB")

    print(f"\nDone. Files are in {OUT_DIR}")


if __name__ == "__main__":
    main()
