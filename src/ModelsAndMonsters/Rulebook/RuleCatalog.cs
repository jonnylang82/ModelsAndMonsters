using System.Security.Cryptography;
using System.Text;
using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// The built-in rulebook: one small, versioned card per supported action, holding the detailed action rules
/// that used to live in the Dungeon Master's system prompt. Adapting these cards to the actions the engine
/// actually supports keeps the DM prompt a compact constitution while the specifics live here, retrieved a
/// few at a time and passed to the stateless Rulebook Resolver.
/// </summary>
public sealed class RuleCatalog : IRuleRepository
{
    /// <summary>The stable id of the generic rejection/failure card, always available to retrieval.</summary>
    public const string RejectRuleId = "action.reject";

    private readonly IReadOnlyDictionary<string, RuleCard> _byId;

    public RuleCatalog()
    {
        AllCards = BuildCards();
        _byId = AllCards.ToDictionary(c => c.RuleId, StringComparer.OrdinalIgnoreCase);
        RejectCard = _byId[RejectRuleId];
        RulebookVersion = ComputeRulebookVersion(AllCards);
    }

    public IReadOnlyList<RuleCard> AllCards { get; }

    public RuleCard RejectCard { get; }

    public string RulebookVersion { get; }

    public RuleCard? Find(string ruleId) =>
        !string.IsNullOrWhiteSpace(ruleId) && _byId.TryGetValue(ruleId.Trim(), out var card) ? card : null;

    public bool IsValid(string ruleId, string version) =>
        Find(ruleId) is { } card && string.Equals(card.Version, version, StringComparison.Ordinal);

    private static string ComputeRulebookVersion(IReadOnlyList<RuleCard> cards)
    {
        var combined = string.Join("|", cards.OrderBy(c => c.RuleId, StringComparer.Ordinal).Select(c => $"{c.RuleId}@{c.Version}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return "rulebook-" + Convert.ToHexStringLower(hash)[..10];
    }

    private static IReadOnlyList<RuleCard> BuildCards() =>
    [
        new RuleCard
        {
            RuleId = "combat.attack",
            ActionName = DungeonMasterTools.AttackCharacterName,
            Description = "One character striking another with the weapon they are carrying — a direct melee blow that MAKES CONTACT. The weapon must actually reach and strike the target: a swing, slash, stab, cut, chop, thrust or blow that lands (or is thrown to land). Movement counts as part of an attack ONLY when it is how the blow is delivered (lunging or stepping in to strike). A character merely moving, readying, threatening or posturing — with no blow described — is NOT attacking.",
            RequiredBindings = ["the acting character (attacker)", "the target character", "the attacker's weapon"],
            Preconditions = ["attacker is active and armed", "a blow is actually described making contact with the target — not merely a threat, an advance, or a readied weapon", "target is another active character (not dead, surrendered or escaped, and not the attacker)"],
            TurnCost = "consumes the turn",
            RngRequirement = "the engine rolls to hit and, on a hit, for a glancing blow — the resolver never decides the outcome",
            Visibility = "public: the whole room sees the blow and its result",
            SuccessBehaviour = "the engine applies damage and may record an injury or a death; only the engine decides whether it lands",
            FailureBehaviour = "a miss changes nothing but still spends the turn",
            Exclusions = ["throwing a weapon", "shoving, grappling or knocking down", "raising a guard or bracing", "frightening a foe into yielding", "a threatening step, advance, stance or menacing gesture that lands no blow — posturing is not an attack", "brandishing, gripping, raising, levelling or readying a weapon without actually striking", "feinting, intimidating or 'making ready' — with no blow described, this is speech or nothing, never attack_character"]
        },
        new RuleCard
        {
            RuleId = "inventory.use",
            ActionName = DungeonMasterTools.UseItemName,
            Description = "A character using an item from their own inventory on themselves; the item is consumed.",
            RequiredBindings = ["the acting character", "the item from their own inventory"],
            Preconditions = ["actor is active and carries the item", "the item has a supported effect (a healing item)"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: the room sees the item used",
            SuccessBehaviour = "the engine applies the item's effect and consumes it",
            FailureBehaviour = "refused if the actor does not carry the item or it has no supported effect",
            Exclusions = ["using an item on another character"]
        },
        new RuleCard
        {
            RuleId = "inventory.give",
            ActionName = DungeonMasterTools.GiveItemName,
            Description = "The acting character handing one of their own ordinary inventory items to another character present in the room. The recipient may be an ally or an enemy; consent is not modelled.",
            RequiredBindings = ["the acting character (giver)", "the recipient character", "the item from the giver's inventory"],
            Preconditions = ["giver is the current actor, active and present", "recipient is alive and present in the room", "the item is an ordinary inventory item the giver owns, not an equipped weapon"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees the item change hands and learns the recipient now carries it",
            SuccessBehaviour = "the item moves atomically from giver to recipient",
            FailureBehaviour = "refused if the giver does not own the item, it is an equipped weapon, or the recipient is not present",
            Exclusions = ["giving an equipped weapon", "forcing the recipient to accept or use it"]
        },
        new RuleCard
        {
            RuleId = "inventory.drop",
            ActionName = DungeonMasterTools.DropItemName,
            Description = "The acting character dropping one of their own ordinary inventory items onto the floor, where anyone present may later pick it up.",
            RequiredBindings = ["the acting character", "the item from their own inventory"],
            Preconditions = ["actor is the current actor, active and present", "the item is an ordinary inventory item the actor owns, not an equipped weapon"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees it fall and learns it lies on the floor",
            SuccessBehaviour = "the item moves to the floor keeping its stable id, and can afterwards be taken with take_item",
            FailureBehaviour = "refused if the actor does not own the item or it is an equipped weapon",
            Exclusions = ["dropping an equipped weapon", "throwing an item to a specific person to catch (that is not a supported action)"]
        },
        new RuleCard
        {
            RuleId = "inventory.steal",
            ActionName = DungeonMasterTools.StealItemName,
            Description = "The acting character trying to snatch one ordinary inventory item from another active character. The attempt is always noticed and may fail.",
            RequiredBindings = ["the acting character (thief)", "the target character", "the item from the target's inventory"],
            Preconditions = ["thief and target are both active and present", "the item is an ordinary inventory item the target owns, not an equipped weapon", "the thief has a legitimate informational basis to know the target carries the item (seen it carried, or seen it taken, given or dropped, or been told of it)"],
            TurnCost = "consumes the turn whether the theft succeeds or fails",
            RngRequirement = "the engine makes exactly one seeded draw against a base theft chance — the resolver never decides the outcome",
            Visibility = "public: the attempt is always noticed by everyone present, whether it succeeds or fails",
            SuccessBehaviour = "on success the item moves atomically from target to thief",
            FailureBehaviour = "on failure nothing changes hands, but the turn is still spent and the attempt is seen",
            Exclusions = ["stealing an equipped weapon", "stealing from a surrendered, escaped or dead character", "stealing an item the thief has no way of knowing exists", "secret or unnoticed theft"]
        },
        new RuleCard
        {
            RuleId = "container.open",
            ActionName = DungeonMasterTools.OpenContainerName,
            Description = "A character opening a closed container in the room. Opening only opens it; it never takes anything out.",
            RequiredBindings = ["the acting character", "the container"],
            Preconditions = ["actor is active", "the container is in the room and closed"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public that the lid is up; but the contents are private to whoever looked inside — being open does not reveal them to the room",
            SuccessBehaviour = "the container becomes open and the opener alone observes its contents",
            FailureBehaviour = "refused if it is already open or there is no such container",
            Exclusions = ["taking anything out in the same act (that is a separate turn)", "opening and fleeing in one act", "any lock, trap or key — containers are never locked"]
        },
        new RuleCard
        {
            RuleId = "container.take",
            ActionName = DungeonMasterTools.TakeItemName,
            // Container-CONTEXTUAL phrases, not bare verbs: "seize"/"snatch"/"grab" are shared with stealing
            // from a person (inventory.steal), so keying on them here would wrongly outrank a theft. Instead
            // this matches phrasings that name the container context ("from the open", "out of the case", "off
            // the floor"); for the many other ways a take is worded, the container family (see RuleRetriever)
            // co-retrieves this card whenever a container noun ("case", "chest", "the floor", …) appears at all.
            Description = "A character taking one item out of an already-open container (including the floor) into their own inventory.",
            RequiredBindings = ["the acting character", "the open container (or the floor)", "the item to take"],
            Preconditions = ["actor is active", "the container is open", "the item is in the container and the actor has a legitimate basis to know it is there"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: the room sees the item carried out",
            SuccessBehaviour = "the item moves atomically from the container to the actor's inventory",
            FailureBehaviour = "refused if the container is closed or the item is not in it",
            Exclusions = ["taking from a closed container without opening it first", "taking an item the character has no way of knowing is there"]
        },
        new RuleCard
        {
            RuleId = "object.inspect",
            ActionName = DungeonMasterTools.InspectObjectName,
            Description = "A character examining an object in the room closely to learn more than a glance gives — a marking, or the contents of an already-open container. It is looking, not opening and not taking.",
            RequiredBindings = ["the acting character", "the object"],
            Preconditions = ["actor is active", "the object is in the room and has something a close look could reveal"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "the room sees only that the character examined the object; what is found is the inspector's alone",
            SuccessBehaviour = "the inspector privately learns the marking and, for an open container, its current contents",
            FailureBehaviour = "refused if a closer look reveals nothing beyond a glance",
            Exclusions = ["opening the object", "taking anything"]
        },
        new RuleCard
        {
            RuleId = "encounter.open-exit",
            ActionName = DungeonMasterTools.OpenExitName,
            // Short, robust tokens: substring matching breaks on any inserted word (e.g. "open the CELLAR
            // door"), so exact multi-word phrases are avoided. The exit-location tokens ("door", "stair",
            // "exit", "way out") are shared with encounter.escape so any door intent retrieves BOTH cards and
            // the resolver — which, unlike this stateless retriever, is disambiguating open-vs-through — picks.
            Description = "A character opening a closed exit (a door or way out) so it can be passed through. Opening only opens; it never carries anyone through.",
            RequiredBindings = ["the acting character", "the exit"],
            Preconditions = ["actor is active", "the exit is in the room"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees the door swing open",
            SuccessBehaviour = "the exit becomes open; nobody has passed through it yet",
            FailureBehaviour = "refused if it already stands open",
            Exclusions = ["going through it in the same act (escaping is a separate turn)", "any lock or bar — exits are never locked, only shut"]
        },
        new RuleCard
        {
            RuleId = "encounter.escape",
            ActionName = DungeonMasterTools.EscapeEncounterName,
            // Escape is uniquely disadvantaged by the leaf-verb bonus: models say "run", "flee", "bolt",
            // "get clear" — almost never "escape" — so, unlike attack/take/open, the action's own leaf verb
            // rarely appears. These short verb tokens plus the shared exit-location tokens ("door", "stair",
            // "exit", "way out", also on encounter.open-exit) are what make a "run through the open door"
            // intent retrieve this card at all, so the resolver can propose escape_encounter.
            Description = "A character passing through an already-open exit to leave the encounter, abandoning the fight.",
            RequiredBindings = ["the acting character", "the open exit"],
            Preconditions = ["actor is active", "the exit is open"],
            TurnCost = "consumes the turn",
            RngRequirement = "none, and there is no escape roll, opportunity attack or pursuit",
            Visibility = "public: everyone present sees them go",
            SuccessBehaviour = "the character leaves the encounter alive and can no longer be reached or targeted",
            FailureBehaviour = "refused if the exit is still shut — it must be opened first",
            Exclusions = ["escaping through a closed exit", "opening and fleeing in one act"]
        },
        new RuleCard
        {
            RuleId = "encounter.surrender",
            ActionName = DungeonMasterTools.SurrenderName,
            Description = "The acting character giving up their own fight and taking no further part in it. Unilateral: it needs no opponent's approval, no roll and no prior demand.",
            RequiredBindings = ["the acting character (the one giving up)"],
            Preconditions = ["actor is active"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees them yield",
            SuccessBehaviour = "the character becomes surrendered — alive and present, out of the fight, no longer a valid target; they keep their weapon and belongings",
            FailureBehaviour = "n/a",
            Exclusions = ["making another character surrender", "surrendering because someone told, threatened or asked them to — that is speech, not this action", "disarming or taking anything from the one who yields"]
        },
        new RuleCard
        {
            RuleId = RejectRuleId,
            ActionName = DungeonMasterTools.RejectActionName,
            Description = "The generic refusal, used when the intent is not any supported action. 'impossible' when the character simply could not do it; 'unsupported' when a person could try it but the world has no way to resolve it.",
            RequiredBindings = ["a category (impossible or unsupported)", "a short in-world reason addressed to the character"],
            Preconditions = ["the intent matches no supported action, or a supported action's exclusions"],
            TurnCost = "a refusal changes nothing and does not consume the turn",
            RngRequirement = "none",
            Visibility = "private to the character who tried; a refusal makes nothing happen in the world",
            SuccessBehaviour = "the character is told in-world why the attempt did not happen and may try something else",
            FailureBehaviour = "n/a",
            Exclusions = ["inventing an outcome", "describing the attempt half-happening", "naming the machinery (rules/engine/what can be resolved)"]
        }
    ];
}
