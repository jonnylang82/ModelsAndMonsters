using System.Reflection;
using System.Text.Json;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Web;

// The config files (appsettings.json, scenario.json) and prompt templates are copied next to the built
// assembly, so anchor the content root there rather than at the current directory — otherwise `dotnet run`
// (whose working directory is the project folder) cannot find them.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Reuse the core app's configuration: appsettings.json, the scenario file, and user secrets (so an OpenAI
// or Anthropic key configured for the CLI works here too; Ollama needs none).
builder.Configuration
    .AddJsonFile("scenario.json", optional: false, reloadOnChange: false)
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true, reloadOnChange: false);

builder.Services.Configure<SimulationOptions>(builder.Configuration.GetSection(SimulationOptions.SectionName));
builder.Services.Configure<ScenarioDefinition>(builder.Configuration.GetSection(ScenarioDefinition.SectionName));
builder.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();
builder.Services.AddSingleton(_ => PromptLibrary.LoadFromDirectory(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates")));
builder.Services.AddSingleton<RunManager>();

// The React dev server (Vite) runs on its own origin during development.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Start a run. The optional scenario id is reserved for scenario selection later; the shipped scenario is
// used for now. Returns immediately with the run id a viewer subscribes to.
app.MapPost("/api/runs", (RunManager runs, StartRunRequest? body) =>
{
    var session = runs.Start(body?.Scenario);
    return Results.Ok(new { runId = session.RunId });
});

// Live event stream for a run (Server-Sent Events). Replays everything so far, then streams new events.
app.MapGet("/api/runs/{id}/events", async (string id, RunManager runs, HttpContext ctx) =>
{
    var session = runs.Get(id);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // don't let a proxy buffer the stream

    var reader = session.Subscribe();
    try
    {
        await foreach (var uiEvent in reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(uiEvent, json)}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // The viewer disconnected; nothing to do — the run keeps going for any other viewer.
    }
});

app.MapPost("/api/runs/{id}/cancel", (string id, RunManager runs) =>
{
    runs.Cancel(id);
    return Results.Accepted();
});

app.Run("http://localhost:5170");

internal sealed record StartRunRequest(string? Scenario);
