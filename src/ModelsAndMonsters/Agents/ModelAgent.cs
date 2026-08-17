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
    protected async Task<ChatResponse> CallModelAsync(
        string purpose,
        IReadOnlyList<AITool>? tools,
        CancellationToken cancellationToken)
    {
        var resolved = ChatOptionsFactory.Create(Profile, tools);

        using (_client.BeginCall(purpose, resolved.UnsupportedOptionsDropped))
        {
            var response = await _client
                .GetResponseAsync(Conversation.BuildRequestMessages(), resolved.Options, cancellationToken)
                .ConfigureAwait(false);

            foreach (var message in response.Messages)
            {
                Conversation.Append(message);
            }

            return response;
        }
    }

    /// <summary>Extracts every tool call the model requested, in order.</summary>
    public static IReadOnlyList<FunctionCallContent> GetToolCalls(ChatResponse response) =>
        [.. response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()];

    /// <summary>Records the application's answer to a tool call in this agent's history.</summary>
    public void AppendToolResult(FunctionCallContent call, object? result) =>
        Conversation.AppendToolResult(call.CallId, result);
}
