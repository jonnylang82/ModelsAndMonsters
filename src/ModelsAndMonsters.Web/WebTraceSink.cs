using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Web;

/// <summary>
/// An <see cref="ITraceSink"/> that turns a curated subset of trace events into structured UI events — the
/// authoritative state snapshots that drive the character cards, and the attacks that drive the combat
/// ticker. The verbose model requests/responses are ignored here; they remain only in the file trace. It
/// swallows its own failures so a disconnected viewer can never disrupt the file trace or the run.
/// </summary>
public sealed class WebTraceSink : ITraceSink
{
    private readonly Action<UiEvent> _publish;

    public WebTraceSink(Action<UiEvent> publish) => _publish = publish;

    public void Write(TraceEvent traceEvent)
    {
        try
        {
            switch (traceEvent.EventType)
            {
                case TraceEventType.ScenarioSeeded when traceEvent.Data is GameState initial:
                    _publish(UiEvent.State(StateDto.From(initial)));
                    break;

                case TraceEventType.TurnStarted when traceEvent.Data is TurnStartedPayload turn:
                    _publish(UiEvent.TurnStarted(turn.CharacterName));
                    _publish(UiEvent.State(StateDto.From(turn.StateAtTurnStart)));
                    break;

                case TraceEventType.EngineAction when traceEvent.Data is EngineActionPayload engine && engine.Accepted:
                    if (engine.Outcome is AttackOutcome attack)
                    {
                        _publish(UiEvent.Attack(new AttackDto(
                            attack.AttackerName, attack.TargetName, attack.Hit, attack.Glancing,
                            attack.DamageDealt, attack.TargetHealthAfter, attack.TargetMaxHealth, attack.TargetDied)));
                    }

                    _publish(UiEvent.State(StateDto.From(engine.StateAfter)));
                    break;

                case TraceEventType.RunCompleted when traceEvent.Data is RunCompletedPayload completed:
                    _publish(UiEvent.State(StateDto.From(completed.FinalState)));
                    _publish(UiEvent.Completed(completed.TerminalCondition, completed.Survivors));
                    break;
            }
        }
        catch
        {
            // Best-effort live view: never let a rendering slip disrupt the authoritative trace or the run.
        }
    }

    public void Dispose()
    {
    }
}
