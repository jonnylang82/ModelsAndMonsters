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
    /// <param name="attempt">
    /// Which transient-retry attempt this is, counting from 1. Recorded because a retry is otherwise
    /// invisible in the trace except as an unexplained shift in the sampling options: a reader had to know
    /// that the seed is offset by the attempt number to tell a re-send from a fresh call.
    /// </param>
    public IDisposable BeginCall(string purpose, ImmutableArray<string> unsupportedOptionsDropped = default, int attempt = 1)
    {
        var previous = _current;
        _current = new CallScopeState(
            purpose,
            unsupportedOptionsDropped.IsDefault ? [] : unsupportedOptionsDropped,
            attempt);
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
            EmitTruncationIfAny(callId, scope, response, options);
            EmitContextSaturationIfAny(callId, scope, response, materialised);
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
                Attempt = scope.Attempt,
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
            RequestedOptions = ChatTraceMapper.MapOptions(options, scope.UnsupportedOptionsDropped, _profile.ContextWindow, _profile.Thinking, _profile.Effort?.ToString()),
            Attempt = scope.Attempt,
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

    /// <summary>
    /// Flags a reply that stopped because it ran out of room. It is recorded here, at the one place
    /// every model call passes through, so no call site can forget to look.
    /// </summary>
    private void EmitTruncationIfAny(string callId, CallScopeState scope, ChatResponse response, ChatOptions? options)
    {
        if (response.FinishReason != ChatFinishReason.Length)
        {
            return;
        }

        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        var hadToolCalls = contents.OfType<FunctionCallContent>().Any();
        var hadVisibleText = contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
        var hadReasoning = contents.OfType<TextReasoningContent>().Any(r => !string.IsNullOrWhiteSpace(r.Text));

        // The distinctive failure of a reasoning model in this harness: the whole output budget went on
        // a private reasoning block, leaving no tool call and no visible prose. This is worth calling out
        // by name, because the fix is not "narrate less" — it is to disable thinking or grant more room.
        var reasoningOnly = !hadToolCalls && !hadVisibleText && hadReasoning;

        _trace.Emit(TraceEventType.ModelResponseTruncated, new ModelTruncatedPayload
        {
            AgentName = _profile.AgentName,
            Provider = _profile.Provider.ToString(),
            ModelId = _profile.ModelId,
            Purpose = scope.Purpose,
            CallId = callId,
            OutputTokenCount = response.Usage?.OutputTokenCount,
            MaxOutputTokensRequested = options?.MaxOutputTokens,
            HadToolCalls = hadToolCalls,
            ReasoningOnly = reasoningOnly,
            Effect = reasoningOnly
                ? "The model spent its entire output budget reasoning and produced no visible reply. " +
                  "Disable thinking for this agent, or raise MaxOutputTokens well above the reasoning length."
                : hadToolCalls
                    ? "A tool call survived, but any text alongside it is incomplete."
                    : "The reply was cut off before any tool call was produced."
        }, _profile.AgentName);
    }

    /// <summary>
    /// Flags a request whose reported input size fell well below what we sent.
    /// </summary>
    /// <remarks>
    /// This is the "compare what you sent against what the response says arrived" check. It needs no
    /// configured window and does not trust one: it works purely from the reported input size, so it
    /// catches truncation below the nominal window and truncation when no window was declared.
    /// </remarks>
    private void EmitContextSaturationIfAny(string callId, CallScopeState scope, ChatResponse response, IReadOnlyList<ChatMessage> messagesSent)
    {
        // The sent-versus-reported inference only makes sense where the provider truncates silently. A
        // provider that errors on overflow (OpenAI) never drops history unannounced, so here the gap is
        // only tokenizer-estimate noise and would raise false alarms — skip it entirely.
        if (!ProviderCapabilities.For(_profile.Provider).SilentlyTruncatesHistory)
        {
            return;
        }

        if (response.Usage?.InputTokenCount is not { } reportedInputTokens)
        {
            return;
        }

        var estimatedSent = ContextTruncation.EstimateSentTokens(messagesSent);
        if (!ContextTruncation.WasInputTruncated(estimatedSent, reportedInputTokens, out var dropped))
        {
            return;
        }

        _trace.Emit(TraceEventType.ContextWindowSaturated, new ContextSaturationPayload
        {
            AgentName = _profile.AgentName,
            Purpose = scope.Purpose,
            CallId = callId,
            EstimatedSentTokens = estimatedSent,
            ReportedInputTokens = reportedInputTokens,
            EstimatedDroppedTokens = dropped,
            MessagesSent = messagesSent.Count,
            ConfiguredContextWindow = _profile.ContextWindow,
            Effect = $"We sent roughly {estimatedSent} tokens but the provider reported processing only " +
                     $"{reportedInputTokens}. About {dropped} tokens of earlier history were discarded " +
                     "before the model saw them, so this reply was not formed from the whole conversation."
        }, _profile.AgentName);
    }

    private readonly record struct CallScopeState(
        string Purpose, IReadOnlyList<string> UnsupportedOptionsDropped, int Attempt = 1)
    {
        public static readonly CallScopeState Unspecified = new("unspecified", []);
    }

    private sealed class CallScope(TracingChatClient owner, CallScopeState previous) : IDisposable
    {
        public void Dispose() => owner._current = previous;
    }
}
