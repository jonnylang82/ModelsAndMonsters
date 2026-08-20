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
            Exclusions = ["throwing a weapon", "shoving, grappling or knocking down", "raising a guard, bracing, standing one's ground or readying to parry — that is the defend rule, not an attack", "frightening a foe into yielding", "a threatening step, advance, stance or menacing gesture that lands no blow — posturing is not an attack", "brandishing, gripping, raising, levelling or readying a weapon without actually striking", "feinting, intimidating or 'making ready' — with no blow described, this is speech, the defend rule, or nothing, never attack_character", "shielding a companion from a blow — that is the guard-ally technique"]
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
            Exclusions = ["stealing an equipped weapon", "stealing from a surrendered, escaped or dead character", "stealing an item the thief has no way of knowing exists", "secret or unnoticed theft", "reaching for the very thing a pending surrender offer to this character promised — taking that is ACCEPTING the offer, not stealing"]
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
            Exclusions = ["taking from a closed container without opening it first", "taking an item the character has no way of knowing is there", "taking an item out of another character's hand or off their belt — that is a theft, or, when the item is what a pending offer to this character promised, an acceptance of that offer"]
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
            Exclusions = ["escaping through a closed exit", "opening and fleeing in one act", "giving up the fight — that is an offer of surrender, not an escape"]
        },
        new RuleCard
        {
            RuleId = "encounter.offer-surrender",
            ActionName = DungeonMasterTools.OfferSurrenderName,
            Description = "The acting character offering to give up their own fight to ONE named opponent, on concrete terms they promise to hand over — at least one item they carry (coin or otherwise), optionally with the weapon in their hand alongside it — or, for a character stripped of everything, the weapon alone. This is how a character yields: giving up is never unilateral and never free. Recognise it in words like yielding, surrendering, giving in, throwing down the fight, begging to be spared, buying their life, or offering payment or their weapon in exchange for mercy — PROVIDED something concrete is promised. The plea, argument or threat itself is ordinary speech; this action is only the enforceable terms. CRITICAL — WHOSE fight is being given up. This rule is ONLY for a character giving up THEIR OWN fight, promising THEIR OWN belongings. A character who DEMANDS that an opponent give up, threatens them, or says they will spare them if they hand something over is doing the opposite of surrendering: that is a demand, it binds nobody, and it is ordinary speech, never this action. ‘Take my purse and let me live’ is this rule; ‘hand over your purse and I will spare you’ is not, and recording it as one would make the speaker the one who gave up.",
            RequiredBindings = ["the acting character (the one offering to give up)", "the ONE opposing character the terms are offered to", "the ordinary inventory items promised (may be none)", "whether the weapon in hand is promised"],
            Preconditions = ["offerer is the current actor and active", "the recipient is a living, present, active character on an OPPOSING side", "every promised item is an ordinary item the offerer owns right now", "the offerer holds nothing back: every offer must promise a carried item, unless they carry nothing at all, in which case the weapon in hand alone is enough", "the offerer has no other offer already awaiting an answer"],
            TurnCost = "consumes the offerer's WHOLE turn: putting terms on the table is the entire action and cannot be combined with a blow, a guard or anything else. An intent that both strikes (or guards, or braces) AND offers terms is the striking action, with the terms as mere speech",
            RngRequirement = "none",
            Visibility = "public: everyone present hears the terms",
            SuccessBehaviour = "a pending offer is recorded. NOTHING moves, nobody is disarmed, no disposition changes and the offerer stays an active, targetable combatant. Only the named recipient can accept it, on their own turn",
            FailureBehaviour = "refused if the offer promises nothing concrete, names an item the offerer does not own, names an ally rather than an opponent, or duplicates an offer already awaiting an answer",
            Exclusions = ["a bare plea to be spared with nothing promised — that is speech, not an offer", "terms promising only the weapon while the offerer still carries other things — holding possessions back is not a real offer (a character carrying NOTHING may promise the weapon alone)", "yielding without naming an opponent", "offering an item the character does not carry", "making somebody else surrender", "DEMANDING an opponent yield, or threatening to spare them if they pay — the speaker is not the one giving up, so this is speech and nothing else", "promising an item another character carries — only what the offerer holds can be put on the table", "promises about the future — service, ransom paid later, or anything beyond what is handed over on acceptance", "a secret offer: terms are always public"]
        },
        new RuleCard
        {
            RuleId = "encounter.accept-surrender",
            ActionName = DungeonMasterTools.AcceptSurrenderName,
            // The physical description of an acceptance IS reaching out and taking the promised thing, which
            // reads exactly like taking-from-a-hand — an unsupported action. A live run refused a recipient
            // three turns running for saying "I take the vial from Vark's hand and tell him he may live" and
            // even "I reach forward and take the vial as part of accepting his surrender terms". So the card
            // has to claim that phrasing explicitly, or the natural words for accepting never reach this rule.
            Description = "The acting character taking up a pending offer of surrender that was made TO THEM, sparing the offerer on the promised terms. Recognise it in words like accepting, agreeing, taking the deal, taking the coin and letting them live, sparing them, or telling them to keep their life. Recognise it ESPECIALLY when the character describes physically taking the promised thing from the one offering it — reaching out for it, taking it from their hand or belt, holding out a hand for it — while sparing them, letting them live, or saying they accept. That is not an item-grab and not a theft: when the thing named is what a pending offer to this character promised, taking it IS the acceptance, and this rule resolves it. Only the named recipient of a pending offer can do this. An acceptance stays an acceptance however much the character piles on top of it: a threat, a demand for something further, a condition (‘he lives IF he yields the salve too’), or a restatement of the terms that gets them slightly wrong. None of that is a new kind of action and none of it makes the intent unresolvable — the engine settles exactly what the standing offer promised, and the extra simply does not happen. Never call such an intent unsupported.",
            RequiredBindings = ["the acting character (the named recipient)", "the stable id of the pending offer being accepted"],
            Preconditions = ["the accepter is the current actor and active", "the accepter is the NAMED recipient of that offer", "the offer is still pending", "the offerer is still active and present", "every promised asset is still the offerer's"],
            TurnCost = "consumes the accepter's turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees the tribute change hands",
            SuccessBehaviour = "the whole agreement is enforced at once — every promised item passes to the accepter, the offerer is disarmed and any promised weapon falls to the floor, a durable agreement is recorded, and the offerer becomes surrendered: alive, present, out of the fight and no longer a valid target",
            FailureBehaviour = "refused, with nothing transferred, if the actor is not the named recipient, the offer is no longer pending, or any promised asset has since left the offerer's hands",
            Exclusions = ["accepting an offer made to somebody else", "accepting part of the terms — acceptance is all or nothing", "demanding different terms (that is speech; the offerer may propose new terms on a later turn)", "attacking the offerer and taking the tribute anyway", "going back on an accepted surrender"]
        },
        new RuleCard
        {
            RuleId = "combat.defend",
            ActionName = DungeonMasterTools.DefendName,
            Description = "The acting character spending the whole turn braced behind their guard instead of striking, so the next blow that lands on them is softened. Every active character can do this, as often as they like. Recognise it in words like bracing, standing one's ground, holding or keeping the guard up, readying to parry, turning aside a blow, covering oneself, or setting one's feet — any defensive posture with no blow described. Defensive posturing is THIS, never an attack.",
            RequiredBindings = ["the acting character"],
            Preconditions = ["actor is the current actor and active", "the actor is not already braced"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees them set themselves",
            SuccessBehaviour = "the actor becomes braced: the next blow that lands on them deals one less damage, applied after armour and after any glancing reduction and never below zero. A miss leaves the guard up; it falls away at the start of the actor's next turn",
            FailureBehaviour = "refused only if they already have their guard up",
            Exclusions = ["striking as well as bracing", "protecting somebody else (that is the guard-ally technique)", "dodging out of the way — there is no distance or movement in this world", "blocking a doorway or holding anyone back"]
        },
        new RuleCard
        {
            RuleId = "ability.guard-ally",
            ActionName = DungeonMasterTools.UseAbilityName,
            Description = "Ability 'guard-ally' (Guard Ally), a repeatable technique: the acting character spends the whole turn standing over ONE companion so the next blow an enemy aims at that companion lands on the guardian instead. Recognise it in words like shielding, covering, standing over, stepping in front of, putting oneself between an enemy and a companion, or taking the next blow meant for them. Only a character whose ability list includes 'guard-ally' can do it.",
            RequiredBindings = ["the acting character (the guardian)", "the ability id 'guard-ally'", "the ONE companion being guarded"],
            Preconditions = ["the guardian is the current actor, active, and holds the guard-ally ability", "the target is another living, present, active character on the guardian's OWN side", "the guardian is not already guarding somebody, and the target is not already guarded"],
            TurnCost = "consumes the guardian's whole turn",
            RngRequirement = "none when taken up; the redirection itself makes NO extra roll — the attacker's ordinary attack rolls are simply resolved against the guardian",
            Visibility = "public: everyone present sees the guardian take up position over their companion",
            SuccessBehaviour = "a linked guarding relationship is recorded. The next attack an enemy makes on the guarded companion is redirected to the guardian and resolved with the guardian's armour and health; that one redirection uses the guard up. Unused, it falls away at the start of the guardian's next turn",
            FailureBehaviour = "refused if the character does not have the ability, names themselves, names an enemy, or either party is already in a guard",
            Exclusions = ["guarding oneself", "guarding an enemy", "guarding two companions at once", "guarding an object, a container or a door", "striking in the same act", "any guarantee of safety — the guardian takes the blow, they do not prevent it"]
        },
        new RuleCard
        {
            RuleId = "ability.healing-prayer",
            ActionName = DungeonMasterTools.UseAbilityName,
            Description = "Ability 'healing-prayer' (Healing Prayer), a spell usable ONCE per encounter: the acting character prays over themselves or one companion and closes a fixed amount of their wounds. Recognise it in words like praying over someone, calling on a god to mend a wound, laying on hands, closing or knitting a gash, or blessing a companion's hurts. Only a character whose ability list includes 'healing-prayer' can do it.",
            RequiredBindings = ["the acting character (the caster)", "the ability id 'healing-prayer'", "the target: the caster themselves or one companion"],
            Preconditions = ["the caster is the current actor, active, holds the ability, and has a use of it left", "the target is the caster or a living, present, active character on the caster's own side", "the target is actually wounded — below their full health"],
            TurnCost = "consumes the caster's turn",
            RngRequirement = "none: the amount healed is fixed and no dice are rolled",
            Visibility = "public: everyone present sees and hears the prayer",
            SuccessBehaviour = "a fixed amount of health is restored, never past the target's maximum, and one use of the spell is spent",
            FailureBehaviour = "refused, WITHOUT spending the use, if the ability is not held, has no uses left, the target is unwounded, or the target is dead, fled or an enemy",
            Exclusions = ["raising the dead", "healing an enemy", "reaching someone who has fled the encounter", "healing past full health", "healing more than once in an encounter", "curing an injury already recorded — health is restored, scars are not"]
        },
        new RuleCard
        {
            RuleId = "ability.rally-grunt",
            ActionName = DungeonMasterTools.UseAbilityName,
            Description = "Ability 'rally-grunt' (Rally Grunt), a command usable ONCE per encounter: the acting character barks an order that steadies ONE companion so their next attack is markedly more likely to land. Recognise it in words like ordering, urging, spurring, steadying, encouraging or driving a companion on to strike. Only a character whose ability list includes 'rally-grunt' can do it. Merely shouting at somebody is speech; this is the deliberate use of the ability.",
            RequiredBindings = ["the acting character (the commander)", "the ability id 'rally-grunt'", "the ONE companion being rallied"],
            Preconditions = ["the commander is the current actor, active, holds the ability, and has a use of it left", "the target is another living, present, active character on the commander's own side", "the target is not already steadied"],
            TurnCost = "consumes the commander's turn",
            RngRequirement = "none when applied; the bonus is folded into that companion's next attack roll and the engine still rolls that",
            Visibility = "public: everyone present hears the order",
            SuccessBehaviour = "the companion's next attack gains a marked bonus to land. It is used up by that next attack whether it lands or misses, and lapses at the end of that companion's next turn if unused. One use of the ability is spent",
            FailureBehaviour = "refused, WITHOUT spending the use, if the ability is not held, has no uses left, the target is the commander, an enemy, or unavailable, or the target is already steadied",
            Exclusions = ["rallying oneself", "rallying an enemy", "stacking two rallies on the same character", "ordering a companion to take a specific action — a rally steadies their hand, it does not command their choice", "forcing anyone to attack"]
        },
        new RuleCard
        {
            RuleId = "ability.dirty-strike",
            ActionName = DungeonMasterTools.UseAbilityName,
            Description = "Ability 'dirty-strike' (Dirty Strike), a trick usable ONCE per encounter: the acting character makes one underhanded blow with the weapon in hand — a kick or foul behind the strike — that on landing leaves the enemy off balance so their next attack is markedly less likely to land. Recognise it in words like a dirty or low blow, kicking, tripping, fouling, striking below the belt, flinging grit, or hitting someone while distracting them. Only a character whose ability list includes 'dirty-strike' can do it.",
            RequiredBindings = ["the acting character", "the ability id 'dirty-strike'", "the opposing character struck"],
            Preconditions = ["the actor is the current actor, active, armed, holds the ability, and has a use of it left", "the target is a living, present, active character on an OPPOSING side"],
            TurnCost = "consumes the turn",
            RngRequirement = "exactly the ORDINARY attack rolls and no others: one roll to hit and, on a hit, one for a glancing blow. There is no separate roll for the trick",
            Visibility = "public: everyone present sees the foul blow",
            SuccessBehaviour = "ordinary weapon damage is applied, and on a hit the target is left off balance for their next attack. The use is spent whether the blow lands or misses",
            FailureBehaviour = "a miss deals nothing, applies nothing, and still spends the turn and the use. Refused, WITHOUT spending the use, if the ability is not held, has no uses left, the actor is unarmed, or the target is an ally, dead, surrendered or fled",
            Exclusions = ["knocking a target down, stunning or dazing them — the only effect is the off-balance penalty to their next attack", "disarming the target", "using it on an ally", "using it more than once in an encounter", "moving anyone: there is no distance in this world"]
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
