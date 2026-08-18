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
builder.Services.AddScoped(_ =>
{
    var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    // Dev/test FakeAuth: the API's write endpoints (cancel, client-result, from-url)
    // require an authenticated caller. Production replaces this with a real auth provider.
    http.DefaultRequestHeaders.Add("X-Fake-User", "guest");
    return http;
});

await builder.Build().RunAsync();
