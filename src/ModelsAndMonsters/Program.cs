using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelsAndMonsters.AI;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Presentation;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Rulebook.Selection;
using ModelsAndMonsters.Tracing;

// Re-render the reports for a run that already happened, without starting a simulation. Both the full
// report.md and the trace-free report-summary.md are written.
if (args is ["--report", var runDirectory, ..])
{
    try
    {
        var (full, summary) = RunReportWriter.WriteAll(runDirectory);
        Console.WriteLine($"Reports written to {full} (full) and {summary} (summary, no trace)");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Could not write reports for '{runDirectory}': {ex.Message}");
        return 1;
    }
}

// Print the content-hash version of the rulebook and exit. No host, no scenario, no model — just the
// catalog. Handy after editing a rule card, since the corpus pins the version and the pin must be updated.
if (args is ["--rulebook-version", ..])
{
    Console.WriteLine(new RuleCatalog().RulebookVersion);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

// Which scenario file to run. Defaults to scenario.json, but a run can be pointed at any other scenario
// (they are all copied beside the binary by the scenario*.json glob) with `--scenario-file <name>` or the
// `ScenarioFile` config key — without editing application settings. The flag name is deliberately NOT
// "--scenario", which the command-line config provider would fold into the "Scenario" section itself.
var scenarioFile = ResolveScenarioFile(args, builder.Configuration);
var scenarioPath = FindScenarioPath(scenarioFile);
if (scenarioPath is null)
{
    Console.Error.WriteLine(
        $"Scenario file '{scenarioFile}' was not found beside the application or in the working directory. " +
        $"Available scenarios: {string.Join(", ", ListAvailableScenarios())}.");
    return 1;
}

// appsettings.json, environment variables and command line come from the host builder. The scenario
// lives in its own file so scenarios can be swapped without touching application settings, and user
// secrets are added explicitly so an OpenAI key works outside the Development environment too.
builder.Configuration
    .AddJsonFile(scenarioPath, optional: false, reloadOnChange: false)
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

// Ask the live Rulebook Resolver what it makes of one intent, and print the guidance. No encounter, no
// engine, no trace file — the smallest possible way to check that a phrasing routes to the action you
// expect, which otherwise only shows up buried in a forty-minute run's trace.
if (args is ["--rulebook-probe", var probeIntent, ..])
{
    try
    {
        var prompts = host.Services.GetRequiredService<PromptLibrary>();
        var options = host.Services.GetRequiredService<IOptions<SimulationOptions>>().Value;
        var factory = host.Services.GetRequiredService<IChatClientFactory>();

        // The same window regime a run would use, so the probe measures what a run would measure.
        var profile = AgentModelProfile.FromOptions(
            "RulebookResolver", options.Agents.RulebookResolver.Overlay(options.Agents.Default),
            options.Harness.EnforceContextWindowOnHostedModels);

        var catalog = new RuleCatalog();
        var trace = new ExperimentTrace("rulebook-probe", new ProbeTraceSink());
        using var probeClient = factory.Create(profile);
        var resolver = new RulebookResolver(profile, new TracingChatClient(probeClient, profile, trace), prompts);

        // The probe goes through the SAME selection path a run would, or it answers a different question
        // than the one being asked of it. One selector, one factory, both callers.
        var probeSelector = RuleSelectorFactory.Create(
            options.Harness, options.Providers, catalog, profile,
            p => new TracingChatClient(factory.Create(p), p, trace));

        var consultant = new RulebookConsultant(
            catalog,
            new RuleRetriever(catalog, options.Harness.RulebookMaxCards, options.Harness.RulebookMaxInputChars),
            resolver,
            new RuleGuidanceValidator(catalog),
            new RuleGuidanceCache(),
            trace,
            new RulebookConsultationOptions(
                options.Harness.RulebookMaxCards, options.Harness.RulebookMaxInputChars,
                options.Harness.RulebookOutputTokens, CacheEnabled: false));

        // The raw reply is captured here rather than only in a trace: when a probe comes back malformed,
        // what the model actually wrote is the whole diagnosis.
        string? raw = null;
        var selectionNote = "(whole rulebook)";
        var capturing = new RulebookConsultant(
            catalog,
            new RuleRetriever(catalog, options.Harness.RulebookMaxCards, options.Harness.RulebookMaxInputChars),
            new CapturingResolver(resolver, text => raw = text),
            new RuleGuidanceValidator(catalog),
            new RuleGuidanceCache(),
            trace,
            new RulebookConsultationOptions(
                options.Harness.RulebookMaxCards, options.Harness.RulebookMaxInputChars,
                options.Harness.RulebookOutputTokens, CacheEnabled: false),
            probeSelector);

        if (probeSelector is not null)
        {
            var chosen = await probeSelector.SelectAsync(probeIntent, cancellation.Token);
            selectionNote = chosen.FellBack
                ? $"{chosen.Cards.Count} (FELL BACK: {chosen.FallbackReason})"
                : $"{chosen.Cards.Count} — picked [{string.Join(", ", chosen.DirectlySelectedRuleIds)}]" +
                  (chosen.ExpandedRuleIds.Count == 0
                      ? ""
                      : $", links added [{string.Join(", ", chosen.ExpandedRuleIds)}]");
        }

        var probe = await capturing.ConsultAsync("probe", "Probe", probeIntent, cancellation.Token);

        Console.WriteLine($"intent:    {probeIntent}");
        Console.WriteLine($"model:     {profile.Provider}:{profile.ModelId}");
        Console.WriteLine($"selection: {probeSelector?.Mode.ToString() ?? nameof(RuleSelectionMode.WholeRulebook)}");
        Console.WriteLine($"cards:     {selectionNote}");
        Console.WriteLine($"outcome:   {probe.Outcome}{(probe.FailureDetail is { } why ? $" ({why})" : "")}");
        Console.WriteLine($"actions:   {string.Join(", ", probe.Guidance?.CandidateActions ?? [])}");
        Console.WriteLine($"cited:     {string.Join(", ", probe.Guidance?.CitedRules.Select(c => c.RuleId) ?? [])}");
        Console.WriteLine($"dm tools:  {string.Join(", ", probe.CandidateTools.Select(t => t.Name))}");
        Console.WriteLine($"turn cost: {probe.Guidance?.TurnCost}");
        Console.WriteLine($"rng:       {probe.Guidance?.RngSpecification}");
        if (probe.Guidance?.UnsupportedReason is { } reason)
        {
            Console.WriteLine($"reason:    {reason}");
        }

        if (probe.Outcome is not (RulebookOutcome.Supported or RulebookOutcome.Unsupported))
        {
            Console.WriteLine($"raw:       {raw}");
        }

        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Probe cancelled.");
        return 130;
    }
}

// Measure the rule-selection strategies against the labelled corpus and write the findings. It starts no
// encounter and writes nothing into runs/: it is an experiment about the rulebook stage, not a run.
if (args.Contains("--rulebook-eval"))
{
    try
    {
        var prompts = host.Services.GetRequiredService<PromptLibrary>();
        var options = host.Services.GetRequiredService<IOptions<SimulationOptions>>().Value;
        var factory = host.Services.GetRequiredService<IChatClientFactory>();

        // The index selector runs on the resolver's own profile: same provider, same model, same low
        // temperature, so the comparison is between STRATEGIES rather than between models.
        var profile = AgentModelProfile.FromOptions(
            "RuleIndexSelector", options.Agents.RulebookResolver.Overlay(options.Agents.Default),
            options.Harness.EnforceContextWindowOnHostedModels);

        // Honour a configured seed so a measurement is reproducible — the model-backed selectors then choose
        // identically run to run, and a before/after comparison is not muddied by sampling noise. Null (the
        // default) leaves the eval unseeded, matching the originally published methodology.
        if (options.Harness.Seed is { } evalSeed)
        {
            profile = profile with { Seed = evalSeed };
        }

        IChatClient? client = null;
        try
        {
            client = factory.Create(profile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"No model available for the index selection step ({ex.GetType().Name}: {ex.Message}). " +
                "The offline strategies will still be measured, and the record will say so.");
        }

        // A real embedding provider, when one is configured, so approach 2 is measured rather than assumed.
        var embeddingModel = options.Harness.RulebookEmbeddingModel;
        using var embedder = string.IsNullOrWhiteSpace(embeddingModel)
            ? null
            : new OllamaRuleEmbedder(options.Providers.Ollama.Endpoint, embeddingModel);

        var markdown = await RuleSelectionEvaluationRun.RunAsync(
            prompts, client is null ? null : profile, client, options.Harness.RulebookSelectionTopK,
            cancellation.Token, embedder, embedder is null ? null : $"Ollama:{embeddingModel}");

        var target = args.SkipWhile(a => a != "--rulebook-eval").Skip(1).FirstOrDefault()
                     ?? Path.Combine("reports", "rulebook-efficiency-measurements.md");
        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(target, markdown, cancellation.Token);
        Console.WriteLine(markdown);
        Console.WriteLine($"Measurements written to {target}");
        client?.Dispose();
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Measurement cancelled.");
        return 130;
    }
    catch (InvalidOperationException ex)
    {
        // The corpus-coverage check (or a catalog validation) fails the eval on purpose rather than warning.
        Console.Error.WriteLine($"Rulebook eval failed: {ex.Message}");
        return 1;
    }
}

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

// Which scenario file the run should load: `--scenario-file <name>` / `--scenario-file=<name>` first, then the
// `ScenarioFile` config key (appsettings or environment), then the default. Parsed from args by hand rather
// than left to the command-line config provider, whose only latitude here is the fallback config key.
static string ResolveScenarioFile(string[] arguments, IConfiguration configuration)
{
    for (var i = 0; i < arguments.Length; i++)
    {
        if (string.Equals(arguments[i], "--scenario-file", StringComparison.Ordinal) && i + 1 < arguments.Length)
        {
            return arguments[i + 1];
        }

        const string inlinePrefix = "--scenario-file=";
        if (arguments[i].StartsWith(inlinePrefix, StringComparison.Ordinal))
        {
            return arguments[i][inlinePrefix.Length..];
        }
    }

    var configured = configuration["ScenarioFile"];
    return string.IsNullOrWhiteSpace(configured) ? "scenario.json" : configured.Trim();
}

// Resolves a scenario file name to an absolute path, looking beside the binary and in the working directory
// (the same two places the scenario*.json glob and a `dotnet run` invocation put it), or null if absent.
static string? FindScenarioPath(string scenarioFile)
{
    if (Path.IsPathRooted(scenarioFile))
    {
        return File.Exists(scenarioFile) ? scenarioFile : null;
    }

    foreach (var baseDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var candidate = Path.Combine(baseDirectory, scenarioFile);
        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }
    }

    return null;
}

// The scenario files actually present, for a helpful error when the requested one is missing.
static IReadOnlyList<string> ListAvailableScenarios()
{
    var names = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var baseDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        if (!Directory.Exists(baseDirectory))
        {
            continue;
        }

        foreach (var path in Directory.EnumerateFiles(baseDirectory, "scenario*.json"))
        {
            names.Add(Path.GetFileName(path));
        }
    }

    return names.Count == 0 ? ["scenario.json"] : [.. names];
}

/// <summary>A trace sink for the one-shot probe: the call is traced, and nothing is kept.</summary>
file sealed class ProbeTraceSink : ITraceSink
{
    public void Write(TraceEvent traceEvent)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>Passes a probe's resolver call through unchanged, keeping the raw reply for the diagnosis.</summary>
file sealed class CapturingResolver(IRulebookResolver inner, Action<string> capture) : IRulebookResolver
{
    public AgentModelProfile Profile => inner.Profile;

    public async Task<ResolverResult> ResolveAsync(
        string intent, IReadOnlyList<RuleCard> cards, CancellationToken cancellationToken)
    {
        var result = await inner.ResolveAsync(intent, cards, cancellationToken).ConfigureAwait(false);
        capture($"[{result.OutputTokens?.ToString() ?? "?"} output tokens] {result.RawResponse}");
        return result;
    }
}
