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
    protected async Task<ChatResponse> CallModelAsync(
        AgentConversation conversation,
        string purpose,
        IReadOnlyList<AITool>? tools,
        CancellationToken cancellationToken)
    {
        var response = await SendWithTransientRetryAsync(conversation, purpose, tools, cancellationToken)
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
    /// Sends the request, re-sending on a transient provider failure before giving up. A provider can
    /// return a 5xx for a passing reason — a busy server, or its own tool-call parser rejecting a reply the
    /// model happened to malform — and a fresh send re-samples the reply, which almost always succeeds. One
    /// bad draw should not end an hour-long run. User cancellation is never retried, and a 4xx (a request we
    /// shaped wrong) is surfaced at once rather than hammered. Each attempt is its own traced call, so a
    /// retried failure still shows in the trace as a model error followed by the successful send.
    /// </summary>
    private async Task<ChatResponse> SendWithTransientRetryAsync(
        AgentConversation conversation,
        string purpose,
        IReadOnlyList<AITool>? tools,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var resolved = ChatOptionsFactory.Create(Profile, tools);
                using (_client.BeginCall(purpose, resolved.UnsupportedOptionsDropped))
                {
                    return await _client
                        .GetResponseAsync(conversation.BuildRequestMessages(), resolved.Options, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex)
                when (attempt < MaxTransientAttempts && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                // Back off briefly, then re-send: a new sample rarely reproduces the same fault.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
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

    /// <summary>Records the application's answer to a tool call in this agent's history.</summary>
    public void AppendToolResult(FunctionCallContent call, object? result) =>
        Conversation.AppendToolResult(call.CallId, result);
}
