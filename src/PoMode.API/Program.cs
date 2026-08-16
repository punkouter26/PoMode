using Microsoft.AspNetCore.Authentication;
using PoMode.API.Features.Hardware;
using PoMode.API.Features.Session;
using PoMode.API.Infrastructure;
using PoMode.Shared.Serialization;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var secretSource = SecretsBootstrap.Configure(builder);
builder.Services.AddSingleton(secretSource);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PoModeJsonContext.Default));

builder.Services.AddAuthentication(FakeAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddHealthChecks().AddCheck<JobStorageHealthCheck>("job-storage");

var app = builder.Build();

if (secretSource.FellBack)
{
    app.Logger.LogWarning("Key Vault unreachable — secrets are coming from environment variables this run.");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference(); // serves /scalar
app.MapDiagnostics();

app.MapSession();

app.Run();

public partial class Program;
