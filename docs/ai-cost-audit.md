# AI cost audit — cloud tiers and model choices

Written 2026-08-17. Prices move; treat every number here as an order-of-magnitude estimate and
verify against the provider's current pricing page before committing spend.

## What the app actually pays for

Only one stage ever spends money: **stem separation** on the Cloud tier, and only when the Local
tier (HTDemucs ONNX) is unavailable or fails. Pitch tracking, chord recognition, modal analysis,
and the copilot are free (local ONNX / pure DSP / local Ollama). The planner already prefers
free tiers and treats Cloud as the last resort — that ordering is the single biggest cost control
and should not change.

## Stem separation, per ~4-minute track

| Provider | Rough cost | Notes |
| --- | --- | --- |
| Local HTDemucs (current default) | $0 | ~3-10 min on CPU; needs ~6 GB RAM headroom. |
| Replicate (demucs-family model) | ~$0.03-0.10 | Billed per GPU-second (T4/A40 class); cold starts add latency, not much cost. |
| LALAL.AI | ~$0.20-0.60 | Sold as minute packages (~$0.05-0.15/min of input audio depending on bundle size). |
| Azure (self-hosted GPU: ACI/AKS, T4) | ~$0.01-0.03 marginal | ~$0.50/hr for the VM — only cheaper than Replicate at sustained volume; idle time is pure waste. |
| GCP (self-hosted GPU, T4 spot) | ~$0.01 marginal | Same caveat as Azure; spot preemption needs the retry the pipeline already has. |

**Recommendation:** keep the current order (Local → fake → Replicate → LALAL). Replicate is the
cheapest capable metered option; the self-hosted cloud GPU only wins if this app ever processes
hundreds of tracks per day, which contradicts its single-user design.

## Copilot (explanations)

| Runtime | Cost | Latency for 2 sentences |
| --- | --- | --- |
| Ollama llama3.2:3b / qwen2.5:7b (spec-preferred) | $0 | ~1-4 s warm |
| Ollama gemma4:26b (currently installed) | $0 | ~10-60 s, minutes cold |
| Cloud small models (GPT-4o-mini / Gemini Flash class) | ~$0.0001-0.001 per answer | ~1 s |

**Recommendation:** stay local — the loopback-only policy is a design rule, not an accident.
Installing one of the spec-preferred small models (`ollama pull llama3.2:3b`) is the cheapest
possible speedup: ~10x faster than the 26B model with no quality loss at this answer length.
The server already prefers those models automatically when installed.

## Config presets

- Fastest copilot: `ollama pull llama3.2:3b` (no config change needed — preference order picks it up).
- No spend ever: leave all cloud credentials unset; the planner then never reaches a paid tier.
