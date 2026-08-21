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
        WriteSurrenderNegotiation(report, events);
        WritePersuasionAndIntimidation(report, events);
        WriteMoraleSummary(report, events, finalState);
        WriteIntimidationSummary(report, events);
        WriteAttackQualitySummary(report, events);
        WriteAbilityActivity(report, events, finalState);
        WriteStatusTimeline(report, events);
        WriteStateGroundingHealth(report, events);
        WriteInventoryActivity(report, events, finalState);
        WriteRulebookConsultations(report, events);
        WriteRulebookEfficiency(report, events, manifest);
        WriteContextHealth(report, events, manifest);
        WriteScenario(report, manifest);
        WriteTeams(report, manifest);
        WriteKnowledge(report, events, manifest, finalState);
        WriteExitState(report, manifest, finalState);
        WriteEnvironmentalObjects(report, manifest, finalState, events);
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
        // Every object action that occurred gets a row, not just the two the table was first written for:
        // a run whose only object interaction was inspect_object otherwise showed two zero rows above a
        // non-zero total, which reads as a reporting bug rather than as "she looked at the crate".
        foreach (var action in new[] { "open_container", "take_item", "inspect_object" })
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
    // Negotiated surrender (v0.7): every offer, what it promised, and how it was answered
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The negotiation record: each offer with its offerer, recipient, exact terms, the speech that carried it,
    /// how it was answered and how long that took, plus the agreements actually struck and what moved under
    /// them. Skipped when nobody offered terms, so a run with no negotiation adds no empty section.
    /// </summary>
    private static void WriteSurrenderNegotiation(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var offers = events.Where(e => e.EventType == "SurrenderOfferMade").ToList();
        var resolutions = events.Where(e => e.EventType == "SurrenderOfferResolved").ToList();
        var agreements = events.Where(e => e.EventType == "SurrenderAgreementRecorded").ToList();
        if (offers.Count == 0 && agreements.Count == 0)
        {
            return;
        }

        report.AppendLine("## Surrender negotiation");
        report.AppendLine();
        report.AppendLine(
            "Giving up the fight takes both sides: a concrete offer from one, and acceptance from the " +
            "one named opponent. An offer moves nothing and protects nobody — only the acceptance column below " +
            "records assets that actually changed hands.");
        report.AppendLine();

        var resolutionByOffer = resolutions
            .GroupBy(r => Text(r.Data, "OfferId") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        report.AppendLine("| Offer | Round.Turn | Offerer | Recipient | Terms promised | Result | Turns to answer | Assets moved |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in offers.OrderBy(r => r.Sequence))
        {
            var id = Text(row.Data, "OfferId") ?? "";
            var resolved = resolutionByOffer.TryGetValue(id, out var r) ? r : null;
            var result = resolved is null ? "still open" : Text(resolved.Data, "NewState")?.ToLowerInvariant() ?? "";
            var turns = resolved is null ? "" : Text(resolved.Data, "TurnsToRespond") ?? "";
            var moved = resolved is not null && IsTrue(resolved.Data, "AssetsTransferred") ? "yes" : "no";
            report.AppendLine(
                $"| {id} | {row.Round}.{row.Turn} | {Text(row.Data, "OffererName")} | {Text(row.Data, "RecipientName")} | " +
                $"{CellText(DescribeOfferTerms(row))} | {result} | {turns} | {moved} |");
        }

        report.AppendLine();

        // A run may also resolve an offer that was seeded rather than traced as made; list any such orphans.
        var orphans = resolutions
            .Where(r => !offers.Any(o => string.Equals(Text(o.Data, "OfferId"), Text(r.Data, "OfferId"), StringComparison.Ordinal)))
            .ToList();
        if (orphans.Count > 0)
        {
            report.AppendLine("Offers resolved without a recorded creation event (seeded state):");
            report.AppendLine();
            foreach (var row in orphans)
            {
                report.AppendLine(
                    $"- {Text(row.Data, "OfferId")}: {Text(row.Data, "OffererName")} → {Text(row.Data, "RecipientName")} — " +
                    $"{Text(row.Data, "NewState")?.ToLowerInvariant()} ({Text(row.Data, "Cause")}).");
            }

            report.AppendLine();
        }

        if (agreements.Count == 0)
        {
            report.AppendLine("**No offer was accepted: nothing changed hands through negotiation.**");
            report.AppendLine();
            return;
        }

        report.AppendLine("**Surrender agreements struck**");
        report.AppendLine();
        report.AppendLine("| Agreement | Round.Turn | Who yielded | Who accepted | Tribute transferred | Weapon | Disarmed |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in agreements.OrderBy(r => r.Sequence))
        {
            var tribute = JoinArray(row.Data, "TransferredItemNames");
            var weapon = Text(row.Data, "ForfeitedWeaponName") is { } name
                ? $"{name} — {Text(row.Data, "WeaponDisposition")}"
                : Text(row.Data, "WeaponDisposition") ?? "none promised";
            report.AppendLine(
                $"| {Text(row.Data, "AgreementId")} | {row.Round}.{row.Turn} | {Text(row.Data, "OffererName")} | " +
                $"{Text(row.Data, "AcceptedByName")} | {CellText(string.IsNullOrWhiteSpace(tribute) ? "none" : tribute)} | " +
                $"{CellText(weapon)} | {(IsTrue(row.Data, "OffererDisarmed") ? "yes" : "no")} |");
        }

        report.AppendLine();
    }

    /// <summary>
    /// The persuasion and intimidation record, kept deliberately descriptive.
    /// </summary>
    /// <remarks>
    /// Every offer is listed with the speech that accompanied it, the visible battle state it was made under,
    /// and whether it was taken up. It does NOT claim the speech caused the acceptance, and reports no
    /// statistic: a single run cannot separate a persuasive argument from a favourable roll, and pretending
    /// otherwise would be the most misleading number in the report.
    /// </remarks>
    private static void WritePersuasionAndIntimidation(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var offers = events.Where(e => e.EventType == "SurrenderOfferMade").ToList();
        if (offers.Count == 0)
        {
            return;
        }

        var accepted = events
            .Where(e => e.EventType == "SurrenderOfferResolved"
                        && string.Equals(Text(e.Data, "NewState"), "Accepted", StringComparison.Ordinal))
            .Select(e => Text(e.Data, "OfferId") ?? "")
            .ToHashSet(StringComparer.Ordinal);

        report.AppendLine("## Persuasion and intimidation");
        report.AppendLine();
        report.AppendLine(
            "Each offer with the words that carried it and the odds it was made under. This is descriptive " +
            "only: **nothing here shows that speech caused an acceptance**, and no statistic is drawn from it — " +
            "one run cannot separate a persuasive argument from a recipient who was going to accept anyway.");
        report.AppendLine();

        foreach (var row in offers.OrderBy(r => r.Sequence))
        {
            var id = Text(row.Data, "OfferId") ?? "";
            var outcome = accepted.Contains(id) ? "**accepted**" : "not accepted";
            report.AppendLine($"**{id} — {Text(row.Data, "OffererName")} to {Text(row.Data, "RecipientName")} (round {row.Round}): {outcome}**");
            report.AppendLine();
            report.AppendLine($"- Terms promised: {DescribeOfferTerms(row)}");
            report.AppendLine($"- Assets offered: {(JoinArray(row.Data, "OfferedItemNames") is { Length: > 0 } items ? items : "none beyond the weapon")}");
            report.AppendLine($"- Visible battle state: {Text(row.Data, "BattleStateSummary")}");
            var speech = Text(row.Data, "AssociatedSpeech");
            report.AppendLine(speech is null
                ? "- Speech: none — the offer was made without a word spoken."
                : $"- Speech: {Quote(SingleLine(speech))}");
            report.AppendLine();
        }

        report.AppendLine(
            $"Offers made: **{offers.Count}**; accepted: **{offers.Count(o => accepted.Contains(Text(o.Data, "OfferId") ?? ""))}**.");
        report.AppendLine();
    }

    // -------------------------------------------------------------------------------------------
    // Ability activity and the status timeline (v0.7)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Every ability attempt: accepted and refused, its target, what it did, and what charge remains. Refusals
    /// are listed for the same reason the trace records them — a refused use must be visible as having spent
    /// nothing. Skipped when no ability was attempted.
    /// </summary>
    private static void WriteAbilityActivity(StringBuilder report, IReadOnlyList<TraceRow> events, JsonElement? finalState)
    {
        var uses = events.Where(e => e.EventType == "AbilityUsed").ToList();
        if (uses.Count == 0)
        {
            return;
        }

        report.AppendLine("## Ability activity");
        report.AppendLine();

        var accepted = uses.Count(u => string.Equals(Text(u.Data, "ValidationResult"), "accepted", StringComparison.Ordinal));
        var healing = uses.Sum(u => LongField(u.Data, "HealingPerformed"));
        var redirections = events.Count(e => e.EventType == "AttackRedirected");
        var rallyConsumed = events.Count(e => e.EventType == "StatusConsumed"
                                             && string.Equals(Text(e.Data, "Kind"), "Rallied", StringComparison.Ordinal));
        var offBalanceApplied = events.Count(e => e.EventType == "StatusApplied"
                                                 && string.Equals(Text(e.Data, "Kind"), "OffBalance", StringComparison.Ordinal));
        var defendUses = uses.Count(u => string.Equals(Text(u.Data, "AbilityId"), "defend", StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(Text(u.Data, "ValidationResult"), "accepted", StringComparison.Ordinal));
        var damagePrevented = events
            .Where(e => e.EventType == "EngineAction" && IsTrue(e.Data, "Accepted"))
            .Sum(e => LongField(Property(e.Data, "Outcome") ?? default, "DefendReduction"));

        report.AppendLine($"- Ability uses attempted: **{uses.Count}** — {accepted} accepted, {uses.Count - accepted} refused.");
        report.AppendLine($"- Healing performed: **{healing}** health restored in total.");
        report.AppendLine($"- Guard redirections: **{redirections}** blow(s) taken by a guardian in an ally's place.");
        report.AppendLine($"- Rally modifiers consumed by an attack: **{rallyConsumed}**.");
        report.AppendLine($"- Dirty Strike / OffBalance applications: **{offBalanceApplied}**.");
        report.AppendLine($"- Defend uses: **{defendUses}**; damage turned aside by a raised guard: **{damagePrevented}**.");
        report.AppendLine();

        report.AppendLine("| Round.Turn | Actor | Ability | Target | Result | Outcome | Charges left |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in uses.OrderBy(r => r.Sequence))
        {
            var result = Text(row.Data, "ValidationResult") ?? "";
            var detail = string.Equals(result, "accepted", StringComparison.Ordinal)
                ? DescribeAbilityEffect(row)
                : $"refused: {Text(row.Data, "RejectionReason")} (no charge spent)";
            var charges = Text(row.Data, "RemainingUsesAfter") ?? "unlimited";
            report.AppendLine(
                $"| {row.Round}.{row.Turn} | {Text(row.Data, "ActorName")} | {Text(row.Data, "AbilityName")} | " +
                $"{Text(row.Data, "TargetName") ?? "—"} | {result} | {CellText(detail)} | {charges} |");
        }

        report.AppendLine();
        WriteFinalAbilityCharges(report, finalState);
    }

    /// <summary>What one accepted ability use actually did, in a table cell.</summary>
    private static string DescribeAbilityEffect(TraceRow row)
    {
        var parts = new List<string>();
        var healing = LongField(row.Data, "HealingPerformed");
        if (healing > 0)
        {
            parts.Add($"{healing} health restored");
        }

        var statuses = JoinArray(row.Data, "StatusesApplied");
        if (!string.IsNullOrWhiteSpace(statuses))
        {
            parts.Add($"applied {statuses}");
        }

        if (IsTrue(row.Data, "RngConsulted"))
        {
            parts.Add("used the ordinary attack draws");
        }

        return parts.Count == 0 ? "resolved" : string.Join("; ", parts);
    }

    /// <summary>What each character had left of each ability when the run stopped.</summary>
    private static void WriteFinalAbilityCharges(StringBuilder report, JsonElement? finalState)
    {
        if (!TryGet(finalState, out var state, "State")
            || !TryGet(state, out var characters, "Characters")
            || characters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var rows = new List<string>();
        foreach (var character in characters.EnumerateArray())
        {
            if (Property(character, "Abilities") is not { ValueKind: JsonValueKind.Array } abilities)
            {
                continue;
            }

            foreach (var ability in abilities.EnumerateArray())
            {
                var max = Scalar(ability, "MaxUses");
                var remaining = Scalar(ability, "RemainingUses");
                var left = string.IsNullOrWhiteSpace(max) ? "unlimited" : $"{remaining} of {max}";
                rows.Add($"| {Scalar(character, "Name")} | {Scalar(ability, "Name")} | {Scalar(ability, "Category")} | {left} |");
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        report.AppendLine("**Ability charges at the end of the run**");
        report.AppendLine();
        report.AppendLine("| Character | Ability | Category | Uses left |");
        report.AppendLine("| --- | --- | --- | --- |");
        foreach (var line in rows)
        {
            report.AppendLine(line);
        }

        report.AppendLine();
    }

    /// <summary>
    /// The status timeline: every application, consumption, expiry and removal, with its source, target,
    /// modifier, how long it stood, and — where it changed a roll — which draw it fed. Skipped when no status
    /// was ever applied.
    /// </summary>
    private static void WriteStatusTimeline(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var statusEvents = events
            .Where(e => e.EventType is "StatusApplied" or "StatusConsumed" or "StatusExpired" or "StatusRemoved")
            .OrderBy(e => e.Sequence)
            .ToList();
        if (statusEvents.Count == 0)
        {
            return;
        }

        report.AppendLine("## Status timeline");
        report.AppendLine();
        report.AppendLine(
            "Every status effect the engine applied and every way one ended. Duration is measured in global " +
            "turns from application to the transition; a status consumed on the turn it was applied shows 0.");
        report.AppendLine();

        // Application turn per status id, so a later transition can report how long it stood.
        var appliedTurn = statusEvents
            .Where(e => e.EventType == "StatusApplied")
            .GroupBy(e => Text(e.Data, "StatusId") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (int)LongField(g.First().Data, "AppliedTurn"), StringComparer.Ordinal);

        report.AppendLine("| Round.Turn | Status | Transition | Source | Target | Modifier | Turns held | Expiry rule | Affected draw | Cause |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in statusEvents)
        {
            var id = Text(row.Data, "StatusId") ?? "";
            var held = row.EventType == "StatusApplied" || !appliedTurn.TryGetValue(id, out var applied)
                ? ""
                : Math.Max(0, row.Turn - applied).ToString(CultureInfo.InvariantCulture);
            var modifier = LongField(row.Data, "Modifier");
            report.AppendLine(
                $"| {row.Round}.{row.Turn} | {Text(row.Data, "Kind")} | {Text(row.Data, "Transition")} | " +
                $"{Text(row.Data, "SourceCharacterName")} | {Text(row.Data, "TargetCharacterName")} | " +
                $"{(modifier == 0 ? "—" : modifier.ToString("+#;-#;0", CultureInfo.InvariantCulture))} | {held} | " +
                $"{Text(row.Data, "ExpiryRule")} | {Text(row.Data, "AffectedRngPurpose") ?? "—"} | " +
                $"{CellText(Text(row.Data, "Cause"))} |");
        }

        report.AppendLine();

        var byKind = statusEvents
            .GroupBy(e => Text(e.Data, "Kind") ?? "")
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        report.AppendLine("| Status | Applied | Consumed | Expired unused | Removed |");
        report.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var group in byKind)
        {
            report.AppendLine(
                $"| {group.Key} | {group.Count(e => e.EventType == "StatusApplied")} | " +
                $"{group.Count(e => e.EventType == "StatusConsumed")} | " +
                $"{group.Count(e => e.EventType == "StatusExpired")} | " +
                $"{group.Count(e => e.EventType == "StatusRemoved")} |");
        }

        report.AppendLine();
    }

    // -------------------------------------------------------------------------------------------
    // State-grounding health (v0.7): were answers grounded, and did stale references get through?
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// How well the world held its boundaries: how many questions were answered from a bounded projection and
    /// how much each withheld, how often the harness had to correct a Dungeon Master answer, how many attempts
    /// the engine or the knowledge gate refused on a stale or unknown reference, and what post-resolution
    /// output was discarded. Skipped when a run asked no questions and discarded nothing.
    /// </summary>
    private static void WriteStateGroundingHealth(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var projections = events.Where(e => e.EventType == "AnswerFactsProjected").ToList();
        var questions = events.Where(e => e.EventType == "CharacterQuestion").ToList();
        var discarded = events.Where(e => e.EventType == "PostResolutionOutputDiscarded").ToList();
        if (projections.Count == 0 && questions.Count == 0 && discarded.Count == 0)
        {
            return;
        }

        report.AppendLine("## State-grounding health");
        report.AppendLine();
        report.AppendLine(
            "Questions are answered from a deterministic projection of current permitted facts, not from the " +
            "authoritative state, so the Dungeon Master rephrases rather than decides. This section is the " +
            "evidence that the boundary held.");
        report.AppendLine();

        var corrections = events
            .Where(e => e.EventType == "AdjudicationCorrected"
                        && string.Equals(Text(e.Data, "Parameter"), "answer", StringComparison.Ordinal))
            .ToList();

        var staleReferences = events
            .Where(e => e.EventType == "EngineAction" && !IsTrue(e.Data, "Accepted"))
            .Select(e => Text(e.Data, "RejectionReason"))
            .Where(reason => reason is "ItemNotPossessed" or "ItemNotInContainer" or "UnknownTarget"
                or "UnknownContainer" or "UnknownObject" or "UnknownExit" or "UnknownOffer"
                or "PromisedAssetNoLongerAvailable" or "OfferedItemNotOwned" or "AbilityNotHeld" or "UnknownAbility")
            .ToList();

        var knowledgeRefusals = events
            .Where(e => e.EventType == "InventoryInteraction"
                        && string.Equals(Text(e.Data, "RejectionReason"), "NoInformationalBasis", StringComparison.Ordinal))
            .Count();

        var actionLimits = events
            .Where(e => e.EventType == "HarnessLimitReached"
                        && Text(e.Data, "Limit") is "MaxActionAttemptsPerTurn" or "MaxModelCallsPerTurn")
            .ToList();

        report.AppendLine($"- Character questions asked: **{questions.Count}**; answered from a bounded projection: **{projections.Count}**.");
        if (projections.Count > 0)
        {
            report.AppendLine(
                $"- Facts projected per question: {Average(projections.Sum(p => LongField(p.Data, "ProjectedFactCount")), projections.Count)} on average; " +
                $"facts deliberately withheld: {Average(projections.Sum(p => LongField(p.Data, "OmittedFactCount")), projections.Count)}.");
            report.AppendLine(
                $"- Affordances offered per question: {Average(projections.Sum(p => LongField(p.Data, "AffordanceCount")), projections.Count)} on average " +
                "(the closed list of what that character could actually attempt).");
            report.AppendLine(
                $"- Authoritative state withheld from the answering call: **{projections.Count(p => IsTrue(p.Data, "FullStateWithheld"))} of {projections.Count}**.");
        }

        report.AppendLine($"- Answers the harness had to rephrase for breaking character or naming the machinery: **{corrections.Count}**.");
        report.AppendLine($"- Engine refusals on a stale or unknown reference: **{staleReferences.Count}**" +
                          (staleReferences.Count == 0 ? "." : $" ({string.Join(", ", staleReferences.GroupBy(r => r).Select(g => $"{g.Key} ×{g.Count()}"))})."));
        report.AppendLine($"- Reaches refused for want of an informational basis (before the engine, before any roll): **{knowledgeRefusals}**.");
        // The two kinds are counted apart on purpose. A surplus tool call is a model trying to act twice; a
        // stretch of loose text alongside an accepted call is usually only flavour. Both were discarded, but
        // folding them into one number would make harmless prose look like a protocol breach.
        var discardedCalls = discarded.Count(d => string.Equals(Text(d.Data, "DiscardedKind"), "tool-call", StringComparison.Ordinal));
        report.AppendLine(
            $"- Post-resolution output discarded: **{discarded.Count}** — {discardedCalls} further tool call(s) that " +
            $"never reached the world, and {discarded.Count - discardedCalls} stretch(es) of loose text the world never read.");
        report.AppendLine($"- Turns abandoned on an action or model-call limit: **{actionLimits.Count}**.");
        report.AppendLine();

        if (discarded.Count > 0)
        {
            report.AppendLine("**Discarded output** — produced once the turn was already resolved, and never acted on.");
            report.AppendLine();
            report.AppendLine("| Round.Turn | Character | Kind | Already resolved by | Discarded content |");
            report.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var row in discarded.OrderBy(r => r.Sequence))
            {
                report.AppendLine(
                    $"| {row.Round}.{row.Turn} | {Text(row.Data, "CharacterName")} | {Text(row.Data, "DiscardedKind")} | " +
                    $"{CellText(Text(row.Data, "ResolvedAction"))} | {CellText(Text(row.Data, "DiscardedContent"))} |");
            }

            report.AppendLine();
        }

        if (corrections.Count > 0)
        {
            report.AppendLine("**Answers corrected in-world**");
            report.AppendLine();
            foreach (var row in corrections.OrderBy(r => r.Sequence))
            {
                report.AppendLine($"- Round {row.Round}: {Quote(SingleLine(Text(row.Data, "DungeonMasterValue")))} → {Quote(SingleLine(Text(row.Data, "CorrectedValue")))}");
            }

            report.AppendLine();
        }
    }

    private static string SingleLine(string? text) =>
        (text ?? "").Replace("\r\n", " ").Replace('\n', ' ').Trim();

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

    /// <summary>
    /// Each item's journey, in order, with its stable id and the kind of movement that carried it.
    /// </summary>
    /// <remarks>
    /// The stable id column matters more than it looks: every character carries an identically named purse of
    /// gold, so a name alone cannot tell you whose coin ended up where. The movement column distinguishes an
    /// ordinary gift from surrender tribute and from weapon forfeiture, which look alike in a name-only view
    /// and mean very different things.
    /// </remarks>
    private static void WriteItemProvenanceTimeline(StringBuilder report, IReadOnlyList<TraceRow> provenance)
    {
        if (provenance.Count == 0)
        {
            return;
        }

        report.AppendLine("**Item provenance timeline**");
        report.AppendLine();
        report.AppendLine("| Round.Turn | Item | Stable id | Movement | From | To | RNG |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in provenance.OrderBy(r => r.Sequence))
        {
            var rng = IsTrue(row.Data, "RngInvolved") ? "yes" : "no";
            report.AppendLine(
                $"| {row.Round}.{row.Turn} | {Text(row.Data, "ItemName")} | `{Text(row.Data, "ItemId")}` | " +
                $"{DescribeMovement(Text(row.Data, "ActionType"))} | " +
                $"{Text(row.Data, "PreviousOwnerOrLocation")} | {Text(row.Data, "NewOwnerOrLocation")} | {rng} |");
        }

        report.AppendLine();

        var byKind = provenance
            .GroupBy(r => Text(r.Data, "ActionType") ?? "")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        report.AppendLine("| Movement | Count |");
        report.AppendLine("| --- | --- |");
        foreach (var group in byKind)
        {
            report.AppendLine($"| {DescribeMovement(group.Key)} | {group.Count()} |");
        }

        report.AppendLine();
    }

    /// <summary>
    /// The kinds of item movement the provenance history distinguishes. Each is a different fact about the
    /// world, so none is folded into another: tribute handed over under accepted terms is not a gift, and a
    /// forfeited weapon is not a dropped item.
    /// </summary>
    private static string DescribeMovement(string? actionType) => actionType switch
    {
        "give_item" => "given",
        "drop_item" => "dropped",
        "steal_item" => "stolen",
        "take_item" => "taken (container, body or floor)",
        "surrender_tribute" => "surrender tribute",
        "weapon_forfeiture" => "weapon forfeiture",
        null or "" => "(unrecorded)",
        _ => actionType
    };

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
    // Morale, intimidation and attack quality (v0.8)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// What happened to everybody's nerve: where each character started and finished, the worst it got, when
    /// they broke and when (if ever) they came back, every change grouped by cause, and what they actually
    /// chose to do while frightened. That last table is the one that matters to the experiment — fear in v0.8
    /// exerts pressure and never chooses, so the only way to see whether the pressure did anything is to look
    /// at the actions taken under it. Skipped entirely when no fear ever moved.
    /// </summary>
    private static void WriteMoraleSummary(StringBuilder report, IReadOnlyList<TraceRow> events, JsonElement? finalState)
    {
        var changes = events.Where(e => e.EventType == "FearChanged").OrderBy(e => e.Sequence).ToList();
        var finalFear = FinalFearByName(finalState);
        if (changes.Count == 0 && finalFear.Values.All(f => f == 0))
        {
            return;
        }

        report.AppendLine("## Morale summary");
        report.AppendLine();
        report.AppendLine(
            "Fear is engine state on a 0-5 scale, and it is shown here in full because a report is an " +
            "experiment artefact — no character in the fiction ever sees another's figure. Nothing in this " +
            "section chose an action: at 3 or more a character is told plainly that they are afraid and " +
            "should weigh their life, and then decides for themselves.");
        report.AppendLine();
        report.AppendLine(
            "Every character in the encounter is listed, including those whose nerve never moved — a run " +
            "where three of four held steady is a result, and a table that showed only the two who broke " +
            "would read as though the others had been left out.");
        report.AppendLine();

        var names = changes.Select(c => Text(c.Data, "CharacterName") ?? "")
            .Concat(finalFear.Keys)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        report.AppendLine("| Character | Initial | Final | Peak | Became scared | Recovered |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var name in names)
        {
            var mine = changes.Where(c => string.Equals(Text(c.Data, "CharacterName"), name, StringComparison.Ordinal)).ToList();
            var initial = mine.Count == 0 ? finalFear.GetValueOrDefault(name) : (int)LongField(mine[0].Data, "FearBefore");
            var final = finalFear.TryGetValue(name, out var f)
                ? f
                : mine.Count == 0 ? 0 : (int)LongField(mine[^1].Data, "FearAfter");
            var peak = mine.Count == 0
                ? Math.Max(initial, final)
                : Math.Max(initial, mine.Max(c => (int)LongField(c.Data, "FearAfter")));

            var broke = mine.FirstOrDefault(c => string.Equals(Text(c.Data, "ScaredTransition"), "BecameScared", StringComparison.Ordinal));
            var back = mine.FirstOrDefault(c => string.Equals(Text(c.Data, "ScaredTransition"), "RecoveredFromScared", StringComparison.Ordinal));

            report.AppendLine(
                $"| {name} | {initial} | {final} | {peak} | " +
                $"{(broke is null ? "never" : $"round {broke.Round}, turn {broke.Turn}")} | " +
                $"{(back is null ? (broke is null ? "n/a" : "never") : $"round {back.Round}, turn {back.Turn}")} |");
        }

        report.AppendLine();

        if (changes.Count > 0)
        {
            report.AppendLine("### Fear changes by cause");
            report.AppendLine();
            report.AppendLine("| Cause | Times | Net change | Absorbed by the clamp |");
            report.AppendLine("| --- | --- | --- | --- |");
            foreach (var group in changes.GroupBy(c => Text(c.Data, "Cause") ?? "unknown").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var net = group.Sum(c => LongField(c.Data, "FearAfter") - LongField(c.Data, "FearBefore"));
                var absorbed = group.Count(c => IsTrue(c.Data, "Absorbed"));
                report.AppendLine($"| {group.Key} | {group.Count()} | {net:+#;-#;0} | {absorbed} |");
            }

            report.AppendLine();

            report.AppendLine("### Every change, in order");
            report.AppendLine();
            report.AppendLine("| Round | Turn | Character | Fear | Cause | Threshold |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var change in changes)
            {
                var transition = Text(change.Data, "ScaredTransition");
                report.AppendLine(
                    $"| {change.Round} | {change.Turn} | {Text(change.Data, "CharacterName")} | " +
                    $"{LongField(change.Data, "FearBefore")} → {LongField(change.Data, "FearAfter")}" +
                    $"{(IsTrue(change.Data, "Absorbed") ? " (absorbed)" : "")} | " +
                    $"{SingleLine(Text(change.Data, "CauseDetail"))} | " +
                    $"{(transition is null or "None" ? "-" : transition)} |");
            }

            report.AppendLine();
        }

        WriteActionsTakenWhileScared(report, events, changes);
    }

    /// <summary>
    /// What each character actually did on every turn they took while publicly Scared. This is the honest
    /// answer to "did the pressure change behaviour", and it is deliberately presented as a list rather than
    /// a rate: one encounter cannot separate a frightened character choosing to run from one who would have
    /// run anyway, and a percentage here would imply a causal claim the run cannot support.
    /// </summary>
    private static void WriteActionsTakenWhileScared(
        StringBuilder report, IReadOnlyList<TraceRow> events, IReadOnlyList<TraceRow> changes)
    {
        // Rebuild who was scared at each moment from the threshold crossings, then read the turns off the
        // turn-ended events. Reconstructed rather than recorded, so an older trace still renders.
        var crossings = changes
            .Where(c => Text(c.Data, "ScaredTransition") is "BecameScared" or "RecoveredFromScared")
            .Select(c => (c.Sequence, Name: Text(c.Data, "CharacterName") ?? "",
                Scared: string.Equals(Text(c.Data, "ScaredTransition"), "BecameScared", StringComparison.Ordinal)))
            .ToList();
        if (crossings.Count == 0)
        {
            return;
        }

        var rows = new List<string>();
        foreach (var turn in events.Where(e => e.EventType == "TurnEnded").OrderBy(e => e.Sequence))
        {
            var name = Text(turn.Data, "CharacterName") ?? "";
            var wasScared = crossings
                .Where(c => string.Equals(c.Name, name, StringComparison.Ordinal) && c.Sequence < turn.Sequence)
                .Select(c => (bool?)c.Scared)
                .LastOrDefault();

            if (wasScared is not true)
            {
                continue;
            }

            var action = Text(turn.Data, "AcceptedAction");
            rows.Add(
                $"| {turn.Round} | {turn.Turn} | {name} | {Text(turn.Data, "Result")} | " +
                $"{(string.IsNullOrWhiteSpace(action) ? "nothing took effect" : SingleLine(action))} |");
        }

        if (rows.Count == 0)
        {
            report.AppendLine("No character took a turn while scared: every crossing happened too late in the encounter to act on.");
            report.AppendLine();
            return;
        }

        report.AppendLine("### Turns taken while scared");
        report.AppendLine();
        report.AppendLine(
            "What was actually chosen under the pressure. **Nothing here shows that fear caused any of it** — " +
            "one run cannot separate a frightened character who ran from one who would have run anyway.");
        report.AppendLine();
        report.AppendLine("| Round | Turn | Character | Outcome | Action chosen |");
        report.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            report.AppendLine(row);
        }

        report.AppendLine();
    }

    /// <summary>
    /// Every attempt to frighten an enemy, with its whole derivation. The modifier column is the point: it
    /// shows the effective chance being built from the state of the fight, and that the words spoken —
    /// reproduced beside it — reached the odds nowhere at all.
    /// </summary>
    private static void WriteIntimidationSummary(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var attempts = events.Where(e => e.EventType == "IntimidationAttempted").OrderBy(e => e.Sequence).ToList();
        var steadyings = events.Where(e => e.EventType == "AllySteadied").OrderBy(e => e.Sequence).ToList();
        if (attempts.Count == 0 && steadyings.Count == 0)
        {
            return;
        }

        report.AppendLine("## Intimidation and reassurance");
        report.AppendLine();

        if (attempts.Count > 0)
        {
            var told = attempts.Count(a => IsTrue(a.Data, "Succeeded"));
            report.AppendLine(
                $"**{attempts.Count}** threat(s) made, **{told}** of which told. Each is one seeded draw against a " +
                "chance built only from the state of the fight. The spoken words are reproduced for the reader " +
                "and reached the odds nowhere: eloquence, length and phrasing carry no modifier at all.");
            report.AppendLine();

            foreach (var row in attempts)
            {
                var modifiers = JoinArray(row.Data, "Modifiers");
                report.AppendLine(
                    $"**{Text(row.Data, "ActorName")} → {Text(row.Data, "TargetName")} " +
                    $"(round {row.Round}, turn {row.Turn}): " +
                    $"{(IsTrue(row.Data, "Succeeded") ? "**told**" : "no flinch")}**");
                report.AppendLine();
                report.AppendLine($"- Base chance: {LongField(row.Data, "BaseChance")}");
                report.AppendLine($"- Modifiers: {(string.IsNullOrEmpty(modifiers) || modifiers == "none" ? "none — the base chance stood" : modifiers)}");
                report.AppendLine($"- Effective chance: {LongField(row.Data, "EffectiveChance")}; raw roll: {LongField(row.Data, "Roll")}");
                report.AppendLine(
                    $"- Target fear: {LongField(row.Data, "TargetFearBefore")} → {LongField(row.Data, "TargetFearAfter")}" +
                    $"{(Text(row.Data, "ScaredTransition") is { } t && t != "None" ? $" ({t})" : "")}");
                var speech = Text(row.Data, "AssociatedSpeech");
                report.AppendLine(speech is null
                    ? "- Threat spoken: none recorded"
                    : $"- Threat spoken (no mechanical effect): {Quote(SingleLine(speech))}");
                report.AppendLine();
            }
        }

        if (steadyings.Count > 0)
        {
            report.AppendLine("### Allies steadied");
            report.AppendLine();
            report.AppendLine("| Round | Turn | Who | Steadied | Fear | Effect |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var row in steadyings)
            {
                report.AppendLine(
                    $"| {row.Round} | {row.Turn} | {Text(row.Data, "ActorName")} | {Text(row.Data, "TargetName")} | " +
                    $"{LongField(row.Data, "TargetFearBefore")} → {LongField(row.Data, "TargetFearAfter")} | " +
                    $"{(IsTrue(row.Data, "NoEffect") ? "none — the ally was not afraid, and the turn was spent anyway" : SingleLine(Text(row.Data, "ScaredTransition")) is "RecoveredFromScared" ? "steadied, and no longer scared" : "steadied")} |");
            }

            report.AppendLine();
        }
    }

    /// <summary>
    /// How the one quality draw actually fell across the run: the count and damage of each band, who dealt
    /// and took the critical hits, and the fear those criticals moved. The damage-per-band figures are what
    /// make the v0.8 volatility change legible — a critical costs the same roll as a glance and doubles
    /// instead of halving.
    /// </summary>
    private static void WriteAttackQualitySummary(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var attacks = events
            .Where(e => e.EventType == "EngineAction"
                        && string.Equals(Text(e.Data, "Outcome", "OutcomeType"), "attack", StringComparison.Ordinal)
                        && IsTrue(e.Data, "Accepted")
                        && IsTrue(e.Data, "Outcome", "Hit"))
            .ToList();
        if (attacks.Count == 0)
        {
            return;
        }

        report.AppendLine("## Attack quality");
        report.AppendLine();
        report.AppendLine(
            "One draw per landed blow selects among all three bands, so a glancing blow and a critical hit " +
            "are opposite ends of the same roll rather than two separate checks.");
        report.AppendLine();

        report.AppendLine("| Quality | Blows | Damage dealt | Average |");
        report.AppendLine("| --- | --- | --- | --- |");
        foreach (var band in new[] { "Glancing", "Solid", "Critical" })
        {
            var inBand = attacks.Where(a => QualityOf(a) == band).ToList();
            var damage = inBand.Sum(a => LongField(a.Data, "Outcome", "DamageDealt"));
            report.AppendLine(
                $"| {band} | {inBand.Count} | {damage} | {(inBand.Count == 0 ? "-" : $"{(double)damage / inBand.Count:F1}")} |");
        }

        report.AppendLine();

        var criticals = attacks.Where(a => QualityOf(a) == "Critical").ToList();
        if (criticals.Count == 0)
        {
            report.AppendLine("No critical hit landed in this run.");
            report.AppendLine();
            return;
        }

        report.AppendLine("### Critical hits");
        report.AppendLine();
        report.AppendLine("| Round | Attacker | Recipient | Damage | Killed | Fear moved |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var row in criticals)
        {
            var moved = FearChangesOn(row);
            report.AppendLine(
                $"| {row.Round} | {Text(row.Data, "Outcome", "AttackerName")} | {Text(row.Data, "Outcome", "TargetName")} | " +
                $"{LongField(row.Data, "Outcome", "DamageDealt")} | " +
                $"{(IsTrue(row.Data, "Outcome", "TargetDied") ? "yes" : "no")} | {moved} |");
        }

        report.AppendLine();
    }

    /// <summary>The quality band of a landed blow, tolerating an older trace that recorded only a glancing flag.</summary>
    private static string QualityOf(TraceRow attack) =>
        Text(attack.Data, "Outcome", "Quality")
        ?? (IsTrue(attack.Data, "Outcome", "Glancing") ? "Glancing" : "Solid");

    /// <summary>
    /// How a draw turned its roll into its result, in the draw's own words. Falls back to the raw roll alone
    /// rather than inventing a comparison, because a draw that recorded none has nothing to compare against.
    /// </summary>
    private static string DescribeDraw(TraceRow row) =>
        Text(row.Data, "Comparison") is { Length: > 0 } comparison
            ? comparison
            : $"rolled {Text(row.Data, "RawRoll")}";

    /// <summary>The fear an attack moved, rendered for a table cell. "none" when it moved none.</summary>
    private static string FearChangesOn(TraceRow attack)
    {
        if (Property(attack.Data, "Outcome") is not { } outcome
            || !outcome.TryGetProperty("FearChanges", out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() == 0)
        {
            return "none";
        }

        var parts = list.EnumerateArray().Select(change =>
        {
            var name = Text(change, "CharacterName");
            var before = LongField(change, "Before");
            var after = LongField(change, "After");
            return $"{name} {before}→{after}";
        });
        return string.Join("; ", parts);
    }

    /// <summary>Every character's final fear from the final-state snapshot, so departures keep the nerve they left with.</summary>
    /// <remarks>
    /// The characters live under the snapshot's <c>State</c> wrapper, not at its root. Reading the root
    /// returned nothing at all, and because the morale table takes the UNION of this and the characters who
    /// had fear events, the failure was invisible: the table still rendered, just silently missing everyone
    /// whose nerve never moved. A gpt-5.4 run listed two of four characters and read as though Rowan and
    /// Elara had been left out on purpose.
    /// </remarks>
    private static Dictionary<string, int> FinalFearByName(JsonElement? finalState)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!TryGet(finalState, out var state, "State")
            || Property(state, "Characters") is not { ValueKind: JsonValueKind.Array } characters)
        {
            return result;
        }

        foreach (var character in characters.EnumerateArray())
        {
            var name = Text(character, "Name");
            if (!string.IsNullOrEmpty(name))
            {
                result[name] = (int)LongField(character, "Fear");
            }
        }

        return result;
    }

    /// <summary>
    /// What the rulebook stage cost this run, and — when an experimental selection mode was configured —
    /// how it compared with sending the whole book. The figures come from the consultation events, so this
    /// section is the run's own evidence rather than a claim carried in from elsewhere.
    /// </summary>
    private static void WriteRulebookEfficiency(StringBuilder report, IReadOnlyList<TraceRow> events, JsonElement? manifest)
    {
        var consultations = events.Where(e => e.EventType == "RulebookConsultation").ToList();
        if (consultations.Count == 0)
        {
            return;
        }

        var count = consultations.Count;
        var cacheHits = consultations.Count(c => IsTrue(c.Data, "CacheHit"));
        var called = count - cacheHits;
        var cardsSum = consultations.Sum(c => LongField(c.Data, "CardCount"));
        var charsSum = consultations.Sum(c => LongField(c.Data, "TotalRequestChars"));
        var inTokens = consultations.Sum(c => LongField(c.Data, "InputTokens"));
        var citedSum = consultations.Sum(c => CountOf(c.Data, "CitedRules"));
        var fallbacks = consultations.Count(c => Text(c.Data, "SelectionFallback") is { Length: > 0 });
        var modes = consultations
            .Select(c => Text(c.Data, "SelectionMode") ?? "WholeRulebook")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        report.AppendLine("## Rulebook efficiency");
        report.AppendLine();
        report.AppendLine($"- Configured selection mode(s): **{string.Join(", ", modes)}**.");
        report.AppendLine($"- Consultations: **{count}**; resolver actually called **{called}** time(s) ({cacheHits} served from cache).");
        report.AppendLine($"- Cards supplied: **{Average(cardsSum, count)}** per consultation; cited: **{Average(citedSum, count)}**.");
        report.AppendLine($"- Request size: **{Average(charsSum, count)}** characters per consultation.");
        report.AppendLine(called == 0 || inTokens == 0
            ? "- Resolver input tokens: not reported by the provider for this run."
            : $"- Resolver input tokens: **{Average(inTokens, called)}** per resolver call ({inTokens} total).");
        report.AppendLine($"- Selection fallbacks to the full bounded rulebook: **{fallbacks}**.");
        report.AppendLine();
        report.AppendLine(
            "The standing measurement and the strategies evaluated against it are in " +
            "`reports/rulebook-efficiency.md`; this section is only what this run itself cost.");
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
            // A traced message carries its concatenated Text AND the Contents blocks that text came from, so
            // adding both counted every character twice and reported a request roughly double its real size —
            // which is exactly the wrong direction to be wrong in when the point is judging headroom against a
            // context window. Prefer the concatenated text, and fall back to the blocks only when there is none
            // (a tool-call or reasoning-only message).
            var text = Scalar(message, "Text");
            if (text.Length > 0)
            {
                total += text.Length;
                continue;
            }

            if (Property(message, "Contents") is { ValueKind: JsonValueKind.Array } contents)
            {
                foreach (var content in contents.EnumerateArray())
                {
                    total += Scalar(content, "Text").Length;
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

    // -------------------------------------------------------------------------------------------
    // Environmental objects (v0.9)
    // -------------------------------------------------------------------------------------------

    /// <summary>Room objects with a "HitChanceModifier" field — the distinguishing shape of a cover object.</summary>
    private static IEnumerable<JsonElement> Covers(JsonElement state) =>
        state.TryGetProperty("Room", out var room)
        && room.TryGetProperty("Objects", out var objects)
        && objects.ValueKind == JsonValueKind.Array
            ? objects.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("HitChanceModifier", out _))
            : [];

    private static string? NameOfCharacter(JsonElement state, string? id)
    {
        if (id is null)
        {
            return null;
        }

        foreach (var c in Characters(state))
        {
            if (string.Equals(Scalar(c, "Id"), id, StringComparison.OrdinalIgnoreCase))
            {
                return Scalar(c, "Name");
            }
        }

        return id;
    }

    /// <summary>
    /// Environmental objects (v0.9): each object's initial and final state; the full occupancy timeline; how
    /// covered attacks actually went; and every deliberate strike on an object. Reconstructed from the initial
    /// and final authoritative state plus the trace, so it is independent of any narration. Skipped when the
    /// scenario seeded no cover object, so a pre-v0.9 run adds no empty section.
    /// </summary>
    private static void WriteEnvironmentalObjects(
        StringBuilder report, JsonElement? manifest, JsonElement? finalState, IReadOnlyList<TraceRow> events)
    {
        var initial = new List<(string Id, string Name, int Capacity, int MaxDurability, int InitialDurability)>();
        if (TryGet(manifest, out var initState, "InitialState"))
        {
            foreach (var o in Covers(initState))
            {
                initial.Add((
                    Scalar(o, "Id"), Scalar(o, "Name"), (int)LongField(o, "Capacity"),
                    (int)LongField(o, "MaximumDurability"), (int)LongField(o, "CurrentDurability")));
            }
        }

        if (initial.Count == 0)
        {
            return;
        }

        var final = new Dictionary<string, (int Durability, string State, string? Occupant)>(StringComparer.OrdinalIgnoreCase);
        if (TryGet(finalState, out var fs, "State"))
        {
            foreach (var o in Covers(fs))
            {
                var maxDurability = (int)LongField(o, "MaximumDurability");
                var durability = (int)LongField(o, "CurrentDurability");
                var state = durability <= 0 ? "Destroyed" : durability < maxDurability ? "Damaged" : "Intact";
                final[Scalar(o, "Id")] = (durability, state, NameOfCharacter(fs, Text(o, "CurrentOccupantId")));
            }
        }

        var destroyedBy = events
            .Where(e => e.EventType == "EnvironmentalObjectDestroyed")
            .GroupBy(e => Text(e.Data, "ObjectId") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        report.AppendLine("## Environmental objects");
        report.AppendLine();
        report.AppendLine(
            "Every environmental object the scenario seeded: its condition at the start and end of the run, " +
            "and, when it was destroyed, who destroyed it.");
        report.AppendLine();
        report.AppendLine("| Object | Capacity | Max durability | Initial state | Final durability | Final state | Final occupant | Destroyed by |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var (id, name, capacity, maxDurability, initialDurability) in initial)
        {
            var initialState = initialDurability <= 0 ? "Destroyed" : initialDurability < maxDurability ? "Damaged" : "Intact";
            var (finalDurability, finalObjectState, finalOccupant) = final.TryGetValue(id, out var f)
                ? f : (initialDurability, initialState, null);
            var destroyer = destroyedBy.TryGetValue(id, out var row)
                ? Text(row.Data, "DestroyedByCharacterName") ?? "(unknown)"
                : "—";
            report.AppendLine(
                $"| {name} | {capacity} | {maxDurability} | {initialState} | {finalDurability} | {finalObjectState} | " +
                $"{finalOccupant ?? "(nobody)"} | {destroyer} |");
        }

        report.AppendLine();

        WriteOccupancyTimeline(report, events);
        WriteCoverEffectiveness(report, events);
        WriteObjectDamage(report, events);
    }

    /// <summary>
    /// Every entry into and exit from environmental cover, in order, with its cause. Reconstructed from the
    /// <c>InCover</c> status transitions — every occupancy change is one of these, whether voluntary, forced
    /// by an exposing action, caused by the cover's destruction, or a consequence of leaving active play.
    /// </summary>
    private static void WriteOccupancyTimeline(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var transitions = events
            .Where(e => e.EventType is "StatusApplied" or "StatusConsumed" or "StatusExpired" or "StatusRemoved"
                        && string.Equals(Text(e.Data, "Kind"), "InCover", StringComparison.Ordinal))
            .OrderBy(e => e.Sequence)
            .ToList();

        if (transitions.Count == 0)
        {
            return;
        }

        report.AppendLine("### Occupancy timeline");
        report.AppendLine();
        report.AppendLine(
            "Every entry into and exit from cover. \"Entered\" is always voluntary (`take_cover`); every other " +
            "transition ends the occupancy, whether by choice, by an exposing action, by the cover's " +
            "destruction, or because the occupant left active play.");
        report.AppendLine();
        report.AppendLine("| Round.Turn | Character | Transition | Cause |");
        report.AppendLine("| --- | --- | --- | --- |");
        foreach (var row in transitions)
        {
            var transition = row.EventType == "StatusApplied" ? "Entered" : "Left";
            report.AppendLine(
                $"| {row.Round}.{row.Turn} | {Text(row.Data, "TargetCharacterName")} | {transition} | " +
                $"{CellText(Text(row.Data, "Cause"))} |");
        }

        report.AppendLine();
    }

    /// <summary>
    /// How attacks against covered characters actually went: direct hits despite cover, interceptions, and
    /// ordinary misses, per cover object. Only the hit itself is reported as prevented on an interception —
    /// never a damage figure, since an intercepted attack never reaches the quality draw that would have
    /// decided one, so no such number can be calculated honestly.
    /// </summary>
    private static void WriteCoverEffectiveness(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var attacks = events.Where(e => e.EventType == "AttackAgainstCover").OrderBy(e => e.Sequence).ToList();
        if (attacks.Count == 0)
        {
            return;
        }

        report.AppendLine("### Cover effectiveness");
        report.AppendLine();
        report.AppendLine(
            "Every attack whose target was sheltering behind cover, classified by what the single hit-check " +
            "roll actually decided. A direct hit found its mark despite the cover; an interception is a hit the " +
            "cover — not the roll alone — turned aside, costing the object one point of durability; an ordinary " +
            "miss would have missed with or without the cover, which changed nothing.");
        report.AppendLine();
        report.AppendLine("| Cover | Attacks against it | Direct hits | Interceptions | Ordinary misses | Durability lost to interceptions | Destroyed by interception |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var group in attacks.GroupBy(e => Text(e.Data, "CoverName") ?? "", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var direct = group.Count(e => Text(e.Data, "Classification") == "DirectHit");
            var intercepted = group.Count(e => Text(e.Data, "Classification") == "Intercepted");
            var missed = group.Count(e => Text(e.Data, "Classification") == "OrdinaryMiss");
            var durabilityLost = group.Where(e => Text(e.Data, "Classification") == "Intercepted")
                .Sum(e => LongField(e.Data, "DurabilityBefore") - LongField(e.Data, "DurabilityAfter"));
            var destroyedCount = group.Count(e => Text(e.Data, "Classification") == "Intercepted" && IsTrue(e.Data, "Destroyed"));
            report.AppendLine(
                $"| {group.Key} | {group.Count()} | {direct} | {intercepted} | {missed} | {durabilityLost} | {destroyedCount} |");
        }

        report.AppendLine();

        report.AppendLine("| Round.Turn | Attacker | Target | Cover | Pre-cover chance | Covered chance | Roll | Result |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in attacks)
        {
            var classification = Text(row.Data, "Classification") switch
            {
                "DirectHit" => "direct hit despite cover",
                "Intercepted" => "cover intercepted the blow",
                _ => "ordinary miss"
            };
            report.AppendLine(
                $"| {row.Round}.{row.Turn} | {Text(row.Data, "AttackerName")} | {Text(row.Data, "TargetName")} | " +
                $"{Text(row.Data, "CoverName")} | {Text(row.Data, "PreCoverHitChance")} | {Text(row.Data, "CoveredHitChance")} | " +
                $"{Text(row.Data, "RawRoll")} | {classification} |");
        }

        report.AppendLine();
    }

    /// <summary>Every deliberate strike on an environmental object, accepted or rejected.</summary>
    private static void WriteObjectDamage(StringBuilder report, IReadOnlyList<TraceRow> events)
    {
        var attempts = events.Where(e => e.EventType == "EnvironmentalObjectDamaged").OrderBy(e => e.Sequence).ToList();
        if (attempts.Count == 0)
        {
            return;
        }

        report.AppendLine("### Object damage");
        report.AppendLine();
        report.AppendLine("Every deliberate attempt to strike an environmental object rather than a character, accepted or rejected.");
        report.AppendLine();
        report.AppendLine("| Round.Turn | Actor | Weapon | Object | Damage | Durability before | Durability after | Destroyed | Result |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in attempts)
        {
            var accepted = string.Equals(Text(row.Data, "ValidationResult"), "accepted", StringComparison.Ordinal);
            var result = accepted ? "accepted" : $"rejected ({Text(row.Data, "RejectionReason")})";
            report.AppendLine(
                $"| {row.Round}.{row.Turn} | {Text(row.Data, "ActorName")} | {Text(row.Data, "WeaponName") ?? "—"} | " +
                $"{Text(row.Data, "ObjectName") ?? "—"} | {Text(row.Data, "DamageApplied") ?? "—"} | " +
                $"{Text(row.Data, "DurabilityBefore") ?? "—"} | {Text(row.Data, "DurabilityAfter") ?? "—"} | " +
                $"{(IsTrue(row.Data, "Destroyed") ? "yes" : "no")} | {result} |");
        }

        report.AppendLine();
    }

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
                // Every draw authors its own Comparison, because only the draw knows what its roll was
                // measured against. Rendering RawRoll against the generic Threshold field instead produced
                // "rolled 99 vs 25 → critical" for a quality draw, whose Threshold is the top of the
                // GLANCING band — a number at the opposite end of the roll from the band that actually won.
                "RngDraw" => $"*[{Text(row.Data, "Purpose")}: {DescribeDraw(row)} → {Text(row.Data, "Result")}]*",
                "EngineAction" => TranscribeEngineAction(row),
                // A departure from active combat, in plain readable terms — never "fallen" for a survivor.
                "CharacterSurrendered" => $"*— {Text(row.Data, "CharacterName")} surrenders and takes no further part in the fight (still alive)*",
                "CharacterEscaped" => $"*— {Text(row.Data, "CharacterName")} escapes through the {Text(row.Data, "ExitName")} and leaves the encounter (still alive)*",
                // Negotiation, abilities and statuses (v0.7). An offer and an accepted surrender read
                // deliberately differently: only one of them has changed anything.
                "SurrenderOfferMade" =>
                    $"*— {Text(row.Data, "OffererName")} offers {Text(row.Data, "RecipientName")} terms to end their " +
                    $"fight: {DescribeOfferTerms(row)}. Nothing has changed hands; {Text(row.Data, "OffererName")} is " +
                    $"still armed and still a target, and only {Text(row.Data, "RecipientName")} may accept*",
                "SurrenderOfferResolved" when Text(row.Data, "NewState") != "Accepted" =>
                    $"*— {Text(row.Data, "OffererName")}'s offer to {Text(row.Data, "RecipientName")} is " +
                    $"{Text(row.Data, "NewState")?.ToLowerInvariant()} ({Text(row.Data, "Cause")}); nothing transferred*",
                "SurrenderAgreementRecorded" =>
                    $"*— {Text(row.Data, "AcceptedByName")} accepts {Text(row.Data, "OffererName")}'s surrender: " +
                    $"{DescribeAgreementTerms(row)}*",
                "AbilityUsed" => TranscribeAbilityUse(row),
                "AttackRedirected" =>
                    $"*[{Text(row.Data, "AttackerName")} struck at {Text(row.Data, "IntendedTargetName")}, but " +
                    $"{Text(row.Data, "AuthoritativeTargetName")} was guarding them and took the blow — " +
                    $"{Text(row.Data, "RngDrawCount")} draw(s), the ordinary count, with no extra roll]*",
                "StatusApplied" or "StatusConsumed" or "StatusExpired" or "StatusRemoved" =>
                    $"*[status {Text(row.Data, "Kind")?.ToLowerInvariant()} " +
                    $"{Text(row.Data, "Transition")?.ToLowerInvariant()} on {Text(row.Data, "TargetCharacterName")} " +
                    $"(from {Text(row.Data, "SourceCharacterName")}): {Text(row.Data, "Cause")}]*",
                // Only a surplus *tool call* earns a line in the story: that is a model trying to act twice,
                // and a reader should see it happen. Loose text alongside an accepted call is usually the
                // model thinking aloud, and on a verbose model it appears on nearly every turn — so it stays
                // in the trace and in the state-grounding table rather than burying the transcript.
                "PostResolutionOutputDiscarded" when string.Equals(Text(row.Data, "DiscardedKind"), "tool-call", StringComparison.Ordinal) =>
                    $"*[{Text(row.Data, "CharacterName")}'s turn was already resolved; the further " +
                    $"`{Text(row.Data, "ToolName")}` was discarded without effect: \"{CellText(Text(row.Data, "DiscardedContent"))}\"]*",
                // Morale (v0.8). A point of nerve moving and a nerve actually breaking are separate lines,
                // because only the second is something anyone in the room could see.
                "FearChanged" => TranscribeFearChange(row),
                "IntimidationAttempted" =>
                    $"*— {Text(row.Data, "ActorName")} threatens {Text(row.Data, "TargetName")} openly, and it " +
                    $"{(IsTrue(row.Data, "Succeeded") ? "tells" : "does not tell")}. Nothing else changes: " +
                    $"{Text(row.Data, "TargetName")} keeps their weapon, their belongings and their turn*",
                "AllySteadied" =>
                    $"*— {Text(row.Data, "ActorName")} spends the turn steadying {Text(row.Data, "TargetName")}" +
                    $"{(IsTrue(row.Data, "NoEffect") ? ", who had not lost their nerve — the turn buys nothing" : "")}*",
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
                "SpeechNotHeard" =>
                    $"*[{Text(row.Data, "CharacterName")} had already spoken this turn; not heard again: " +
                    $"\"{Text(row.Data, "Unheard")}\"]*",
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
    /// One fear change, for the transcript. A crossing of the public threshold reads as something anyone
    /// would notice; a change below it reads as the private bookkeeping it is, in brackets.
    /// </summary>
    private static string TranscribeFearChange(TraceRow row)
    {
        var who = Text(row.Data, "CharacterName");
        var cause = Text(row.Data, "CauseDetail");

        return Text(row.Data, "ScaredTransition") switch
        {
            "BecameScared" => $"*— {who}'s nerve goes, and everyone present can see it ({cause})*",
            "RecoveredFromScared" => $"*— {who} has their nerve back and no longer looks afraid ({cause})*",
            _ when IsTrue(row.Data, "Absorbed") =>
                $"*[{who}'s nerve is unchanged at {Text(row.Data, "FearAfter")} — already at the limit ({cause})]*",
            _ => $"*[{who}'s nerve: {Text(row.Data, "FearBefore")} → {Text(row.Data, "FearAfter")} ({cause})]*"
        };
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
    /// <summary>The exact terms of an offer, for the transcript: the items promised, and the weapon if promised.</summary>
    private static string DescribeOfferTerms(TraceRow row)
    {
        var parts = new List<string>();
        var items = JoinArray(row.Data, "OfferedItemNames");
        if (!string.IsNullOrWhiteSpace(items))
        {
            parts.Add(items);
        }

        if (IsTrue(row.Data, "ForfeitWeapon") && Text(row.Data, "WeaponName") is { } weapon)
        {
            parts.Add($"their {weapon}");
        }

        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }

    /// <summary>What an accepted surrender actually moved, for the transcript.</summary>
    private static string DescribeAgreementTerms(TraceRow row)
    {
        var items = JoinArray(row.Data, "TransferredItemNames");
        var tribute = string.IsNullOrWhiteSpace(items) ? "no items changed hands" : $"took {items}";
        var weapon = Text(row.Data, "ForfeitedWeaponName") is { } forfeited
            ? $", the {forfeited} forfeited to the floor"
            : "";
        var disarmed = IsTrue(row.Data, "OffererDisarmed")
            ? $", and {Text(row.Data, "OffererName")} is disarmed and out of the fight"
            : "";
        return $"{tribute}{weapon}{disarmed}";
    }

    /// <summary>One ability use in the transcript, accepted or refused, with what it left behind.</summary>
    private static string TranscribeAbilityUse(TraceRow row)
    {
        var actor = Text(row.Data, "ActorName");
        var ability = Text(row.Data, "AbilityName");
        var target = Text(row.Data, "TargetName") is { } t && t != actor ? $" on {t}" : "";

        if (!string.Equals(Text(row.Data, "ValidationResult"), "accepted", StringComparison.Ordinal))
        {
            return $"*[{actor}'s {ability}{target} was refused ({Text(row.Data, "RejectionReason")}); " +
                   "no charge spent and the turn is not consumed]*";
        }

        var healing = LongField(row.Data, "HealingPerformed");
        var healed = healing > 0 ? $", restoring {healing} health" : "";
        var statuses = JoinArray(row.Data, "StatusesApplied");
        var applied = string.IsNullOrWhiteSpace(statuses) ? "" : $", applying {statuses}";
        var left = Text(row.Data, "RemainingUsesAfter") is { } remaining ? $" ({remaining} use(s) left)" : "";
        return $"*[{actor} used {ability}{target}{healed}{applied}{left}]*";
    }

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

            case "SpeechNotHeard":
                yield return Bullets(
                    ("Character", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Already spoken", $"{Text(d, "SpeechActsAlready")} of {Text(d, "Allowance")} this turn"),
                    ("Outcome", "recorded, not delivered to anyone"));
                yield return "";
                yield return Quote($"**Not heard:** {Text(d, "Unheard")}");
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

                // A surrendered character has been disarmed by the acceptance, so "none" here is a fact about
                // the agreement rather than a character who never carried a weapon: say so plainly.
                var surrendered = string.Equals(disposition, "Surrendered", StringComparison.OrdinalIgnoreCase);
                if (surrendered && weapon == "none")
                {
                    weapon = "none — disarmed on surrender";
                }

                report.AppendLine(
                    $"| {Scalar(character, "Name")} | {Scalar(character, "Team")} | {health}/{Scalar(character, "MaxHealth")} " +
                    $"| {disposition} | {location} | {weapon} | {NamesOf(character, "Inventory")} " +
                    $"| {DescriptionsOf(character, "Injuries")} |");
            }

            report.AppendLine();
            WriteFinalStatusesAndOffers(report, state);
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

    /// <summary>
    /// The status effects still standing when the run stopped, and the whole surrender-offer ledger — pending,
    /// settled and accepted. Both are authoritative state, so a reader can check the transcript against them.
    /// </summary>
    private static void WriteFinalStatusesAndOffers(StringBuilder report, JsonElement state)
    {
        if (Property(state, "Statuses") is { ValueKind: JsonValueKind.Array } statuses && statuses.GetArrayLength() > 0)
        {
            report.AppendLine("**Status effects still standing**");
            report.AppendLine();
            report.AppendLine("| Status | On | From | Modifier | Expiry rule |");
            report.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var status in statuses.EnumerateArray())
            {
                report.AppendLine(
                    $"| {Scalar(status, "Kind")} | {Scalar(status, "TargetCharacterId")} | {Scalar(status, "SourceCharacterId")} | " +
                    $"{Scalar(status, "Modifier")} | {Scalar(status, "ExpiryRule")} |");
            }

            report.AppendLine();
        }

        if (Property(state, "SurrenderOffers") is { ValueKind: JsonValueKind.Array } offers && offers.GetArrayLength() > 0)
        {
            report.AppendLine("**Surrender offers (the complete ledger)**");
            report.AppendLine();
            report.AppendLine("| Offer | Offerer | Recipient | Promised items | Weapon promised | State | Cause |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var offer in offers.EnumerateArray())
            {
                var items = JoinArray(offer, "OfferedItemIds");
                report.AppendLine(
                    $"| {Scalar(offer, "Id")} | {Scalar(offer, "OffererId")} | {Scalar(offer, "RecipientId")} | " +
                    $"{(string.IsNullOrWhiteSpace(items) ? "none" : items)} | " +
                    $"{(IsTrue(offer, "ForfeitWeapon") ? "yes" : "no")} | {Scalar(offer, "State")} | " +
                    $"{CellText(Scalar(offer, "ResolutionCause"))} |");
            }

            report.AppendLine();
        }

        if (Property(state, "SurrenderAgreements") is { ValueKind: JsonValueKind.Array } agreements && agreements.GetArrayLength() > 0)
        {
            report.AppendLine("**Surrender agreements (durable evidence)**");
            report.AppendLine();
            report.AppendLine("| Agreement | Offer | Who yielded | Who accepted | Items transferred | Weapon forfeited | Round.Turn |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var agreement in agreements.EnumerateArray())
            {
                var items = JoinArray(agreement, "TransferredItemIds");
                report.AppendLine(
                    $"| {Scalar(agreement, "Id")} | {Scalar(agreement, "OfferId")} | {Scalar(agreement, "OffererId")} | " +
                    $"{Scalar(agreement, "AcceptedById")} | {(string.IsNullOrWhiteSpace(items) ? "none" : items)} | " +
                    $"{(string.IsNullOrWhiteSpace(Scalar(agreement, "ForfeitedWeaponId")) ? "none promised" : Scalar(agreement, "ForfeitedWeaponId"))} | " +
                    $"{Scalar(agreement, "AcceptedRound")}.{Scalar(agreement, "AcceptedTurn")} |");
            }

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
