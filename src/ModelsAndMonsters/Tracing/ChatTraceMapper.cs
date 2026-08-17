using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Tracing;

/// <summary>
/// Converts <c>Microsoft.Extensions.AI</c> types into serialisable trace shapes.
/// </summary>
/// <remarks>
/// Deliberately never touches <c>RawRepresentation</c>: we capture the logical model interaction our
/// application sees, not provider transport objects, which could carry credentials or HTTP detail.
/// </remarks>
public static class ChatTraceMapper
{
    public static IReadOnlyList<TracedMessage> MapMessages(IEnumerable<ChatMessage>? messages) =>
        messages is null ? [] : [.. messages.Select(MapMessage)];

    public static TracedMessage MapMessage(ChatMessage message) => new()
    {
        Role = message.Role.Value,
        AuthorName = message.AuthorName,
        Text = string.IsNullOrEmpty(message.Text) ? null : message.Text,
        Contents = [.. message.Contents.Select(MapContent)]
    };

    /// <summary>Tool arguments arrive as a mutable dictionary; trace payloads keep a read-only view.</summary>
    public static IReadOnlyDictionary<string, object?>? MapArguments(IDictionary<string, object?>? arguments) =>
        arguments?.AsReadOnly();

    public static TracedContent MapContent(AIContent content) => content switch
    {
        TextContent text => new TracedContent { Type = "text", Text = text.Text },
        TextReasoningContent reasoning => new TracedContent { Type = "reasoning", Text = reasoning.Text },
        FunctionCallContent call => new TracedContent
        {
            Type = "functionCall",
            CallId = call.CallId,
            Name = call.Name,
            Arguments = MapArguments(call.Arguments),
            Detail = call.Exception?.Message
        },
        FunctionResultContent result => new TracedContent
        {
            Type = "functionResult",
            CallId = result.CallId,
            Result = ToSerialisableResult(result.Result),
            Detail = result.Exception?.Message
        },
        ErrorContent error => new TracedContent
        {
            Type = "error",
            Text = error.Message,
            Detail = error.ErrorCode
        },
        UsageContent usage => new TracedContent
        {
            Type = "usage",
            Detail = $"in={usage.Details.InputTokenCount} out={usage.Details.OutputTokenCount}"
        },
        _ => new TracedContent
        {
            Type = content.GetType().Name,
            Detail = content.ToString()
        }
    };

    public static IReadOnlyList<TracedFunctionCall> MapFunctionCalls(IEnumerable<ChatMessage>? messages) =>
        messages is null
            ? []
            : [.. messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Select(c => new TracedFunctionCall
                {
                    CallId = c.CallId,
                    Name = c.Name,
                    Arguments = MapArguments(c.Arguments)
                })];

    public static IReadOnlyList<TracedToolDefinition> MapTools(IEnumerable<AITool>? tools) =>
        tools is null
            ? []
            : [.. tools.Select(tool => new TracedToolDefinition
            {
                Name = tool.Name,
                Description = string.IsNullOrEmpty(tool.Description) ? null : tool.Description,
                JsonSchema = tool is AIFunctionDeclaration declaration ? declaration.JsonSchema : null
            })];

    public static TracedChatOptions MapOptions(ChatOptions? options, IReadOnlyList<string>? unsupportedDropped = null) => new()
    {
        ModelId = options?.ModelId,
        Temperature = options?.Temperature,
        TopP = options?.TopP,
        TopK = options?.TopK,
        MaxOutputTokens = options?.MaxOutputTokens,
        Seed = options?.Seed,
        ToolMode = options?.ToolMode?.GetType().Name,
        UnsupportedOptionsDropped = unsupportedDropped ?? []
    };

    public static TracedUsage? MapUsage(UsageDetails? usage) => usage is null
        ? null
        : new TracedUsage
        {
            InputTokenCount = usage.InputTokenCount,
            OutputTokenCount = usage.OutputTokenCount,
            TotalTokenCount = usage.TotalTokenCount,
            AdditionalCounts = usage.AdditionalCounts?.ToDictionary(kv => kv.Key, kv => kv.Value)
        };

    /// <summary>
    /// Flattens provider metadata to strings. Values from providers are arbitrary objects and may not
    /// be serialisable, so they are stringified rather than trusted to the serialiser.
    /// </summary>
    public static IReadOnlyDictionary<string, string?>? MapAdditionalProperties(AdditionalPropertiesDictionary? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        var mapped = new Dictionary<string, string?>(properties.Count, StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            mapped[key] = value?.ToString();
        }

        return mapped;
    }

    /// <summary>
    /// Tool results are application-authored objects. Strings and JSON elements pass through; anything
    /// else is round-tripped so an awkward type can never break the trace line.
    /// </summary>
    private static object? ToSerialisableResult(object? result) => result switch
    {
        null => null,
        string or JsonElement => result,
        _ => TrySerialise(result)
    };

    private static object? TrySerialise(object result)
    {
        try
        {
            return JsonSerializer.SerializeToElement(result, TraceJson.Compact);
        }
        catch (Exception ex)
        {
            return $"<unserialisable {result.GetType().Name}: {ex.Message}>";
        }
    }
}
