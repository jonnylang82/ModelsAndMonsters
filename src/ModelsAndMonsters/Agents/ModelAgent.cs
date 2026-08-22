using System.Net;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// Shared plumbing for an agent backed by a model: its own profile, its own conversation, and one
/// traced model call.
/// </summary>
/// <remarks>
/// Note what is absent: no automatic function invocation, no agent loop, no framework. A call sends
/// the history and returns whatever the model said. Deciding what to do about a requested tool call
/// is the orchestration layer's job.
/// </remarks>
public abstract class ModelAgent
{
    private readonly TracingChatClient _client;

    protected ModelAgent(string agentName, AgentModelProfile profile, TracingChatClient client, string systemPrompt)
    {
        AgentName = agentName;
        Profile = profile;
        _client = client;
        Conversation = new AgentConversation(agentName, systemPrompt);
    }

    public string AgentName { get; }

    public AgentModelProfile Profile { get; }

    /// <summary>This agent's private history. Never shared with another agent.</summary>
    public AgentConversation Conversation { get; }

    private int _observedPromptOverhead = ContextTruncation.DefaultPromptOverheadTokens;

    /// <summary>
    /// The measured request overhead, in tokens, beyond the message text our estimate counts — the tool-call
    /// schemas and the model's chat-template scaffolding. Learned as the largest gap seen between the
    /// provider's reported input size and our message-only estimate, so history summarisation can budget
    /// against the FULL request. Starts at a conservative default until the first reported usage refines it.
    /// </summary>
    public int ObservedPromptOverheadTokens => _observedPromptOverhead;

    /// <summary>
    /// Sends the agent's full history to its model and appends the reply to the history.
    /// </summary>
    /// <param name="purpose">Recorded in the trace so every call can be attributed to a task.</param>
    /// <param name="tools">Declaration-only tools exposed for this call, or null for none.</param>
    protected Task<ChatResponse> CallModelAsync(
        string purpose,
        IReadOnlyList<AITool>? tools,
        CancellationToken cancellationToken) =>
        CallModelAsync(Conversation, purpose, tools, cancellationToken);

    /// <summary>
    /// Sends a specific conversation to the model. An agent normally has exactly one, but the Dungeon
    /// Master can run a task on a separate short-lived conversation.
    /// </summary>
    /// <param name="maxOutputTokens">
    /// A smaller output budget for this one call, when the task cannot need the agent's configured
    /// allowance. On Ollama the context window covers input and output together, so an output reserve sized            `
    /// for prose is a quarter of the window held back from a task whose whole reply is one tool call. Null
    /// uses the agent's own configured limit.
    /// </param>
    protected async Task<ChatResponse> CallModelAsync(
        AgentConversation conversation,
        string purpose,
        IReadOnlyList<AITool>? tools,
        CancellationToken cancellationToken,
        int? maxOutputTokens = null,
        ChatResponseFormat? responseFormat = null)
    {
        var response = await SendWithTransientRetryAsync(
                conversation, purpose, tools, maxOutputTokens, responseFormat, cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in response.Messages)
        {
            conversation.Append(message);
        }

        return response;
    }

    /// <summary>The most attempts a single model call makes before a transient failure is allowed to surface.</summary>
    private const int MaxTransientAttempts = 3;

    /// <summary>
    /// The temperature a retry is floored to, so a re-send actually re-samples rather than reproducing the
    /// same reply. The common transient 5xx is the provider's tool-call parser rejecting a reply the model
    /// malformed; at temperature zero the draw is greedy and deterministic, so an identical re-send returns
    /// the identical bad reply (measured: a temp-0 IntentParser 500'd three times on the same malformed XML).
    /// A small floor breaks the loop while barely perturbing an agent that is already sampling.
    /// </summary>
    private static float RetryTemperatureFloor(int attempt) => attempt <= 2 ? 0.4f : 0.8f;

    /// <summary>
    /// Sends the request, re-sending on a transient provider failure before giving up. A provider can
    /// return a 5xx for a passing reason — a busy server, or its own tool-call parser rejecting a reply the
    /// model happened to malform — and a fresh send re-samples the reply, which almost always succeeds. One
    /// bad draw should not end an hour-long run. User cancellation is never retried, and a 4xx (a request we
    /// shaped wrong) is surfaced at once rather than hammered. Each attempt is its own traced call, so a
    /// retried failure still shows in the trace as a model error followed by the successful send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first attempt uses the agent's exact profile, so a run with no failures still replays identically.
    /// A retry re-samples: the temperature is floored by <see cref="RetryTemperatureFloor"/> and the seed is
    /// offset by the attempt number, so the draw genuinely moves rather than repeating the same fault.
    /// </para>
    /// <para>
    /// The floor ESCALATES across attempts (0.4, then 0.8), and one live run is the whole argument for that.
    /// It contained both outcomes at once. Elara, configured at 0.8, took two 500s and succeeded on the third
    /// attempt — the mechanism works. The intent parser, configured at 0, was lifted to the old flat floor of
    /// 0.1 and 500'd three times with the identical provider-side error, because 0.1 is barely off greedy: the
    /// seed moved but the distribution did not, so the model reproduced the same malformed tool call and the
    /// run died. A floor near zero is not re-sampling, it only looks like it.
    /// </para>
    /// <para>
    /// Escalating also changes the REQUEST, which matters beyond sampling. Ollama's own tool-call parser is
    /// what rejects the reply and turns it into a 500 (ollama#14834, still open), and ollama#17825 records
    /// that re-sending the identical request after such a failure could wedge the server outright — a poisoned
    /// prompt cache, fixed in ollama#17883. That hang only reproduced with thinking enabled, which is why this
    /// harness has never seen it: every agent here runs with reasoning off. A retry that draws differently is
    /// the cheapest way to stay off that path as well as escape the fault.
    /// </para>
    /// </remarks>
    private async Task<ChatResponse> SendWithTransientRetryAsync(
        AgentConversation conversation,
        string purpose,
        IReadOnlyList<AITool>? tools,
        int? maxOutputTokens,
        ChatResponseFormat? responseFormat,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var profile = attempt == 1 ? Profile : WithRetrySampling(Profile, attempt);
            if (maxOutputTokens is { } cap && (profile.MaxOutputTokens is null || cap < profile.MaxOutputTokens))
            {
                // Only ever tightens: a per-task cap is a promise that this reply is small, never a licence
                // to exceed what the agent was configured to allow.
                profile = profile with { MaxOutputTokens = cap };
            }

            try
            {
                var resolved = ChatOptionsFactory.Create(profile, tools, responseFormat);
                using (_client.BeginCall(purpose, resolved.UnsupportedOptionsDropped, attempt))
                {
                    var response = await _client
                        .GetResponseAsync(conversation.BuildRequestMessages(), resolved.Options, cancellationToken)
                        .ConfigureAwait(false);

                    // Learn the request's non-message overhead (tool schemas + chat template) from what the
                    // provider says it actually processed, so history budgeting reflects the full prompt. The
                    // response is not yet appended to the conversation here, so this estimates exactly what was sent.
                    RecordPromptOverhead(conversation, response);
                    return response;
                }
            }
            catch (HttpRequestException ex)
                when (attempt < MaxTransientAttempts && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                // Back off briefly, then re-send with re-sampling (see WithRetrySampling) so a new draw
                // rarely reproduces the same fault.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The profile a retry attempt uses: the agent's own, but with the temperature raised to at least the
    /// floor for this attempt and the seed offset by the attempt number. Together these ensure a re-send
    /// draws a different sample — a temperature floor so sampling is not greedy, and a moved seed so the draw
    /// is not pinned to the same sequence — which is what lets a retry escape a deterministic fault such as a
    /// malformed tool call. It never lowers an agent's temperature (the floor is a minimum), so an agent
    /// already sampling above the floor keeps its own temperature and only gets the seed offset.
    /// </summary>
    private static AgentModelProfile WithRetrySampling(AgentModelProfile profile, int attempt) =>
        profile with
        {
            Temperature = Math.Max(profile.Temperature ?? 0f, RetryTemperatureFloor(attempt)),
            Seed = profile.Seed is { } seed ? seed + attempt : null
        };

    /// <summary>
    /// Refines <see cref="ObservedPromptOverheadTokens"/> from the provider's reported input size: the gap
    /// between it and our message-only estimate is the tool-schema and chat-template overhead the estimate
    /// cannot see. The largest gap seen is kept (conservative — better to summarise a little early than to
    /// truncate). A provider that reports no usage leaves the current estimate untouched.
    /// </summary>
    private void RecordPromptOverhead(AgentConversation conversation, ChatResponse response)
    {
        if (response.Usage?.InputTokenCount is not long reported)
        {
            return;
        }

        var overhead = (int)reported - ContextTruncation.EstimateSentTokens(conversation.BuildRequestMessages());
        if (overhead > _observedPromptOverhead)
        {
            _observedPromptOverhead = overhead;
        }
    }

    /// <summary>
    /// A transient provider failure worth re-sending: a 5xx, or a transport-level fault that carried no
    /// status at all (a dropped or refused connection). A 4xx is our own request's fault and is not retried.
    /// </summary>
    private static bool IsTransient(HttpRequestException ex) =>
        ex.StatusCode is null or >= HttpStatusCode.InternalServerError;

    /// <summary>Extracts every tool call the model requested, in order.</summary>
    public static IReadOnlyList<FunctionCallContent> GetToolCalls(ChatResponse response) =>
        [.. response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()];

    /// <summary>
    /// True when the model stopped because it ran out of output budget.
    /// </summary>
    /// <remarks>
    /// Worth distinguishing: a truncated reply that lost its tool call looks exactly like a model
    /// ignoring its protocol, and treating the two the same sends the harness chasing the wrong fault.
    /// </remarks>
    public static bool WasTruncated(ChatResponse response) =>
        response.FinishReason == ChatFinishReason.Length;

    /// <summary>
    /// True when a truncated reply ran out of <em>context window</em> rather than output budget — the input
    /// had already filled the window, so there was no room left to answer in. The remedy is the opposite of
    /// the one a plain output-limit calls for: send less, never reserve more output.
    /// </summary>
    /// <summary>
    /// True when a reply was cut short — by finish reason where the provider reports one, and otherwise by
    /// its own usage numbers.
    /// </summary>
    /// <param name="maxOutputTokensOverride">
    /// The output budget actually applied to the call that produced <paramref name="response"/>, when it
    /// differs from <see cref="AI.AgentModelProfile.MaxOutputTokens"/> — for example the Dungeon Master's
    /// tightened adjudication cap. A per-call cap is never written back into <see cref="Profile"/> (see
    /// <see cref="SendWithTransientRetryAsync"/>), so a caller diagnosing a call made under one must pass it
    /// explicitly or this falls back to the agent's general-purpose configured limit, which can be
    /// substantially larger than what the call was actually allowed to spend. Null uses the profile's figure.
    /// </param>
    /// <remarks>
    /// Not every provider reports a finish reason. An OpenAI-driven run produced 109 responses with
    /// <see cref="ChatResponse.FinishReason"/> null on every one, which left <see cref="WasTruncated"/>
    /// permanently false for that provider: a cut-off reply would have been handed to the intent parser as
    /// though it were complete, and none of the truncation handling would have run. Usage IS reported, and
    /// it answers the question directly — a reply that spent its whole output budget, or that filled the
    /// context window, was cut short whatever the provider chose to say about it.
    /// </remarks>
    public bool WasReplyCutShort(ChatResponse response, int? maxOutputTokensOverride = null)
    {
        if (WasTruncated(response))
        {
            return true;
        }

        var maxOutputTokens = maxOutputTokensOverride ?? Profile.MaxOutputTokens;

        // Only inferred when the provider declined to say. A reported reason is authoritative, so a normal
        // stop is never second-guessed just because the reply happened to be long.
        return response.FinishReason is null
            && (WasContextExhausted(response, maxOutputTokens)
                || ContextTruncation.WasOutputBudgetSpent(response.Usage?.OutputTokenCount, maxOutputTokens));
    }

    public bool WasContextExhausted(ChatResponse response, int? maxOutputTokensOverride = null) =>
        (WasTruncated(response) || response.FinishReason is null)
        && ContextTruncation.WasContextExhausted(
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount,
            Profile.BindingContextWindow,
            maxOutputTokensOverride ?? Profile.MaxOutputTokens);

    /// <summary>Records the application's answer to a tool call in this agent's history.</summary>
    public void AppendToolResult(FunctionCallContent call, object? result) =>
        Conversation.AppendToolResult(call.CallId, result);
}
