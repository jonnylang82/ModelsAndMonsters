using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Tracing;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Orchestration;

public sealed partial class TurnCoordinator
{
    private readonly ConversationRequests _requests = new();
    private readonly Dictionary<string, Queue<string>> _recentFailures = new();
    private int _speechActsCurrent;
    private bool _surrenderConfirmed;
    private readonly HashSet<string> _routedRequestsThisTurn = new(StringComparer.OrdinalIgnoreCase);

    private async Task<string?> RouteConversationAsync(CharacterAgent actor, string text, bool physicalIntent, CancellationToken cancellationToken)
    {
        if (_intentParser is null || !_limits.UseIntentParser || !_limits.RouteOrdinaryConversation) return null;
        var people = string.Join(", ", _engine.State.Characters.Where(c => c.IsPresent && c.IsAlive && c.Id != actor.CharacterId).Select(c => $"{c.Name} ({c.Disposition})"));
        IntentParser.ConversationRoute? route;
        try
        {
            route = await _intentParser.RouteConversationAsync(actor.Name, text, people,
                _requests.Render(actor.CharacterId, _engine.State), physicalIntent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _trace.Emit(TraceEventType.ConversationRequest, new { Status = "routing-unavailable", ActorId = actor.CharacterId, ErrorType = ex.GetType().Name });
            return null; // Speech remains public; no answer, transfer or consent is invented.
        }
        if (route?.Kind == "request")
        {
            // The speaker's declared addressee outranks an inferred name in their words.
            var recipient = !physicalIntent && _speechAddressedToThisTurn is { } explicitRecipient
                ? _engine.State.FindById(explicitRecipient)
                : route.Recipient is null ? null : _engine.State.Resolve(route.Recipient);
            if (recipient is null || !recipient.IsPresent || !recipient.IsAlive || recipient.Id == actor.CharacterId) return null;
            if (!physicalIntent)
            {
                var request = _requests.Add(actor.CharacterId, recipient.Id, text);
                _routedRequestsThisTurn.Add(recipient.Id);
                _trace.Emit(TraceEventType.ConversationRequest, new { request.Id, request.SenderId, request.RecipientId, request.Message, Status = "pending", Source = "spoken-request" });
                return "Your request is waiting for their decision; nobody is forced to act. Do not repeat it as a physical action. You can act or end your turn.";
            }
            if (_routedRequestsThisTurn.Contains(recipient.Id))
                return "Your spoken request is already awaiting their answer. Do not repeat it as a physical action; you can act or end your turn.";
            return QueueRequest(actor, recipient.Id, text, _engine.CurrentRound, _engine.CurrentTurn);
        }
        if (!physicalIntent && route?.Kind == "response" && route.Decision is "accept" or "decline" or "counter" or "ignore"
            && route.RequestId is { } id && _requests.Resolve(actor.CharacterId, id, _engine.State) is { } answered)
        {
            _trace.Emit(TraceEventType.ConversationRequest, new { answered.Id, answered.SenderId, answered.RecipientId, Status = route.Decision, Reply = text, Source = "spoken-response" });
            if (route.Decision == "counter")
            {
                var counter = _requests.Add(actor.CharacterId, answered.SenderId, text);
                _trace.Emit(TraceEventType.ConversationRequest, new { counter.Id, counter.SenderId, counter.RecipientId, counter.Message, Status = "pending" });
            }
            return "Your reply is recorded; no item or surrender changed.";
        }
        return null;
    }
    private readonly Dictionary<(string Actor, string Object), string> _knownInspections = new();

    private static string InspectionSignature(Container container) => System.Text.Json.JsonSerializer.Serialize(new
    {
        container.IsOpen, container.ExteriorClue,
        Items = container.IsOpen ? container.Contents.Select(i => i.Id).Order().ToArray() : []
    });

    private void RememberInspection(string actorId, string objectId)
    {
        if (FindContainer(objectId) is not { } container) return;
        foreach (var recipient in DiscoveryRecipients(actorId))
            _knownInspections[(recipient, objectId)] = InspectionSignature(container);
    }

    private bool IsRedundantInspection(string actorId, GameAction action) => action is InspectObjectAction inspect
        && _engine.State.ResolveObject(inspect.ObjectRef).Object is Container container
        && _knownInspections.TryGetValue((actorId, container.Id), out var observed)
        && observed == InspectionSignature(container);

    // The guest input form currently has no structured response selector; never trap a human behind an AI-only tool.
    private bool MustAnswerRequest(CharacterAgent actor) => !actor.LastDecisionWasHuman && _speechActsCurrent < _limits.MaxSpeechActsPerTurn
        && _requests.For(actor.CharacterId, _engine.State).Count > 0;

    private string RenderRecentFailures(string actorId) => _recentFailures.TryGetValue(actorId, out var failures)
        ? "\nRECENT REFUSALS: do not repeat these unless the relevant circumstances changed.\n" + string.Join("\n", failures)
        : "";

    private void RememberFailure(string actorId, string action, string reason)
    {
        if (!_recentFailures.TryGetValue(actorId, out var failures)) _recentFailures[actorId] = failures = new();
        var entry = $"- {action}: {reason}";
        if (failures.Contains(entry)) return;
        failures.Enqueue(entry);
        while (failures.Count > 3) failures.Dequeue();
    }

    private string QueueRequest(CharacterAgent sender, string? recipientRef, string? message, int round, int turn)
    {
        var recipient = recipientRef is null ? null : _engine.State.Resolve(recipientRef);
        if (recipient is null || !recipient.IsPresent || !recipient.IsAlive || recipient.Id == sender.CharacterId)
            return "Choose another living person who is still here. Nobody has been asked yet.";
        if (string.IsNullOrWhiteSpace(message) || message.Length > _limits.MaxSpeechCharacters)
            return "Give a short, non-empty spoken request. Nobody has been asked yet.";
        if (_speechActsCurrent >= _limits.MaxSpeechActsPerTurn)
            return "You have used your speech allowance this turn. No request was sent.";

        DeliverSpeech(sender, message.Trim(), round, turn, _speechActsCurrent + 1, recipient.Id);
        var request = _requests.Add(sender.CharacterId, recipient.Id, message.Trim());
        _trace.Emit(TraceEventType.ConversationRequest, new { request.Id, request.SenderId, request.RecipientId, request.Message, Status = "pending" });
        return $"{request.Id}: {recipient.Name} heard your request and will decide at their next opportunity. "
            + "No item moved, nobody surrendered, and no agreement exists yet. You can still act or end your turn.";
    }

    private void HandleConversationCall(CharacterAgent actor, FunctionCallContent call, int round, int turn)
    {
        string result;
        if (call.Name == CharacterTools.RequestName)
        {
            result = QueueRequest(actor, ToolArguments.GetString(call, "recipient"), ToolArguments.GetString(call, "message"), round, turn);
        }
        else
        {
            var id = ToolArguments.GetString(call, "request_id");
            var decision = ToolArguments.GetString(call, "decision");
            var message = ToolArguments.GetString(call, "message");
            var pending = _requests.For(actor.CharacterId, _engine.State).FirstOrDefault(r => r.Id == id);
            if (pending is null) result = "That request is not pending for you. You cannot answer for someone else.";
            else if (decision is not ("accept" or "decline" or "counter" or "ignore")
                || (decision != "ignore" && string.IsNullOrWhiteSpace(message))
                || message?.Length > _limits.MaxSpeechCharacters)
                result = "Choose accept, decline, counter or ignore, with a short reply (empty only for silence).";
            else if (_speechActsCurrent >= _limits.MaxSpeechActsPerTurn)
                result = "You have used your speech allowance; the request remains pending for next turn.";
            else
            {
                _requests.Resolve(actor.CharacterId, pending.Id, _engine.State);
                if (decision == "ignore") _speechActsCurrent++;
                else DeliverSpeech(actor, message!.Trim(), round, turn, _speechActsCurrent + 1, pending.SenderId);
                _trace.Emit(TraceEventType.ConversationRequest, new { pending.Id, pending.SenderId, pending.RecipientId, Status = decision, Reply = message });
                if (decision == "counter")
                {
                    var counter = _requests.Add(actor.CharacterId, pending.SenderId, message!.Trim());
                    _trace.Emit(TraceEventType.ConversationRequest, new { counter.Id, counter.SenderId, counter.RecipientId, counter.Message, Status = "pending" });
                }
                result = "Your decision is recorded. No possessions, allegiance or surrender changed. Carry out any promise using your own action, or choose another action.";
            }
        }
        DispatchAndRecord(actor, call, result, "conversation-only");
    }

    private ActionAttemptOutcome HandleAdjudicatedRequest(CharacterAgent actor, string intent, FunctionCallContent call)
    {
        var result = QueueRequest(actor, ToolArguments.GetString(call, "recipient"), ToolArguments.GetString(call, "message"),
            _engine.CurrentRound, _engine.CurrentTurn);
        RecordDungeonMasterToolResult(call, result);
        EmitAdjudication(actor, intent, ActionResolutionCategory.ConversationOnly, result, null, null);
        return new ActionAttemptOutcome { Category = ActionResolutionCategory.ConversationOnly, MessageToCharacter = result };
    }
}
