using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// A stand-in for a model. Returns pre-scripted responses in order and records exactly what it was
/// asked, so orchestration can be tested without Ollama or OpenAI.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses;

    public ScriptedChatClient(params ChatResponse[] responses)
    {
        _responses = new Queue<ChatResponse>(responses);
    }

    /// <summary>Every message collection this client was sent, in call order.</summary>
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> RequestOptions { get; } = [];

    public int CallCount => Requests.Count;

    public int RemainingResponses => _responses.Count;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        RequestOptions.Add(options);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"The scripted client ran out of responses on call {Requests.Count}. " +
                "The orchestration made more model calls than the test expected.");
        }

        return Task.FromResult(_responses.Dequeue());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The harness only makes non-streaming calls.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    // -------------------------------------------------------------------------------------------
    // Response builders
    // -------------------------------------------------------------------------------------------

    public static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

    public static ChatResponse Call(string callId, string toolName, params (string Name, object? Value)[] arguments) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, arguments.ToDictionary(a => a.Name, a => a.Value))]))
        {
            FinishReason = ChatFinishReason.ToolCalls
        };

    /// <summary>A single assistant message requesting several tools at once.</summary>
    public static ChatResponse Calls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls])) { FinishReason = ChatFinishReason.ToolCalls };

    public static FunctionCallContent CallContent(string callId, string toolName, params (string Name, object? Value)[] arguments) =>
        new(callId, toolName, arguments.ToDictionary(a => a.Name, a => a.Value));
}
