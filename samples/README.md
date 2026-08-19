# Demo songs

Five short songs for trying the app out. Drop any of them on the home page.

They are **generated**, not recorded — see [`scripts/make-demo-songs.py`](../scripts/make-demo-songs.py).
That settles the licensing question outright (nothing here belongs to anyone else), keeps the repo
small, and lets each song be written to exercise a different corner of the analysis. Re-run the
script to change them:

```powershell
python scripts/make-demo-songs.py
```

Each is 8 bars of a synthesised vocal line over a quiet chord pad. There is no bass and the pad is
deliberately faint: these clips are short enough that the pipeline skips stem separation, so the
pitch tracker reads the full mix, and at ordinary backing levels it transcribes the accompaniment as
melody.

## What each one is, and what the app reports

| File | Written as | App reports | What it exercises |
|---|---|---|---|
| `01-sunrise-c-major` | C major, 96 BPM, stepwise | **C**, 96 BPM, 82% steps, 94% in key | A calm, easy-to-sing line |
| `02-blue-room-a-minor` | A minor, 76 BPM, one phrase per bar | **A**, 76 BPM, **4 phrases** | Phrase and breath detection |
| `03-dorian-walk-d-dorian` | D Dorian, 112 BPM, sings the natural 6th | **D**, 112 BPM, 78% steps, 97% in key | The scale-degree chart — D E F G A B C |
| `04-skip-along-g-major` | G major, 132 BPM, continuous quavers | **G MajorPentatonic**, 132 BPM, **45% syncopated**, 68% leaps | Syncopation and wide leaps |
| `05-quiet-hymn-e-pentatonic` | E minor pentatonic, 68 BPM, long notes | **E MinorPentatonic**, 68 BPM, **6 phrases** | Five tall bars and seven empty ones in the scale chart |

Every tonic and every tempo comes back exactly as written.

## Why three of them say "mode unclear"

The modal engine treats one detected chord span as one window, and a window needs **three distinct
sung pitch classes** before it will name a mode. A chord span here is roughly 0.6 s, so a melody in
quarter notes puts a single note in each window — not enough evidence, and the engine says so rather
than guessing.

The two songs that do get a named mode are the ones with enough notes per window: `04` runs
continuous quavers, and `05` holds long notes across longer spans.

This is worth having in the demo set rather than tuning away. "Mode unclear" is a real answer the app
gives on real material, and `01` and `03` are the songs to look at to see how it presents it — the
scale-degree chart still shows the full scale being sung, which is exactly the evidence a reader
needs to judge the call themselves.
