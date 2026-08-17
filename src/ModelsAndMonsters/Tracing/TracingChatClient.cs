using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;

namespace ModelsAndMonsters.Tracing;

/// <summary>
/// Wraps one agent's <see cref="IChatClient"/> and records the complete logical request and response
/// for every call.
/// </summary>
/// <remarks>
/// <para>
/// This is the tracing boundary. It sees exactly what the application sends and receives through the
/// abstraction: messages, tool declarations, sampling options, returned content, tool calls, usage and
/// elapsed time. It deliberately does not inspect provider transport objects, so credentials and HTTP
/// headers can never reach the trace.
/// </para>
/// <para>
/// One instance per agent. <see cref="BeginCall"/> supplies the call's purpose; orchestration is
/// sequential per agent, so a scoped field is sufficient and keeps call sites free of plumbing.
/// </para>
/// </remarks>
public sealed class TracingChatClient : DelegatingChatClient
{
    private readonly ExperimentTrace _trace;
    private readonly AgentModelProfile _profile;
    private CallScopeState _current = CallScopeState.Unspecified;
    private int _messagesTracedLastCall;
    private long _callCounter;

    public TracingChatClient(IChatClient innerClient, AgentModelProfile profile, ExperimentTrace trace)
        : base(innerClient)
    {
        _profile = profile;
        _trace = trace;
    }

    /// <summary>Declares the purpose of the next call(s) made through this client.</summary>
    public IDisposable BeginCall(string purpose, ImmutableArray<string> unsupportedOptionsDropped = default)
    {
        var previous = _current;
        _current = new CallScopeState(
            purpose,
            unsupportedOptionsDropped.IsDefault ? [] : unsupportedOptionsDropped);
        return new CallScope(this, previous);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scope = _current;
        var materialised = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var callId = $"{_profile.AgentName}-{Interlocked.Increment(ref _callCounter):D4}";

        EmitRequest(callId, scope, materialised, options);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(materialised, options, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            EmitResponse(callId, scope, response, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _trace.Emit(TraceEventType.ModelError, new ModelErrorPayload
            {
                AgentName = _profile.AgentName,
                Provider = _profile.Provider.ToString(),
                ModelId = _profile.ModelId,
                Purpose = scope.Purpose,
                CallId = callId,
                ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                Message = ex.Message,
                StackTrace = ex.StackTrace,
                ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds
            }, _profile.AgentName);

            throw;
        }
    }

    private void EmitRequest(string callId, CallScopeState scope, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var traced = ChatTraceMapper.MapMessages(messages);

        // Everything appended since this client's previous call is what was newly injected for this one.
        IReadOnlyList<TracedMessage> newlyInjected = traced.Count > _messagesTracedLastCall
            ? [.. traced.Skip(_messagesTracedLastCall)]
            : [];
        _messagesTracedLastCall = traced.Count;

        _trace.Emit(TraceEventType.ModelRequest, new ModelRequestPayload
        {
            AgentName = _profile.AgentName,
            Provider = _profile.Provider.ToString(),
            ModelId = _profile.ModelId,
            Purpose = scope.Purpose,
            CallId = callId,
            SystemPrompt = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text,
            Messages = traced,
            NewlyInjected = newlyInjected,
            RequestedOptions = ChatTraceMapper.MapOptions(options, scope.UnsupportedOptionsDropped),
            Tools = ChatTraceMapper.MapTools(options?.Tools)
        }, _profile.AgentName);
    }

    private void EmitResponse(string callId, CallScopeState scope, ChatResponse response, double elapsedMilliseconds)
    {
        _trace.Emit(TraceEventType.ModelResponse, new ModelResponsePayload
        {
            AgentName = _profile.AgentName,
            Provider = _profile.Provider.ToString(),
            ModelId = _profile.ModelId,
            Purpose = scope.Purpose,
            CallId = callId,
            ResponseId = response.ResponseId,
            ReportedModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason?.Value,
            Messages = ChatTraceMapper.MapMessages(response.Messages),
            Text = string.IsNullOrEmpty(response.Text) ? null : response.Text,
            ToolCalls = ChatTraceMapper.MapFunctionCalls(response.Messages),
            Usage = ChatTraceMapper.MapUsage(response.Usage),
            ProviderMetadata = ChatTraceMapper.MapAdditionalProperties(response.AdditionalProperties),
            ElapsedMilliseconds = elapsedMilliseconds
        }, _profile.AgentName);
    }

    private readonly record struct CallScopeState(string Purpose, IReadOnlyList<string> UnsupportedOptionsDropped)
    {
        public static readonly CallScopeState Unspecified = new("unspecified", []);
    }

    private sealed class CallScope(TracingChatClient owner, CallScopeState previous) : IDisposable
    {
        public void Dispose() => owner._current = previous;
    }
}
