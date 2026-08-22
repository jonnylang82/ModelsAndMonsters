using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// Routes an intent to rule cards through the engine's ACTION surface: the model is shown one line per engine
/// action — what it is, and what it must be told apart from — and returns one action label plus the actions it
/// is ruling out. Code expands that to cards from metadata declared on the cards themselves.
/// </summary>
/// <remarks>
/// <para>
/// Every other selector maps intent to cards in one hop. This one inserts a bounded intermediate — intent to
/// engine action to cards — so the model never reads the rulebook, only the action surface (~19 lines, frozen
/// by the engine, not growing a line per card). The field doing the work embeddings and summaries could not is
/// <c>ruleOut</c>: naming what an intent is NOT recovers the cases similarity cannot reach, where the card the
/// decision turns on is the one the intent never mentions.
/// </para>
/// <para>
/// It is deliberately paranoid, exactly like <see cref="CompactIndexSelector"/>: it is a separate stateless
/// call, it caches on the static index, and every failure falls back to the whole bounded rulebook. The
/// fallbacks are split SEMANTIC (the model said it was unclear, or named an action outside the closed set —
/// the boundaries) from MECHANICAL (unparseable, or the call threw — the machinery), because collapsing the
/// two turns a diagnostic into a number. Nothing here parses game language: the model returns a label, code
/// validates it against the closed action set and looks up cards. No verb lists, no keyword tables.
/// </para>
/// </remarks>
public sealed class ActionRoutingSelector : ModelAgent, IRuleSelector
{
    /// <summary>The selector's whole instruction. Public so an offline measurement can size a request exactly.</summary>
    public const string SystemPrompt = """
        You are an action router for a game engine. You are given a character's stated intent, in their own
        words, and a list of the engine's actions — one line each saying what the action IS and, where it
        matters, what it must be told APART from. Your only job is to name the engine action that is the
        character's PRIMARY deed, and the actions you are ruling out to be sure it is that one.

        - Choose exactly one primary action, from the list and nothing else. Pick the deed that actually
          happens this turn; a hoped-for second act, or words spoken alongside a deed, is not the primary one.
        - In ruleOut, list the actions this intent could be confused with and that you are deciding against —
          especially the ones the list says this action must be told apart from. It may be empty.
        - Set confidence to "unclear" when the intent could genuinely be two of these actions, or none.
        - You NEVER decide whether the action is allowed, whether it is valid right now, or how it turns out.
          Something downstream reads the full rules and decides; you only name what it should read.

        Reply with ONLY a JSON object of exactly this shape and nothing else:
        {"action": "<action_id>", "ruleOut": ["<action_id>", "..."], "confidence": "clear" | "unclear"}
        """;

    private readonly IRuleRepository _repository;
    private readonly Dictionary<string, RuleSelection> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _cacheEnabled;

    /// <summary>The closed set of engine actions the model may name, in catalog order.</summary>
    private readonly IReadOnlyList<string> _actionIds;
    private readonly HashSet<string> _knownActions;
    private readonly ChatResponseFormat _schema;

    public ActionRoutingSelector(
        AgentModelProfile profile, TracingChatClient client, IRuleRepository repository, bool cacheEnabled = true)
        : base("RuleActionRouter", profile, client, SystemPrompt)
    {
        _repository = repository;
        _cacheEnabled = cacheEnabled;
        _actionIds = RoutableActions(repository);
        _knownActions = new HashSet<string>(_actionIds, StringComparer.OrdinalIgnoreCase);
        Index = BuildIndex(repository);
        _schema = BuildSchema(_actionIds);
    }

    /// <summary>The action index this router shows the model. Static: it changes only when the rulebook does.</summary>
    public string Index { get; }

    public RuleSelectionMode Mode => RuleSelectionMode.ActionRouting;

    public async Task<RuleSelection> SelectAsync(string intent, CancellationToken cancellationToken)
    {
        var key = $"{_repository.RulebookVersion}|{intent.Trim()}";
        if (_cacheEnabled && _cache.TryGetValue(key, out var cached))
        {
            // A cache hit made no call, so it reports none — the same discipline as CompactIndexSelector.
            return cached with { ModelCalls = 0, InputTokens = null, OutputTokens = null, LatencyMs = 0 };
        }

        var request =
            $"""
             A character has stated this intent, in their own words:

             "{intent}"

             The engine's actions:

             {Index}

             Name the ONE action that is the primary deed, and the actions you are ruling out. Reply with the JSON object only.
             """;

        var conversation = new AgentConversation(AgentName, Conversation.SystemPrompt);
        conversation.AppendUser(request);

        var stopwatch = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await CallModelAsync(
                    conversation, "rulebook.route-action", tools: null, cancellationToken, responseFormat: _schema)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return Fallback(
                $"the action-routing call failed ({ex.GetType().Name}), so the whole rulebook was sent",
                RuleSelectionFallbackKind.Mechanical, modelCalls: 1, latency: stopwatch.Elapsed.TotalMilliseconds);
        }

        stopwatch.Stop();
        var input = response.Usage?.InputTokenCount;
        var output = response.Usage?.OutputTokenCount;
        var latency = stopwatch.Elapsed.TotalMilliseconds;

        var reply = TryParseReply(ModelText.Clean(response));
        if (reply is null || string.IsNullOrWhiteSpace(reply.Action))
        {
            // The reply could not be parsed into the contract, or carried no action — the machinery, not the
            // boundaries.
            return Fallback(
                "the action-routing reply could not be parsed, so the whole rulebook was sent",
                RuleSelectionFallbackKind.Mechanical, 1, input, output, latency);
        }

        if (string.Equals(reply.Confidence, "unclear", StringComparison.OrdinalIgnoreCase))
        {
            // The model understood the format and judged the intent genuinely ambiguous or unsupported — a
            // rule of the world firing, not a fault. Semantic.
            return Fallback(
                "the router judged the intent unclear between actions, so the whole rulebook was sent",
                RuleSelectionFallbackKind.Semantic, 1, input, output, latency);
        }

        if (!_knownActions.Contains(reply.Action))
        {
            // A well-formed reply that named an action outside the closed set: the model understood the format
            // but chose outside the boundaries. Semantic, and the invented label is recorded.
            return Fallback(
                $"the router named '{reply.Action}', which is not an engine action, so the whole rulebook was sent",
                RuleSelectionFallbackKind.Semantic, 1, input, output, latency, droppedLabels: [reply.Action])
                // Keep the invalid label the model chose, so the confusion table shows what it wrongly named.
                with { PrimaryActionLabel = reply.Action };
        }

        // Valid primary action. Split ruleOut into known actions (kept) and invented ones (dropped, recorded).
        var ruledOut = reply.RuleOut
            .Where(a => _knownActions.Contains(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(a => !string.Equals(a, reply.Action, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var droppedActions = reply.RuleOut
            .Where(a => !string.IsNullOrWhiteSpace(a) && !_knownActions.Contains(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selection = Expand(reply.Action, ruledOut, droppedActions, input, output, latency);

        if (_cacheEnabled)
        {
            _cache[key] = selection;
        }

        return selection;
    }

    /// <summary>
    /// Turns a routed action, the ruled-out actions and the dropped labels into a card selection, recording
    /// each card's provenance. The card set is: the routed action's cards; each ruled-out action's cards; the
    /// cards reached by <see cref="RuleCard.DistinguishedFrom"/> from the routed action, traversed
    /// SYMMETRICALLY; and finally the declared related-rule expansion, which <see cref="RuleSelectionSupport"/>
    /// adds — never reimplemented here.
    /// </summary>
    /// <summary>
    /// The most ruled-out actions taken from the model's reply. "The actions you are ruling out to be sure"
    /// is legitimately a handful; a reply naming a dozen is not narrowing, it is hedging, and following every
    /// one — plus their cards — fans the selection out to the whole book. The declared DistinguishedFrom edges
    /// carry the boundaries that actually matter, so trimming the model's dynamic list to the first few loses
    /// little and stops the explosion.
    /// </summary>
    private const int MaxRuleOut = 3;

    private RuleSelection Expand(
        string action, IReadOnlyList<string> ruledOut, IReadOnlyList<string> droppedActions,
        long? input, long? output, double latency)
    {
        var ids = new List<string>();
        var reasons = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routedIds = new List<string>();

        void Add(string ruleId, string provenance)
        {
            if (seen.Add(ruleId))
            {
                ids.Add(ruleId);
                reasons.Add(provenance);
            }
        }

        // 1. Cards governing the routed action. Only these follow their declared related-rule links (below),
        //    so the primary pick keeps its safety net without every also-ran dragging in its own.
        foreach (var card in CardsGoverning(action))
        {
            Add(card.RuleId, $"routed: {action} -> {card.RuleId}");
            routedIds.Add(card.RuleId);
        }

        // 2. Cards governing each ruled-out action — capped, because a model that rules out a dozen actions is
        //    hedging, not narrowing (see MaxRuleOut).
        var cappedRuleOut = ruledOut.Take(MaxRuleOut).ToList();
        foreach (var ruled in cappedRuleOut)
        {
            foreach (var card in CardsGoverning(ruled))
            {
                Add(card.RuleId, $"ruled-out: {ruled} -> {card.RuleId}");
            }
        }

        // 3. Cards reached by DistinguishedFrom from the routed action, traversed symmetrically. These are the
        //    declared boundaries that survive the ruleOut cap.
        foreach (var (ruleId, via) in DistinguishedNeighbours(action))
        {
            Add(ruleId, $"distinguished: {action} <-> {via} -> {ruleId}");
        }

        // 4. Declared related-rule expansion (and the always-present rejection) are RuleSelectionSupport's job —
        //    followed ONLY from the routed action's cards (linkExpandOnly), not the ruled-out/distinguished ones.
        var selection = RuleSelectionSupport.Build(
            _repository, Mode, ids, reasons,
            modelCalls: 1, inputTokens: input, outputTokens: output, latencyMs: latency,
            droppedLabels: droppedActions, linkExpandOnly: routedIds)
            with { PrimaryActionLabel = action };

        // Backstop: if the expansion still reaches most of the book, the routing did not narrow. Report that as
        // a semantic fallback rather than letting a de-facto whole-rulebook send read as a confident selection.
        var ceiling = _repository.AllCards.Count * 3 / 5;
        if (selection.Cards.Count > ceiling)
        {
            return Fallback(
                $"routing to '{action}' expanded to {selection.Cards.Count} of {_repository.AllCards.Count} cards, " +
                "so the selection did not narrow and the whole rulebook was sent",
                RuleSelectionFallbackKind.Semantic, 1, input, output, latency, droppedLabels: droppedActions)
                with { PrimaryActionLabel = action };
        }

        return selection;
    }

    /// <summary>The rule cards that govern one engine action (its <see cref="RuleCard.ActionName"/>).</summary>
    private IEnumerable<RuleCard> CardsGoverning(string action) =>
        _repository.AllCards.Where(c => string.Equals(c.ActionName, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every card reachable from an action by a <see cref="RuleCard.DistinguishedFrom"/> edge, in EITHER
    /// direction — forward (a card governing this action declares another action) and backward (any card
    /// declares this action). One edge, declared once, surfaces both neighbours.
    /// </summary>
    private IEnumerable<(string RuleId, string ViaAction)> DistinguishedNeighbours(string action)
    {
        // Forward: this action's cards say which actions they must be told apart from.
        foreach (var card in CardsGoverning(action))
        {
            foreach (var other in card.DistinguishedFrom)
            {
                foreach (var neighbour in CardsGoverning(other))
                {
                    yield return (neighbour.RuleId, other);
                }
            }
        }

        // Backward: any card in the book that declares it must be told apart from THIS action.
        foreach (var card in _repository.AllCards)
        {
            if (card.DistinguishedFrom.Contains(action, StringComparer.OrdinalIgnoreCase))
            {
                yield return (card.RuleId, card.ActionName);
            }
        }
    }

    private RuleSelection Fallback(
        string reason, RuleSelectionFallbackKind kind, int modelCalls = 0,
        long? input = null, long? output = null, double latency = 0, IReadOnlyList<string>? droppedLabels = null) =>
        RuleSelectionSupport.Fallback(_repository, Mode, reason, kind, modelCalls, input, output, latency, droppedLabels);

    /// <summary>The engine actions a card can govern, in catalog order — reference cards and the rejection excluded.</summary>
    private static IReadOnlyList<string> RoutableActions(IRuleRepository repository)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = new List<string>();
        foreach (var card in repository.AllCards)
        {
            if (card.IsReference
                || string.Equals(card.ActionName, DungeonMasterTools.RejectActionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(card.ActionName))
            {
                actions.Add(card.ActionName);
            }
        }

        return actions;
    }

    /// <summary>
    /// The action index: one line per engine action, carrying what it is (its governing cards' summaries) and,
    /// where it matters, the actions it must be told apart from. Composed from card metadata, never a
    /// hand-written copy. Public and static so a test and the offline measurement can size it exactly.
    /// </summary>
    public static string BuildIndex(IRuleRepository repository)
    {
        var builder = new StringBuilder();
        foreach (var action in RoutableActions(repository))
        {
            var cards = repository.AllCards
                .Where(c => string.Equals(c.ActionName, action, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var whatItIs = string.Join(" / ", cards.Select(c => c.Summary));

            // Symmetric neighbours: actions this one declares, and actions that declare this one.
            var neighbours = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var neighbour in cards.SelectMany(c => c.DistinguishedFrom))
            {
                neighbours.Add(neighbour);
            }

            foreach (var card in repository.AllCards)
            {
                if (card.DistinguishedFrom.Contains(action, StringComparer.OrdinalIgnoreCase)
                    && !card.IsReference)
                {
                    neighbours.Add(card.ActionName);
                }
            }

            builder.Append(action).Append(" — ").Append(whatItIs);
            if (neighbours.Count > 0)
            {
                builder.Append(" NOT: ").Append(string.Join(", ", neighbours));
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>The JSON-schema response format: the action set as a closed enum, so an invalid label is unrepresentable where a provider constrains decoding.</summary>
    private static ChatResponseFormat BuildSchema(IReadOnlyList<string> actionIds)
    {
        var enumJson = string.Join(", ", actionIds.Select(a => JsonSerializer.Serialize(a)));
        var schemaText =
            $$"""
              {
                "type": "object",
                "properties": {
                  "action": { "type": "string", "enum": [{{enumJson}}] },
                  "ruleOut": { "type": "array", "items": { "type": "string", "enum": [{{enumJson}}] } },
                  "confidence": { "type": "string", "enum": ["clear", "unclear"] }
                },
                "required": ["action", "ruleOut", "confidence"],
                "additionalProperties": false
              }
              """;

        using var document = JsonDocument.Parse(schemaText);
        return ChatResponseFormat.ForJsonSchema(
            document.RootElement.Clone(),
            schemaName: "action_routing",
            schemaDescription: "One primary engine action, the actions ruled out, and confidence.");
    }

    /// <summary>The parsed router reply: a primary action, the actions ruled out, and confidence.</summary>
    public sealed record ActionRoutingReply(string? Action, IReadOnlyList<string> RuleOut, string? Confidence);

    /// <summary>
    /// Reads the routing object out of the reply. Tolerant of surrounding prose or code fences, and returns
    /// null rather than throwing, so a malformed reply becomes a mechanical fallback and never an exception.
    /// </summary>
    public static ActionRoutingReply? TryParseReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;

            var ruleOut = new List<string>();
            if (root.TryGetProperty("ruleOut", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                ruleOut.AddRange(array.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            return new ActionRoutingReply(action, ruleOut, confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
