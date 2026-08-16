# PoMode Phase 1: Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A running PoMode host: Minimal API serving a Blazor WASM shell (Radzen header layout, mock-data banner, auto light/dark), with FakeAuth, Key Vault→env secrets fallback, `/health`, `/diag` (secret-redacting), Scalar docs, and all four test projects green.

**Architecture:** Vertical Slice Minimal API (`PoMode.API`) hosts the WASM client (`PoMode.Client`); contracts live in `PoMode.Shared` with a System.Text.Json source-gen context. Secrets resolve Key Vault-first with environment-variable fallback recorded for `/diag`. FakeAuthHandler is the only auth scheme and hard-throws in Production.

**Tech Stack:** .NET 10 / C# 15, ASP.NET Core Minimal APIs, Blazor WebAssembly, Radzen.Blazor, Azure.Identity + Key Vault config provider, Scalar.AspNetCore, xUnit, Microsoft.AspNetCore.Mvc.Testing, Microsoft.Playwright.

**Spec:** `docs/superpowers/specs/2026-08-16-pomode-design.md` (this plan implements §3 layout, §9 API surface/cross-cutting, and the Phase-1 slice of §7 and §10).

## Global Constraints

- TargetFramework `net10.0` everywhere; `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` come ONLY from `Directory.Build.props` — never per-project.
- Central Package Management: versions live ONLY in `Directory.Packages.props`; `PackageReference` items never carry `Version`. Add packages with `dotnet add <proj> package <name>` (the .NET 10 SDK writes the version into `Directory.Packages.props` automatically).
- Naming prefix `PoMode.` for every project and root namespace.
- Secrets NEVER in `appsettings*.json` or code. Only Key Vault or environment variables. `/diag` must never emit a secret value — booleans only.
- Endpoints: `IEndpointRouteBuilder` + `MapGroup()` + `TypedResults`. No controllers.
- UI: Radzen components; zero inline CSS/`style=` attributes — scoped `.razor.css` + global CSS variables in `wwwroot/css/app.css` only.
- Every commit message follows `feat:`/`test:`/`chore:` conventional style and ends with the co-author line from CLAUDE.md guidance.
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: Solution Scaffold & Compiler Guards

**Files:**
- Create: `.gitignore`, `Directory.Build.props`, `Directory.Packages.props`, `PoMode.sln`
- Create: `src/PoMode.API/PoMode.API.csproj`, `src/PoMode.Client/PoMode.Client.csproj`, `src/PoMode.Shared/PoMode.Shared.csproj`
- Create: `tests/PoMode.Unit/PoMode.Unit.csproj`, `tests/PoMode.Integration/PoMode.Integration.csproj`, `tests/PoMode.E2EAPI/PoMode.E2EAPI.csproj`, `tests/PoMode.E2EUI/PoMode.E2EUI.csproj`
- Create: minimal `Program.cs` for API and Client, `src/PoMode.Shared/Placeholder.cs` (deleted in Task 2), `tests/PoMode.Unit/SmokeTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: buildable solution; every later task adds to these projects. Project refs: API → Client, Shared; Client → Shared; Unit → Shared; Integration → API; E2EAPI → API, Shared; E2EUI → none (drives a real process).

- [ ] **Step 1: Write `.gitignore`**

```gitignore
bin/
obj/
*.user
.vs/
models/
jobs/
TestResults/
```

- [ ] **Step 2: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Write `Directory.Packages.props`** (versions get appended by `dotnet add package` in later steps)

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write the seven `.csproj` files**

`src/PoMode.Shared/PoMode.Shared.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
</Project>
```

`src/PoMode.Client/PoMode.Client.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <ItemGroup>
    <ProjectReference Include="..\PoMode.Shared\PoMode.Shared.csproj" />
  </ItemGroup>
</Project>
```

`src/PoMode.API/PoMode.API.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <ProjectReference Include="..\PoMode.Client\PoMode.Client.csproj" />
    <ProjectReference Include="..\PoMode.Shared\PoMode.Shared.csproj" />
  </ItemGroup>
</Project>
```

`tests/PoMode.Unit/PoMode.Unit.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PoMode.Shared\PoMode.Shared.csproj" />
  </ItemGroup>
</Project>
```

`tests/PoMode.Integration/PoMode.Integration.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PoMode.API\PoMode.API.csproj" />
  </ItemGroup>
</Project>
```

`tests/PoMode.E2EAPI/PoMode.E2EAPI.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PoMode.API\PoMode.API.csproj" />
    <ProjectReference Include="..\..\src\PoMode.Shared\PoMode.Shared.csproj" />
  </ItemGroup>
</Project>
```

`tests/PoMode.E2EUI/PoMode.E2EUI.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Write minimal program files so everything compiles**

`src/PoMode.Shared/Placeholder.cs` (Task 2 replaces this):
```csharp
namespace PoMode.Shared;

/// <summary>Temporary compile anchor; removed when real contracts arrive.</summary>
public static class Placeholder;
```

`src/PoMode.API/Program.cs`:
```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => TypedResults.Ok("PoMode API"));
app.Run();

public partial class Program;
```

`src/PoMode.Client/Program.cs`:
```csharp
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
await builder.Build().RunAsync();
```

`src/PoMode.Client/wwwroot/index.html`:
```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>PoMode</title>
    <base href="/" />
</head>
<body>
    <div id="app">Loading…</div>
    <script src="_framework/blazor.webassembly.js"></script>
</body>
</html>
```

- [ ] **Step 6: Create the solution and add projects**

```powershell
dotnet new sln -n PoMode
dotnet sln add src/PoMode.API src/PoMode.Client src/PoMode.Shared tests/PoMode.Unit tests/PoMode.Integration tests/PoMode.E2EAPI tests/PoMode.E2EUI
```

- [ ] **Step 7: Add test packages to all four test projects**

```powershell
dotnet add tests/PoMode.Unit package xunit
dotnet add tests/PoMode.Unit package xunit.runner.visualstudio
dotnet add tests/PoMode.Unit package Microsoft.NET.Test.Sdk
dotnet add tests/PoMode.Integration package xunit
dotnet add tests/PoMode.Integration package xunit.runner.visualstudio
dotnet add tests/PoMode.Integration package Microsoft.NET.Test.Sdk
dotnet add tests/PoMode.E2EAPI package xunit
dotnet add tests/PoMode.E2EAPI package xunit.runner.visualstudio
dotnet add tests/PoMode.E2EAPI package Microsoft.NET.Test.Sdk
dotnet add tests/PoMode.E2EAPI package Microsoft.AspNetCore.Mvc.Testing
dotnet add tests/PoMode.E2EUI package xunit
dotnet add tests/PoMode.E2EUI package xunit.runner.visualstudio
dotnet add tests/PoMode.E2EUI package Microsoft.NET.Test.Sdk
```

Verify: open `Directory.Packages.props` — all versions landed there; no `Version=` attribute exists in any `.csproj`.

- [ ] **Step 8: Write the smoke test** — `tests/PoMode.Unit/SmokeTests.cs`:

```csharp
namespace PoMode.Unit;

public class SmokeTests
{
    [Fact]
    public void Solution_compiles_and_tests_run() => Assert.True(true);
}
```

- [ ] **Step 9: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds with **zero warnings** (warnings are errors); 1 test passes.

- [ ] **Step 10: Commit**

```powershell
git add -A
git commit -m "chore: scaffold PoMode solution with compiler guards and CPM"
```

---

### Task 2: Shared Contracts & JSON Source-Gen Context

**Files:**
- Delete: `src/PoMode.Shared/Placeholder.cs`
- Create: `src/PoMode.Shared/Diagnostics/DiagnosticsReport.cs`, `src/PoMode.Shared/Session/SessionInfo.cs`, `src/PoMode.Shared/Serialization/PoModeJsonContext.cs`
- Test: `tests/PoMode.Unit/Serialization/JsonContextTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (used by Tasks 4–6):
  - `PoMode.Shared.Diagnostics.DiagnosticsReport(string EnvironmentName, bool IsAzureHosted, string SecretSource, bool SecretFellBack, IReadOnlyList<ProviderKeyStatus> ProviderKeys)`
  - `PoMode.Shared.Diagnostics.ProviderKeyStatus(string Provider, bool Configured)`
  - `PoMode.Shared.Session.SessionInfo(string UserName, IReadOnlyList<string> Roles)`
  - `PoMode.Shared.Serialization.PoModeJsonContext.Default` (JsonSerializerContext)

- [ ] **Step 1: Write the failing test** — `tests/PoMode.Unit/Serialization/JsonContextTests.cs`:

```csharp
using System.Text.Json;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Serialization;
using PoMode.Shared.Session;

namespace PoMode.Unit.Serialization;

public class JsonContextTests
{
    [Fact]
    public void DiagnosticsReport_round_trips_via_source_gen_context()
    {
        var report = new DiagnosticsReport(
            EnvironmentName: "Development",
            IsAzureHosted: false,
            SecretSource: "EnvironmentVariables",
            SecretFellBack: true,
            ProviderKeys: [new ProviderKeyStatus("ReplicateApiToken", Configured: true)]);

        var json = JsonSerializer.Serialize(report, PoModeJsonContext.Default.DiagnosticsReport);
        var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.DiagnosticsReport);

        Assert.NotNull(back);
        Assert.Equal("Development", back.EnvironmentName);
        Assert.True(back.SecretFellBack);
        Assert.Single(back.ProviderKeys);
        Assert.True(back.ProviderKeys[0].Configured);
    }

    [Fact]
    public void SessionInfo_round_trips_via_source_gen_context()
    {
        var session = new SessionInfo("alice", ["admin", "user"]);
        var json = JsonSerializer.Serialize(session, PoModeJsonContext.Default.SessionInfo);
        var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.SessionInfo);
        Assert.NotNull(back);
        Assert.Equal("alice", back.UserName);
        Assert.Equal(2, back.Roles.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PoMode.Unit --filter JsonContextTests`
Expected: FAIL — compile error, `DiagnosticsReport` does not exist.

- [ ] **Step 3: Implement the contracts**

`src/PoMode.Shared/Diagnostics/DiagnosticsReport.cs`:
```csharp
namespace PoMode.Shared.Diagnostics;

public sealed record DiagnosticsReport(
    string EnvironmentName,
    bool IsAzureHosted,
    string SecretSource,
    bool SecretFellBack,
    IReadOnlyList<ProviderKeyStatus> ProviderKeys);

public sealed record ProviderKeyStatus(string Provider, bool Configured);
```

`src/PoMode.Shared/Session/SessionInfo.cs`:
```csharp
namespace PoMode.Shared.Session;

public sealed record SessionInfo(string UserName, IReadOnlyList<string> Roles);
```

`src/PoMode.Shared/Serialization/PoModeJsonContext.cs`:
```csharp
using System.Text.Json.Serialization;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Session;

namespace PoMode.Shared.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagnosticsReport))]
[JsonSerializable(typeof(SessionInfo))]
public sealed partial class PoModeJsonContext : JsonSerializerContext;
```

Delete `src/PoMode.Shared/Placeholder.cs`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PoMode.Unit --filter JsonContextTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: shared diagnostics/session contracts with JSON source-gen context"
```

---

### Task 3: Secrets Bootstrap — Key Vault First, Environment Fallback

**Files:**
- Create: `src/PoMode.API/Infrastructure/SecretsBootstrap.cs`
- Test: `tests/PoMode.Unit/Infrastructure/SecretsBootstrapTests.cs`
- Modify: `tests/PoMode.Unit/PoMode.Unit.csproj` (add API project reference)

**Interfaces:**
- Consumes: nothing.
- Produces (used by Tasks 5):
  - `PoMode.API.Infrastructure.SecretSource` enum: `KeyVault | EnvironmentVariables`
  - `PoMode.API.Infrastructure.SecretSourceInfo(SecretSource Source, bool FellBack)`
  - `static SecretSourceInfo SecretsBootstrap.Decide(string? vaultUri, Func<bool> tryConnectKeyVault)` — pure decision logic
  - `static SecretSourceInfo SecretsBootstrap.Configure(WebApplicationBuilder builder)` — wires the Key Vault config provider; reads `KeyVault:VaultUri`

- [ ] **Step 1: Add project reference and Azure packages**

```powershell
dotnet add tests/PoMode.Unit reference src/PoMode.API
dotnet add src/PoMode.API package Azure.Identity
dotnet add src/PoMode.API package Azure.Extensions.AspNetCore.Configuration.Secrets
```

- [ ] **Step 2: Write the failing test** — `tests/PoMode.Unit/Infrastructure/SecretsBootstrapTests.cs`:

```csharp
using PoMode.API.Infrastructure;

namespace PoMode.Unit.Infrastructure;

public class SecretsBootstrapTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_vault_uri_means_environment_variables_without_fallback_flag(string? vaultUri)
    {
        var info = SecretsBootstrap.Decide(vaultUri, tryConnectKeyVault: () => throw new Exception("must not be called"));
        Assert.Equal(SecretSource.EnvironmentVariables, info.Source);
        Assert.False(info.FellBack);
    }

    [Fact]
    public void Reachable_vault_wins()
    {
        var info = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => true);
        Assert.Equal(SecretSource.KeyVault, info.Source);
        Assert.False(info.FellBack);
    }

    [Fact]
    public void Unreachable_vault_falls_back_to_environment_and_flags_it()
    {
        var info = SecretsBootstrap.Decide("https://poshared-kv.vault.azure.net/", () => false);
        Assert.Equal(SecretSource.EnvironmentVariables, info.Source);
        Assert.True(info.FellBack);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PoMode.Unit --filter SecretsBootstrapTests`
Expected: FAIL — compile error, `SecretsBootstrap` does not exist.

- [ ] **Step 4: Implement** — `src/PoMode.API/Infrastructure/SecretsBootstrap.cs`:

```csharp
using Azure.Identity;

namespace PoMode.API.Infrastructure;

public enum SecretSource
{
    KeyVault,
    EnvironmentVariables,
}

public sealed record SecretSourceInfo(SecretSource Source, bool FellBack);

public static class SecretsBootstrap
{
    /// <summary>Pure tier decision: Key Vault when configured and reachable, else environment variables.</summary>
    public static SecretSourceInfo Decide(string? vaultUri, Func<bool> tryConnectKeyVault)
    {
        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            return new SecretSourceInfo(SecretSource.EnvironmentVariables, FellBack: false);
        }

        return tryConnectKeyVault()
            ? new SecretSourceInfo(SecretSource.KeyVault, FellBack: false)
            : new SecretSourceInfo(SecretSource.EnvironmentVariables, FellBack: true);
    }

    /// <summary>Wires the Key Vault configuration provider. Reads "KeyVault:VaultUri" (env: KEYVAULT__VAULTURI).</summary>
    public static SecretSourceInfo Configure(WebApplicationBuilder builder)
    {
        var vaultUri = builder.Configuration["KeyVault:VaultUri"];
        return Decide(vaultUri, () =>
        {
            try
            {
                builder.Configuration.AddAzureKeyVault(new Uri(vaultUri!), new DefaultAzureCredential());
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PoMode.Unit --filter SecretsBootstrapTests`
Expected: PASS (5 tests: 3 theory cases + 2 facts).

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: Key Vault-first secrets bootstrap with environment fallback"
```

---

### Task 4: FakeAuthHandler with Production Guard + Session Endpoint

**Files:**
- Create: `src/PoMode.API/Infrastructure/FakeAuthHandler.cs`, `src/PoMode.API/Features/Session/SessionEndpoints.cs`
- Modify: `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.E2EAPI/FakeAuthTests.cs`

**Interfaces:**
- Consumes: `SessionInfo` (Task 2), `PoModeJsonContext` (Task 2).
- Produces (used by Task 5–6 and every future authenticated endpoint):
  - Auth scheme `FakeAuthHandler.SchemeName == "FakeAuth"`, reads `X-Fake-User` / `X-Fake-Roles` (comma-separated) headers; unauthenticated ⇒ 401.
  - `GET /api/session` → 200 `SessionInfo` when authenticated, 401 otherwise.
  - `static IEndpointRouteBuilder SessionEndpoints.MapSession(this IEndpointRouteBuilder app)`

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.E2EAPI/FakeAuthTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using PoMode.Shared.Session;

namespace PoMode.E2EAPI;

public class FakeAuthTests
{
    [Fact]
    public async Task Session_without_fake_user_header_returns_401()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Session_with_fake_user_and_roles_returns_identity()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", "alice");
        client.DefaultRequestHeaders.Add("X-Fake-Roles", "admin, listener");

        var session = await client.GetFromJsonAsync<SessionInfo>("/api/session");

        Assert.NotNull(session);
        Assert.Equal("alice", session.UserName);
        Assert.Equal(["admin", "listener"], session.Roles);
    }

    [Fact]
    public async Task FakeAuth_throws_InvalidOperationException_in_production()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", "mallory");

        // TestServer rethrows unhandled server exceptions to the caller.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api/session"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PoMode.E2EAPI --filter FakeAuthTests`
Expected: FAIL — `/api/session` returns 404 (endpoint and scheme don't exist yet).

- [ ] **Step 3: Implement the handler** — `src/PoMode.API/Infrastructure/FakeAuthHandler.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PoMode.API.Infrastructure;

/// <summary>Dev/test-only header auth (X-Fake-User / X-Fake-Roles). Hard-fails in Production per NET_RULES.</summary>
public sealed class FakeAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "FakeAuth";
    public const string UserHeader = "X-Fake-User";
    public const string RolesHeader = "X-Fake-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "FakeAuthHandler must never run in Production. Configure a real authentication provider.");
        }

        var userName = Request.Headers[UserHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, userName) };
        var roles = Request.Headers[RolesHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(roles))
        {
            claims.AddRange(roles
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(role => new Claim(ClaimTypes.Role, role)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

- [ ] **Step 4: Implement the session slice** — `src/PoMode.API/Features/Session/SessionEndpoints.cs`:

```csharp
using System.Security.Claims;
using PoMode.Shared.Session;

namespace PoMode.API.Features.Session;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSession(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/session").RequireAuthorization();

        group.MapGet("", (ClaimsPrincipal user) => TypedResults.Ok(new SessionInfo(
            user.Identity?.Name ?? "unknown",
            user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray())));

        return app;
    }
}
```

- [ ] **Step 5: Wire into `src/PoMode.API/Program.cs`** (replace file):

```csharp
using Microsoft.AspNetCore.Authentication;
using PoMode.API.Features.Session;
using PoMode.API.Infrastructure;
using PoMode.Shared.Serialization;

var builder = WebApplication.CreateBuilder(args);

var secretSource = SecretsBootstrap.Configure(builder);
builder.Services.AddSingleton(secretSource);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PoModeJsonContext.Default));

builder.Services.AddAuthentication(FakeAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

if (secretSource.FellBack)
{
    app.Logger.LogWarning("Key Vault unreachable — secrets are coming from environment variables this run.");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapSession();

app.Run();

public partial class Program;
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/PoMode.E2EAPI --filter FakeAuthTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "feat: FakeAuth header authentication with production guard and session endpoint"
```

---

### Task 5: Diagnostics Slice — `/health`, `/diag`, OpenAPI + Scalar

**Files:**
- Create: `src/PoMode.API/Features/Hardware/DiagnosticsService.cs`, `src/PoMode.API/Features/Hardware/DiagnosticsEndpoints.cs`, `src/PoMode.API/Infrastructure/JobStorageHealthCheck.cs`, `src/PoMode.API/appsettings.json` (replace template), `src/PoMode.API/appsettings.Development.json` (replace template)
- Modify: `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.E2EAPI/DiagnosticsTests.cs`, `tests/PoMode.Integration/JobStorageHealthCheckTests.cs`

**Interfaces:**
- Consumes: `DiagnosticsReport`/`ProviderKeyStatus` (Task 2), `SecretSourceInfo` (Task 3).
- Produces:
  - `GET /health` → 200 `"Healthy"` text (anonymous)
  - `GET /diag` → 200 `DiagnosticsReport` JSON (anonymous, fully redacted)
  - `/scalar` + `/openapi/v1.json` docs
  - `DiagnosticsService.BuildReport()` — Phase 2 extends this with the GPU probe
  - Config key `Jobs:RootPath` (default `<content root>/jobs`) — Phase 2's job store uses the same key

- [ ] **Step 1: Add API doc packages**

```powershell
dotnet add src/PoMode.API package Microsoft.AspNetCore.OpenApi
dotnet add src/PoMode.API package Scalar.AspNetCore
```

- [ ] **Step 2: Write the failing E2EAPI tests** — `tests/PoMode.E2EAPI/DiagnosticsTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Diagnostics;

namespace PoMode.E2EAPI;

public class DiagnosticsTests
{
    private const string FakeSecret = "sk-super-secret-value-9000";

    [Fact]
    public async Task Health_returns_healthy()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Diag_reports_provider_key_presence_without_leaking_values()
    {
        Environment.SetEnvironmentVariable("ReplicateApiToken", FakeSecret);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var raw = await client.GetStringAsync("/diag");
            var report = await client.GetFromJsonAsync<DiagnosticsReport>("/diag");

            Assert.DoesNotContain(FakeSecret, raw); // redaction is non-negotiable
            Assert.NotNull(report);
            Assert.True(report.ProviderKeys.Single(k => k.Provider == "ReplicateApiToken").Configured);
            Assert.False(report.ProviderKeys.Single(k => k.Provider == "SonicApiKey").Configured);
            Assert.False(report.ProviderKeys.Single(k => k.Provider == "LalalApiKey").Configured);
            Assert.False(report.IsAzureHosted);
            Assert.Equal("EnvironmentVariables", report.SecretSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ReplicateApiToken", null);
        }
    }

    [Fact]
    public async Task OpenApi_document_and_scalar_ui_are_served()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var doc = await client.GetAsync("/openapi/v1.json");
        var scalar = await client.GetAsync("/scalar");

        doc.EnsureSuccessStatusCode();
        scalar.EnsureSuccessStatusCode();
    }
}
```

- [ ] **Step 3: Write the failing integration test** — `tests/PoMode.Integration/JobStorageHealthCheckTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoMode.API.Infrastructure;

namespace PoMode.Integration;

public class JobStorageHealthCheckTests
{
    private static IConfiguration ConfigWith(string rootPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:RootPath"] = rootPath })
            .Build();

    [Fact]
    public async Task Writable_directory_is_healthy()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pomode-health-{Guid.NewGuid():N}");
        try
        {
            var check = new JobStorageHealthCheck(ConfigWith(dir));
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unwritable_path_is_unhealthy()
    {
        var check = new JobStorageHealthCheck(ConfigWith("Z:\\pomode-does-not-exist\\<>|invalid"));
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
```

- [ ] **Step 4: Run both to verify they fail**

Run: `dotnet test tests/PoMode.E2EAPI --filter DiagnosticsTests` and `dotnet test tests/PoMode.Integration --filter JobStorageHealthCheckTests`
Expected: FAIL — compile errors / 404s (services don't exist).

- [ ] **Step 5: Implement the diagnostics slice**

`src/PoMode.API/Features/Hardware/DiagnosticsService.cs`:
```csharp
using PoMode.API.Infrastructure;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.Hardware;

/// <summary>Builds the /diag report. Reports secret PRESENCE only — never values. Phase 2 adds the GPU probe.</summary>
public sealed class DiagnosticsService(
    IConfiguration configuration,
    IHostEnvironment environment,
    SecretSourceInfo secretSource)
{
    private static readonly string[] ProviderKeyNames = ["ReplicateApiToken", "SonicApiKey", "LalalApiKey"];

    public DiagnosticsReport BuildReport() => new(
        EnvironmentName: environment.EnvironmentName,
        IsAzureHosted: IsAzureHosted(),
        SecretSource: secretSource.Source.ToString(),
        SecretFellBack: secretSource.FellBack,
        ProviderKeys: ProviderKeyNames
            .Select(name => new ProviderKeyStatus(name, !string.IsNullOrEmpty(configuration[name])))
            .ToArray());

    private static bool IsAzureHosted() =>
        Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") is not null
        || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
}
```

`src/PoMode.API/Features/Hardware/DiagnosticsEndpoints.cs`:
```csharp
namespace PoMode.API.Features.Hardware;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnostics(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/diag");
        group.MapGet("", (DiagnosticsService diagnostics) => TypedResults.Ok(diagnostics.BuildReport()));
        return app;
    }
}
```

`src/PoMode.API/Infrastructure/JobStorageHealthCheck.cs`:
```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoMode.API.Infrastructure;

/// <summary>Verifies the job artifact root exists and is writable.</summary>
public sealed class JobStorageHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = configuration["Jobs:RootPath"] ?? Path.Combine(AppContext.BaseDirectory, "jobs");
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".healthprobe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy("Job storage writable."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Job storage not writable.", ex));
        }
    }
}
```

`src/PoMode.API/appsettings.json` (replace — note: NO secrets, ever):
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "KeyVault": {
    "VaultUri": ""
  },
  "Jobs": {
    "RootPath": ""
  }
}
```

`src/PoMode.API/appsettings.Development.json` (replace):
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

- [ ] **Step 6: Wire into `Program.cs`** — add below the authorization registration:

```csharp
builder.Services.AddOpenApi();
builder.Services.AddSingleton<PoMode.API.Features.Hardware.DiagnosticsService>();
builder.Services.AddHealthChecks().AddCheck<JobStorageHealthCheck>("job-storage");
```

and in the pipeline (after `app.UseAuthorization();`):

```csharp
app.MapOpenApi();
app.MapScalarApiReference(); // serves /scalar
app.MapHealthChecks("/health");
app.MapDiagnostics();
```

Add `using PoMode.API.Features.Hardware;` and `using Scalar.AspNetCore;` to the top of `Program.cs`.

- [ ] **Step 7: Run all tests to verify they pass**

Run: `dotnet test`
Expected: PASS — all tests across Unit, Integration, E2EAPI.

- [ ] **Step 8: Commit**

```powershell
git add -A
git commit -m "feat: /health, secret-redacting /diag, and Scalar API docs"
```

---

### Task 6: Blazor Shell — Radzen Layout, Theme, Mock Banner, Hosted by API

**Files:**
- Create: `src/PoMode.Client/App.razor`, `src/PoMode.Client/_Imports.razor`, `src/PoMode.Client/Layout/MainLayout.razor`, `src/PoMode.Client/Layout/MainLayout.razor.css`, `src/PoMode.Client/Components/MockDataBanner.razor`, `src/PoMode.Client/Components/MockDataBanner.razor.css`, `src/PoMode.Client/Pages/Home.razor`, `src/PoMode.Client/Services/MockDataState.cs`, `src/PoMode.Client/wwwroot/css/app.css`
- Modify: `src/PoMode.Client/Program.cs`, `src/PoMode.Client/wwwroot/index.html`, `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.E2EAPI/ClientHostingTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `GET /` on the API serves the WASM `index.html`; `/_framework/blazor.webassembly.js` resolves.
  - `PoMode.Client.Services.MockDataState { bool IsMockData }` — singleton; Phase 2 flips it to `false` when a real job's results load. Default `true`.
  - Layout slots later phases fill: header center actions (`Upload`, `Analyze`, `Export MIDI` buttons — disabled for now), session area showing `/api/session` name later.

- [ ] **Step 1: Add Radzen + hosting packages**

```powershell
dotnet add src/PoMode.Client package Microsoft.AspNetCore.Components.WebAssembly
dotnet add src/PoMode.Client package Radzen.Blazor
dotnet add src/PoMode.API package Microsoft.AspNetCore.Components.WebAssembly.Server
```

- [ ] **Step 2: Write the failing test** — `tests/PoMode.E2EAPI/ClientHostingTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;

namespace PoMode.E2EAPI;

public class ClientHostingTests
{
    [Fact]
    public async Task Root_serves_blazor_index_html()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("blazor.webassembly.js", html);
        Assert.Contains("<title>PoMode</title>", html);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PoMode.E2EAPI --filter ClientHostingTests`
Expected: FAIL — `/` currently 404s (no static hosting wired).

- [ ] **Step 4: Implement the client shell**

`src/PoMode.Client/Program.cs` (replace):
```csharp
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoMode.Client;
using PoMode.Client.Services;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddRadzenComponents();
builder.Services.AddSingleton<MockDataState>();
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
```

`src/PoMode.Client/Services/MockDataState.cs`:
```csharp
namespace PoMode.Client.Services;

/// <summary>True whenever displayed analysis data is mock/local. Real job results set this false (Phase 2+).</summary>
public sealed class MockDataState
{
    public bool IsMockData { get; set; } = true;
}
```

`src/PoMode.Client/_Imports.razor`:
```razor
@using System.Net.Http
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Radzen
@using Radzen.Blazor
@using PoMode.Client.Components
@using PoMode.Client.Layout
@using PoMode.Client.Services
```

`src/PoMode.Client/App.razor`:
```razor
<Router AppAssembly="@typeof(App).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)" />
        <FocusOnNavigate RouteData="@routeData" Selector="h1" />
    </Found>
    <NotFound>
        <LayoutView Layout="@typeof(MainLayout)">
            <RadzenText Text="Page not found." />
        </LayoutView>
    </NotFound>
</Router>
```

`src/PoMode.Client/Layout/MainLayout.razor` (NET_RULES header: left brand | center actions | right session):
```razor
@inherits LayoutComponentBase

<header class="app-header">
    <div class="brand">PoMode</div>
    <nav class="actions">
        <RadzenButton Text="Upload" Disabled="true" ButtonStyle="ButtonStyle.Primary" />
        <RadzenButton Text="Analyze" Disabled="true" ButtonStyle="ButtonStyle.Secondary" />
        <RadzenButton Text="Export MIDI" Disabled="true" ButtonStyle="ButtonStyle.Secondary" />
    </nav>
    <div class="session">Guest</div>
</header>

<MockDataBanner />

<main class="app-main">
    @Body
</main>
```

`src/PoMode.Client/Layout/MainLayout.razor.css`:
```css
.app-header {
    display: grid;
    grid-template-columns: 1fr auto 1fr;
    align-items: center;
    gap: 1rem;
    padding: 0.5rem 1rem;
    background: var(--pm-surface);
    border-bottom: 1px solid var(--pm-border);
}

.brand {
    font-size: 1.25rem;
    font-weight: 700;
    color: var(--pm-accent);
}

.actions {
    display: flex;
    gap: 0.5rem;
}

.session {
    justify-self: end;
    color: var(--pm-fg-muted);
}

.app-main {
    padding: 1rem;
}
```

`src/PoMode.Client/Components/MockDataBanner.razor`:
```razor
@inject MockDataState MockData

@if (MockData.IsMockData)
{
    <div class="mock-banner" role="alert">USING MOCK DATA</div>
}
```

`src/PoMode.Client/Components/MockDataBanner.razor.css`:
```css
.mock-banner {
    background: var(--pm-warn-bg);
    color: var(--pm-warn-fg);
    text-align: center;
    font-weight: 700;
    letter-spacing: 0.08em;
    padding: 0.25rem;
}
```

`src/PoMode.Client/Pages/Home.razor`:
```razor
@page "/"

<PageTitle>PoMode</PageTitle>

<RadzenCard>
    <RadzenText TextStyle="TextStyle.H5" Text="Audio Modal Analyzer" />
    <RadzenText Text="Upload a track to analyze its melody, chords, and scale modes. (Coming in Phase 2.)" />
</RadzenCard>
```

`src/PoMode.Client/wwwroot/css/app.css` (global variables + auto light/dark per NET_RULES):
```css
:root {
    --pm-bg: #ffffff;
    --pm-surface: #f5f5f7;
    --pm-fg: #1a1a1a;
    --pm-fg-muted: #55555f;
    --pm-border: #d9d9e0;
    --pm-accent: #6750a4;
    --pm-warn-bg: #b45309;
    --pm-warn-fg: #ffffff;
}

@media (prefers-color-scheme: dark) {
    :root {
        --pm-bg: #121214;
        --pm-surface: #1d1d21;
        --pm-fg: #e8e8ec;
        --pm-fg-muted: #a0a0ab;
        --pm-border: #33333c;
        --pm-accent: #cfbcff;
        --pm-warn-bg: #d97706;
        --pm-warn-fg: #121214;
    }
}

body {
    margin: 0;
    background: var(--pm-bg);
    color: var(--pm-fg);
    font-family: "Segoe UI", system-ui, sans-serif;
}
```

`src/PoMode.Client/wwwroot/index.html` (replace):
```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>PoMode</title>
    <base href="/" />
    <link rel="stylesheet" href="_content/Radzen.Blazor/css/material-base.css" />
    <link rel="stylesheet" href="css/app.css" />
    <link rel="stylesheet" href="PoMode.Client.styles.css" />
</head>
<body>
    <div id="app">Loading…</div>
    <script src="_framework/blazor.webassembly.js"></script>
    <script src="_content/Radzen.Blazor/Radzen.Blazor.js"></script>
</body>
</html>
```

- [ ] **Step 5: Host the client from the API** — in `src/PoMode.API/Program.cs`, immediately after `var app = builder.Build();` block's fallback warning, add:

```csharp
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
```

and after `app.MapDiagnostics();` add:

```csharp
app.MapFallbackToFile("index.html");
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS — all tests, including `ClientHostingTests`.

- [ ] **Step 7: Manual smoke check**

Run: `dotnet run --project src/PoMode.API --urls http://127.0.0.1:5199` then open `http://127.0.0.1:5199`.
Expected: header `PoMode | Upload · Analyze · Export MIDI | Guest`, orange **USING MOCK DATA** banner, card content. Toggle OS dark mode → colors flip. Stop the server.

- [ ] **Step 8: Commit**

```powershell
git add -A
git commit -m "feat: Blazor WASM shell with Radzen layout, theme variables, and mock-data banner"
```

---

### Task 7: E2EUI — Playwright Smoke Test Against the Real App

**Files:**
- Create: `tests/PoMode.E2EUI/AppFixture.cs`, `tests/PoMode.E2EUI/ShellSmokeTests.cs`

**Interfaces:**
- Consumes: the running app from Task 6 (`/health` for readiness, `/` for UI).
- Produces: `AppFixture` (xUnit collection fixture launching `dotnet run` on `http://127.0.0.1:5199`) — every future E2EUI test reuses it.

- [ ] **Step 1: Add Playwright package and install browsers**

```powershell
dotnet add tests/PoMode.E2EUI package Microsoft.Playwright
dotnet build tests/PoMode.E2EUI
pwsh tests/PoMode.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium
```

- [ ] **Step 2: Write the fixture** — `tests/PoMode.E2EUI/AppFixture.cs`:

```csharp
using System.Diagnostics;

namespace PoMode.E2EUI;

/// <summary>Boots the real PoMode.API (which hosts the WASM client) for browser tests.</summary>
public sealed class AppFixture : IAsyncLifetime
{
    public string BaseUrl => "http://127.0.0.1:5199";
    private Process? _server;

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        _server = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project src/PoMode.API --urls {BaseUrl}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start PoMode.API");

        using var http = new HttpClient();
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                var response = await http.GetAsync($"{BaseUrl}/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // server not up yet
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("PoMode.API did not become healthy within 60s.");
    }

    public Task DisposeAsync()
    {
        if (_server is { HasExited: false })
        {
            _server.Kill(entireProcessTree: true);
        }
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoMode.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("PoMode.sln not found above test bin dir.");
    }
}

[CollectionDefinition("App")]
public sealed class AppCollection : ICollectionFixture<AppFixture>;
```

- [ ] **Step 3: Write the failing-then-passing smoke test** — `tests/PoMode.E2EUI/ShellSmokeTests.cs`:

```csharp
using Microsoft.Playwright;

namespace PoMode.E2EUI;

[Collection("App")]
public class ShellSmokeTests(AppFixture app)
{
    [Fact]
    public async Task Shell_renders_header_and_mock_data_banner()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();

        await page.GotoAsync(app.BaseUrl);

        await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("PoMode").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Export MIDI" })).ToBeVisibleAsync();
    }
}
```

- [ ] **Step 4: Run the E2EUI test**

Run: `dotnet test tests/PoMode.E2EUI`
Expected: PASS (1 test). If it fails on WASM load timing, Playwright's auto-waiting `Expect` handles it — a failure here is a real hosting bug; debug with `superpowers:systematic-debugging`, do not add `Task.Delay`.

- [ ] **Step 5: Full-solution verification**

Run: `dotnet test`
Expected: every test in all four projects passes; build has zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "test: Playwright E2EUI smoke test with real-app fixture"
```

---

## Phase 1 Exit Criteria

- `dotnet test` green across Unit / Integration / E2EAPI / E2EUI; zero build warnings.
- `dotnet run --project src/PoMode.API` serves the Radzen shell with mock banner at `/`, docs at `/scalar`, `/health` Healthy, `/diag` redacted.
- No `Version=` in any csproj; no secret in any tracked file.
- Phase 2 planning starts from: `SecretSourceInfo`, `DiagnosticsService` (probe extension point), `Jobs:RootPath` config key, `MockDataState`, and the four test project patterns established here.
