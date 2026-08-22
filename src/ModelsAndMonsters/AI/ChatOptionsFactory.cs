using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models.Chat;

namespace ModelsAndMonsters.AI;

/// <summary>The <see cref="ChatOptions"/> built for a call, plus anything the provider could not honour.</summary>
public sealed record ResolvedChatOptions(ChatOptions Options, ImmutableArray<string> UnsupportedOptionsDropped);

/// <summary>Builds provider-appropriate <see cref="ChatOptions"/> from an agent's profile.</summary>
public static class ChatOptionsFactory
{
    public static ResolvedChatOptions Create(
        AgentModelProfile profile, IReadOnlyList<AITool>? tools = null, ChatResponseFormat? responseFormat = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var capabilities = ProviderCapabilities.For(profile.Provider);
        var dropped = ImmutableArray.CreateBuilder<string>();

        var options = new ChatOptions
        {
            ModelId = profile.ModelId
        };

        // The modern Claude models (Claude 5, Opus 4.7+) both forbid sampling and reach reasoning through
        // a different request shape than the M.E.AI adapter emits (see the effort handling below).
        var anthropicModernClaude = profile.Provider == ModelProvider.Anthropic
            && ModelForbidsSampling(profile.Provider, profile.ModelId);

        // Whether extended thinking is actually turned on for this request. Anthropic disallows custom
        // sampling while it is (temperature may only be 1, top_p/top_k unset), so it forces sampling to be
        // omitted. It is only truly enabled on the 4.x models: on modern Claude the adapter cannot emit
        // the required form, so effort is dropped there (below) and thinking stays off.
        var anthropicThinkingEnabled = profile.Provider == ModelProvider.Anthropic
            && !anthropicModernClaude
            && profile.Effort is { } reasoningEffort && reasoningEffort != ReasoningEffort.None;

        // OpenAI's reasoning models reject sampling (temperature/top_p) the same way, but only once
        // reasoning is actually engaged — effort above None. At None they still accept temperature (the
        // Effort:none run confirmed it), so this must not fire there, or it would needlessly drop it.
        var openAiReasoningActive = profile.Provider == ModelProvider.OpenAI
            && IsOpenAIReasoningModel(profile.ModelId)
            && profile.Effort is { } openAiEffort && openAiEffort != ReasoningEffort.None;

        // Some models forbid all sampling parameters (Anthropic Opus 4.7+ 400s on temperature, top_p or
        // top_k). When so, they are dropped rather than sent. The decision is explicit when configured,
        // otherwise inferred from the model — and engaging reasoning (Anthropic thinking or an OpenAI
        // reasoning model's effort) forces it either way.
        var omitSampling = (profile.OmitSampling ?? ModelForbidsSampling(profile.Provider, profile.ModelId))
            || anthropicThinkingEnabled
            || openAiReasoningActive;

        if (profile.Temperature is { } temperature)
        {
            if (!omitSampling && capabilities.SupportsTemperature)
            {
                options.Temperature = temperature;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.Temperature));
            }
        }

        if (profile.TopP is { } topP)
        {
            if (!omitSampling && capabilities.SupportsTopP)
            {
                options.TopP = topP;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.TopP));
            }
        }

        // Anthropic rejects temperature and top_p together. When both are configured, keep temperature
        // (the primary knob) and drop top_p, so a shared Default that sets both still works there.
        if (!capabilities.AllowsTemperatureAndTopPTogether && options.Temperature is not null && options.TopP is not null)
        {
            options.TopP = null;
            dropped.Add(nameof(AgentModelProfile.TopP));
        }

        if (profile.TopK is { } topK)
        {
            if (!omitSampling && capabilities.SupportsTopK)
            {
                options.TopK = topK;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.TopK));
            }
        }

        // The penalties are sampling parameters and follow the same rules: dropped where the provider has
        // no equivalent (Anthropic), and dropped wholesale when the model forbids sampling.
        if (profile.PresencePenalty is { } presencePenalty)
        {
            if (!omitSampling && capabilities.SupportsPenalties)
            {
                options.PresencePenalty = presencePenalty;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.PresencePenalty));
            }
        }

        if (profile.FrequencyPenalty is { } frequencyPenalty)
        {
            if (!omitSampling && capabilities.SupportsPenalties)
            {
                options.FrequencyPenalty = frequencyPenalty;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.FrequencyPenalty));
            }
        }

        if (profile.RepeatPenalty is { } repeatPenalty)
        {
            if (!omitSampling && capabilities.SupportsRepeatPenalty)
            {
                options.AddOllamaOption(OllamaSharp.Models.OllamaOption.RepeatPenalty, repeatPenalty);
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.RepeatPenalty));
            }
        }

        if (profile.RepeatLastN is { } repeatLastN)
        {
            if (!omitSampling && capabilities.SupportsRepeatPenalty)
            {
                options.AddOllamaOption(OllamaSharp.Models.OllamaOption.RepeatLastN, repeatLastN);
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.RepeatLastN));
            }
        }

        if (profile.MaxOutputTokens is { } maxOutputTokens)
        {
            if (capabilities.SupportsMaxOutputTokens)
            {
                options.MaxOutputTokens = maxOutputTokens;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.MaxOutputTokens));
            }
        }

        if (profile.Seed is { } seed)
        {
            if (capabilities.SupportsSeed)
            {
                options.Seed = seed;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.Seed));
            }
        }

        if (profile.ContextWindow is { } contextWindow)
        {
            if (capabilities.SupportsContextWindow)
            {
                ApplyOllamaContextWindow(options, contextWindow);
            }
            else
            {
                // Not a failure: the provider simply fixes the window per model. The profile value is
                // still used by the harness to warn about approaching it.
                dropped.Add(nameof(AgentModelProfile.ContextWindow));
            }
        }

        // OpenAI accepts reasoning_effort only on its reasoning models (GPT-5+, o-series); the
        // general-purpose models (gpt-4o, gpt-4o-mini, gpt-4.1, …) reject the argument outright. The
        // client uses the Responses API (/v1/responses) which — unlike chat completions — allows
        // reasoning effort alongside function tools, so having tools no longer forces effort off; only a
        // non-reasoning model does.
        var openAiCannotTakeEffort = profile.Provider == ModelProvider.OpenAI
            && !IsOpenAIReasoningModel(profile.ModelId);

        // Reasoning effort is the unified cross-provider knob and supersedes the legacy Thinking toggle.
        // Ollama takes a native think level; OpenAI and Anthropic take it through ChatOptions.Reasoning,
        // which their Microsoft.Extensions.AI adapters translate to the provider's reasoning-effort field.
        // Some provider/model/tool combinations cannot take it, so those are handled rather than sent to fail.
        if (profile.Effort is { } effort)
        {
            // The M.E.AI Anthropic adapter maps ChatOptions.Reasoning to the legacy thinking.type.enabled,
            // which the modern Claude models (Claude 5, Opus 4.7+) reject: they require thinking.type.
            // adaptive plus output_config.effort, which the adapter cannot currently emit. So a non-None
            // effort cannot be expressed there and is dropped-and-reported rather than 400ing the call.
            // None (thinking off) is fine.
            if (anthropicModernClaude && effort != ReasoningEffort.None)
            {
                dropped.Add(nameof(AgentModelProfile.Effort));
            }
            // OpenAI can't take it here (non-reasoning model, or reasoning model with tools present): a
            // raised effort is dropped-and-reported; None needs nothing sent, so it is omitted.
            else if (openAiCannotTakeEffort)
            {
                if (effort != ReasoningEffort.None)
                {
                    dropped.Add(nameof(AgentModelProfile.Effort));
                }
            }
            else
            {
                ApplyReasoningEffort(options, profile.Provider, effort);
            }
        }
        else if (profile.Thinking is { } thinking)
        {
            if (capabilities.SupportsThinkingToggle)
            {
                ApplyOllamaThinking(options, thinking);
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.Thinking));
            }
        }

        // A forced tool choice is honoured only by providers that support it — and not while Anthropic
        // thinking is on, which is incompatible with forced tool use (it would 400). On anything that
        // cannot honour it the request is left in Auto and the drop is recorded, like any other option.
        var forceToolChoice = profile.ForceToolChoice == true;
        var forcedChoiceHonoured = forceToolChoice && capabilities.SupportsForcedToolChoice && !anthropicThinkingEnabled;
        if (forceToolChoice && !forcedChoiceHonoured)
        {
            dropped.Add(nameof(AgentModelProfile.ForceToolChoice));
        }

        // A schema-constrained reply, where the provider can enforce it at the decoder. Dropped-and-reported
        // where it cannot (Anthropic); the caller still asks for the shape in the prompt and parses tolerantly.
        if (responseFormat is not null)
        {
            if (capabilities.SupportsStructuredOutputSchema)
            {
                options.ResponseFormat = responseFormat;
            }
            else
            {
                dropped.Add("ResponseFormat");
            }
        }

        if (tools is { Count: > 0 })
        {
            options.Tools = [.. tools];

            // Tools are declaration-only and are dispatched by our own orchestration code. Nothing in
            // the pipeline is allowed to invoke them, so no automatic-invocation middleware is used.
            // RequireAny only tells the provider the model must pick a tool; it does not invoke one.
            options.ToolMode = forcedChoiceHonoured ? ChatToolMode.RequireAny : ChatToolMode.Auto;
        }

        return new ResolvedChatOptions(options, dropped.ToImmutable());
    }

    /// <summary>
    /// Whether a model forbids sampling parameters outright. Anthropic Opus 4.7 and later (and Opus 5+)
    /// deprecated temperature, top-p and top-k — any non-default value returns a 400 — in favour of an
    /// effort parameter and adaptive thinking. This is a coarse name match; a per-agent
    /// <c>OmitSampling</c> setting overrides it either way.
    /// </summary>
    private static bool ModelForbidsSampling(ModelProvider provider, string modelId)
    {
        if (provider != ModelProvider.Anthropic || string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        // Anthropic deprecated temperature/top_p/top_k on Opus 4.7+ and then across the whole Claude 5
        // family (opus-5, sonnet-5, …), in favour of an effort parameter and adaptive thinking. Haiku 4.5
        // and other 4.x models still accept them. Match the major version, careful that "haiku-4-5" is
        // major 4 (minor 5), not major 5.
        var model = modelId.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(model, @"opus-4-[7-9]")               // Opus 4.7 / 4.8 / 4.9
            || System.Text.RegularExpressions.Regex.IsMatch(model, @"claude-[a-z]+-(?:[5-9]|\d\d)\b"); // Claude 5+
    }

    /// <summary>
    /// Whether an OpenAI model accepts the <c>reasoning_effort</c> request argument. Only the reasoning
    /// models do — the o-series (<c>o1</c>, <c>o3-mini</c>, <c>o4-…</c>) and GPT-5 and later. The
    /// general-purpose chat models (<c>gpt-4o</c>, <c>gpt-4o-mini</c>, <c>gpt-4.1</c>, <c>gpt-3.5</c>, …)
    /// reject the argument outright with a 400, so effort must not be sent to them. A coarse name match.
    /// </summary>
    private static bool IsOpenAIReasoningModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var model = modelId.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(model, @"^o\d")            // o1, o3-mini, o4-…
            || System.Text.RegularExpressions.Regex.IsMatch(model, @"gpt-(?:[5-9]|\d\d)"); // GPT-5 and later
    }

    /// <summary>
    /// Sets Ollama's <c>num_ctx</c>. This is a provider-specific detail, and it stays here in the AI
    /// layer rather than reaching agents or orchestration.
    /// </summary>
    private static void ApplyOllamaContextWindow(ChatOptions options, int contextWindow) =>
        options.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, contextWindow);

    /// <summary>Sets Ollama's top-level <c>think</c> flag, which enables or disables reasoning.</summary>
    private static void ApplyOllamaThinking(ChatOptions options, bool thinking) =>
        options.AddOllamaOption(OllamaSharp.Models.OllamaOption.Think, thinking);

    /// <summary>
    /// Applies a unified reasoning effort to the request. Ollama takes it as a native <c>think</c> level
    /// on the top-level flag; OpenAI and Anthropic take it through <see cref="ChatOptions.Reasoning"/>,
    /// whose Microsoft.Extensions.AI adapters map it to each provider's reasoning-effort request field.
    /// </summary>
    private static void ApplyReasoningEffort(ChatOptions options, ModelProvider provider, ReasoningEffort effort)
    {
        if (provider == ModelProvider.Ollama)
        {
            options.AddOllamaOption(OllamaSharp.Models.OllamaOption.Think, ToThinkValue(effort));
        }
        else
        {
            options.Reasoning = new ReasoningOptions { Effort = effort };
        }
    }

    /// <summary>
    /// Maps a <see cref="ReasoningEffort"/> to Ollama's <see cref="ThinkValue"/>. <c>None</c> disables
    /// thinking (identical on the wire to the legacy <c>think:false</c>); the graduated levels map
    /// straight across; <c>ExtraHigh</c> clamps to Ollama's top level (<c>high</c>), the most it exposes.
    /// </summary>
    private static ThinkValue ToThinkValue(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Low => ThinkValue.Low,
        ReasoningEffort.Medium => ThinkValue.Medium,
        ReasoningEffort.High => ThinkValue.High,
        ReasoningEffort.ExtraHigh => ThinkValue.High,
        _ => new ThinkValue((bool?)false)
    };
}
