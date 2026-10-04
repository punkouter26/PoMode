# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```powershell
docker compose up -d                     # one shared Azurite (blob emulator) for all PoMode* repos
dotnet build                             # builds PoMode.slnx (kill any running PoMode.API first — it locks DLLs)
dotnet run --project src/PoMode.API      # serves API + Blazor client at http://localhost:5000 / https://localhost:5001
dotnet test tests/PoMode.Unit            # pure business logic, fast
dotnet test tests/PoMode.Integration     # needs Azurite running
dotnet test tests/PoMode.E2EAPI          # HTTP contract tests (in-process app)
dotnet test tests/PoMode.E2EUI           # Playwright browser tests (boots the real app)
dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~TempoEstimator"   # single test/class
```

- `tests/PoMode.Integration` includes `ModelAccuracyReportTests`: it renders a known-truth sample MP3, plus a voice-like stem with vibrato and bleed and two drum grooves with a known bar grid, races every free pitch/chord/beat executor against them, and rewrites `test-reports/model-accuracy.html`. It is a reporting tool, not a test, so it is opt-in: `POMODE_MODEL_REPORT=1 dotnet test tests/PoMode.Integration`. Otherwise it skips.
- `InterpreterReportTests` does the same for the written interpretation: every Ollama model installed on the machine writes the summary and answers four questions over the real prompts, scored on schema, invented figures (`GroundingCheck`) and declining the unanswerable one, into `test-reports/interpreter-report.html`, and it fails when `OllamaSongInterpreter.PreferredModel` is not the winner. Minutes per model on a CPU: `POMODE_LLM_REPORT=1 dotnet test tests/PoMode.Integration --filter InterpreterReport`.

- First E2EUI run: install browsers with `pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium`.
- API reference UI: `/scalar`. Health: `/health`, `/health/live`, `/health/ready`. Diagnostics: `/diag`.

## Working rules

These override any default instinct. Where one contradicts a habit, the rule wins.

### Orientation

- **Read `docs/` first.** The root `docs/` folder carries background that is not derivable from the
  code. Skim it before planning work, so a decision already made and written down is not
  re-litigated from scratch — and so a feature that was deliberately removed is not rebuilt.
- **Test suites are capped**: 100 Unit, 50 Integration, 25 E2EAPI, 25 E2EUI. Counted as the runner counts
  them: every `InlineData` row is a test. A new test that would
  breach a cap has to earn its place against an existing one.

- **An unused `using` fails the build.** `.editorconfig` raises IDE0005 to a warning and
  `Directory.Build.props` treats warnings as errors; delete the directive rather than suppress it.

### Verifying a change

- **Build, restart, then check the restart actually worked.** After a code change: `dotnet build`,
  then restart `PoMode.API` and confirm it came up — `/health/ready` answering is the check, not the
  absence of a crash in the first second. Kill any running instance before building; it locks the DLLs.
- **Run only the tests that cover what you changed**, with `--filter`. Never the whole suite, and
  never all four suites — a full run costs minutes on every edit and tells you almost nothing about a
  two-line change. `dotnet test tests/PoMode.Unit --filter "FullyQualifiedName~HumTakeSeeder"` is
  the shape. If nothing existing covers the change, say so rather than running everything to feel safe.
- **Say what you did not verify.** A targeted test run plus a successful restart is not a full pass;
  report it as what it is.

### Git

- **Work on `master`.** Do not create, switch to, or work on another branch unless explicitly asked.
- **Never push without being asked.** Committing locally is fine; `git push` happens only when the
  user says so, or types **`git sync`**.
- **`git sync` means: commit everything, then push.** Stage all outstanding changes first — a sync
  that leaves work uncommitted is not a sync — then commit and push in one go.
- **Commit messages are short and casual**, written the way a person actually types them. American
  English, plain and offhand ("fix the busted tempo math", "wire up practice page"), not a formal
  changelog entry. One line is usually right.

### Secrets

- **No `dotnet user-secrets`, ever.** Configuration goes in `appsettings*.json`; anything genuinely
  secret goes in Azure Key Vault and is read through `SecretsBootstrap` / `DefaultAzureCredential`.
  This matches NET_RULES below, which also forbids connection strings in `appsettings`.

### Working with the user

- **Run the command yourself.** If a step can be done from the tools available, do it rather than
  printing an instruction for the user to copy into a terminal. Hand them a command only when it
  genuinely needs their machine, their credentials, or their decision.
- **Answers over ~100 words end with a TLDR.** One line, about 20 words, saying what happened.

## Architecture

One process: `PoMode.API` hosts the Blazor WASM client (`PoMode.Client`), the REST endpoints, a SignalR
hub (`/hubs/analysis`) and the background analysis worker; `PoMode.Shared` holds the DTOs, the
source-generated `PoModeJsonContext` and the few pure lookups both sides need. The rest is in `docs/`:

- `docs/architecture.md` - every feature slice and why it is shaped the way it is.
- `docs/ui-rules.md` - colour tokens, the 12px floor, the 640px breakpoint, buttons, and "an effect
  animates a fact, never a mood".
- `docs/removed.md` - features that were deliberately removed. Do not rebuild one by accident.

# NET_RULES (New project: apply all / Existing project: verify & fix)

## 1. Core Principles & Architecture
* **Naming Standard:** Prefix solutions, projects, and root namespaces with `Po{Name}`.
* **Tech Stack:** .NET 10 / C# 15 with Centralized Package Management (`/Directory.Packages.props`).
* **Compiler Guards:** Enforce `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` globally in `Directory.Build.props`.
* **Solution Layout:**
  * `src/Po{Name}.API/`: Minimal API host using autonomous, decoupled Vertical Slice Architecture (`Features/{FeatureName}`).
  * `src/Po{Name}.Client/`: Blazor WASM UI hosted directly by `Po{Name}.API`.
  * `src/Po{Name}.Shared/`: DTOs, Enums, Interfaces, JSON contexts. Zero business logic or data access.
  * `tests/`: `Po{Name}.Unit` (business logic), `Po{Name}.Integration` (Testcontainers/Azurite), `Po{Name}.E2EAPI` (HTTP contract tests), `Po{Name}.E2EUI` (Playwright tests).

## 2. API, Security & Infrastructure
* **Endpoints:** Map via `IEndpointRouteBuilder` + `MapGroup()`. Auto-document with `Microsoft.AspNetCore.OpenApi` and serve via Scalar UI.
* **Dev/Test Auth:** Use `FakeAuthHandler` reading `X-Fake-User` and `X-Fake-Roles` headers. MUST throw `InvalidOperationException` in Production.
* **Secrets & Identity:** Resource Group `PoShared` (or `Po{Name}`). Authenticate exclusively via System-Assigned Managed Identity / `DefaultAzureCredential` + Azure Key Vault (Local & Azure). Connection strings, `appsettings` secrets, and `dotnet-secrets` are strictly forbidden. Get keys from key vault in dev env and prod env.
* **Health & Diagnostics:**
  * `/health`: Native .NET health status for external dependencies.
  * `/diag`: Real-time operational summary. Must strictly redact all secrets, tokens, and connection strings.

## 3. UI/UX & Blazor WASM
* **Layout Structure:** Header format: `[Left: Branding | Center: Contextual Actions | Right: Session / Logout]`.
* **UI Controls & Styling:** Radzen Blazor library (prefer advanced Radzen controls when possible). Zero inline CSS—use scoped `.razor.css` and global CSS variables only. Auto-detect system Light/Dark themes.
* **Mock Indicator:** Display a persistent warning banner ("USING MOCK DATA") whenever an active state uses mock/local data.
* **Code Hygiene:** Continuously purge unused files, dead code, orphaned assets, and unused `using` directives across all commits.
* **Ports:** HTTP 5000, HTTPS 5001.
* **Home page title:** exactly `Po{Name}` (this app: `PoMode`).
