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

    /// <summary>Stamps a reply with the input size the provider claims it processed.</summary>
    public static ChatResponse WithInputTokens(ChatResponse response, long reportedInputTokens)
    {
        response.Usage = new UsageDetails { InputTokenCount = reportedInputTokens, OutputTokenCount = 20 };
        return response;
    }

    /// <summary>A reply cut off at the output-token limit, losing whatever came next.</summary>
    public static ChatResponse Truncated(string partialText) =>
        new(new ChatMessage(ChatRole.Assistant, partialText))
        {
            FinishReason = ChatFinishReason.Length,
            Usage = new UsageDetails { OutputTokenCount = 500 }
        };

    /// <summary>
    /// A reasoning model that spent its whole output budget thinking: the reply is a reasoning block
    /// with no visible text and no tool call, cut off at the token limit.
    /// </summary>
    public static ChatResponse ReasoningOnly(string reasoning) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextReasoningContent(reasoning)]))
        {
            FinishReason = ChatFinishReason.Length,
            Usage = new UsageDetails { OutputTokenCount = 500 }
        };

    /// <summary>A single assistant message requesting several tools at once.</summary>
    public static ChatResponse Calls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls])) { FinishReason = ChatFinishReason.ToolCalls };

    public static FunctionCallContent CallContent(string callId, string toolName, params (string Name, object? Value)[] arguments) =>
        new(callId, toolName, arguments.ToDictionary(a => a.Name, a => a.Value));
}
