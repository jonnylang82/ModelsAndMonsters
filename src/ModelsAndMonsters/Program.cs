using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Presentation;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

// Re-render report.md for a run that already happened, without starting a simulation.
if (args is ["--report", var runDirectory, ..])
{
    try
    {
        Console.WriteLine($"Report written to {RunReportWriter.Write(runDirectory)}");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Could not write a report for '{runDirectory}': {ex.Message}");
        return 1;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// appsettings.json, environment variables and command line come from the host builder. The scenario
// lives in its own file so scenarios can be swapped without touching application settings, and user
// secrets are added explicitly so an OpenAI key works outside the Development environment too.
builder.Configuration
    .AddJsonFile("scenario.json", optional: false, reloadOnChange: false)
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true, reloadOnChange: false);

builder.Services.Configure<SimulationOptions>(builder.Configuration.GetSection(SimulationOptions.SectionName));
builder.Services.Configure<ScenarioDefinition>(builder.Configuration.GetSection(ScenarioDefinition.SectionName));

builder.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();
builder.Services.AddSingleton(_ => PromptLibrary.LoadFromDirectory(
    Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates")));
builder.Services.AddSingleton<IGameConsole, ConsolePresenter>();
builder.Services.AddSingleton<SimulationRunner>();

using var host = builder.Build();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var runner = host.Services.GetRequiredService<SimulationRunner>();
    await runner.RunAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Run cancelled. Partial trace has been written.");
    return 130;
}
catch (Exception ex)
{
    // Operational failure reporting only; the experiment trace already holds the detail.
    Console.Error.WriteLine($"Run failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
