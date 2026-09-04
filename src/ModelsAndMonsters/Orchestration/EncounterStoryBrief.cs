using System.Text.Json;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;

namespace ModelsAndMonsters.Orchestration;

/// <summary>One trace row reduced to what the story brief needs: its type and raw event data.</summary>
public readonly record struct EncounterEventRecord(string EventType, JsonElement Data);

/// <summary>One character's condition when the run stopped, for the deterministic ending.</summary>
public sealed record CharacterFinalState(string Name, string Team, CharacterDisposition Disposition, int Health, int MaxHealth);

/// <summary>
/// The compact, deterministic brief the Encounter Summariser is given instead of the raw public transcript —
/// authoritative facts drawn from the engine's own accepted actions and final state, never invented. The
/// model is asked to write "Opening" and "The Encounter" from this and nothing else; the ending is never
/// written by the model at all (see <see cref="RenderEndingParagraph"/>), so it cannot contradict what the
/// engine actually recorded.
/// </summary>
public sealed record EncounterStoryBrief
{
    public required string ScenarioPremise { get; init; }

    public required string Roster { get; init; }

    /// <summary>Chronological, one-line-each accounts of every accepted action, in order.</summary>
    public required IReadOnlyList<string> ChronologicalEvents { get; init; }

    /// <summary>How many events actually occurred, before any trim for length.</summary>
    public required int EventsTotal { get; init; }

    /// <summary>Explicit killer/victim pairs, extracted directly from lethal attack outcomes.</summary>
    public required IReadOnlyList<string> Kills { get; init; }

    public required IReadOnlyList<CharacterFinalState> FinalCharacterStates { get; init; }

    public required string TerminalCondition { get; init; }

    public required EncounterOutcome Outcome { get; init; }

    public required int RoundsPlayed { get; init; }

    public bool EventsTrimmed => ChronologicalEvents.Count < EventsTotal;

    /// <summary>No generative rewriting: item ownership and chronology remain exactly as recorded.</summary>
    public string RenderFactualRecap() => "## Encounter record\n\n"
        + (EventsTrimmed ? $"{EventsTotal - ChronologicalEvents.Count} earlier events omitted for length.\n\n" : "")
        + string.Join("\n", ChronologicalEvents.Select((entry, index) => $"{index + 1}. {entry}"));

    /// <summary>True when the harness stopped the encounter on a round or idle limit rather than a decision.</summary>
    public bool EndedUnresolvedAtRoundLimit => Outcome == EncounterOutcome.HarnessLimit;

    /// <summary>The bounded, numbered account of what happened, for the model's "The Encounter" section.</summary>
    public string RenderEventsForModel()
    {
        var lines = new List<string>();
        if (EventsTrimmed)
        {
            lines.Add($"[{EventsTotal - ChronologicalEvents.Count} earlier event(s) omitted here for length.]");
            lines.Add("");
        }

        if (ChronologicalEvents.Count == 0)
        {
            lines.Add("(No mechanical event took effect during this encounter.)");
        }
        else
        {
            for (var i = 0; i < ChronologicalEvents.Count; i++)
            {
                lines.Add($"{i + 1}. {ChronologicalEvents[i]}");
            }
        }

        if (Kills.Count > 0)
        {
            lines.Add("");
            lines.Add("Exactly who killed whom (state it no other way):");
            foreach (var kill in Kills)
            {
                lines.Add($"- {kill}");
            }
        }

        lines.Add("");
        lines.Add(EndedUnresolvedAtRoundLimit
            ? $"IMPORTANT: observation stopped after {RoundsPlayed} round(s) with NO decision reached — nobody " +
              "above has fallen, surrendered or fled as the fight's outcome. The Encounter section must not " +
              "resolve the fight; end your account where the record above ends."
            : "The record above already shows how the fight was decided. Narrate only what is listed; do not " +
              "add a death, surrender, escape or item change beyond it.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The deterministic "Ending" section text — composed entirely from engine state, never by the model, so
    /// it can never disagree with the terminal state whatever the model wrote above it.
    /// </summary>
    public string RenderEndingParagraph()
    {
        if (EndedUnresolvedAtRoundLimit)
        {
            var active = NamesWith(CharacterDisposition.Active);
            var activeText = active.Count == 0 ? "nobody" : string.Join(", ", active);
            var text = $"The conflict remained unresolved when observation stopped after {RoundsPlayed} round(s). " +
                       $"{TerminalCondition} Still standing and still fighting: {activeText}.";

            var partial = new List<string>();
            AppendGroup(partial, CharacterDisposition.Dead, "Killed before the record stopped");
            AppendGroup(partial, CharacterDisposition.Surrendered, "Surrendered before the record stopped");
            AppendGroup(partial, CharacterDisposition.Detained, "Still detained when the record stopped");
            AppendGroup(partial, CharacterDisposition.Escaped, "Escaped before the record stopped");
            return partial.Count == 0 ? text : text + " " + string.Join(" ", partial);
        }

        var parts = new List<string> { TerminalCondition };
        AppendGroup(parts, CharacterDisposition.Dead, "Killed");
        AppendGroup(parts, CharacterDisposition.Surrendered, "Surrendered");
        AppendGroup(parts, CharacterDisposition.Detained, "Detained");
        AppendGroup(parts, CharacterDisposition.Escaped, "Escaped");
        AppendGroup(parts, CharacterDisposition.Active, "Still standing");
        return string.Join(" ", parts);
    }

    private void AppendGroup(List<string> parts, CharacterDisposition disposition, string label)
    {
        var names = NamesWith(disposition);
        if (names.Count > 0)
        {
            parts.Add($"{label}: {string.Join(", ", names)}.");
        }
    }

    private List<string> NamesWith(CharacterDisposition disposition) =>
        FinalCharacterStates.Where(c => c.Disposition == disposition).Select(c => c.Name).ToList();
}

/// <summary>Builds an <see cref="EncounterStoryBrief"/> from a run's own trace and final state.</summary>
public static class EncounterStoryBriefBuilder
{
    /// <summary>Never trim below this many of the most recent events, so a trim never leaves a bare fragment.</summary>
    private const int MinimumRecentEventsKept = 4;

    /// <summary>
    /// Reads a run's trace.jsonl into the minimal records the brief needs — the production entry point. Mirrors
    /// <see cref="Tracing.RunReportWriter"/>'s own discipline of reading only from the run's written artefacts,
    /// never from a live simulation reference; <see cref="Build"/> itself takes the parsed records so it can be
    /// exercised directly against a fabricated event timeline in tests.
    /// </summary>
    /// <remarks>
    /// The story is generated before the run's own <see cref="Tracing.JsonlTraceSink"/> is disposed, so its
    /// writer handle (opened with <c>FileShare.Read</c>) is still open on this same file. <c>File.ReadLines</c>
    /// opens with a share mode that is not guaranteed compatible with a concurrently open writer and throws a
    /// sharing-violation <see cref="IOException"/>; opening explicitly with <see cref="FileShare.ReadWrite"/>
    /// here is what actually tolerates that still-open writer.
    /// </remarks>
    public static IReadOnlyList<EncounterEventRecord> ReadEvents(string tracePath)
    {
        var records = new List<EncounterEventRecord>();
        if (!File.Exists(tracePath))
        {
            return records;
        }

        using var stream = new FileStream(tracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }

            if (root.TryGetProperty("EventType", out var type) && root.TryGetProperty("Data", out var data))
            {
                records.Add(new EncounterEventRecord(type.GetString() ?? "", data.Clone()));
            }
        }

        return records;
    }

    /// <summary>
    /// Builds the brief. Only accepted actions are ever read — a rejected attempt never happened, so it must
    /// never appear as something the story can narrate. Kills are read directly off the lethal attack's own
    /// outcome (attacker/target names, <c>TargetDied</c>) rather than inferred from prose, so the story is
    /// never left to guess who struck the blow. Final dispositions and health come straight from the
    /// authoritative <paramref name="finalState"/>, not from the trace.
    /// </summary>
    public static EncounterStoryBrief Build(
        IReadOnlyList<EncounterEventRecord> events,
        string scenarioPremise,
        string roster,
        GameState finalState,
        string terminalCondition,
        EncounterOutcome outcome,
        int roundsPlayed,
        int contextWindow,
        double inputBudgetFraction)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(finalState);

        var chronological = new List<string>();
        var kills = new List<string>();

        foreach (var row in events)
        {
            if (!string.Equals(row.EventType, "EngineAction", StringComparison.Ordinal) || !IsTrue(row.Data, "Accepted"))
            {
                continue;
            }

            if (Text(row.Data, "OutcomeSummary") is { Length: > 0 } summary)
            {
                chronological.Add(summary);
            }

            if (TryGetProperty(row.Data, "Outcome", out var outcomeElement)
                && string.Equals(Text(outcomeElement, "OutcomeType"), "attack", StringComparison.Ordinal)
                && IsTrue(outcomeElement, "TargetDied"))
            {
                var attacker = Text(outcomeElement, "AttackerName") ?? "someone";
                var target = Text(outcomeElement, "TargetName") ?? "someone";
                kills.Add($"{attacker} killed {target}.");
            }
        }

        var (included, total) = Bound(chronological, contextWindow, inputBudgetFraction);

        var finalCharacterStates = finalState.Characters
            .Select(c => new CharacterFinalState(c.Name, c.Team, c.Disposition, c.Health, c.MaxHealth))
            .ToList();

        return new EncounterStoryBrief
        {
            ScenarioPremise = scenarioPremise,
            Roster = roster,
            ChronologicalEvents = included,
            EventsTotal = total,
            Kills = kills,
            FinalCharacterStates = finalCharacterStates,
            TerminalCondition = terminalCondition,
            Outcome = outcome,
            RoundsPlayed = roundsPlayed
        };
    }

    private static (List<string> Events, int Total) Bound(List<string> events, int contextWindow, double inputBudgetFraction)
    {
        var budgetTokens = Math.Max(256, (int)(contextWindow * Math.Clamp(inputBudgetFraction, 0.05, 1.0)));
        var budgetChars = ContextTruncation.EstimateCharactersForTokens(budgetTokens);

        var full = string.Join("\n", events);
        if (full.Length <= budgetChars || events.Count <= MinimumRecentEventsKept)
        {
            return (events, events.Count);
        }

        // Over budget: keep the most recent events, the same discipline EncounterTranscript applied to raw
        // narration — how the fight actually went, and how it ended, matters more than exhaustive coverage of
        // the opening moves. Always keep at least the minimum so a trim never leaves nothing.
        var kept = new List<string>();
        var used = 0;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var cost = events[i].Length + 1;
            if (used + cost > budgetChars && kept.Count >= MinimumRecentEventsKept)
            {
                break;
            }

            kept.Add(events[i]);
            used += cost;
        }

        kept.Reverse();
        return (kept, events.Count);
    }

    private static bool TryGetProperty(JsonElement data, string name, out JsonElement value)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? Text(JsonElement data, string name) =>
        TryGetProperty(data, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement data, string name) =>
        TryGetProperty(data, name, out var value) && value.ValueKind == JsonValueKind.True;
}
