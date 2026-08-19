using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ModelsAndMonsters.Tracing;

/// <summary>
/// Renders a run's artefacts into a single readable <c>report.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The report is built purely from <c>run.json</c>, <c>trace.jsonl</c> and <c>final-state.json</c> —
/// it holds no reference to the live simulation. That keeps the trace files honest (anything the
/// report can show, the artefacts already contain) and means any past run can be re-rendered.
/// </para>
/// <para>
/// Every trace row appears. Each is rendered as a readable summary with its complete raw JSON folded
/// into a collapsed block underneath, so nothing is lost to summarising.
/// </para>
/// </remarks>
public static class RunReportWriter
{
    /// <summary>
    /// Ceiling on the raw JSON reproduced per event.
    /// </summary>
    /// <remarks>
    /// Every model request carries the whole conversation so far, so reproducing them all in full makes
    /// the report grow with the square of the run length — a four-round run produced 11 MB. Oversized
    /// rows are truncated with a pointer to the exact line of trace.jsonl, which always holds the
    /// complete record.
    /// </remarks>
    private const int RawEventCharacterLimit = 3000;

    /// <summary>The full report: everything, including the exhaustive event-by-event trace dump.</summary>
    public static string ReportPath(string runDirectory) => Path.Combine(runDirectory, "report.md");

    /// <summary>The readable summary: the same report without the full per-event trace section.</summary>
    public static string SummaryReportPath(string runDirectory) => Path.Combine(runDirectory, "report-summary.md");

    /// <summary>Writes report.md (full, with the trace) for a run directory and returns its path.</summary>
    public static string Write(string runDirectory) => Write(runDirectory, includeFullTrace: true);

    /// <summary>
    /// Writes report-summary.md — everything the full report has except the event-by-event <c>## Trace</c>
    /// section — for a run directory, and returns its path. The transcript, per-agent activity and event
    /// census remain, so it reads as the story without the megabytes of raw events.
    /// </summary>
    public static string WriteSummary(string runDirectory) => Write(runDirectory, includeFullTrace: false);

    /// <summary>Writes both the full report and the summary, returning both paths.</summary>
    public static (string Full, string Summary) WriteAll(string runDirectory) =>
        (Write(runDirectory), WriteSummary(runDirectory));

    private static string Write(string runDirectory, bool includeFullTrace)
    {
        var tracePath = Path.Combine(runDirectory, "trace.jsonl");
        if (!File.Exists(tracePath))
        {
            throw new FileNotFoundException($"No trace.jsonl found in {runDirectory}.", tracePath);
        }

        var manifest = TryReadJson(Path.Combine(runDirectory, "run.json"));
        var finalState = TryReadJson(Path.Combine(runDirectory, "final-state.json"));
        var events = ReadEvents(tracePath);

        var report = new StringBuilder();
        WriteHeader(report, manifest, finalState, events, runDirectory);
        WriteOutcome(report, events, finalState);
        WritePerAgentActivity(report, events);
        WriteCommunicationAndObjects(report, events);
        WriteNonCombatOutcomes(report, events);
        WriteAlliedAttacks(report, events);
        WriteInventoryActivity(report, events, finalState);
        WriteRulebookConsultations(report, events);
        WriteContextHealth(report, events, manifest);
        WriteScenario(report, manifest);
        WriteTeams(report, manifest);
        WriteKnowledge(report, events, manifest, finalState);
        WriteExitState(report, manifest, finalState);
        WriteTranscript(report, events);
        if (includeFullTrace)
        {
            WriteTrace(report, events);
        }
        else
        {
            WriteTracePointer(report);
        }

        WriteFinalState(report, finalState);

        var path = includeFullTrace ? ReportPath(runDirectory) : SummaryReportPath(runDirectory);
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>In the summary, points to where the omitted per-event detail can be found.</summary>
    private static void WriteTracePointer(StringBuilder report)
    {
        report.AppendLine("## Full trace");
        report.AppendLine();
        report.AppendLine(
            "Omitted from this summary. Every event with its raw JSON is in `report.md`; the raw event " +
            "stream is in `trace.jsonl`.");
        report.AppendLine();
    }

    // -------------------------------------------------------------------------------------------
    // Run details
    // -------------------------------------------------------------------------------------------

    private static void WriteHeader(
        StringBuilder report,
        JsonElement? manifest,
        JsonElement? finalState,
        IReadOnlyList<TraceRow> events,
        string runDirectory)
    {
        var runId = Text(manifest, "RunId") ?? Path.GetFileName(runDirectory.TrimEnd(Path.DirectorySeparatorChar));

        report.AppendLine($"# Models & Monsters — run `{runId}`");
        report.AppendLine();
        report.AppendLine("Generated from `run.json`, `trace.jsonl` and `final-state.json`.");
        report.AppendLine();

        report.AppendLine("## Run details");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        Row(report, "Run ID", runId);
        Row(report, "Started", Text(manifest, "StartedAt"));
        Row(report, "Completed", Text(finalState, "CompletedAt"));
        Row(report, "Application version", Text(manifest, "ApplicationVersion"));
        Row(report, "Operating system", Text(manifest, "MachineOperatingSystem"));

        var master = Text(manifest, "Seeds", "MasterSeed");
        if (master is not null)
        {
            // The flag serialises as a JSON boolean, so match on the value kind rather than its text.
            var provided = IsTrue(manifest, "Seeds", "SeedWasProvided");
            Row(report, "Run seed", $"{master} ({(provided ? "fixed" : "random")})");
            Row(report, "Replay", $"set `ModelsAndMonsters:Harness:Seed` to `{master}`");
        }
        Row(report, "Scenario", Text(manifest, "Scenario", "Name") ?? Text(manifest, "Scenario", "Id"));
        Row(report, "Terminal condition", Text(finalState, "TerminalCondition"));
        Row(report, "Rounds played", Text(finalState, "RoundsPlayed"));
        Row(report, "Trace events", events.Count.ToString(CultureInfo.InvariantCulture));

        var tokens = SumTokens(events);
        if (tokens.Input + tokens.Output > 0)
        {
            Row(report, "Model calls", events.Count(e => e.EventType == "ModelResponse").ToString(CultureInfo.InvariantCulture));
            Row(report, "Total tokens",
                $"{tokens.Input + tokens.Output:N0} ({tokens.Input:N0} input / {tokens.Output:N0} output)");
            if (tokens.CacheRead > 0)
            {
                Row(report, "Cached input (read)", tokens.CacheRead.ToString("N0", CultureInfo.InvariantCulture));
            }

            if (tokens.CacheWrite > 0)
            {
                Row(report, "Cache writes", tokens.CacheWrite.ToString("N0", CultureInfo.InvariantCulture));
            }
        }

        report.AppendLine();

        WriteAgentProfiles(report, manifest);
        WriteKeyValueSection(report, "Harness limits", manifest, "Harness");
        WriteKeyValueSection(report, "Prompt versions", manifest, "PromptVersions");
        WriteEventCensus(report, events);
    }

    private static void WriteAgentProfiles(StringBuilder report, JsonElement? manifest)
    {
        if (!TryGet(manifest, out var profiles, "AgentProfiles") || profiles.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        report.AppendLine("## Model profiles");
        report.AppendLine();
        report.AppendLine("| Agent | Provider | Model | Temperature | Top P | Top K | Max tokens | Seed | Dropped by provider |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var agent in profiles.EnumerateObject())
        {
            var p = agent.Value;
            report.AppendLine(
                $"| {agent.Name} | {Scalar(p, "Provider")} | `{Scalar(p, "ModelId")}` | {Scalar(p, "Temperature")} | " +
                $"{Scalar(p, "TopP")} | {Scalar(p, "TopK")} | {Scalar(p, "MaxOutputTokens")} | {Scalar(p, "Seed")} | " +
                $"{JoinArray(p, "OptionsUnsupportedByProvider")} |");
        }

        report.AppendLine();
    }

    private static void WriteKeyValueSection(StringBuilder report, string title, JsonElement? manifest, string property)
    {
        if (!TryGet(manifest, out var section, property) || section.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        report.AppendLine($"## {title}");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        foreach (var entry in section.EnumerateObject())
        {
            Row(report, entry.Name, Flatten(entry.Value));
        }

        report.AppendLine();
    }

    /// <summary>
    /// Sums the token usage the providers reported across every model response, including cache reads and
    /// writes drawn from the usage's additional counts (OpenAI reports cached input automatically;
    /// Anthropic reports cache reads and cache-creation writes when prompt caching is used).
    /// </summary>
    private static (long Input, long Output, long CacheRead, long CacheWrite) SumTokens(IReadOnlyList<TraceRow> events)
    {
        long input = 0;
        long output = 0;
        long cacheRead = 0;
        long cacheWrite = 0;

        foreach (var row in events)
        {
            if (row.EventType != "ModelResponse")
            {
                continue;
            }

            input += LongField(row.Data, "Usage", "InputTokenCount");
            output += LongField(row.Data, "Usage", "OutputTokenCount");

            if (!TryGet(row.Data, out var additional, "Usage", "AdditionalCounts") || additional.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var count in additional.EnumerateObject())
            {
                if (count.Value.ValueKind != JsonValueKind.Number || !count.Value.TryGetInt64(out var value))
                {
                    continue;
                }

                var key = count.Name.ToLowerInvariant();
                if (!key.Contains("cache"))
                {
                    continue;
                }

                if (key.Contains("creation") || key.Contains("write"))
                {
                    cacheWrite += value;
                }
                else if (key.Contains("read") || key.Contains("cached"))
                {
                    cacheRead += value;
                }
            }
        }

        return (input, output, cacheRead, cacheWrite);
    }

    private static void WriteEventCensus(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        report.AppendLine("## Trace event census");
        report.AppendLine();
        report.AppendLine("| Event type | Count |");
        report.AppendLine("| --- | --- |");
        foreach (var group in events.GroupBy(e => e.EventType).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"| {group.Key} | {group.Count()} |");
        }

        report.AppendLine();
    }

    /// <summary>
    /// Per-agent activity, aggregated from the trace: how much each of the five agents did and cost.
    /// Questions, attempts, refusals, passes and skips come from the character-level events; model
    /// calls, tokens and latency from the model responses, so the Dungeon Master's cost is counted too.
    /// </summary>
    private static void WritePerAgentActivity(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var agents = new Dictionary<string, AgentActivity>(StringComparer.Ordinal);

        AgentActivity For(string? name) =>
            string.IsNullOrEmpty(name) ? new AgentActivity() : agents.TryGetValue(name, out var a) ? a : agents[name] = new AgentActivity();

        foreach (var row in events)
        {
            var d = row.Data;
            switch (row.EventType)
            {
                case "ModelResponse":
                {
                    var agent = For(Text(d, "AgentName"));
                    agent.ModelCalls++;
                    agent.InputTokens += LongField(d, "Usage", "InputTokenCount");
                    agent.OutputTokens += LongField(d, "Usage", "OutputTokenCount");
                    agent.LatencyMs += DoubleField(d, "ElapsedMilliseconds");
                    break;
                }

                case "TurnEnded":
                {
                    var agent = For(Text(d, "CharacterName"));
                    agent.Questions += (int)LongField(d, "QuestionsAsked");
                    agent.Attempts += (int)LongField(d, "ActionAttempts");
                    if (Text(d, "Result") == "EndedByCharacter")
                    {
                        agent.Passes++;
                    }

                    break;
                }

                case "TurnSkipped":
                    For(Text(d, "CharacterName")).Skips++;
                    break;

                case "CharacterSpeech":
                    For(Text(d, "SpeakerName")).Speeches++;
                    break;

                case "ToolCallRecovered":
                    For(Text(d, "AgentName")).Recovered++;
                    break;

                case "DmAdjudication":
                {
                    var agent = For(Text(d, "CharacterName"));
                    if (Text(d, "Category") == "EngineAccepted")
                    {
                        agent.AcceptedActions++;
                    }
                    else
                    {
                        agent.Rejections++;
                    }

                    break;
                }
            }
        }

        if (agents.Count == 0)
        {
            return;
        }

        var anyRecovered = agents.Values.Any(a => a.Recovered > 0);

        report.AppendLine("## Per-agent activity");
        report.AppendLine();
        var recoveredHeader = anyRecovered ? " Recovered |" : "";
        var recoveredDivider = anyRecovered ? " --- |" : "";
        report.AppendLine("| Agent | Model calls | Input tokens | Output tokens | Latency (ms) | Questions | Attempts | Accepted | Rejected | Speeches | Passes | Skips |" + recoveredHeader);
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |" + recoveredDivider);
        foreach (var (name, a) in agents.OrderByDescending(kv => kv.Value.ModelCalls).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var recoveredCell = anyRecovered ? $" {a.Recovered} |" : "";
            report.AppendLine(
                $"| {name} | {a.ModelCalls} | {a.InputTokens} | {a.OutputTokens} | {a.LatencyMs:N0} | " +
                $"{a.Questions} | {a.Attempts} | {a.AcceptedActions} | {a.Rejections} | {a.Speeches} | {a.Passes} | {a.Skips} |" + recoveredCell);
        }

        var t = agents.Values;
        var totalRecoveredCell = anyRecovered ? $" {t.Sum(a => a.Recovered)} |" : "";
        report.AppendLine(
            $"| **Total** | {t.Sum(a => a.ModelCalls)} | {t.Sum(a => a.InputTokens)} | {t.Sum(a => a.OutputTokens)} | " +
            $"{t.Sum(a => a.LatencyMs):N0} | {t.Sum(a => a.Questions)} | {t.Sum(a => a.Attempts)} | " +
            $"{t.Sum(a => a.AcceptedActions)} | {t.Sum(a => a.Rejections)} | {t.Sum(a => a.Speeches)} | {t.Sum(a => a.Passes)} | {t.Sum(a => a.Skips)} |" + totalRecoveredCell);

        report.AppendLine();
        if (anyRecovered)
        {
            report.AppendLine(
                "*Recovered = tool calls the model wrote as prose that the harness parsed and dispatched " +
                "(`RecoverTextToolCalls`). The model did not make these as structured calls.*");
            report.AppendLine();
        }
    }

    private sealed class AgentActivity
    {
        public int ModelCalls;
        public long InputTokens;
        public long OutputTokens;
        public double LatencyMs;
        public int Questions;
        public int Attempts;
        public int AcceptedActions;
        public int Rejections;
        public int Speeches;
        public int Passes;
        public int Skips;
        public int Recovered;
    }

    /// <summary>
    /// Attempt / acceptance / rejection counts for the two object actions, and the per-character speech
    /// tally, drawn from the object-interaction and speech trace rows. Skipped entirely when a run had no
    /// object interactions and no speech, so runs without the v0.3 features add no empty section.
    /// </summary>
    private static void WriteCommunicationAndObjects(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var objectEvents = events.Where(e => e.EventType == "ObjectInteraction").ToList();
        var speechEvents = events.Where(e => e.EventType == "CharacterSpeech").ToList();
        var speechAttempts = events.Where(e => e.EventType == "UnstructuredSpeechAttempt").ToList();

        if (objectEvents.Count == 0 && speechEvents.Count == 0 && speechAttempts.Count == 0)
        {
            return;
        }

        report.AppendLine("## Communication and object interaction");
        report.AppendLine();

        report.AppendLine($"Public utterances: **{speechEvents.Count}**.");
        report.AppendLine();

        if (speechAttempts.Count > 0)
        {
            // So a reader never concludes a character stayed silent when it in fact tried to speak in prose.
            report.AppendLine(
                $"Unstructured speech attempts (written as prose, not via `say`; recorded, not delivered): " +
                $"**{speechAttempts.Count}**.");
            report.AppendLine();
            report.AppendLine("| Character | Attempts |");
            report.AppendLine("| --- | --- |");
            foreach (var group in speechAttempts
                .GroupBy(e => Text(e.Data, "CharacterName") ?? "")
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"| {group.Key} | {group.Count()} |");
            }

            report.AppendLine();
        }

        if (speechEvents.Count > 0)
        {
            report.AppendLine("| Speaker | Utterances |");
            report.AppendLine("| --- | --- |");
            foreach (var group in speechEvents
                .GroupBy(e => Text(e.Data, "SpeakerName") ?? "")
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"| {group.Key} | {group.Count()} |");
            }

            report.AppendLine();
        }

        report.AppendLine("| Object action | Attempts | Accepted | Rejected |");
        report.AppendLine("| --- | --- | --- | --- |");
        foreach (var action in new[] { "open_container", "take_item" })
        {
            var rows = objectEvents.Where(e => Text(e.Data, "ActionType") == action).ToList();
            var accepted = rows.Count(e => Text(e.Data, "ValidationResult") == "accepted");
            report.AppendLine($"| `{action}` | {rows.Count} | {accepted} | {rows.Count - accepted} |");
        }

        var totalAccepted = objectEvents.Count(e => Text(e.Data, "ValidationResult") == "accepted");
        report.AppendLine($"| **Total** | {objectEvents.Count} | {totalAccepted} | {objectEvents.Count - totalAccepted} |");
        report.AppendLine();
    }

    /// <summary>
    /// The headline outcome: who won, how the encounter was classified, and — split by disposition — what
    /// became of every character. Distinguishing the dead from the surrendered and the escaped is the point
    /// of v0.5, so none of the three is ever collapsed into "fallen". Skipped when neither a final team
    /// evaluation nor a run-completed event was recorded.
    /// </summary>
    private static void WriteOutcome(StringBuilder report, IReadOnlyList<TraceRow> events, JsonElement? finalState)
    {
        var final = events.LastOrDefault(e => e.EventType == "TeamOutcomeEvaluated" && Text(e.Data, "Trigger") == "final");
        var completed = events.LastOrDefault(e => e.EventType == "RunCompleted");
        if (final is null && completed is null)
        {
            return;
        }

        var classification = (final is null ? null : Text(final.Data, "Outcome"))
            ?? (completed is null ? null : Text(completed.Data, "Outcome"))
            ?? "unknown";
        var winners = final is not null ? JoinArray(final.Data, "WinningTeams")
            : completed is not null ? JoinArray(completed.Data, "WinningTeams") : "";
        var terminal = completed is not null ? Text(completed.Data, "TerminalCondition")
            : final is not null ? Text(final.Data, "Description") : "";

        report.AppendLine("## Outcome");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        Row(report, "Result", terminal);
        Row(report, "Winner", string.IsNullOrWhiteSpace(winners) ? "none — no team held the field" : winners);
        Row(report, "Classification", classification);
        report.AppendLine();

        // The per-disposition breakdown, read from the authoritative final state so it is always exact.
        if (TryGet(finalState, out var state, "State"))
        {
            var byDisposition = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Characters(state))
            {
                var disposition = Scalar(c, "Disposition");
                (byDisposition.TryGetValue(disposition, out var list) ? list : byDisposition[disposition] = []).Add(Scalar(c, "Name"));
            }

            string Names(string disposition) =>
                byDisposition.TryGetValue(disposition, out var list) && list.Count > 0 ? string.Join(", ", list) : "(none)";

            report.AppendLine($"- **Killed:** {Names("Dead")}");
            report.AppendLine($"- **Surrendered:** {Names("Surrendered")}");
            report.AppendLine($"- **Escaped:** {Names("Escaped")}");
            report.AppendLine($"- **Still active:** {Names("Active")}");
            report.AppendLine();
        }

        // The per-character account of who left the fight and how, one bullet each, taken from the deciding
        // evaluation's structured resolutions (not the flattened summary string, whose newlines are stripped
        // for table safety).
        if (final is not null && Property(final.Data, "Resolutions") is { ValueKind: JsonValueKind.Array } resolutions
            && resolutions.GetArrayLength() > 0)
        {
            report.AppendLine("How each character left active combat:");
            report.AppendLine();
            foreach (var resolution in resolutions.EnumerateArray())
            {
                report.AppendLine($"- {Scalar(resolution, "Summary")}");
            }

            report.AppendLine();
        }
    }

    /// <summary>
    /// Per-character counts of the v0.5 non-combat actions — surrender, exit-opening and escape — with
    /// attempts and acceptances separated, drawn deterministically from the engine-action rows. Skipped
    /// when a run had none of them, so pre-v0.5 runs add no empty section. Persuasive or threatening speech
    /// is deliberately NOT tallied here: it cannot be classified deterministically and is reported as
    /// ordinary public speech above.
    /// </summary>
    private static void WriteNonCombatOutcomes(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var actions = new[] { "surrender", "open_exit", "escape_encounter" };
        var rows = events
            .Where(e => e.EventType == "EngineAction" && actions.Contains(Text(e.Data, "ActionType")))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var tallies = new Dictionary<string, NonCombatTally>(StringComparer.Ordinal);
        NonCombatTally For(string name) =>
            tallies.TryGetValue(name, out var t) ? t : tallies[name] = new NonCombatTally();

        foreach (var row in rows)
        {
            var actor = Text(row.Data, "Action", "ActorRef") ?? "(unknown)";
            var accepted = IsTrue(row.Data, "Accepted");
            var tally = For(actor);
            switch (Text(row.Data, "ActionType"))
            {
                case "surrender":
                    tally.SurrenderAttempts++;
                    if (accepted) tally.Surrenders++;
                    break;
                case "open_exit":
                    tally.ExitOpenAttempts++;
                    if (accepted) tally.ExitOpenings++;
                    break;
                case "escape_encounter":
                    tally.EscapeAttempts++;
                    if (accepted) tally.Escapes++;
                    break;
            }
        }

        report.AppendLine("## Non-combat outcomes");
        report.AppendLine();
        report.AppendLine("Surrender, exit-opening and escape, per character. \"Attempts\" counts every try reaching the engine; the acceptance columns count those the engine applied.");
        report.AppendLine();
        report.AppendLine("| Character | Surrender attempts | Surrenders | Escape attempts | Escapes | Exit-open attempts | Exit openings |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var (name, t) in tallies.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            report.AppendLine(
                $"| {name} | {t.SurrenderAttempts} | {t.Surrenders} | {t.EscapeAttempts} | {t.Escapes} | {t.ExitOpenAttempts} | {t.ExitOpenings} |");
        }

        var all = tallies.Values;
        report.AppendLine(
            $"| **Total** | {all.Sum(t => t.SurrenderAttempts)} | {all.Sum(t => t.Surrenders)} | " +
            $"{all.Sum(t => t.EscapeAttempts)} | {all.Sum(t => t.Escapes)} | " +
            $"{all.Sum(t => t.ExitOpenAttempts)} | {all.Sum(t => t.ExitOpenings)} |");
        report.AppendLine();
    }

    private sealed class NonCombatTally
    {
        public int SurrenderAttempts;
        public int Surrenders;
        public int EscapeAttempts;
        public int Escapes;
        public int ExitOpenAttempts;
        public int ExitOpenings;
    }

    /// <summary>
    /// Allied attacks — a character aiming a blow at its own side. Friendly fire is deliberately permitted (the
    /// world resolves the strike a character actually described and does not second-guess a confused actor), so
    /// this is a coherence metric for the experiment, not a rule breach. Each row carries the stated intent so
    /// the motivation — incoherent ally-target slip, or deliberate betrayal — can be inspected. A no-op when
    /// none occurred.
    /// </summary>
    private static void WriteAlliedAttacks(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var allied = events
            .Where(e => e.EventType == "TargetResolved" && IsTrue(e.Data, "TargetIsAlly"))
            .ToList();
        if (allied.Count == 0)
        {
            return;
        }

        // The stated motivation for each attack, keyed by (attacker, target) from the adjudication.
        var intents = new Dictionary<(string Attacker, string Target), string>();
        foreach (var row in events.Where(e => e.EventType == "DmAdjudication"))
        {
            if (!string.Equals(Text(row.Data, "TranslatedAction", "ActionType"), "attack_character", StringComparison.Ordinal))
            {
                continue;
            }

            var attacker = Text(row.Data, "CharacterName") ?? "";
            var target = Text(row.Data, "TranslatedAction", "TargetRef") ?? "";
            intents[(attacker, target)] = Text(row.Data, "Intent") ?? "";
        }

        report.AppendLine("## Allied attacks");
        report.AppendLine();
        report.AppendLine(
            "Blows a character aimed at its own side. Friendly fire is deliberately permitted — the world resolves the " +
            "strike a character described and never redirects it — so this is a coherence metric, not a rule breach. " +
            "Inspect the stated intent to tell an incoherent ally-target slip from a deliberate betrayal.");
        report.AppendLine();
        report.AppendLine("| Attacker | Ally targeted | Stated intent |");
        report.AppendLine("| --- | --- | --- |");
        foreach (var row in allied)
        {
            var attacker = Text(row.Data, "AttackerName") ?? "(unknown)";
            var target = Text(row.Data, "ResolvedTargetName") ?? "(unknown)";
            var intent = intents.TryGetValue((attacker, target), out var i) ? i : "";
            report.AppendLine($"| {attacker} | {target} | {CellText(intent)} |");
        }

        report.AppendLine();
        report.AppendLine($"**Total allied attacks: {allied.Count}.**");
        report.AppendLine();
    }

    /// <summary>Sanitises free text for a Markdown table cell: single line, escaped pipes, bounded length.</summary>
    private static string CellText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var single = text.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        return single.Length <= 140 ? single : single[..137] + "…";
    }

    // -------------------------------------------------------------------------------------------
    // Inventory activity (v0.6): gives, drops and thefts, the final ownership, and the item journeys
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The v0.6 inventory transfers: give/drop/steal counts, theft success and failure, the final ownership
    /// and location of every item, and each item's provenance timeline reconstructed from the movement events.
    /// Skipped when a run had no inventory activity, so pre-v0.6 runs add no empty section.
    /// </summary>
    private static void WriteInventoryActivity(StringBuilder report, IReadOnlyList<TraceRow> events, JsonElement? finalState)
    {
        var interactions = events.Where(e => e.EventType == "InventoryInteraction").ToList();
        var provenance = events.Where(e => e.EventType == "ItemProvenance").ToList();
        if (interactions.Count == 0 && provenance.Count == 0)
        {
            return;
        }

        report.AppendLine("## Inventory activity");
        report.AppendLine();

        int gives = 0, giveOk = 0, drops = 0, dropOk = 0, thefts = 0, theftOk = 0, theftFail = 0;
        foreach (var row in interactions)
        {
            var accepted = string.Equals(Text(row.Data, "ValidationResult"), "accepted", StringComparison.Ordinal);
            switch (Text(row.Data, "ActionType"))
            {
                case "give_item": gives++; if (accepted) giveOk++; break;
                case "drop_item": drops++; if (accepted) dropOk++; break;
                case "steal_item":
                    thefts++;
                    if (IsTrue(row.Data, "TheftSucceeded")) theftOk++;
                    else if (accepted) theftFail++;
                    break;
            }
        }

        report.AppendLine("| Action | Attempts | Succeeded | Failed/refused |");
        report.AppendLine("| --- | --- | --- | --- |");
        report.AppendLine($"| Gives | {gives} | {giveOk} | {gives - giveOk} |");
        report.AppendLine($"| Drops | {drops} | {dropOk} | {drops - dropOk} |");
        report.AppendLine($"| Theft attempts | {thefts} | {theftOk} | {thefts - theftOk} |");
        report.AppendLine();
        report.AppendLine($"Of {thefts} theft attempt(s), {theftOk} succeeded and {theftFail} were noticed but failed on the roll; the rest were refused before any roll.");
        report.AppendLine();

        WriteFinalItemOwnership(report, finalState);
        WriteItemProvenanceTimeline(report, provenance);
    }

    /// <summary>The final resting place of every item: in an inventory, in a container, or on the floor.</summary>
    private static void WriteFinalItemOwnership(StringBuilder report, JsonElement? finalState)
    {
        if (!TryGet(finalState, out var state, "State"))
        {
            return;
        }

        var lines = new List<string>();

        if (TryGet(state, out var characters, "Characters") && characters.ValueKind == JsonValueKind.Array)
        {
            foreach (var character in characters.EnumerateArray())
            {
                if (Property(character, "Inventory") is { ValueKind: JsonValueKind.Array } inv)
                {
                    foreach (var item in inv.EnumerateArray())
                    {
                        lines.Add($"| {Scalar(item, "Name")} | carried by {Scalar(character, "Name")} |");
                    }
                }
            }
        }

        if (TryGet(state, out var room, "Room") && Property(room, "Objects") is { ValueKind: JsonValueKind.Array } objects)
        {
            foreach (var obj in objects.EnumerateArray())
            {
                var where = IsTrue(obj, "IsGround") ? "on the floor" : $"in {Scalar(obj, "Name")}";
                if (Property(obj, "Contents") is { ValueKind: JsonValueKind.Array } contents)
                {
                    foreach (var item in contents.EnumerateArray())
                    {
                        lines.Add($"| {Scalar(item, "Name")} | {where} |");
                    }
                }
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        report.AppendLine("**Final item ownership and location**");
        report.AppendLine();
        report.AppendLine("| Item | Location |");
        report.AppendLine("| --- | --- |");
        foreach (var line in lines)
        {
            report.AppendLine(line);
        }

        report.AppendLine();
    }

    /// <summary>Each item's journey, in order: the movements recorded as provenance events.</summary>
    private static void WriteItemProvenanceTimeline(StringBuilder report, IReadOnlyList<TraceRow> provenance)
    {
        if (provenance.Count == 0)
        {
            return;
        }

        report.AppendLine("**Item provenance timeline**");
        report.AppendLine();
        report.AppendLine("| Round.Turn | Item | Movement | From | To | RNG |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var row in provenance.OrderBy(r => r.Sequence))
        {
            var rng = IsTrue(row.Data, "RngInvolved") ? "yes" : "no";
            report.AppendLine(
                $"| {row.Round}.{row.Turn} | {Text(row.Data, "ItemName")} | {Text(row.Data, "ActionType")} | " +
                $"{Text(row.Data, "PreviousOwnerOrLocation")} | {Text(row.Data, "NewOwnerOrLocation")} | {rng} |");
        }

        report.AppendLine();
    }

    // -------------------------------------------------------------------------------------------
    // Rulebook consultations (v0.6): the bounded resolver stage
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The rulebook-resolution stage: consultation and cache counts, cards retrieved and cited, supported vs
    /// unsupported intents, failures by stage, resolver tokens and latency, and how often the DM or engine
    /// still rejected an intent the rulebook supported. Skipped when the run consulted no rulebook.
    /// </summary>
    private static void WriteRulebookConsultations(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var consultations = events.Where(e => e.EventType == "RulebookConsultation").ToList();
        if (consultations.Count == 0)
        {
            return;
        }

        int supported = 0, unsupported = 0, resolverFail = 0, malformed = 0, retrievalFail = 0, cacheHits = 0;
        long inTokens = 0, outTokens = 0, cardsSum = 0, citedSum = 0;
        double latencySum = 0;
        foreach (var row in consultations)
        {
            switch (Text(row.Data, "Outcome"))
            {
                case "Supported": supported++; break;
                case "Unsupported": unsupported++; break;
                case "ResolverFailure": resolverFail++; break;
                case "MalformedGuidance": malformed++; break;
                case "RetrievalFailure": retrievalFail++; break;
            }

            if (IsTrue(row.Data, "CacheHit")) cacheHits++;
            inTokens += LongField(row.Data, "InputTokens");
            outTokens += LongField(row.Data, "OutputTokens");
            cardsSum += LongField(row.Data, "CardCount");
            latencySum += DoubleField(row.Data, "LatencyMs");
            if (Property(row.Data, "CitedRules") is { ValueKind: JsonValueKind.Array } cited)
            {
                citedSum += cited.GetArrayLength();
            }
        }

        // DM-to-engine rejection rate after supported guidance: join by consultation id to the adjudication.
        var adjudicationByConsultation = events
            .Where(e => e.EventType == "DmAdjudication")
            .Select(e => (Id: Text(e.Data, "ConsultationId"), Category: Text(e.Data, "Category")))
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .GroupBy(x => x.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Category, StringComparer.Ordinal);

        var supportedIds = consultations
            .Where(r => string.Equals(Text(r.Data, "Outcome"), "Supported", StringComparison.Ordinal))
            .Select(r => Text(r.Data, "ConsultationId"))
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();
        var rejectedAfterSupport = supportedIds.Count(id =>
            adjudicationByConsultation.TryGetValue(id!, out var cat) &&
            cat is "EngineRejected" or "DmUnsupported" or "DmImpossible");

        var count = consultations.Count;
        report.AppendLine("## Rulebook consultations");
        report.AppendLine();
        report.AppendLine($"- Consultations: **{count}** (one per adjudicated intent).");
        report.AppendLine($"- Cache: {cacheHits} hit(s), {count - cacheHits} miss(es).");
        report.AppendLine($"- Outcomes: {supported} supported, {unsupported} unsupported.");
        report.AppendLine($"- Failures by stage: {retrievalFail} retrieval, {resolverFail} resolver-model, {malformed} malformed-guidance.");
        report.AppendLine($"- Cards: {Average(cardsSum, count)} sent per consultation on average; {Average(citedSum, count)} cited.");
        report.AppendLine($"- Resolver tokens: {inTokens} in / {outTokens} out (total). Average latency: {(count == 0 ? 0 : latencySum / count):F0} ms.");
        report.AppendLine($"- After the rulebook supported an intent, the DM or engine still refused it {rejectedAfterSupport} of {supportedIds.Count} time(s) — the engine remaining authoritative over the guidance.");
        report.AppendLine();
    }

    // -------------------------------------------------------------------------------------------
    // Context health (v0.6): request sizes against the configured limits
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Request-size health: the maximum Dungeon Master and Rulebook Resolver request sizes against their
    /// configured limits, and evidence that the resolver request stays bounded and does not grow with the
    /// encounter. Skipped when neither a DM adjudication nor a rulebook consultation was recorded.
    /// </summary>
    private static void WriteContextHealth(StringBuilder report, IReadOnlyList<TraceRow> events, JsonElement? manifest)
    {
        var consultations = events.Where(e => e.EventType == "RulebookConsultation").ToList();
        var dmRequests = events
            .Where(e => e.EventType == "ModelRequest"
                        && string.Equals(Text(e.Data, "AgentName"), "DungeonMaster", StringComparison.Ordinal)
                        && (Text(e.Data, "Purpose") ?? "").StartsWith("dm.adjudicate", StringComparison.Ordinal))
            .ToList();

        if (consultations.Count == 0 && dmRequests.Count == 0)
        {
            return;
        }

        report.AppendLine("## Context health");
        report.AppendLine();

        var contextWindow = LongField(manifest, "AgentProfiles", "DungeonMaster", "ContextWindow");
        if (dmRequests.Count > 0)
        {
            var maxDm = dmRequests.Max(r => RequestChars(r.Data));
            report.AppendLine($"- Maximum DM adjudication request: **~{maxDm} chars**" +
                (contextWindow > 0 ? $" (against a ~{contextWindow}-token context window)." : "."));
        }

        if (consultations.Count > 0)
        {
            var maxResolver = consultations.Max(r => LongField(r.Data, "TotalRequestChars"));
            var limit = consultations.Max(r => LongField(r.Data, "MaxInputCharsConfigured"));
            var anyTrimmed = consultations.Any(r => IsTrue(r.Data, "Trimmed"));
            report.AppendLine($"- Maximum Rulebook Resolver request: **~{maxResolver} chars** (card budget {limit} chars). Cards trimmed to fit on {consultations.Count(r => IsTrue(r.Data, "Trimmed"))} consultation(s).");
            report.AppendLine($"- Any request approached or exceeded its configured limit: {(anyTrimmed ? "yes — see the trimmed consultations above" : "no")}.");

            // Independence from encounter length: resolver request size by round should stay flat.
            var byRound = consultations
                .GroupBy(r => r.Round)
                .OrderBy(g => g.Key)
                .Select(g => (Round: g.Key, Max: g.Max(r => LongField(r.Data, "TotalRequestChars"))))
                .ToList();
            if (byRound.Count > 1)
            {
                report.AppendLine();
                report.AppendLine("Resolver request size by round (evidence it does not grow with the encounter):");
                report.AppendLine();
                report.AppendLine("| Round | Max resolver request (chars) |");
                report.AppendLine("| --- | --- |");
                foreach (var (round, max) in byRound)
                {
                    report.AppendLine($"| {round} | ~{max} |");
                }
            }
        }

        report.AppendLine();
    }

    /// <summary>A rough character size of a model request: the text of every message and its contents.</summary>
    private static long RequestChars(JsonElement? data)
    {
        if (!TryGet(data, out var messages, "Messages") || messages.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        long total = 0;
        foreach (var message in messages.EnumerateArray())
        {
            total += (Scalar(message, "Text")).Length;
            if (Property(message, "Contents") is { ValueKind: JsonValueKind.Array } contents)
            {
                foreach (var content in contents.EnumerateArray())
                {
                    total += (Scalar(content, "Text")).Length;
                }
            }
        }

        return total;
    }

    private static string Average(long sum, int count) => count == 0 ? "0" : (sum / (double)count).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Team membership, taken from the recorded initial state so it is always present.</summary>
    private static void WriteTeams(StringBuilder report, JsonElement? manifest)
    {
        if (!TryGet(manifest, out var state, "InitialState") ||
            !state.TryGetProperty("Characters", out var characters) ||
            characters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var teams = characters.EnumerateArray()
            .GroupBy(c => Scalar(c, "Team"), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (teams.Count == 0)
        {
            return;
        }

        report.AppendLine("## Teams");
        report.AppendLine();
        report.AppendLine("| Team | Members |");
        report.AppendLine("| --- | --- |");
        foreach (var team in teams)
        {
            var members = team.Select(c => $"{Scalar(c, "Name")} ({Scalar(c, "Role")})");
            report.AppendLine($"| {team.Key} | {string.Join(", ", members)} |");
        }

        report.AppendLine();
    }

    // -------------------------------------------------------------------------------------------
    // Knowledge (v0.4): objective truth, the discovery timeline, and per-character knowledge
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Reconstructs, from the trace and state artefacts alone, the objective container truth and who knew
    /// what and when — keeping first-hand knowledge separate from hearsay. Skipped when a run had no
    /// containers and no knowledge activity, so pre-v0.4 runs add no empty sections.
    /// </summary>
    private static void WriteKnowledge(StringBuilder report, IReadOnlyList<TraceRow> events, JsonElement? manifest, JsonElement? finalState)
    {
        var learned = events.Where(e => e.EventType == "KnowledgeFactLearned").ToList();
        var hasContainers = TryGet(manifest, out var initState, "InitialState") && Containers(initState).Any();

        if (!hasContainers && learned.Count == 0)
        {
            return;
        }

        WriteObjectiveContainerState(report, manifest, finalState);

        if (learned.Count == 0)
        {
            return;
        }

        WriteKnowledgeTimeline(report, learned);
        WritePerCharacterKnowledge(report, events, learned, manifest);
    }

    private static void WriteObjectiveContainerState(StringBuilder report, JsonElement? manifest, JsonElement? finalState)
    {
        var initial = new List<(string Id, string Name, string Contents, bool Open)>();
        if (TryGet(manifest, out var initState, "InitialState"))
        {
            foreach (var c in Containers(initState))
            {
                initial.Add((Scalar(c, "Id"), Scalar(c, "Name"), NamesOf(c, "Contents"), IsOpenContainer(c)));
            }
        }

        if (initial.Count == 0)
        {
            return;
        }

        var final = new Dictionary<string, (string Contents, bool Open)>(StringComparer.OrdinalIgnoreCase);
        if (TryGet(finalState, out var fs, "State"))
        {
            foreach (var c in Containers(fs))
            {
                final[Scalar(c, "Id")] = (NamesOf(c, "Contents"), IsOpenContainer(c));
            }
        }

        report.AppendLine("## Objective container state");
        report.AppendLine();
        report.AppendLine("The authoritative truth, independent of who knew it: each container's initial and final contents.");
        report.AppendLine();
        report.AppendLine("| Container | Initial contents | Final state | Final contents |");
        report.AppendLine("| --- | --- | --- | --- |");
        foreach (var (id, name, contents, _) in initial)
        {
            var f = final.TryGetValue(id, out var v) ? v : (Contents: "unknown", Open: false);
            report.AppendLine($"| {name} | {contents} | {(f.Open ? "open" : "closed")} | {f.Contents} |");
        }

        report.AppendLine();
    }

    private static void WriteKnowledgeTimeline(StringBuilder report, IReadOnlyList<TraceRow> learned)
    {
        report.AppendLine("## Knowledge timeline");
        report.AppendLine();
        report.AppendLine("When each fact was discovered, by whom, and how — in order. A public event is learned by everyone alive at once.");
        report.AppendLine();

        // Collapse same-moment learns of the same fact into one line (a public removal is learned by all).
        var order = new List<(int Round, string FactId, string Source)>();
        var who = new Dictionary<(int, string, string), List<string>>();
        var meta = new Dictionary<(int, string, string), (string Description, string Visibility, string Wv)>();

        foreach (var row in learned)
        {
            var key = ((int)LongField(row.Data, "Round"), Text(row.Data, "FactId") ?? "", Text(row.Data, "Source") ?? "");
            if (!who.TryGetValue(key, out var names))
            {
                who[key] = names = [];
                order.Add(key);
                meta[key] = (Text(row.Data, "Description") ?? "", Text(row.Data, "Visibility") ?? "private",
                    Text(row.Data, "ObservedWorldVersion") ?? "0");
            }

            var name = Text(row.Data, "CharacterName") ?? "";
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        foreach (var key in order)
        {
            var names = string.Join(", ", who[key]);
            var m = meta[key];
            var delivered = m.Visibility == "public" ? $"everyone alive ({names})" : $"{names} only";
            var when = key.Round > 0 ? $"Round {key.Round}" : "Backstory";
            report.AppendLine($"- **{when}** — {names} learned via `{key.Source}`: {m.Description} *(world version {m.Wv}; delivered to {delivered})*");
        }

        report.AppendLine();
    }

    private static void WritePerCharacterKnowledge(StringBuilder report, IReadOnlyList<TraceRow> events, IReadOnlyList<TraceRow> learned, JsonElement? manifest)
    {
        var characters = new List<(string Id, string Name)>();
        if (TryGet(manifest, out var initState, "InitialState"))
        {
            foreach (var c in Characters(initState))
            {
                characters.Add((Scalar(c, "Id"), Scalar(c, "Name")));
            }
        }

        if (characters.Count == 0)
        {
            return;
        }

        report.AppendLine("## Per-character knowledge");
        report.AppendLine();
        report.AppendLine("What each character knew — first-hand knowledge kept strictly separate from what they only heard.");
        report.AppendLine();

        var speech = events.Where(e => e.EventType == "CharacterSpeech").ToList();

        foreach (var (id, name) in characters)
        {
            report.AppendLine($"### {name}");
            report.AppendLine();

            var mine = learned
                .Where(r => string.Equals(Text(r.Data, "CharacterId"), id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (mine.Count == 0)
            {
                report.AppendLine("*Directly knew nothing beyond what anyone in the room could plainly see.*");
            }
            else
            {
                report.AppendLine("**Directly knew (first-hand):**");
                report.AppendLine();
                report.AppendLine("| Fact | Source | Learned | Observed world version |");
                report.AppendLine("| --- | --- | --- | --- |");
                foreach (var r in mine)
                {
                    var round = (int)LongField(r.Data, "Round");
                    var when = round > 0 ? $"round {round}" : "backstory";
                    report.AppendLine(
                        $"| {Text(r.Data, "Description")} | {Text(r.Data, "Source")} | {when} | {Text(r.Data, "ObservedWorldVersion")} |");
                }
            }

            report.AppendLine();

            var heard = speech.Where(s => RecipientsContain(s.Data, id)).ToList();
            if (heard.Count > 0)
            {
                report.AppendLine("**Only heard others say (hearsay — not verified first-hand):**");
                report.AppendLine();
                foreach (var s in heard)
                {
                    report.AppendLine($"- {Text(s.Data, "SpeakerName")} said: \"{Text(s.Data, "Message")}\"");
                }

                report.AppendLine();
            }
        }
    }

    /// <summary>
    /// The room's exits: their opening state, and who escaped through each. Reconstructed from the initial
    /// and final authoritative state so it is independent of any narration. Skipped when the scenario seeded
    /// no exit, so pre-v0.5 runs add no empty section.
    /// </summary>
    private static void WriteExitState(StringBuilder report, JsonElement? manifest, JsonElement? finalState)
    {
        var initial = new List<(string Id, string Name, bool Open, string Destination)>();
        if (TryGet(manifest, out var initState, "InitialState"))
        {
            foreach (var e in Exits(initState))
            {
                initial.Add((Scalar(e, "Id"), Scalar(e, "Name"), IsOpenExit(e), Scalar(e, "DestinationDescription")));
            }
        }

        if (initial.Count == 0)
        {
            return;
        }

        var finalOpen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var escapers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (TryGet(finalState, out var fs, "State"))
        {
            foreach (var e in Exits(fs))
            {
                finalOpen[Scalar(e, "Id")] = IsOpenExit(e);
            }

            foreach (var c in Characters(fs))
            {
                var exitId = Text(c, "EscapedThroughExitId");
                if (!string.IsNullOrEmpty(exitId))
                {
                    (escapers.TryGetValue(exitId, out var list) ? list : escapers[exitId] = []).Add(Scalar(c, "Name"));
                }
            }
        }

        report.AppendLine("## Exit state");
        report.AppendLine();
        report.AppendLine("Each way out of the room: its initial and final state, and who left through it.");
        report.AppendLine();
        report.AppendLine("| Exit | Leads to | Initial state | Final state | Escaped through it |");
        report.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var (id, name, open, destination) in initial)
        {
            var finalStateText = finalOpen.TryGetValue(id, out var f) ? (f ? "open" : "closed") : (open ? "open" : "closed");
            var who = escapers.TryGetValue(id, out var names) && names.Count > 0 ? string.Join(", ", names) : "(nobody)";
            report.AppendLine($"| {name} | {destination} | {(open ? "open" : "closed")} | {finalStateText} | {who} |");
        }

        report.AppendLine();
    }

    private static IEnumerable<JsonElement> Containers(JsonElement state) =>
        state.TryGetProperty("Room", out var room)
        && room.TryGetProperty("Objects", out var objects)
        && objects.ValueKind == JsonValueKind.Array
            ? objects.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("IsOpen", out _))
            : [];

    private static bool IsOpenContainer(JsonElement container) =>
        container.TryGetProperty("IsOpen", out var open) && open.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> Exits(JsonElement state) =>
        state.TryGetProperty("Room", out var room)
        && room.TryGetProperty("Exits", out var exits)
        && exits.ValueKind == JsonValueKind.Array
            ? exits.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static bool IsOpenExit(JsonElement exit) =>
        exit.TryGetProperty("IsOpen", out var open) && open.ValueKind == JsonValueKind.True;

    private static bool RecipientsContain(JsonElement? data, string id) =>
        TryGet(data, out var array, "Recipients")
        && array.ValueKind == JsonValueKind.Array
        && array.EnumerateArray().Any(x =>
            x.ValueKind == JsonValueKind.String && string.Equals(x.GetString(), id, StringComparison.OrdinalIgnoreCase));

    private static void WriteScenario(StringBuilder report, JsonElement? manifest)
    {
        if (!TryGet(manifest, out var scenario, "Scenario"))
        {
            return;
        }

        report.AppendLine("## Scenario");
        report.AppendLine();

        if (Text(manifest, "Scenario", "Summary") is { Length: > 0 } summary)
        {
            report.AppendLine($"> {summary}");
            report.AppendLine();
        }

        if (scenario.TryGetProperty("Room", out var room))
        {
            report.AppendLine($"**{Scalar(room, "Name")}** — {Scalar(room, "Description")}");
            report.AppendLine();

            // Initial room objects and their authoritative contents. This is the authoritative scenario
            // section, so a closed container's contents are shown here even though characters cannot yet
            // see them.
            if (room.TryGetProperty("Containers", out var containers)
                && containers.ValueKind == JsonValueKind.Array
                && containers.GetArrayLength() > 0)
            {
                report.AppendLine("**Objects in the room**");
                report.AppendLine();
                report.AppendLine("| Container | State | Contents (authoritative) |");
                report.AppendLine("| --- | --- | --- |");
                foreach (var container in containers.EnumerateArray())
                {
                    var open = container.TryGetProperty("IsOpen", out var o) && o.ValueKind == JsonValueKind.True;
                    report.AppendLine(
                        $"| {Scalar(container, "Name")} | {(open ? "open" : "closed")} | {NamesOf(container, "Contents")} |");
                }

                report.AppendLine();
            }
        }

        if (scenario.TryGetProperty("Characters", out var characters) && characters.ValueKind == JsonValueKind.Array)
        {
            foreach (var character in characters.EnumerateArray())
            {
                report.AppendLine($"### {Scalar(character, "Name")} ({Scalar(character, "Role")})");
                report.AppendLine();
                report.AppendLine($"- Health `{Scalar(character, "MaxHealth")}` · Armour `{Scalar(character, "Armour")}`");
                if (character.TryGetProperty("Weapon", out var weapon) && weapon.ValueKind == JsonValueKind.Object)
                {
                    report.AppendLine($"- Weapon: {Scalar(weapon, "Name")} (damage `{Scalar(weapon, "Damage")}`)");
                }

                if (character.TryGetProperty("Persona", out var persona) && persona.ValueKind == JsonValueKind.Object)
                {
                    foreach (var trait in persona.EnumerateObject())
                    {
                        if (trait.Value.ValueKind == JsonValueKind.String)
                        {
                            report.AppendLine($"- {trait.Name}: {trait.Value.GetString()}");
                        }
                    }
                }

                report.AppendLine();
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // Readable transcript
    // -------------------------------------------------------------------------------------------

    /// <summary>The story as a person would read it, before the event-by-event detail.</summary>
    private static void WriteTranscript(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        report.AppendLine("## Transcript");
        report.AppendLine();

        var round = -1;
        foreach (var row in events)
        {
            if (row.Round != round && row.EventType == "RoundStarted")
            {
                round = row.Round;
                report.AppendLine($"### Round {round}");
                report.AppendLine();
            }

            var line = row.EventType switch
            {
                // A model's private reasoning, when it produced any, folded above the move it led to.
                "ModelResponse" => TranscribeReasoning(row),
                // The intent is transcribed where it was spoken, not where it was ruled on, so the
                // reader sees the character act before the engine and the DM respond to it.
                "ToolCallDispatched" => TranscribeIntent(row),
                "Narration" => $"**DM (public):** {Text(row.Data, "Narration")}",
                // Public speech, kept visually distinct from DM narration and from private questions.
                "CharacterSpeech" => $"**{Text(row.Data, "SpeakerName")} says (public):** \"{Text(row.Data, "Message")}\"",
                "CharacterQuestion" => $"**{Text(row.Data, "CharacterName")} asks (private):** \"{Text(row.Data, "Question")}\"",
                "DungeonMasterAnswer" => $"**DM (private to {Text(row.Data, "CharacterName")}):** {Text(row.Data, "Answer")}",
                // A private observation only the named character receives — an inspection result, or contents seen on opening.
                "PrivateObservationDelivered" => $"**DM (only {Text(row.Data, "RecipientName")} sees):** {Text(row.Data, "Observation")}",
                // A publicly observable fact everyone alive learns — a visibly carried-off item.
                "PublicFactDelivered" => $"*[in plain view of all: {Text(row.Data, "Fact")}]*",
                "CharacterPassed" => $"**{Text(row.Data, "CharacterName")} holds back:** \"{Text(row.Data, "Reason")}\"",
                "DmAdjudication" => TranscribeRuling(row),
                "TargetResolved" => $"*[target — {Text(row.Data, "Note")}]*",
                "RngDraw" =>
                    $"*[{Text(row.Data, "Purpose")}: rolled {Text(row.Data, "RawRoll")} vs {Text(row.Data, "Threshold")} " +
                    $"→ {Text(row.Data, "Result")}]*",
                "EngineAction" => TranscribeEngineAction(row),
                // A departure from active combat, in plain readable terms — never "fallen" for a survivor.
                "CharacterSurrendered" => $"*— {Text(row.Data, "CharacterName")} surrenders and takes no further part in the fight (still alive)*",
                "CharacterEscaped" => $"*— {Text(row.Data, "CharacterName")} escapes through the {Text(row.Data, "ExitName")} and leaves the encounter (still alive)*",
                "TurnSkipped" => TranscribeTurnSkipped(row),
                "TeamOutcomeEvaluated" => TranscribeTeamOutcome(row),
                "ContextWindowSaturated" =>
                    $"*[{Text(row.Data, "AgentName")} sent ~{Text(row.Data, "EstimatedSentTokens")} tokens but only " +
                    $"{Text(row.Data, "ReportedInputTokens")} were processed — ~{Text(row.Data, "EstimatedDroppedTokens")} " +
                    "tokens of earlier history were discarded before the model saw them]*",
                "ModelResponseTruncated" =>
                    $"*[{Text(row.Data, "AgentName")}'s reply hit the output-token limit — {Text(row.Data, "Effect")}]*",
                "AdjudicationCorrected" =>
                    $"*[harness corrected `{Text(row.Data, "Parameter")}` from \"{Text(row.Data, "DungeonMasterValue")}\" " +
                    $"to \"{Text(row.Data, "CorrectedValue")}\"]*",
                "ToolCallRecovered" =>
                    $"*[{Text(row.Data, "AgentName")} wrote its move as text; harness recovered " +
                    $"`{Text(row.Data, "ToolName")}`]*",
                "UnstructuredSpeechAttempt" =>
                    $"*[{Text(row.Data, "CharacterName")} tried to speak in prose, not via `say` — " +
                    $"nudged; attempted: \"{Text(row.Data, "AttemptedText")}\"]*",
                "TurnEnded" => $"*— {Text(row.Data, "CharacterName")}'s turn ends: {Text(row.Data, "Result")}*",
                _ => null
            };

            if (line is not null)
            {
                report.AppendLine(line);
                report.AppendLine();
            }
        }
    }

    /// <summary>
    /// A model's private reasoning for a call, when it produced any. Reasoning-on Ollama and Anthropic
    /// return the thinking text (Microsoft.Extensions.AI normalises it to a reasoning content block);
    /// OpenAI's Responses API mostly withholds it, so those calls simply have none. Folded into a
    /// collapsible block so the narrative stays readable but the thinking is one click away.
    /// </summary>
    private static string? TranscribeReasoning(TraceRow row)
    {
        var reasoning = ExtractReasoning(row.Data);
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return null;
        }

        return $"<details><summary>💭 {row.Actor} — thinking</summary>\n\n{Quote(reasoning)}\n\n</details>";
    }

    /// <summary>Concatenates every reasoning content block across a response's messages, or null if none.</summary>
    private static string? ExtractReasoning(JsonElement? data)
    {
        if (!TryGet(data, out var messages, "Messages") || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("Contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var content in contents.EnumerateArray())
            {
                if (content.TryGetProperty("Type", out var type) && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "reasoning"
                    && content.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String
                    && text.GetString() is { } value && !string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value.Trim());
                }
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>A character stating what it attempts, taken from its own take_action dispatch.</summary>
    private static string? TranscribeIntent(TraceRow row)
    {
        if (Text(row.Data, "ToolName") != "take_action")
        {
            return null;
        }

        var intent = Text(row.Data, "Arguments", "intent");
        return string.IsNullOrEmpty(intent) ? null : $"**{row.Actor}:** \"{intent}\"";
    }

    /// <summary>
    /// The Dungeon Master's ruling. An accepted action needs no line here: the engine result and the
    /// narration that follow say everything.
    /// </summary>
    private static string? TranscribeRuling(TraceRow row)
    {
        var category = Text(row.Data, "Category");
        if (category == "EngineAccepted")
        {
            return null;
        }

        var name = Text(row.Data, "CharacterName");
        return $"**DM (to {name}) — `{category}`:** {Text(row.Data, "Reason")}";
    }

    /// <summary>The team standings are noisy per turn, so only the deciding evaluation is transcribed.</summary>
    private static string? TranscribeTeamOutcome(TraceRow row)
    {
        var isOver = row.Data.TryGetProperty("IsOver", out var o) && o.ValueKind == JsonValueKind.True;
        if (!isOver)
        {
            return null;
        }

        // The deciding line names how it ended (Elimination / Surrender / Withdrawal / Mixed / Draw) so the
        // reader sees at a glance that a win came without killing everyone.
        var outcome = Text(row.Data, "Outcome");
        var classified = string.IsNullOrWhiteSpace(outcome) || outcome == "Ongoing" ? "" : $" [{outcome}]";
        return $"*— {Text(row.Data, "Description")}{classified}*";
    }

    /// <summary>
    /// A skipped turn, described by the character's disposition. A surrendered or escaped character must
    /// never be described as "fallen" — only the dead have fallen.
    /// </summary>
    private static string TranscribeTurnSkipped(TraceRow row)
    {
        var name = Text(row.Data, "CharacterName");
        return Text(row.Data, "Disposition") switch
        {
            "Surrendered" => $"*— {name}'s turn is skipped: they have surrendered*",
            "Escaped" => $"*— {name}'s turn is skipped: they have escaped*",
            _ => $"*— {name} lies fallen; their turn is skipped*"
        };
    }

    private static string TranscribeEngineAction(TraceRow row)
    {
        var accepted = row.Data.TryGetProperty("Accepted", out var a) && a.ValueKind == JsonValueKind.True;
        var summary = accepted
            ? Text(row.Data, "OutcomeSummary")
            : $"{Text(row.Data, "RejectionReason")} — {Text(row.Data, "RejectionMessage")}";

        return $"> **ENGINE** `{Text(row.Data, "ActionType")}` {(accepted ? "accepted" : "rejected")}: {summary}";
    }

    // -------------------------------------------------------------------------------------------
    // Full trace
    // -------------------------------------------------------------------------------------------

    private static void WriteTrace(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        report.AppendLine("## Trace");
        report.AppendLine();
        report.AppendLine($"All {events.Count} events from `trace.jsonl`, in order. " +
                          "Each entry shows its key fields, with the complete raw row folded underneath.");
        report.AppendLine();

        foreach (var row in events)
        {
            report.AppendLine($"### `{row.Sequence:D4}` {row.EventType} · {row.Actor}");
            report.AppendLine();
            report.AppendLine($"`round {row.Round}` · `turn {row.Turn}` · `{row.Timestamp}`");
            report.AppendLine();

            foreach (var line in Summarise(row))
            {
                report.AppendLine(line);
            }

            WriteRawEvent(report, row);
        }
    }

    private static void WriteRawEvent(StringBuilder report, TraceRow row)
    {
        var json = JsonSerializer.Serialize(row.Raw, TraceJson.Indented);
        var truncated = json.Length > RawEventCharacterLimit;

        report.AppendLine();
        report.AppendLine($"<details><summary>Raw event{(truncated ? " (truncated)" : "")}</summary>");
        report.AppendLine();
        report.AppendLine("```json");
        report.AppendLine(truncated ? json[..RawEventCharacterLimit] : json);
        report.AppendLine("```");

        if (truncated)
        {
            report.AppendLine();
            report.AppendLine(
                $"*Truncated at {RawEventCharacterLimit:N0} of {json.Length:N0} characters. " +
                $"The complete row is line {row.LineNumber:N0} of `trace.jsonl`.*");
        }

        report.AppendLine();
        report.AppendLine("</details>");
        report.AppendLine();
    }

    /// <summary>The fields worth reading for each event type, without repeating whole histories.</summary>
    private static IEnumerable<string> Summarise(TraceRow row)
    {
        var d = row.Data;

        switch (row.EventType)
        {
            case "ModelRequest":
                yield return Bullets(
                    ("Purpose", Text(d, "Purpose")),
                    ("Model", $"{Text(d, "Provider")} / `{Text(d, "ModelId")}`"),
                    ("Call", $"`{Text(d, "CallId")}`"),
                    ("Messages sent", CountOf(d, "Messages").ToString(CultureInfo.InvariantCulture)),
                    ("Options", Flatten(Property(d, "RequestedOptions"))),
                    ("Tools offered", NamesOf(d, "Tools")));
                if (TryGet(d, out var injected, "NewlyInjected") && injected.ValueKind == JsonValueKind.Array)
                {
                    yield return "";
                    yield return $"Newly injected this call ({injected.GetArrayLength()}):";
                    yield return "";
                    foreach (var message in injected.EnumerateArray())
                    {
                        yield return Quote($"**{Scalar(message, "Role")}:** {Scalar(message, "Text")}");
                    }
                }

                break;

            case "ModelResponse":
                yield return Bullets(
                    ("Purpose", Text(d, "Purpose")),
                    ("Call", $"`{Text(d, "CallId")}`"),
                    ("Finish reason", Text(d, "FinishReason")),
                    ("Elapsed", $"{Text(d, "ElapsedMilliseconds")} ms"),
                    ("Usage", Flatten(Property(d, "Usage"))),
                    ("Tool calls", Flatten(Property(d, "ToolCalls"))));
                if (Text(d, "Text") is { Length: > 0 } text)
                {
                    yield return "";
                    yield return Quote(text);
                }

                break;

            case "ModelResponseTruncated":
                yield return Bullets(
                    ("Agent", Text(d, "AgentName")),
                    ("Purpose", Text(d, "Purpose")),
                    ("Output tokens", $"{Text(d, "OutputTokenCount")} of {Text(d, "MaxOutputTokensRequested")} requested"),
                    ("Kept a tool call", Text(d, "HadToolCalls")),
                    ("Entirely reasoning", Text(d, "ReasoningOnly")),
                    ("Effect", Text(d, "Effect")));
                break;

            case "ContextWindowSaturated":
                yield return Bullets(
                    ("Agent", Text(d, "AgentName")),
                    ("Purpose", Text(d, "Purpose")),
                    ("Sent (estimated)", $"{Text(d, "EstimatedSentTokens")} tokens"),
                    ("Reported received", $"{Text(d, "ReportedInputTokens")} tokens"),
                    ("Dropped (estimated)", $"{Text(d, "EstimatedDroppedTokens")} tokens"),
                    ("Configured window", Text(d, "ConfiguredContextWindow")),
                    ("Messages sent", Text(d, "MessagesSent")),
                    ("Effect", Text(d, "Effect")));
                break;

            case "ModelError":
                yield return Bullets(
                    ("Agent", Text(d, "AgentName")),
                    ("Purpose", Text(d, "Purpose")),
                    ("Exception", $"`{Text(d, "ExceptionType")}`"),
                    ("Message", Text(d, "Message")));
                break;

            case "ToolCallDispatched":
                yield return Bullets(
                    ("Tool", $"`{Text(d, "ToolName")}`"),
                    ("Call", $"`{Text(d, "CallId")}`"),
                    ("Arguments", Flatten(Property(d, "Arguments"))),
                    ("Dispatch decision", Text(d, "DispatchDecision")));
                break;

            case "ToolCallResult":
                yield return Bullets(
                    ("Tool", $"`{Text(d, "ToolName")}`"),
                    ("Call", $"`{Text(d, "CallId")}`"));
                yield return "";
                yield return Quote(Flatten(Property(d, "Result")));
                break;

            case "ToolCallError":
                yield return Bullets(
                    ("Tool", $"`{Text(d, "ToolName")}`"),
                    ("Error", Text(d, "Error")));
                break;

            case "ToolCallRecovered":
                yield return Bullets(
                    ("Agent", Text(d, "AgentName")),
                    ("Recovered tool", $"`{Text(d, "ToolName")}`"),
                    ("Call", $"`{Text(d, "CallId")}`"),
                    ("Argument", Text(d, "RecoveredArgument")));
                yield return "";
                yield return Quote($"**Model wrote (as prose):** {Text(d, "OriginalText")}");
                break;

            case "UnstructuredSpeechAttempt":
                yield return Bullets(
                    ("Character", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Was truncated", Text(d, "WasTruncated")),
                    ("Outcome", "recorded, not delivered; character nudged to call say"));
                yield return "";
                yield return Quote($"**Attempted (in prose):** {Text(d, "AttemptedText")}");
                break;

            case "DmAdjudication":
                yield return Bullets(
                    ("Character", Text(d, "CharacterName")),
                    ("Category", $"`{Text(d, "Category")}`"),
                    ("Translated action", Flatten(Property(d, "TranslatedAction"))));
                yield return "";
                yield return Quote($"**Intent:** {Text(d, "Intent")}");
                if (Text(d, "Reason") is { Length: > 0 } reason)
                {
                    yield return Quote($"**Reason:** {reason}");
                }

                break;

            case "AdjudicationCorrected":
                yield return Bullets(
                    ("Tool", $"`{Text(d, "ToolName")}`"),
                    ("Parameter", $"`{Text(d, "Parameter")}`"),
                    ("Dungeon Master said", Text(d, "DungeonMasterValue")),
                    ("Corrected to", Text(d, "CorrectedValue")),
                    ("Justification", Text(d, "Justification")));
                break;

            case "TargetResolved":
                yield return Bullets(
                    ("Attacker", $"{Text(d, "AttackerName")} (`{Text(d, "AttackerId")}`)"),
                    ("Requested target", $"\"{Text(d, "RequestedTarget")}\""),
                    ("Resolved to", IsTrue(d, "Resolved")
                        ? $"{Text(d, "ResolvedTargetName")} (`{Text(d, "ResolvedTargetId")}`)"
                        : "no character — the reference did not resolve"),
                    ("Target alive", Text(d, "TargetAlive")),
                    ("Target team", Text(d, "TargetTeam")),
                    ("Ally of attacker", Text(d, "TargetIsAlly")),
                    ("Note", Text(d, "Note")));
                break;

            case "EngineAction":
                yield return Bullets(
                    ("Action", $"`{Text(d, "ActionType")}` {Flatten(Property(d, "Action"))}"),
                    ("Accepted", Text(d, "Accepted")),
                    ("Rejection", Text(d, "RejectionReason")),
                    ("Outcome", Text(d, "OutcomeSummary") ?? Text(d, "RejectionMessage")));
                yield return "";
                yield return StateTable(Property(d, "StateBefore"), Property(d, "StateAfter"));
                break;

            case "RngDraw":
                yield return Bullets(
                    ("Purpose", $"`{Text(d, "Purpose")}`"),
                    ("Caused by", $"`{Text(d, "ActionType")}` — {Text(d, "ActorName")} → {Text(d, "TargetName")}"),
                    ("Selecting", Text(d, "OutcomeSelected")),
                    ("Candidates", $"d{Text(d, "Sides")} in [{Text(d, "RangeMin")}, {Text(d, "RangeMax")}]"),
                    ("Raw roll", Text(d, "RawRoll")),
                    ("Modifier (threshold)", Text(d, "Threshold")),
                    ("Comparison", Text(d, "Comparison")),
                    ("Result", $"`{Text(d, "Result")}`"),
                    ("Seed", Text(d, "Seed")),
                    ("Sequence", $"{Text(d, "SequenceBefore")} → {Text(d, "SequenceAfter")}"));
                break;

            case "TeamOutcomeEvaluated":
                yield return Bullets(
                    ("Trigger", Text(d, "Trigger")),
                    ("Encounter over", Text(d, "IsOver")),
                    ("Standings", FormatStandings(Property(d, "Standings"))),
                    ("Winning teams", JoinArray(d, "WinningTeams")),
                    ("Eliminated teams", JoinArray(d, "EliminatedTeams")),
                    ("Outcome", Text(d, "Outcome")),
                    ("Resolution", Text(d, "ResolutionSummary")),
                    ("Description", Text(d, "Description")));
                break;

            case "TurnSkipped":
                yield return Bullets(
                    ("Character", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Team", Text(d, "Team")),
                    ("Disposition", Text(d, "Disposition")),
                    ("Reason", Text(d, "Reason")));
                break;

            case "DispositionChanged":
                yield return Bullets(
                    ("Character", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Team", Text(d, "Team")),
                    ("Change", $"{Text(d, "PreviousDisposition")} → {Text(d, "NewDisposition")}"),
                    ("Cause", Text(d, "Cause")),
                    ("Exit", Text(d, "ExitId")),
                    ("World version", $"{Text(d, "WorldVersionBefore")} → {Text(d, "WorldVersionAfter")}"),
                    ("Learned by", JoinArray(d, "PublicRecipients")));
                break;

            case "ExitInteraction":
                yield return Bullets(
                    ("Actor", $"`{Text(d, "ActorId")}`"),
                    ("Action", $"`{Text(d, "ActionType")}`"),
                    ("Exit", Text(d, "ExitId")),
                    ("Validation", $"`{Text(d, "ValidationResult")}`"),
                    ("Rejection", Text(d, "RejectionReason")),
                    ("Exit state", $"{Text(d, "StateBefore")} → {Text(d, "StateAfter")}"),
                    ("World version", $"{Text(d, "WorldVersionBefore")} → {Text(d, "WorldVersionAfter")}"));
                break;

            case "CharacterSurrendered":
                yield return Bullets(
                    ("Character", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Team", Text(d, "Team")),
                    ("Learned by", JoinArray(d, "PublicRecipients")));
                break;

            case "CharacterEscaped":
                yield return Bullets(
                    ("Character", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Team", Text(d, "Team")),
                    ("Exit", $"{Text(d, "ExitName")} (`{Text(d, "ExitId")}`) → {Text(d, "DestinationDescription")}"),
                    ("Learned by", JoinArray(d, "PublicRecipients")));
                break;

            case "CharacterQuestion":
                yield return Quote($"**{Text(d, "CharacterName")} asks:** {Text(d, "Question")}");
                break;

            case "DungeonMasterAnswer":
                yield return Quote($"**To {Text(d, "CharacterName")}:** {Text(d, "Answer")}");
                break;

            case "CharacterPassed":
                yield return Quote($"**{Text(d, "CharacterName")} does nothing:** {Text(d, "Reason")}");
                break;

            case "CharacterSpeech":
                yield return Bullets(
                    ("Speaker", $"{Text(d, "SpeakerName")} (`{Text(d, "SpeakerId")}`), team {Text(d, "SpeakerTeam")}"),
                    ("Utterance this turn", Text(d, "SpeechIndexWithinTurn")),
                    ("Recipients (living, not the speaker)", JoinArray(d, "Recipients")),
                    ("Delivery", Text(d, "DeliveryMechanism")),
                    ("Public-channel entry", Text(d, "NarrationId")));
                yield return "";
                yield return Quote($"**{Text(d, "SpeakerName")} says:** {Text(d, "Message")}");
                break;

            case "ObjectInteraction":
                yield return Bullets(
                    ("Actor", $"`{Text(d, "ActorId")}`"),
                    ("Action", $"`{Text(d, "ActionType")}`"),
                    ("Container", Text(d, "ContainerId")),
                    ("Item", Text(d, "ItemId")),
                    ("Validation", $"`{Text(d, "ValidationResult")}`"),
                    ("Rejection", Text(d, "RejectionReason")),
                    ("World version", $"{Text(d, "WorldVersionBefore")} → {Text(d, "WorldVersionAfter")}"),
                    ("Container open", $"{Text(d, "ContainerOpenBefore")} → {Text(d, "ContainerOpenAfter")}"),
                    ("Container contents", $"[{JoinArray(d, "ContainerContentsBefore")}] → [{JoinArray(d, "ContainerContentsAfter")}]"),
                    ("Actor inventory", $"[{JoinArray(d, "ActorInventoryBefore")}] → [{JoinArray(d, "ActorInventoryAfter")}]"));
                break;

            case "Narration":
                yield return Bullets(("Purpose", Text(d, "Purpose")), ("Narration ID", Text(d, "NarrationId")));
                yield return "";
                yield return Quote(Text(d, "Narration"));
                break;

            case "NarrationDelivered":
                yield return Bullets(
                    ("Narration ID", Text(d, "NarrationId")),
                    ("Delivered to", JoinArray(d, "DeliveredTo")),
                    ("Mechanism", Text(d, "DeliveryMechanism")));
                break;

            case "TurnStarted":
                yield return Bullets(
                    ("Character", Text(d, "CharacterName")),
                    ("Narrations delivered", JoinArray(d, "NarrationsDelivered")));
                yield return "";
                yield return Quote(Text(d, "SelfStateBlock"));
                break;

            case "TurnEnded":
                yield return Bullets(
                    ("Character", Text(d, "CharacterName")),
                    ("Result", $"`{Text(d, "Result")}`"),
                    ("Questions asked", Text(d, "QuestionsAsked")),
                    ("Action attempts", Text(d, "ActionAttempts")),
                    ("Model calls", Text(d, "ModelCalls")),
                    ("Accepted action", Text(d, "AcceptedAction")));
                break;

            case "HarnessLimitReached":
                yield return Bullets(
                    ("Limit", $"`{Text(d, "Limit")}` = {Text(d, "Value")}"),
                    ("Effect", Text(d, "Effect")));
                break;

            case "RunCompleted":
                yield return Bullets(
                    ("Terminal condition", Text(d, "TerminalCondition")),
                    ("Outcome", Text(d, "Outcome")),
                    ("Winning teams", JoinArray(d, "WinningTeams")),
                    ("Rounds played", Text(d, "RoundsPlayed")),
                    ("Survivors", JoinArray(d, "Survivors")),
                    ("Casualties", JoinArray(d, "Casualties")));
                break;

            case "ObjectInspected":
                yield return Bullets(
                    ("Actor", $"{Text(d, "ActorName")} (`{Text(d, "ActorId")}`)"),
                    ("Object", $"{Text(d, "ObjectName")} (`{Text(d, "ObjectId")}`)"),
                    ("Was open", Text(d, "WasOpen")),
                    ("Discovered facts", JoinArray(d, "DiscoveredFactIds")),
                    ("Learned something new", Text(d, "LearnedSomethingNew")),
                    ("World version", Text(d, "WorldVersion")));
                break;

            case "KnowledgeFactCreated":
                yield return Bullets(
                    ("Fact", $"`{Text(d, "FactId")}`"),
                    ("Subject", Text(d, "SubjectId")),
                    ("Type", Text(d, "FactType")),
                    ("World version", Text(d, "WorldVersion")),
                    ("Created by", Text(d, "CreatedBySource")),
                    ("Related action", Text(d, "RelatedAction")));
                yield return "";
                yield return Quote(Text(d, "Description"));
                break;

            case "KnowledgeFactLearned":
                yield return Bullets(
                    ("Fact", $"`{Text(d, "FactId")}`"),
                    ("Learned by", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Source", Text(d, "Source")),
                    ("Round / turn", $"{Text(d, "Round")} / {Text(d, "Turn")}"),
                    ("Observed world version", Text(d, "ObservedWorldVersion")),
                    ("Visibility", Text(d, "Visibility")),
                    ("Recipients", JoinArray(d, "Recipients")),
                    ("Related action", Text(d, "RelatedAction")));
                yield return "";
                yield return Quote(Text(d, "Description"));
                break;

            case "PrivateObservationDelivered":
                yield return Bullets(
                    ("Recipient", $"{Text(d, "RecipientName")} (`{Text(d, "RecipientId")}`)"),
                    ("Visibility", Text(d, "Visibility")),
                    ("Related facts", JoinArray(d, "RelatedFactIds")),
                    ("World version", Text(d, "WorldVersion")),
                    ("Source event", Text(d, "SourceEvent")));
                yield return "";
                yield return Quote(Text(d, "Observation"));
                break;

            case "PublicFactDelivered":
                yield return Bullets(
                    ("Visibility", Text(d, "Visibility")),
                    ("Recipients", JoinArray(d, "Recipients")),
                    ("Related facts", JoinArray(d, "RelatedFactIds")),
                    ("World version", Text(d, "WorldVersion")),
                    ("Source event", Text(d, "SourceEvent")));
                yield return "";
                yield return Quote(Text(d, "Fact"));
                break;

            default:
                yield return Bullets(("Data", Flatten(d)));
                break;
        }
    }

    private static string FormatStandings(JsonElement? standings)
    {
        if (standings is not { ValueKind: JsonValueKind.Array } array)
        {
            return "";
        }

        var parts = array.EnumerateArray()
            .Select(s => $"{Scalar(s, "Team")} {Scalar(s, "Living")}/{Scalar(s, "Total")} alive");
        return string.Join("; ", parts);
    }

    private static string StateTable(JsonElement? before, JsonElement? after)
    {
        if (before is null || after is null)
        {
            return "";
        }

        var builder = new StringBuilder();
        builder.AppendLine("| Character | Health before | Health after | Injuries after | Inventory after |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");

        var afterByName = Characters(after.Value).ToDictionary(c => Scalar(c, "Name"), StringComparer.Ordinal);

        foreach (var character in Characters(before.Value))
        {
            var name = Scalar(character, "Name");
            if (!afterByName.TryGetValue(name, out var updated))
            {
                continue;
            }

            builder.AppendLine(
                $"| {name} | {Scalar(character, "Health")}/{Scalar(character, "MaxHealth")} " +
                $"| {Scalar(updated, "Health")}/{Scalar(updated, "MaxHealth")} " +
                $"| {DescriptionsOf(updated, "Injuries")} | {NamesOf(updated, "Inventory")} |");
        }

        return builder.ToString();
    }

    private static IEnumerable<JsonElement> Characters(JsonElement state) =>
        state.TryGetProperty("Characters", out var characters) && characters.ValueKind == JsonValueKind.Array
            ? characters.EnumerateArray()
            : [];

    /// <summary>Final container open-state and contents, from the recorded final room objects.</summary>
    private static void WriteFinalContainers(StringBuilder report, JsonElement state)
    {
        if (!state.TryGetProperty("Room", out var room)
            || !room.TryGetProperty("Objects", out var objects)
            || objects.ValueKind != JsonValueKind.Array
            || objects.GetArrayLength() == 0)
        {
            return;
        }

        report.AppendLine("| Container | State | Contents |");
        report.AppendLine("| --- | --- | --- |");
        foreach (var container in objects.EnumerateArray())
        {
            var open = container.TryGetProperty("IsOpen", out var o) && o.ValueKind == JsonValueKind.True;
            report.AppendLine(
                $"| {Scalar(container, "Name")} | {(open ? "open" : "closed")} | {NamesOf(container, "Contents")} |");
        }

        report.AppendLine();
    }

    private static void WriteFinalState(StringBuilder report, JsonElement? finalState)
    {
        report.AppendLine("## Final state");
        report.AppendLine();

        if (finalState is null)
        {
            report.AppendLine("*No final-state.json was written for this run.*");
            report.AppendLine();
            return;
        }

        // The terminal condition, rounds played and trace-event count are already in the Run details
        // header; repeating them here just printed the outcome twice, so this section shows only the
        // end-state itself — the characters, their inventories, and the room's containers.
        if (TryGet(finalState, out var state, "State"))
        {
            // Final location is the room for anyone still present or fallen; only an escaped character is
            // elsewhere. Disposition, not a bare alive/dead flag, is what distinguishes surrendered and
            // escaped survivors from the dead — the report must never call either one "fallen".
            var roomName = TryGet(state, out var roomEl, "Room") ? Scalar(roomEl, "Name") : "the room";

            report.AppendLine("| Character | Team | Health | Disposition | Final location | Weapon | Inventory | Injuries |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var character in Characters(state))
            {
                var health = int.TryParse(Scalar(character, "Health"), out var h) ? h : 0;
                var weapon = character.TryGetProperty("Weapon", out var w) && w.ValueKind == JsonValueKind.Object
                    ? $"{Scalar(w, "Name")} (damage {Scalar(w, "Damage")})"
                    : "none";

                var disposition = Scalar(character, "Disposition");
                var escaped = string.Equals(disposition, "Escaped", StringComparison.OrdinalIgnoreCase);
                var location = escaped ? "Outside the encounter" : roomName;

                report.AppendLine(
                    $"| {Scalar(character, "Name")} | {Scalar(character, "Team")} | {health}/{Scalar(character, "MaxHealth")} " +
                    $"| {disposition} | {location} | {weapon} | {NamesOf(character, "Inventory")} " +
                    $"| {DescriptionsOf(character, "Injuries")} |");
            }

            report.AppendLine();
            WriteFinalContainers(report, state);

            report.AppendLine("<details><summary>Complete final state</summary>");
            report.AppendLine();
            report.AppendLine("```json");
            report.AppendLine(JsonSerializer.Serialize(state, TraceJson.Indented));
            report.AppendLine("```");
            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Reading and formatting helpers
    // -------------------------------------------------------------------------------------------

    private sealed record TraceRow(
        long Sequence,
        int LineNumber,
        string Timestamp,
        string EventType,
        string Actor,
        int Round,
        int Turn,
        JsonElement Data,
        JsonElement Raw);

    private static List<TraceRow> ReadEvents(string tracePath)
    {
        var rows = new List<TraceRow>();
        var lineNumber = 0;

        foreach (var line in File.ReadLines(tracePath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var root = JsonDocument.Parse(line).RootElement.Clone();
                rows.Add(new TraceRow(
                    root.TryGetProperty("Sequence", out var s) && s.TryGetInt64(out var value) ? value : lineNumber,
                    lineNumber,
                    Scalar(root, "Timestamp"),
                    Scalar(root, "EventType"),
                    Scalar(root, "Actor"),
                    root.TryGetProperty("Round", out var r) && r.TryGetInt32(out var round) ? round : 0,
                    root.TryGetProperty("Turn", out var t) && t.TryGetInt32(out var turn) ? turn : 0,
                    root.TryGetProperty("Data", out var data) ? data : default,
                    root));
            }
            catch (JsonException)
            {
                // A malformed line is itself worth reporting rather than skipping silently.
                rows.Add(new TraceRow(lineNumber, lineNumber, "", "UnreadableTraceLine", "harness", 0, 0, default, default));
            }
        }

        return rows;
    }

    private static JsonElement? TryReadJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement? source, out JsonElement value, params string[] path)
    {
        value = default;
        if (source is not { ValueKind: not JsonValueKind.Undefined })
        {
            return false;
        }

        var current = source.Value;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private static JsonElement? Property(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement? source, params string[] path) =>
        TryGet(source, out var value, path) ? Flatten(value) : null;

    /// <summary>True only when the JSON value at the path is the boolean <c>true</c>.</summary>
    private static bool IsTrue(JsonElement? source, params string[] path) =>
        TryGet(source, out var value, path) && value.ValueKind == JsonValueKind.True;

    private static long LongField(JsonElement? source, params string[] path) =>
        TryGet(source, out var value, path) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static double DoubleField(JsonElement? source, params string[] path) =>
        TryGet(source, out var value, path) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static string Scalar(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value)
            ? Flatten(value)
            : "";

    private static string NamesOf(JsonElement? source, string property) => ListOf(source, property, "Name");

    private static string DescriptionsOf(JsonElement source, string property) =>
        ListOf(source, property, "Description");

    private static string ListOf(JsonElement? source, string property, string field)
    {
        if (!TryGet(source, out var array, property) || array.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var names = array.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object ? Scalar(item, field) : Flatten(item))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static string JoinArray(JsonElement? source, string property)
    {
        if (!TryGet(source, out var array, property) || array.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var values = array.EnumerateArray().Select(item => Flatten(item)).Where(v => v.Length > 0).ToList();
        return values.Count == 0 ? "none" : string.Join(", ", values);
    }

    /// <summary>Renders a value compactly and, crucially, safely for a markdown table cell.</summary>
    private static string Flatten(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "";
        }

        var element = value.Value;
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => JsonSerializer.Serialize(element, TraceJson.Compact)
        };

        return text.Replace("\r\n", " ").Replace('\n', ' ').Replace("|", "\\|");
    }

    private static string Bullets(params (string Label, string? Value)[] fields) =>
        string.Join("\n", fields
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .Select(f => $"- **{f.Label}:** {f.Value}"));

    /// <summary>Renders prose as a blockquote, keeping multi-line text inside the quote.</summary>
    private static string Quote(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => $"> {l}"));

    private static int CountOf(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object
        && source.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static void Row(StringBuilder report, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            report.AppendLine($"| **{label}** | {value} |");
        }
    }
}
