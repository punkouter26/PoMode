# AGENTS.md

Rules for any coding agent working in this repository. Architecture and background live in
`CLAUDE.md` and `docs/`; this file is the short list of how to work.

## Rules

- **Work on `master` only.** Do not create or switch to another branch unless explicitly asked.
- **Restart the app after every code change and confirm it came back.** Kill any running
  `PoMode.API` (it locks the DLLs), `dotnet build`, `dotnet run --project src/PoMode.API`, then check
  `/health/ready` answers. A process that did not crash in the first second is not a successful restart.
- **Read `docs/` first.** The root `docs/` folder is the overall summary of the project, including
  decisions already made and features deliberately removed.
- **No `dotnet user-secrets`.** Local configuration goes in `appsettings*.json`; anything genuinely
  secret goes in Azure Key Vault (read through `SecretsBootstrap` / `DefaultAzureCredential`).
- **`git sync` means commit everything, then push.** Stage all outstanding changes, write a short,
  casual commit message in American English that reads like a person typed it ("fix the busted tempo
  math", not a changelog entry), and push. Never push otherwise unless asked.
- **Answers over 100 words end with a TLDR** of about 20 words.
- **Do not run every test after a change.** Run only the tests that cover what changed, with
  `--filter` (e.g. `dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~TempoEstimator"`), or
  none at all when the change is simple. Say what was not verified.
- **Do it rather than hand it over.** If a command or a web-UI step can be done from the tools
  available, do it; only ask the user to act when it needs their machine, credentials or decision.
- **Compiler warnings are errors.** `Directory.Build.props` sets `TreatWarningsAsErrors`; fix every
  warning a change introduces rather than suppressing it.
