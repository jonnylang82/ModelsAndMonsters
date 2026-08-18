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
        WritePerAgentActivity(report, events);
        WriteCommunicationAndObjects(report, events);
        WriteScenario(report, manifest);
        WriteTeams(report, manifest);
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

        if (objectEvents.Count == 0 && speechEvents.Count == 0)
        {
            return;
        }

        report.AppendLine("## Communication and object interaction");
        report.AppendLine();

        report.AppendLine($"Public utterances: **{speechEvents.Count}**.");
        report.AppendLine();

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
                "Narration" => $"**DM:** {Text(row.Data, "Narration")}",
                // Public speech, kept visually distinct from DM narration and from private questions.
                "CharacterSpeech" => $"**{Text(row.Data, "SpeakerName")} says:** \"{Text(row.Data, "Message")}\"",
                "CharacterQuestion" => $"**{Text(row.Data, "CharacterName")} asks (private):** \"{Text(row.Data, "Question")}\"",
                "DungeonMasterAnswer" => $"**DM (private to {Text(row.Data, "CharacterName")}):** {Text(row.Data, "Answer")}",
                "CharacterPassed" => $"**{Text(row.Data, "CharacterName")} holds back:** \"{Text(row.Data, "Reason")}\"",
                "DmAdjudication" => TranscribeRuling(row),
                "TargetResolved" => $"*[target — {Text(row.Data, "Note")}]*",
                "RngDraw" =>
                    $"*[{Text(row.Data, "Purpose")}: rolled {Text(row.Data, "RawRoll")} vs {Text(row.Data, "Threshold")} " +
                    $"→ {Text(row.Data, "Result")}]*",
                "EngineAction" => TranscribeEngineAction(row),
                "TurnSkipped" => $"*— {Text(row.Data, "CharacterName")} lies fallen; their turn is skipped*",
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
        return isOver ? $"*— {Text(row.Data, "Description")}*" : null;
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
                    ("Description", Text(d, "Description")));
                break;

            case "TurnSkipped":
                yield return Bullets(
                    ("Character", $"{Text(d, "CharacterName")} (`{Text(d, "CharacterId")}`)"),
                    ("Team", Text(d, "Team")),
                    ("Reason", Text(d, "Reason")));
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
                    ("Rounds played", Text(d, "RoundsPlayed")),
                    ("Survivors", JoinArray(d, "Survivors")),
                    ("Casualties", JoinArray(d, "Casualties")));
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
            report.AppendLine("| Character | Team | Health | Armour | Weapon | Inventory | Injuries | Alive |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var character in Characters(state))
            {
                var health = int.TryParse(Scalar(character, "Health"), out var h) ? h : 0;
                var weapon = character.TryGetProperty("Weapon", out var w) && w.ValueKind == JsonValueKind.Object
                    ? $"{Scalar(w, "Name")} (damage {Scalar(w, "Damage")})"
                    : "none";

                report.AppendLine(
                    $"| {Scalar(character, "Name")} | {Scalar(character, "Team")} | {health}/{Scalar(character, "MaxHealth")} " +
                    $"| {Scalar(character, "Armour")} | {weapon} | {NamesOf(character, "Inventory")} " +
                    $"| {DescriptionsOf(character, "Injuries")} | {(health > 0 ? "yes" : "no")} |");
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
