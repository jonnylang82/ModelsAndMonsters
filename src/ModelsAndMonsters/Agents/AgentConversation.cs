using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// One agent's private conversation history, owned entirely by this application.
/// </summary>
/// <remarks>
/// No provider-owned thread, session or assistant API is used. Each agent holds its own instance and
/// nothing copies messages between instances, which is what keeps the Hero's and Monster's private
/// exchanges with the Dungeon Master isolated from one another.
/// </remarks>
public sealed class AgentConversation
{
    private readonly List<ChatMessage> _messages = [];

    public AgentConversation(string agentName, string systemPrompt)
    {
        AgentName = agentName;
        SystemPrompt = systemPrompt;
    }

    public string AgentName { get; }

    public string SystemPrompt { get; }

    /// <summary>The history excluding the system prompt, in order.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void Append(ChatMessage message) => _messages.Add(message);

    public void AppendUser(string text) => _messages.Add(new ChatMessage(ChatRole.User, text));

    /// <summary>
    /// Appends the result of a tool call the application dispatched. Every tool call the model makes
    /// must be answered exactly once, or providers reject the next request.
    /// </summary>
    public void AppendToolResult(string callId, object? result) =>
        _messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]));

    /// <summary>The full message collection to send: system prompt followed by the history.</summary>
    public IReadOnlyList<ChatMessage> BuildRequestMessages() =>
        [new ChatMessage(ChatRole.System, SystemPrompt), .. _messages];
}
