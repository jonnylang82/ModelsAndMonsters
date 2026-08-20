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
                    switch (engine.Outcome)
                    {
                        case AttackOutcome attack:
                            _publish(UiEvent.Attack(new AttackDto(
                                attack.AttackerName, attack.TargetName, attack.Hit, attack.Glancing,
                                attack.DamageDealt, attack.TargetHealthAfter, attack.TargetMaxHealth, attack.TargetDied)));
                            break;
                        case OpenExitOutcome openExit:
                            _publish(UiEvent.ExitOpened(openExit.ActorName, openExit.ExitName));
                            break;
                        case GiveItemOutcome give:
                            _publish(UiEvent.Gave(give.GiverName, give.RecipientName, give.ItemName));
                            break;
                        case DropItemOutcome drop:
                            _publish(UiEvent.Dropped(drop.ActorName, drop.ItemName));
                            break;
                        case StealItemOutcome steal:
                            _publish(UiEvent.StoleAttempt(steal.ThiefName, steal.TargetName, steal.ItemName, steal.Succeeded));
                            break;
                        case OfferSurrenderOutcome offer:
                            _publish(UiEvent.SurrenderOffered(
                                offer.OfferId, offer.OffererName, offer.RecipientName, offer.TermsDescription));
                            break;
                        case AcceptSurrenderOutcome accepted:
                            _publish(UiEvent.SurrenderAccepted(
                                accepted.AccepterName, accepted.OffererName, accepted.TransferredItemNames,
                                accepted.ForfeitedWeaponName,
                                accepted.ForfeitedWeaponName is null ? null : accepted.GroundContainerName));
                            break;
                        case GuardAllyOutcome guard:
                            _publish(UiEvent.AbilityUsed(
                                guard.GuardianName, "Guard Ally", guard.AllyName, "accepted", null, null));
                            break;
                        case HealingPrayerOutcome heal:
                            _publish(UiEvent.AbilityUsed(
                                heal.CasterName, heal.AbilityName, heal.TargetName, "accepted",
                                heal.RemainingUses, heal.HealthAfter - heal.HealthBefore));
                            break;
                        case RallyOutcome rally:
                            _publish(UiEvent.AbilityUsed(
                                rally.CommanderName, rally.AbilityName, rally.AllyName, "accepted",
                                rally.RemainingUses, null));
                            break;
                        case DefendOutcome defend:
                            _publish(UiEvent.AbilityUsed(defend.ActorName, "Defend", null, "accepted", null, null));
                            break;
                    }

                    // An attack that a guard moved onto the guardian, and one a braced guard softened, each get
                    // their own line: both are mechanically decisive and invisible in the attack line alone.
                    if (engine.Outcome is AttackOutcome resolved)
                    {
                        if (resolved is { Redirected: true, IntendedTargetName: { } intended })
                        {
                            _publish(UiEvent.AttackRedirected(resolved.AttackerName, intended, resolved.TargetName));
                        }

                        if (resolved.DefendReduction > 0)
                        {
                            _publish(UiEvent.DefendReduced(resolved.TargetName, resolved.DefendReduction));
                        }
                    }

                    _publish(UiEvent.State(StateDto.From(engine.StateAfter)));
                    break;

                // Status transitions and offer resolutions are their own lines: a guard that simply timed out,
                // or an offer nobody answered, otherwise vanishes from the view with no explanation.
                case TraceEventType.StatusApplied or TraceEventType.StatusConsumed
                    or TraceEventType.StatusExpired or TraceEventType.StatusRemoved
                    when traceEvent.Data is StatusEffectPayload status:
                    _publish(UiEvent.StatusChanged(
                        status.Transition, status.Kind,
                        status.TargetCharacterName ?? status.TargetCharacterId,
                        status.SourceCharacterName ?? status.SourceCharacterId,
                        status.Modifier, status.Cause));
                    break;

                case TraceEventType.SurrenderOfferResolved when traceEvent.Data is SurrenderOfferResolvedPayload resolvedOffer:
                    _publish(UiEvent.SurrenderOfferSettled(
                        resolvedOffer.OfferId, resolvedOffer.OffererName, resolvedOffer.RecipientName,
                        resolvedOffer.NewState, resolvedOffer.Cause));
                    break;

                // Surrender and escape are structured departures from combat, so the transcript can read them
                // distinctly from a death; each also re-sends the full state, updating the cards live.
                case TraceEventType.CharacterSurrendered when traceEvent.Data is CharacterSurrenderedPayload surrender:
                    _publish(UiEvent.Surrendered(surrender.CharacterName));
                    break;

                case TraceEventType.CharacterEscaped when traceEvent.Data is CharacterEscapedPayload escape:
                    _publish(UiEvent.Escaped(escape.CharacterName, escape.ExitName));
                    break;

                case TraceEventType.RunCompleted when traceEvent.Data is RunCompletedPayload completed:
                    _publish(UiEvent.State(StateDto.From(completed.FinalState)));
                    _publish(UiEvent.Completed(
                        completed.TerminalCondition, completed.Outcome, completed.WinningTeams, completed.Survivors));
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
