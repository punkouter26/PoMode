# What the AI costs

Everything AI in PoMode is free and local except one optional piece. This is the audit for that piece,
written against the code that is in the repo (`AzureOpenAiSongInterpreter`, `infra/openai.bicep`).

## The free tiers

- **Ollama** on the user's own machine writes the summaries and answers (`OllamaSongInterpreter`), and
  is the default wherever it is installed. The interpreter report
  (`POMODE_LLM_REPORT=1 dotnet test tests/PoMode.Integration --filter InterpreterReport`) decides which
  installed model is preferred.
- **In the browser**: Chrome's built-in model (Prompt API) and WebLLM (Qwen2.5 1.5B on WebGPU, about
  1 GB downloaded once). Both cost the visitor's own GPU and nothing else.
- **Whisper** (lyrics), **HTDemucs / MDX-Net** (separation), **RMVPE / Basic Pitch** (melody),
  **ChordMini** (chords), **Beat This!** (beats): local ONNX or ggml models on the server's CPU.
- **The template writer**: always there, no model at all.

## The paid tier: Azure OpenAI

The hosted app has no Ollama, so without this its written interpretation is the template. Switching
the cloud writer on gives it a small model instead.

- **Off by default.** `infra/main.bicep`'s `enableCloudInterpreter` is false; set it true (and run the
  role assignments once with an identity that may write them, as for the storage role) to deploy one
  `S0` Azure OpenAI account with one `gpt-4.1-nano` GlobalStandard deployment (20K tokens per minute)
  and the app setting pair that turns the interpreter on.
- **No key exists.** The account has local auth disabled; the app reaches it with its managed
  identity (Cognitive Services OpenAI User).
- **Rank.** After a local model, before the template (`SongInterpreterSelector.Rank`): a server with
  Ollama never pays.
- **Per summary.** The prompt is the system message plus the statistics block, about 1,500 input
  tokens; a summary is at most 1,536 output tokens and typically 500-700. At gpt-4.1-nano's list price
  (USD 0.10 per million input tokens, 0.40 per million output) that is about USD 0.0004 for a summary,
  and a follow-up question about the same.
- **Bounded.** Each song's summary is cached per interpreter (`interpretation-{name}.json`, keyed on the
  prompt's hash), so reopening a song costs nothing; `JobAftercare` writes it once per finished job.
  Questions are rate limited by `po-interpret`. A thousand analysed songs with three questions each is
  about USD 1.60.
- **Grounded.** Its replies pass the same `GroundingCheck` as every other writer; a reply that invents
  a figure is sent back once, then falls through to the template, and `/diag` counts both.

Prices are list prices at the time of writing; check the Azure pricing page for the region before
switching it on.
