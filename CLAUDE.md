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
* **UI Controls & Styling:** Radzen Blazor library. Zero inline CSS—use scoped `.razor.css` and global CSS variables only. Auto-detect system Light/Dark themes.
* **Mock Indicator:** Display a persistent warning banner ("USING MOCK DATA") whenever an active state uses mock/local data.
* **Code Hygiene:** Continuously purge unused files, dead code, orphaned assets, and unused `using` directives across all commits.
