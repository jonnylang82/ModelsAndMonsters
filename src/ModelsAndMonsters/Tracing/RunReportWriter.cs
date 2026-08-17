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

    public static string ReportPath(string runDirectory) => Path.Combine(runDirectory, "report.md");

    /// <summary>Writes report.md for a run directory and returns its path.</summary>
    public static string Write(string runDirectory)
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
        WriteScenario(report, manifest);
        WriteTranscript(report, events);
        WriteTrace(report, events);
        WriteFinalState(report, finalState);

        var path = ReportPath(runDirectory);
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
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
        Row(report, "Scenario", Text(manifest, "Scenario", "Name") ?? Text(manifest, "Scenario", "Id"));
        Row(report, "Terminal condition", Text(finalState, "TerminalCondition"));
        Row(report, "Rounds played", Text(finalState, "RoundsPlayed"));
        Row(report, "Trace events", events.Count.ToString(CultureInfo.InvariantCulture));
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
                // The intent is transcribed where it was spoken, not where it was ruled on, so the
                // reader sees the character act before the engine and the DM respond to it.
                "ToolCallDispatched" => TranscribeIntent(row),
                "Narration" => $"**DM:** {Text(row.Data, "Narration")}",
                "CharacterQuestion" => $"**{Text(row.Data, "CharacterName")} asks:** \"{Text(row.Data, "Question")}\"",
                "DungeonMasterAnswer" => $"**DM:** {Text(row.Data, "Answer")}",
                "CharacterPassed" => $"**{Text(row.Data, "CharacterName")} holds back:** \"{Text(row.Data, "Reason")}\"",
                "DmAdjudication" => TranscribeRuling(row),
                "EngineAction" => TranscribeEngineAction(row),
                "ContextWindowSaturated" =>
                    $"*[{Text(row.Data, "AgentName")} sent ~{Text(row.Data, "EstimatedSentTokens")} tokens but only " +
                    $"{Text(row.Data, "ReportedInputTokens")} were processed — ~{Text(row.Data, "EstimatedDroppedTokens")} " +
                    "tokens of earlier history were discarded before the model saw them]*",
                "ModelResponseTruncated" =>
                    $"*[{Text(row.Data, "AgentName")}'s reply hit the output-token limit — {Text(row.Data, "Effect")}]*",
                "AdjudicationCorrected" =>
                    $"*[harness corrected `{Text(row.Data, "Parameter")}` from \"{Text(row.Data, "DungeonMasterValue")}\" " +
                    $"to \"{Text(row.Data, "CorrectedValue")}\"]*",
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

            case "EngineAction":
                yield return Bullets(
                    ("Action", $"`{Text(d, "ActionType")}` {Flatten(Property(d, "Action"))}"),
                    ("Accepted", Text(d, "Accepted")),
                    ("Rejection", Text(d, "RejectionReason")),
                    ("Outcome", Text(d, "OutcomeSummary") ?? Text(d, "RejectionMessage")));
                yield return "";
                yield return StateTable(Property(d, "StateBefore"), Property(d, "StateAfter"));
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

        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        Row(report, "Terminal condition", Text(finalState, "TerminalCondition"));
        Row(report, "Rounds played", Text(finalState, "RoundsPlayed"));
        Row(report, "Trace events", Text(finalState, "TraceEventCount"));
        report.AppendLine();

        if (TryGet(finalState, out var state, "State"))
        {
            report.AppendLine("| Character | Health | Armour | Weapon | Inventory | Injuries | Alive |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var character in Characters(state))
            {
                var health = int.TryParse(Scalar(character, "Health"), out var h) ? h : 0;
                var weapon = character.TryGetProperty("Weapon", out var w) && w.ValueKind == JsonValueKind.Object
                    ? $"{Scalar(w, "Name")} (damage {Scalar(w, "Damage")})"
                    : "none";

                report.AppendLine(
                    $"| {Scalar(character, "Name")} | {health}/{Scalar(character, "MaxHealth")} " +
                    $"| {Scalar(character, "Armour")} | {weapon} | {NamesOf(character, "Inventory")} " +
                    $"| {DescriptionsOf(character, "Injuries")} | {(health > 0 ? "yes" : "no")} |");
            }

            report.AppendLine();
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
