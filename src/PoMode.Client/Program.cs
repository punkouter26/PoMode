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
builder.Services.AddScoped<AnalysisClient>();
builder.Services.AddScoped<SessionState>();
// Same-origin, so the session cookie rides on every request with no header to add; the same is true
// of the JS modules' fetch() calls and the browser's own uploads.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
