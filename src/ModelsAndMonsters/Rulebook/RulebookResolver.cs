using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// The stateless Rulebook Resolver. Each call builds a fresh, single-use conversation — the resolver system
/// prompt, the raw intent and the bounded rule cards — so nothing accumulates between calls and no encounter
/// history, live state or private knowledge can leak in. It is given no tools, and returns a JSON object it
/// is asked to shape to the guidance schema; parsing failures surface as a null <see cref="ResolverResult.Parsed"/>
/// rather than an exception, so the caller can fail safely.
/// </summary>
public sealed class RulebookResolver : ModelAgent, IRulebookResolver
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PromptLibrary _prompts;

    public RulebookResolver(AgentModelProfile profile, TracingChatClient client, PromptLibrary prompts)
        : base("RulebookResolver", profile, client, prompts.Render("rulebook.resolver.system", new Dictionary<string, string?>
        {
            ["schema"] = RuleGuidanceSchema.Description
        }))
    {
        _prompts = prompts;
    }

    public async Task<ResolverResult> ResolveAsync(string intent, IReadOnlyList<RuleCard> cards, CancellationToken cancellationToken)
    {
        // A fresh, single-use conversation. The resolver never inherits the encounter's history — that is what
        // keeps its request bounded and independent of how many rounds have already been played.
        var conversation = new AgentConversation(AgentName, Conversation.SystemPrompt);

        var cardText = string.Join("\n\n", cards.Select(c => c.ToPromptBlock()));
        var requestText = _prompts.Render("rulebook.resolve", new Dictionary<string, string?>
        {
            ["intent"] = intent,
            ["cards"] = cardText
        });
        conversation.AppendUser(requestText);

        var requestChars = (Conversation.SystemPrompt?.Length ?? 0) + requestText.Length;

        var stopwatch = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            // No tools: the resolver has no game-engine tools and shapes its answer as JSON text.
            response = await CallModelAsync(conversation, "rulebook.resolve", tools: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new ResolverResult
            {
                RequestText = requestText,
                RequestChars = requestChars,
                RawResponse = $"(resolver model call failed: {ex.GetType().Name}: {ex.Message})",
                Parsed = null,
                ModelCallFailed = true,
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds
            };
        }

        stopwatch.Stop();

        var raw = ModelText.Clean(response);
        return new ResolverResult
        {
            RequestText = requestText,
            RequestChars = requestChars,
            RawResponse = raw,
            Parsed = TryParse(raw),
            ModelCallFailed = false,
            InputTokens = response.Usage?.InputTokenCount,
            OutputTokens = response.Usage?.OutputTokenCount,
            LatencyMs = stopwatch.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>
    /// Parses the first JSON object in the response into <see cref="RuleGuidance"/>. Tolerant of surrounding
    /// prose or code fences; returns null when nothing parses, so a malformed response fails safely rather
    /// than throwing. Semantic validation (cited ids exist, actions are real) is the consultant's job.
    /// </summary>
    public static RuleGuidance? TryParse(string? text)
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
            var raw = JsonSerializer.Deserialize<RawGuidance>(text[start..(end + 1)], ParseOptions);
            if (raw is null)
            {
                return null;
            }

            return new RuleGuidance
            {
                Supported = raw.Supported,
                CandidateActions = raw.CandidateActions ?? [],
                CitedRules = raw.CitedRules is null
                    ? []
                    : [.. raw.CitedRules.Where(r => r is not null).Select(r => new CitedRule(r!.RuleId ?? "", r.Version ?? ""))],
                RequiredBindings = raw.RequiredBindings ?? [],
                Preconditions = raw.Preconditions ?? [],
                TurnCost = raw.TurnCost ?? "",
                RngSpecification = raw.RngSpecification ?? "",
                Visibility = raw.Visibility ?? "",
                SuccessBehaviour = raw.SuccessBehaviour ?? "",
                FailureBehaviour = raw.FailureBehaviour ?? "",
                UnsupportedReason = string.IsNullOrWhiteSpace(raw.UnsupportedReason) ? null : raw.UnsupportedReason
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The loose JSON shape the resolver returns, before it is normalised into <see cref="RuleGuidance"/>.</summary>
    private sealed class RawGuidance
    {
        public bool Supported { get; set; }
        public List<string>? CandidateActions { get; set; }
        public List<RawCited?>? CitedRules { get; set; }
        public List<string>? RequiredBindings { get; set; }
        public List<string>? Preconditions { get; set; }
        public string? TurnCost { get; set; }
        public string? RngSpecification { get; set; }
        public string? Visibility { get; set; }
        public string? SuccessBehaviour { get; set; }
        public string? FailureBehaviour { get; set; }
        public string? UnsupportedReason { get; set; }
    }

    private sealed class RawCited
    {
        public string? RuleId { get; set; }
        public string? Version { get; set; }
    }
}
