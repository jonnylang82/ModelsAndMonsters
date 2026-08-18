using System.Text;
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

    // True once older turns have been folded into a running summary held as the first message. Turn-start
    // user messages are then counted from index 1, so the summary itself is not mistaken for a turn.
    private bool _summaryPresent;

    public AgentConversation(string agentName, string systemPrompt)
    {
        AgentName = agentName;
        SystemPrompt = systemPrompt;
    }

    public string AgentName { get; }

    public string SystemPrompt { get; }

    /// <summary>The history excluding the system prompt, in order.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>The current message count — a mark a turn records at its start so it can later compact back.</summary>
    public int Count => _messages.Count;

    public void Append(ChatMessage message) => _messages.Add(message);

    /// <summary>
    /// Compacts this turn's history, from <paramref name="mark"/> onward, down to what is worth carrying
    /// forward: the assistant messages that carried a tool call, and the tool results that answered them.
    /// The prose replies that produced no call and the nudges that followed them are dropped — they were
    /// scaffolding to coax out a clean action, and once the turn has resolved they are dead weight that only
    /// eats the context window on later turns. Everything before <paramref name="mark"/> (the system prompt,
    /// prior turns, this turn's injected context) is untouched, and every retained call keeps its matching
    /// result, so the history stays valid to send. The original prose remains in the trace.
    /// </summary>
    public void CompactTurn(int mark)
    {
        if (mark < 0 || mark >= _messages.Count)
        {
            return;
        }

        var kept = _messages.GetRange(0, mark);
        for (var i = mark; i < _messages.Count; i++)
        {
            var message = _messages[i];
            var carriesToolCall = message.Contents.OfType<FunctionCallContent>().Any();
            if (carriesToolCall || message.Role == ChatRole.Tool)
            {
                kept.Add(message);
            }
        }

        _messages.Clear();
        _messages.AddRange(kept);
    }

    /// <summary>
    /// Whether a running summary of older turns is already held at the front of the history.
    /// </summary>
    public bool HasSummary => _summaryPresent;

    /// <summary>
    /// Plans a trim that keeps the last <paramref name="keepRecentTurns"/> turns full and folds everything
    /// before them (including any existing summary) into a fresh summary. Each turn, after compaction, begins
    /// with exactly one injected user message, so those user messages are the turn boundaries. Returns false
    /// when there are not more than <paramref name="keepRecentTurns"/> turns to keep — nothing to summarise.
    /// On true, <paramref name="boundary"/> is where the kept turns begin and <paramref name="olderHistory"/>
    /// is the rendered text to summarise; the caller produces the summary and calls <see cref="ApplySummary"/>.
    /// </summary>
    public bool TryPlanSummaryTrim(int keepRecentTurns, out int boundary, out string olderHistory)
    {
        boundary = 0;
        olderHistory = "";

        var firstTurnStart = _summaryPresent ? 1 : 0;
        var turnStarts = new List<int>();
        for (var i = firstTurnStart; i < _messages.Count; i++)
        {
            if (_messages[i].Role == ChatRole.User)
            {
                turnStarts.Add(i);
            }
        }

        if (turnStarts.Count <= keepRecentTurns)
        {
            return false;
        }

        boundary = turnStarts[turnStarts.Count - keepRecentTurns];
        olderHistory = RenderRange(0, boundary);
        return olderHistory.Length > 0;
    }

    /// <summary>
    /// Replaces everything before <paramref name="boundary"/> with a single running-summary user message,
    /// keeping the recent turns that follow it intact. Tool-call/result pairing is preserved because the
    /// boundary always falls on a turn's opening user message, never between a call and its result.
    /// </summary>
    public void ApplySummary(int boundary, string summary)
    {
        if (boundary <= 0 || boundary > _messages.Count)
        {
            return;
        }

        var kept = _messages.GetRange(boundary, _messages.Count - boundary);
        _messages.Clear();
        _messages.Add(new ChatMessage(ChatRole.User, summary));
        _messages.AddRange(kept);
        _summaryPresent = true;
    }

    /// <summary>Renders a run of history messages into a plain transcript for the summariser to compress.</summary>
    private string RenderRange(int start, int end)
    {
        var builder = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var message = _messages[i];

            var text = message.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine(text.Trim());
            }

            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                var arguments = call.Arguments is null
                    ? ""
                    : string.Join(" ", call.Arguments.Values.Select(value => value?.ToString()));
                builder.AppendLine($"[{call.Name}] {arguments}".Trim());
            }

            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                builder.AppendLine($"[outcome] {result.Result}".Trim());
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Replaces the most recent message. Used when a prose reply is recovered into a real tool call: the
    /// prose assistant turn is swapped for one carrying the structured call, so the tool result that
    /// follows has a matching call in the history rather than dangling. The original prose stays in the
    /// trace.
    /// </summary>
    public void ReplaceLastMessage(ChatMessage message)
    {
        if (_messages.Count == 0)
        {
            _messages.Add(message);
        }
        else
        {
            _messages[^1] = message;
        }
    }

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
