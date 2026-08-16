var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => TypedResults.Ok("PoMode API"));
app.Run();

public partial class Program;
