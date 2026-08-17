using System.Text.Json;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Tracing;

/// <summary>Filesystem layout for one run's output.</summary>
public sealed record RunPaths(string RunId, string Directory)
{
    public string RunJson => Path.Combine(Directory, "run.json");

    public string TraceJsonl => Path.Combine(Directory, "trace.jsonl");

    public string FinalStateJson => Path.Combine(Directory, "final-state.json");

    /// <summary>Creates <c>&lt;root&gt;/&lt;yyyyMMdd-HHmmssZ&gt;_&lt;short-id&gt;/</c> and returns it.</summary>
    public static RunPaths Create(string rootDirectory, DateTimeOffset startedAt)
    {
        var runId = $"{startedAt.UtcDateTime:yyyyMMdd-HHmmss}Z-{Guid.NewGuid().ToString("N")[..8]}";
        var directory = Path.Combine(rootDirectory, runId);
        System.IO.Directory.CreateDirectory(directory);
        return new RunPaths(runId, directory);
    }
}

/// <summary>Run-level metadata written once at the start of a run. Contains no secrets.</summary>
public sealed record RunManifest
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string ApplicationVersion { get; init; }

    public required string MachineOperatingSystem { get; init; }

    public required ScenarioDefinition Scenario { get; init; }

    public required GameState InitialState { get; init; }

    public required IReadOnlyDictionary<string, TracedAgentProfile> AgentProfiles { get; init; }

    /// <summary>Content hash per prompt template, so a run can be tied to the exact prompts used.</summary>
    public required IReadOnlyDictionary<string, string> PromptVersions { get; init; }

    public required HarnessOptions Harness { get; init; }

    /// <summary>Everything needed to replay this run's randomness.</summary>
    public required RunSeedInfo Seeds { get; init; }
}

/// <summary>
/// The seeds a run used, recorded so any run — including a random one — can be replayed exactly by
/// setting Harness.Seed to <see cref="MasterSeed"/>.
/// </summary>
public sealed record RunSeedInfo
{
    /// <summary>The master seed the whole run derives from.</summary>
    public required long MasterSeed { get; init; }

    /// <summary>False when the master was generated randomly because no seed was configured.</summary>
    public required bool SeedWasProvided { get; init; }

    /// <summary>The seed used for the game's dice rolls, derived from the master.</summary>
    public required long GameSeed { get; init; }

    /// <summary>Per-agent model sampling seeds, derived from the master.</summary>
    public required IReadOnlyDictionary<string, long> AgentSeeds { get; init; }

    /// <summary>How to reproduce this run.</summary>
    public string ReplayHint => $"Set ModelsAndMonsters:Harness:Seed to {MasterSeed} to replay this run.";
}

/// <summary>
/// An agent's model configuration as recorded in run.json. Endpoints are included because they are
/// not secret; API keys are never read into this type.
/// </summary>
public sealed record TracedAgentProfile
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

    public int? MaxOutputTokens { get; init; }

    public long? Seed { get; init; }

    public int? ContextWindow { get; init; }

    public bool? Thinking { get; init; }

    public string? Endpoint { get; init; }

    /// <summary>Sampling options this provider cannot honour and will therefore never be sent.</summary>
    public required IReadOnlyList<string> OptionsUnsupportedByProvider { get; init; }

    public static TracedAgentProfile From(AgentModelProfile profile)
    {
        var resolved = ChatOptionsFactory.Create(profile);
        return new TracedAgentProfile
        {
            Provider = profile.Provider.ToString(),
            ModelId = profile.ModelId,
            Temperature = profile.Temperature,
            TopP = profile.TopP,
            TopK = profile.TopK,
            MaxOutputTokens = profile.MaxOutputTokens,
            Seed = profile.Seed,
            ContextWindow = profile.ContextWindow,
            Thinking = profile.Thinking,
            Endpoint = profile.Endpoint,
            OptionsUnsupportedByProvider = resolved.UnsupportedOptionsDropped
        };
    }
}

/// <summary>The state of the world when the run stopped.</summary>
public sealed record FinalStateDocument
{
    public required string RunId { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required string TerminalCondition { get; init; }

    public required int RoundsPlayed { get; init; }

    public required long TraceEventCount { get; init; }

    public required GameState State { get; init; }
}

/// <summary>Writes the non-streaming run artefacts.</summary>
public static class RunArtifactWriter
{
    public static void WriteManifest(RunPaths paths, RunManifest manifest) =>
        File.WriteAllText(paths.RunJson, JsonSerializer.Serialize(manifest, TraceJson.Indented));

    public static void WriteFinalState(RunPaths paths, FinalStateDocument document) =>
        File.WriteAllText(paths.FinalStateJson, JsonSerializer.Serialize(document, TraceJson.Indented));
}
