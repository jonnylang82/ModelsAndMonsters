using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// Fails a run at startup when the action-routing metadata is incoherent — a card that governs nothing, an
/// action no card governs, or a declared boundary pointing at an action that does not exist.
/// </summary>
/// <remarks>
/// The same discipline as <see cref="RulebookRequestBudget"/> and the <c>RulebookMaxCards</c> ceiling: a gap
/// here is silent otherwise, and a silent gap is a rule that can never be selected — an engine action a router
/// could name and find no card for, or a card whose declared neighbour does not exist. Caught loudly at
/// construction, because it is a catalog error, not a runtime condition to limp through.
/// </remarks>
public static class ActionSurfaceValidation
{
    /// <summary>
    /// The card <see cref="RuleCard.ActionName"/> values that are not engine actions: the generic rejection,
    /// and the sentinel for a background mechanic that governs no action at all.
    /// </summary>
    private static readonly HashSet<string> NonActionNames =
        new(StringComparer.OrdinalIgnoreCase) { DungeonMasterTools.RejectActionName, RuleCard.ReferenceAction };

    /// <summary>
    /// Checks the routing metadata across the whole catalog. Throws <see cref="InvalidOperationException"/> on
    /// the first incoherence found, with the same loud-at-startup posture as the request-budget guard.
    /// </summary>
    public static void Validate(IReadOnlyList<RuleCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var engineActions = DungeonMasterTools.EngineActionsByName;

        // 1. Every card governs a real engine action, or explicitly says it governs none (reject/reference).
        foreach (var card in cards)
        {
            if (!engineActions.ContainsKey(card.ActionName) && !NonActionNames.Contains(card.ActionName))
            {
                throw new InvalidOperationException(
                    $"Rule card '{card.RuleId}' governs action '{card.ActionName}', which is not an engine action. " +
                    $"A card must govern one of the engine actions, or explicitly govern none " +
                    $"('{DungeonMasterTools.RejectActionName}' or '{RuleCard.ReferenceAction}'). " +
                    "A card that governs a non-existent action can never be offered to the Dungeon Master.");
            }
        }

        // 2. Every engine action is governed by at least one card — otherwise a router could name it and find
        //    no rule to read.
        var governed = cards.Select(c => c.ActionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var action in engineActions.Keys)
        {
            if (!governed.Contains(action))
            {
                throw new InvalidOperationException(
                    $"Engine action '{action}' is governed by no rule card. Every engine action must have at " +
                    "least one card, or the action-routing selector can route to it and find nothing to send. " +
                    "Add a card whose ActionName is this action, or remove the action from the engine surface.");
            }
        }

        // 3. Every declared boundary points at a real engine action — never at the rejection, a reference
        //    sentinel, or a typo.
        foreach (var card in cards)
        {
            foreach (var other in card.DistinguishedFrom)
            {
                if (!engineActions.ContainsKey(other))
                {
                    throw new InvalidOperationException(
                        $"Rule card '{card.RuleId}' declares DistinguishedFrom '{other}', which is not an engine " +
                        "action. A boundary must name an engine action a reader could be told apart from.");
                }
            }
        }
    }

    /// <summary>
    /// Checks that the composed action-routing index fits the resolver's context window with room to answer,
    /// the same guard <see cref="RulebookRequestBudget"/> applies to the whole-rulebook request. Does nothing
    /// when the window is unknown (a hosted model whose real window the harness does not know).
    /// </summary>
    public static void ValidateIndexFits(string index, AgentModelProfile resolverProfile, int configuredOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolverProfile);

        if (resolverProfile.BindingContextWindow is not { } window || window <= 0)
        {
            return;
        }

        var requestChars = ActionRoutingSelector.SystemPrompt.Length
                           + index.Length
                           + RulebookRequestBudget.AssumedIntentChars
                           + RequestScaffoldChars;
        var requestTokens = ContextTruncation.EstimateTokensForCharacters(requestChars);
        var reserve = Math.Max(RulebookRequestBudget.MinimumOutputReserveTokens, configuredOutputTokens);

        if (requestTokens + reserve > window)
        {
            throw new InvalidOperationException(
                $"The action-routing index does not fit the resolver's context window. A routing request is " +
                $"~{requestTokens} tokens against a {window}-token window, leaving {window - requestTokens} to " +
                $"reply in, and a reply needs at least {reserve}. Shorten the card summaries the index is built " +
                "from, or reduce the DistinguishedFrom edges each action carries.");
        }
    }

    /// <summary>The fixed scaffolding of the routing request template around the intent and the index.</summary>
    private const int RequestScaffoldChars = 200;
}
