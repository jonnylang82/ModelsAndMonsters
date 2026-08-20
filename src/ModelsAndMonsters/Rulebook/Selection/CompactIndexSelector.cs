using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// Shows a model a one-line index of the rulebook, asks it which rule ids are relevant, and resolves with
/// only those full cards plus their declared related rules.
/// </summary>
/// <remarks>
/// <para>
/// The model still does every bit of the semantic work — there is no keyword list, no verb table and no
/// phrase matching anywhere in the path. What changes is only how much it has to read to do it: twenty-one
/// summary lines instead of twenty-one full cards, which is roughly a fifteenth of the text.
/// </para>
/// <para>
/// It is deliberately paranoid about its own failure modes. An unparseable reply, a reply naming no rule the
/// catalog knows, or a model call that throws all fall back to the whole bounded rulebook, so the worst case
/// is the cost the baseline already pays rather than an answer given without the rule that mattered. The
/// selection is a separate, stateless call from the resolution that follows it, and it caches: the index is
/// static, so an identical intent selects identically without paying again.
/// </para>
/// </remarks>
public sealed class CompactIndexSelector : ModelAgent, IRuleSelector
{
    /// <summary>
    /// The selector's whole instruction. Public so an offline measurement can size a selection request
    /// exactly rather than estimating its overhead.
    /// </summary>
    public const string SystemPrompt = """
        You are a rulebook index. You are given a character's stated intent, in their own words, and a list of
        rule ids with a one-line summary of each. Your only job is to say which rules somebody would need to
        READ in full to rule on an intent like that.

        - Choose the smallest set that genuinely covers the intent. Usually one or two; occasionally three
          when the intent is ambiguous between them.
        - Include a rule that the decision would TURN on even if the intent does not mention it — if somebody
          means to leave through a door, the rule about opening a door matters too.
        - If the intent seems to match nothing, choose the closest one or two anyway. Something downstream
          reads the full cards and decides; you are only narrowing what it has to read.
        - You never decide whether the action is supported, whether it is valid, or how it turns out.

        Reply with ONLY a JSON object of exactly this shape and nothing else:
        {"ruleIds": ["<id>", "<id>"]}
        """;

    private readonly IRuleRepository _repository;
    private readonly Dictionary<string, RuleSelection> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _cacheEnabled;

    public CompactIndexSelector(
        AgentModelProfile profile, TracingChatClient client, IRuleRepository repository, bool cacheEnabled = true)
        : base("RuleIndexSelector", profile, client, SystemPrompt)
    {
        _repository = repository;
        _cacheEnabled = cacheEnabled;
        Index = RuleSelectionSupport.BuildIndex(repository);
    }

    /// <summary>The compact index this selector shows the model. Static: it changes only when the rulebook does.</summary>
    public string Index { get; }

    public RuleSelectionMode Mode => RuleSelectionMode.CompactIndex;

    public async Task<RuleSelection> SelectAsync(string intent, CancellationToken cancellationToken)
    {
        var key = $"{_repository.RulebookVersion}|{intent.Trim()}";
        if (_cacheEnabled && _cache.TryGetValue(key, out var cached))
        {
            // A cache hit is reported with no model call and no tokens, because it made neither. The cached
            // SELECTION is separate from the resolver's cached answer: one depends on the static index, the
            // other on the resolver's reading of full cards, and conflating them would let a change to one
            // silently serve a stale version of the other.
            return cached with { ModelCalls = 0, InputTokens = null, OutputTokens = null, LatencyMs = 0 };
        }

        var request =
            $"""
             A character has stated this intent, in their own words:

             "{intent}"

             The rulebook's contents:

             {Index}

             Which rules would somebody need to read in full to rule on this? Reply with the JSON object only.
             """;

        var conversation = new AgentConversation(AgentName, Conversation.SystemPrompt);
        conversation.AppendUser(request);

        var stopwatch = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await CallModelAsync(conversation, "rulebook.select-index", tools: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return RuleSelectionSupport.Fallback(_repository, Mode,
                $"the index selection call failed ({ex.GetType().Name}), so the whole rulebook was sent",
                modelCalls: 1, latencyMs: stopwatch.Elapsed.TotalMilliseconds);
        }

        stopwatch.Stop();

        var raw = ModelText.Clean(response);
        var ids = TryParseIds(raw);
        var known = ids.Where(id => _repository.Find(id) is not null).ToList();

        if (known.Count == 0)
        {
            return RuleSelectionSupport.Fallback(_repository, Mode,
                ids.Count == 0
                    ? "the index selection returned nothing parseable, so the whole rulebook was sent"
                    : "the index selection named no rule this catalog holds, so the whole rulebook was sent",
                modelCalls: 1,
                inputTokens: response.Usage?.InputTokenCount,
                outputTokens: response.Usage?.OutputTokenCount,
                latencyMs: stopwatch.Elapsed.TotalMilliseconds);
        }

        var selection = RuleSelectionSupport.Build(_repository, Mode, known,
            [.. known.Select(id => $"model-selected id: {id}")],
            modelCalls: 1,
            inputTokens: response.Usage?.InputTokenCount,
            outputTokens: response.Usage?.OutputTokenCount,
            latencyMs: stopwatch.Elapsed.TotalMilliseconds);

        if (_cacheEnabled)
        {
            _cache[key] = selection;
        }

        return selection;
    }

    /// <summary>
    /// Reads the rule ids out of the reply. Tolerant of surrounding prose or code fences, and returns
    /// nothing rather than throwing, so a malformed reply becomes a fallback and never an exception.
    /// </summary>
    public static IReadOnlyList<string> TryParseIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            if (!document.RootElement.TryGetProperty("ruleIds", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [
                .. array.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
