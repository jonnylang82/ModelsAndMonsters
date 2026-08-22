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
            Summary = "Striking another character with the weapon in hand — a blow that actually makes contact.",
            RelatedRuleIds = ["combat.defend", "ability.dirty-strike", "combat.morale", "environment.cover"],
            // A blow at a person, told apart from a deliberate blow at an OBJECT, and from posturing with no
            // blow — a threat is speech (intimidation), not an attack. Both are live-proven confusions.
            DistinguishedFrom = [DungeonMasterTools.DamageEnvironmentalObjectName, DungeonMasterTools.IntimidateCharacterName],
            ActionName = DungeonMasterTools.AttackCharacterName,
            Description = "One character striking another with the weapon they are carrying — a direct melee blow that MAKES CONTACT. What makes it an attack is the BLOW, never the movement: a swing, slash, stab, cut, chop or thrust that lands. Rushing, lunging or stepping toward somebody counts only when a blow is what arrives; rushing to them to hand them something, to shield them or to speak to them is the rule for that deed, not this one. A character moving, readying, threatening or posturing with no blow described is NOT attacking. This is still the right action when the target is sheltering behind cover — cover changes what the one roll can mean, never which action to call.",
            RequiredBindings = ["the acting character (attacker)", "the target character", "the attacker's weapon"],
            Preconditions = ["attacker is active and armed", "a blow is actually described making contact with the target — not merely a threat, an advance, or a readied weapon", "target is another active character (not dead, surrendered or escaped, and not the attacker)"],
            TurnCost = "consumes the turn",
            RngRequirement = "the engine rolls to hit and, on a hit, ONE quality roll deciding glancing (half damage), solid or critical (double) — never a roll per kind. The resolver never decides the outcome",
            Visibility = "public: the whole room sees the blow and its result",
            SuccessBehaviour = "the engine applies damage and may record an injury or a death; only the engine decides whether it lands. A critical blow also moves morale",
            FailureBehaviour = "a miss changes nothing but still spends the turn",
            Exclusions = ["throwing a weapon", "shoving, grappling or knocking down", "raising a guard, bracing, standing one's ground or readying to parry — that is the defend rule, not an attack", "frightening a foe into yielding", "a threatening step, advance, stance or menacing gesture that lands no blow — posturing is not an attack", "brandishing, gripping, raising, levelling or readying a weapon without actually striking — a weapon held up to make somebody afraid, with no blow, is the intimidation rule", "feinting, intimidating or 'making ready' — with no blow described, this is the intimidation rule, the defend rule, or nothing, never attack_character", "shielding a companion from a blow — that is the guard-ally technique", "rushing or lunging to a companion to press something into their hand — that is the giving rule, however urgent the movement"]
        },
        new RuleCard
        {
            RuleId = "inventory.use",
            Summary = "Using an item from your own inventory on yourself; the item is consumed.",
            RelatedRuleIds = [],
            // A drunk salve is an item; a prayer that closes a wound is an ability. Same hoped-for outcome,
            // different rule — the live-proven use-item-vs-heal-ability confusion.
            DistinguishedFrom = [DungeonMasterTools.UseAbilityName],
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
            Summary = "Handing one of your own ordinary items to another character present in the room.",
            RelatedRuleIds = ["inventory.drop", "inventory.steal", "encounter.offer-surrender"],
            ActionName = DungeonMasterTools.GiveItemName,
            Description = "The acting character handing one of their own ordinary inventory items to another character present in the room, however urgently — rushing or lunging to them to press it into their hand is this rule, not an attack. The recipient may be an ally or an enemy; consent is not modelled. This is an ORDINARY handover with nothing asked in return for it — not an item held out to buy the giver's own life or safety, which is encounter.offer-surrender instead, however alike the physical gesture reads.",
            RequiredBindings = ["the acting character (giver)", "the recipient character", "the item from the giver's inventory"],
            Preconditions = ["giver is the current actor, active and present", "recipient is alive and present in the room", "the item is an ordinary inventory item the giver owns, not an equipped weapon"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees the item change hands and learns the recipient now carries it",
            SuccessBehaviour = "the item moves atomically from giver to recipient",
            FailureBehaviour = "refused if the giver does not own the item, it is an equipped weapon, or the recipient is not present",
            Exclusions = ["giving an equipped weapon", "forcing the recipient to accept or use it", "an item offered specifically in exchange for the giver's own life, freedom, or being spared — that is a surrender term (encounter.offer-surrender), not an ordinary gift"]
        },
        new RuleCard
        {
            RuleId = "inventory.drop",
            Summary = "Dropping one of your own ordinary items on the floor for anyone to pick up.",
            RelatedRuleIds = ["inventory.give", "container.take"],
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
            // The dead are excluded here and claimed by container.take, because a live run had a goblin
            // spend its whole turn on three rewordings of "loot the gold from dead Rowan".
            Summary = "Trying to snatch an ordinary item from another active character; always noticed, and may fail.",
            RelatedRuleIds = ["inventory.give", "container.take"],
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
            Summary = "Opening a closed container in the room — the lid only, taking nothing out.",
            RelatedRuleIds = ["container.take", "object.inspect"],
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
            Summary = "Taking one item out of an already-open container, or off the floor or a body, into your hands.",
            RelatedRuleIds = ["container.open", "object.inspect", "inventory.steal"],
            // Lifting from the floor or a body, told apart from a snatch off a LIVING person (steal), and
            // from taking the tribute a pending offer promised — grabbing that IS the acceptance, not a take.
            DistinguishedFrom = [DungeonMasterTools.StealItemName, DungeonMasterTools.AcceptSurrenderName],
            ActionName = DungeonMasterTools.TakeItemName,
            // Container-CONTEXTUAL phrases, not bare verbs: "seize"/"snatch"/"grab" are shared with stealing
            // from a person (inventory.steal), so keying on them here would wrongly outrank a theft. Instead
            // this matches phrasings that name the container context ("from the open", "out of the case", "off
            // the floor"); for the many other ways a take is worded, the container family (see RuleRetriever)
            // co-retrieves this card whenever a container noun ("case", "chest", "the floor", …) appears at all.
            Description = "A character taking one item into their own inventory from anywhere it lies loose and unheld: an already-open container, the floor, or A FALLEN CHARACTER'S BODY. All three are containers here. Picking up what someone dropped, gathering what spilled from a body, and lifting something out of an open case are the same action — the body of a dead character holds what they carried and is taken from exactly like a chest standing open.",
            RequiredBindings = ["the acting character", "what it is taken from: the open container, the floor, or the fallen character's body", "the item to take"],
            Preconditions = ["actor is active", "the container is open (the floor and a body always are)", "the item is in the container and the actor has a legitimate basis to know it is there"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: the room sees the item carried out",
            SuccessBehaviour = "the item moves atomically from the container to the actor's inventory",
            FailureBehaviour = "refused if the container is closed or the item is not in it",
            Exclusions = ["taking from a closed container without opening it first", "taking an item the character has no way of knowing is there", "taking an item out of a LIVING character's hand or off their belt — that is a theft, or, when the item is what a pending offer to this character promised, an acceptance of that offer. A DEAD character is not stolen from: their belongings lie with their body and are taken from it under this rule"]
        },
        new RuleCard
        {
            RuleId = "object.inspect",
            Summary = "Examining an object closely to learn what a glance does not give; what you find is yours alone.",
            RelatedRuleIds = ["container.open"],
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
            Summary = "Hauling a shut way out open. It moves nobody: leaving through it is a separate act.",
            RelatedRuleIds = ["encounter.escape"],
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
            Summary = "Walking through an already-open way out and leaving the encounter for good.",
            RelatedRuleIds = ["encounter.open-exit", "combat.morale"],
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
            Summary = "Offering to give up YOUR OWN fight to one named opponent, on concrete terms you promise to hand over.",
            RelatedRuleIds = ["encounter.accept-surrender", "combat.morale", "inventory.give"],
            // Yielding on concrete terms, told apart from an ordinary give or drop — a weapon laid down or an
            // item held out to buy one's own life is a surrender term, not a gift and not a discard.
            DistinguishedFrom = [DungeonMasterTools.GiveItemName, DungeonMasterTools.DropItemName],
            ActionName = DungeonMasterTools.OfferSurrenderName,
            Description = "THE ACTOR GIVING UP THEIR OWN FIGHT, to ONE named opponent, on concrete terms they promise to hand over: one or more items they carry, the weapon in their hand, or both — either concession alone is enough, whatever else the offerer does or does not carry. Yielding is never unilateral and never free. Recognise it in yielding, surrendering, giving in, begging to be spared, buying their life, offering payment or a weapon for mercy — PROVIDED something concrete is promised. A weapon held out by the flat, laid down, or offered to buy mercy (\"I hold my sabre out by the flat and offer to lay it down if you spare me\") is this rule with forfeit_weapon alone, not a give_item and not a bare plea. So is an item offered specifically to buy one's own life (\"I offer Rowan the goblin salve in exchange for my life\") — an item traded for being spared is a surrender term, never an ordinary gift. The plea itself is ordinary speech; this action is only the enforceable terms. WHOSE fight is decisive: 'take my purse and let me live' is this rule, while 'hand over your purse and I will spare you' is a DEMAND, binds nobody, and is speech alone — recording it here would make the speaker the one who gave up.",
            RequiredBindings = ["the acting character (the one offering to give up)", "the ONE opposing character the terms are offered to", "the ordinary inventory items promised (may be none)", "whether the weapon in hand is promised"],
            Preconditions = ["offerer is the current actor and active", "the recipient is a living, present, active character on an OPPOSING side", "every promised item is an ordinary item the offerer owns right now", "the offer promises at least one real concession — one or more carried items, the weapon in hand, or both; either alone is a complete offer", "the offerer has no other offer already awaiting an answer"],
            TurnCost = "consumes the offerer's WHOLE turn and cannot be combined with anything else. An intent that both strikes (or guards) AND offers terms is the striking action, with the terms as mere speech",
            RngRequirement = "none",
            Visibility = "public: everyone present hears the terms",
            SuccessBehaviour = "a pending offer is recorded. NOTHING moves, nobody is disarmed, no disposition changes and the offerer stays an active, targetable combatant. Only the named recipient can accept it, on their own turn",
            FailureBehaviour = "refused if the offer promises nothing concrete, names an item the offerer does not own, names an ally rather than an opponent, or duplicates an offer already awaiting an answer",
            Exclusions = ["a bare plea with nothing promised at all — no item and no weapon forfeited — is speech, not an offer", "yielding without naming an opponent", "promising anything the offerer does not carry, including another character's belongings", "making somebody else surrender, or DEMANDING that they yield", "promises about the future — service or ransom beyond what is handed over on acceptance", "a secret offer: terms are always public"]
        },
        new RuleCard
        {
            RuleId = "encounter.accept-surrender",
            Summary = "Taking up a pending offer of surrender that was made to you, sparing the one who offered.",
            RelatedRuleIds = ["encounter.offer-surrender"],
            // Reaching out and taking the promised thing reads exactly like a theft; ruling out a snatch is
            // how you know the grab was an acceptance. The v0.7 defect that cost a release.
            DistinguishedFrom = [DungeonMasterTools.StealItemName],
            ActionName = DungeonMasterTools.AcceptSurrenderName,
            // The physical description of an acceptance IS reaching out and taking the promised thing, which
            // reads exactly like taking-from-a-hand — an unsupported action. A live run refused a recipient
            // three turns running for saying "I take the vial from Vark's hand and tell him he may live" and
            // even "I reach forward and take the vial as part of accepting his surrender terms". So the card
            // has to claim that phrasing explicitly, or the natural words for accepting never reach this rule.
            Description = "The acting character taking up a pending offer of surrender made TO THEM, sparing the offerer on the promised terms. Recognise it in accepting, agreeing, taking the deal, taking the coin and letting them live, sparing them. Recognise it ESPECIALLY when the character describes physically taking the promised thing from the one offering it — reaching for it, taking it from their hand or belt — while sparing them or saying they accept: that is not a theft, because the thing named is what the offer promised, and taking it IS the acceptance. Only the named recipient can do this. An acceptance stays one however much is piled on top — a threat, a further demand, a condition, a slightly wrong restatement of the terms. The engine settles exactly what the standing offer promised and the extra does not happen, so never call such an intent unsupported.",
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
            Summary = "Spending the whole turn braced behind your guard instead of striking — NOT behind a named piece of cover.",
            RelatedRuleIds = ["combat.attack", "environment.take-cover"],
            // One's own guard, told apart from sheltering behind a named object — naming a real object to get
            // behind is take-cover, whatever defensive word accompanies it. The sharpest boundary in the book.
            DistinguishedFrom = [DungeonMasterTools.TakeCoverName],
            ActionName = DungeonMasterTools.DefendName,
            Description = "The acting character spending the whole turn braced behind their OWN guard instead of striking, so the next blow that lands on them is softened. Every active character can do this, as often as they like. Recognise it in words like bracing, standing one's ground, holding or keeping the guard up, readying to parry, turning aside a blow, covering oneself, or setting one's feet — any defensive posture with no blow described AND naming no real object in the room. Defensive posturing with no object named is THIS, never an attack.",
            RequiredBindings = ["the acting character"],
            Preconditions = ["actor is the current actor and active", "the actor is not already braced"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees them set themselves",
            SuccessBehaviour = "the actor becomes braced: the next blow that lands on them deals one less damage, applied after armour and after any glancing reduction and never below zero. A miss leaves the guard up; it falls away at the start of the actor's next turn",
            FailureBehaviour = "refused only if they already have their guard up",
            Exclusions = ["striking as well as bracing", "protecting somebody else (that is the guard-ally technique)", "dodging out of the way — there is no distance or movement in this world", "blocking a doorway or holding anyone back", "bracing, guarding, defending or covering oneself BEHIND a real environmental object named in the room (a workbench, a crate) — however the character phrases the defensive posture, naming a real object to get behind is environment.take-cover, not this card, because it is the object doing the protecting rather than the character's own stance"]
        },
        new RuleCard
        {
            RuleId = "combat.intimidate",
            Summary = "AIMED AT AN ENEMY: breaking their nerve with an open threat spoken aloud, striking no blow. Once per enemy.",
            RelatedRuleIds = ["combat.morale", "encounter.offer-surrender"],
            ActionName = DungeonMasterTools.IntimidateCharacterName,
            Description = "AIMED AT AN ENEMY, NEVER AT A COMPANION. The acting character breaking one OPPOSING character's nerve with an open threat spoken aloud, striking no blow. Two shapes especially. First, a weapon used to menace rather than strike: levelling, raising or pointing a blade at somebody while telling them what is coming — nothing is struck, and a weapon in the description does not make it an attack. Second, naming a consequence aloud: that they are next, that they will die here, that they should get out while they can. It stays a threat even when it invites them to yield or flee, because saying so does not MAKE them do it. An intent that lands a blow AND threatens is the attack rule, with the words as mere speech. Words meant to hearten somebody on the speaker's OWN side are the steadying rule.",
            RequiredBindings = ["the acting character", "the ONE opposing character threatened, who must be the person the words were spoken to"],
            Preconditions = ["the actor is active", "the target is another active opposing character", "the character actually spoke the threat aloud on this turn", "no blow is described landing on anybody", "the actor has not already tried to frighten this same target in this encounter"],
            TurnCost = "consumes the whole turn: threatening is the entire action and cannot be combined with a blow, a guard, an item or an ability",
            RngRequirement = "exactly one seeded draw against a base chance adjusted ONLY by the state of the fight, never by the wording of the threat. The resolver never decides the outcome",
            Visibility = "public: everyone present hears the threat and sees whether it told",
            SuccessBehaviour = "the target becomes more afraid, and NOTHING else happens at all",
            FailureBehaviour = "nothing whatsoever changes, and the attempt is spent — the same enemy cannot be threatened twice",
            Exclusions = ["threatening an ally, or somebody dead, surrendered or fled", "a threat with no words actually spoken", "making the target yield, flee, drop anything, hand anything over, be disarmed or lose a turn — a threat only frightens, and what the frightened do about it is their own choice", "demanding somebody surrender — that is speech; only the one giving up can offer terms", "threatening the same enemy twice"]
        },
        new RuleCard
        {
            RuleId = "combat.steady-ally",
            Summary = "AIMED AT A COMPANION ON YOUR OWN SIDE: spending the whole turn steadying them with words meant for them.",
            RelatedRuleIds = ["combat.morale", "ability.rally-grunt"],
            ActionName = DungeonMasterTools.SteadyAllyName,
            Description = "AIMED AT A COMPANION ON THE SPEAKER'S OWN SIDE, NEVER AT AN ENEMY. The acting character spending their whole turn steadying ONE ally whose nerve has gone: speaking to them to calm, hearten, rally, encourage, reassure or brace them — telling them to hold, that help is beside them, that they can still win. Kindness and command both count, so long as they are meant to put heart back into somebody on the same side. Words meant to frighten somebody on the OTHER side are the intimidation rule and never this one.",
            RequiredBindings = ["the acting character", "the ONE allied character being steadied, who must be the person the words were spoken to and never the actor themselves"],
            Preconditions = ["the actor is active", "the target is a different, active, present character on the actor's own side", "the character actually spoke to them aloud on this turn"],
            TurnCost = "consumes the whole turn and cannot be combined with a blow, movement, an item or an ability",
            RngRequirement = "none: no dice are rolled at all",
            Visibility = "public: everyone present hears the words and can see the companion take heart",
            SuccessBehaviour = "the companion becomes less afraid, and nothing else changes — no wound closes, no odds shift, and they still choose their own actions",
            FailureBehaviour = "refused if aimed at the actor themselves, at an enemy, or at somebody dead, surrendered or fled. A companion who was not afraid gains nothing, and the turn is spent anyway",
            Exclusions = ["steadying yourself, an enemy, or somebody not present", "healing a wound", "improving anyone's odds of hitting or defending", "making a companion do anything — reassurance never commands"]
        },
        new RuleCard
        {
            RuleId = "ability.guard-ally",
            Summary = "The technique of standing over one companion so the next blow aimed at them lands on you.",
            RelatedRuleIds = ["combat.attack", "combat.defend"],
            // Stepping in front of a companion to take the blow meant for them reads like striking or being
            // struck; a live run routed exactly that to attack_character. Declared so routing to attack also
            // surfaces this card.
            DistinguishedFrom = [DungeonMasterTools.AttackCharacterName],
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
            Summary = "The prayer that closes a wound on yourself or one companion, once in an encounter.",
            RelatedRuleIds = [],
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
            Summary = "The barked order that sharpens one companion's next blow and steadies their nerve, once in an encounter.",
            RelatedRuleIds = ["combat.steady-ally", "combat.morale"],
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
            Summary = "The foul blow that strikes with the weapon in hand and leaves the foe off balance, once in an encounter.",
            RelatedRuleIds = ["combat.attack"],
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
            RuleId = "environment.take-cover",
            Summary = "Moving behind a real, named object in the room and taking shelter there — even when described as bracing or defending.",
            RelatedRuleIds = ["environment.cover", "environment.leave-cover", "combat.defend"],
            ActionName = DungeonMasterTools.TakeCoverName,
            Description = "The acting character moving behind one environmental object with the cover capability and taking shelter there — ducking behind something solid rather than standing exposed. Recognise it in words like ducking, getting behind, taking cover, sheltering behind, or putting something solid between themselves and the fight — AND EQUALLY when the character describes it as bracing, guarding, defending or covering themselves WHILE NAMING the real object they are behind (\"I brace behind the workbench\", \"I defend behind the crate\"). Naming a real object the character gets behind is what makes it this action rather than combat.defend, whatever defensive word accompanies it — the object is what is doing the protecting.",
            RequiredBindings = ["the acting character", "the cover object"],
            Preconditions = ["actor is active", "the object provides cover and is not destroyed", "the actor does not already occupy it", "it has spare capacity"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees them move behind it",
            SuccessBehaviour = "an authoritative occupancy relationship is created and a public in-cover status applied; it persists until the character leaves, is exposed by another action, the cover is destroyed, or they leave active play",
            FailureBehaviour = "refused if the character already occupies it, it is full, or it is destroyed",
            Exclusions = ["taking cover behind room scenery not listed as a cover object", "two characters sharing cover beyond its stated capacity", "attacking while also taking cover in the same act", "bracing or defending with no real object named — that is combat.defend"]
        },
        new RuleCard
        {
            RuleId = "environment.leave-cover",
            Summary = "Deliberately stepping out from cover, with nothing else attempted.",
            RelatedRuleIds = ["environment.cover", "environment.take-cover"],
            // A bare step-out (the whole turn), told apart from an ordinary exposing action — an accepted
            // attack, reach or opening vacates cover on its own, with no separate leave.
            DistinguishedFrom = [DungeonMasterTools.AttackCharacterName],
            ActionName = DungeonMasterTools.LeaveCoverName,
            Description = "The acting character deliberately stepping out from the cover they occupy, as the whole of their turn. Use only when leaving is the entire attempt — an accepted attack, reach or opening already exposes the character as a side effect of resolving it, with no separate leave needed.",
            RequiredBindings = ["the acting character"],
            Preconditions = ["actor is active", "the actor currently occupies cover"],
            TurnCost = "consumes the turn",
            RngRequirement = "none",
            Visibility = "public: everyone present sees them step out",
            SuccessBehaviour = "the occupancy relationship and the in-cover status both end",
            FailureBehaviour = "refused if the character occupies no cover",
            Exclusions = ["combining this with a blow, a reach, or opening something in the same act — an exposing action vacates cover on its own"]
        },
        new RuleCard
        {
            RuleId = "environment.damage-object",
            Summary = "Deliberately striking an environmental object itself, with the weapon in hand, rather than a person.",
            RelatedRuleIds = ["environment.cover", "combat.attack"],
            ActionName = DungeonMasterTools.DamageEnvironmentalObjectName,
            Description = "The acting character deliberately striking one present, non-destroyed environmental object with the weapon in hand — battering cover down rather than striking whoever, if anyone, shelters behind it. Not an ordinary attack against a covered character, which is combat.attack; cover there is applied automatically without this action.",
            RequiredBindings = ["the acting character", "the weapon in hand", "the object struck"],
            Preconditions = ["actor is active and armed", "the object is present and not already destroyed", "the object struck is not the cover the actor themself currently occupies"],
            TurnCost = "consumes the turn",
            RngRequirement = "none: a stationary object does not dodge, so damage is deterministic",
            Visibility = "public: everyone present sees the blow land on the object",
            SuccessBehaviour = "durability is reduced deterministically; reaching zero destroys the object and exposes anyone who was sheltering there. Breaks the actor's own cover first, if they occupy any",
            FailureBehaviour = "refused if the object is already destroyed, unknown, has no cover capability, or is the actor's own occupied cover",
            Exclusions = ["striking a covered character (that is combat.attack)", "striking room scenery with no cover capability"]
        },
        new RuleCard
        {
            RuleId = "environment.cover",
            Summary = "How environmental cover works: capacity, what happens when an attack meets it, and how it interacts with Defend, critical hits, surrender, death and escape.",
            RelatedRuleIds = ["environment.take-cover", "environment.leave-cover", "environment.damage-object", "combat.attack", "combat.defend"],
            ActionName = RuleCard.ReferenceAction,
            Description = "How cover works, as background to the three cover actions and to combat.attack against a covered character. Cover has capacity (the seeded object holds one character), durability, and a hit-chance modifier applied only while it is intact or damaged, never once destroyed.",
            RequiredBindings = ["none: this is not an action and cannot be chosen"],
            Preconditions = ["none"],
            TurnCost = "none: cover's own state changes only as a consequence of the three cover actions or of an attack against a covered character",
            RngRequirement = "none of its own; an attack against a covered character still makes only the ordinary single hit-check roll, which the engine alone interprets as a direct hit, a cover interception, or an ordinary miss",
            Visibility = "fully public: the object, its condition, and its occupant are visible to everyone, unlike a container's contents",
            SuccessBehaviour = "a cover interception costs the object exactly one point of durability and no character damage at all; reaching zero destroys it and exposes its occupant. Cover changes only the hit check — never armour, damage, the glancing/solid/critical bands, or fear directly. Defend still reduces damage after a hit that reaches a covered character; a critical hit can occur only after the attack actually lands; surrender, death and escape always release occupancy; accepting someone else's surrender does not, by itself, release the accepter's own cover",
            FailureBehaviour = "n/a",
            Exclusions = ["a hit-chance bonus or penalty from cover applying to anything but the one attacker being resolved against the one covered target", "cover reducing damage directly, as Defend does", "cover protecting against anything but a weapon attack", "a character remaining marked in-cover after death, surrender, escape, or the cover's own destruction"]
        },
        new RuleCard
        {
            RuleId = "combat.morale",
            Summary = "How nerve works: the 0-5 fear scale, what moves it, and that being Scared compels nobody.",
            RelatedRuleIds = ["combat.intimidate", "combat.steady-ally", "encounter.escape", "encounter.offer-surrender"],
            ActionName = RuleCard.ReferenceAction,
            Description = "How nerve works, as background to every other rule. Each character carries a private measure of fear from 0 to 5. At 3 or more they are visibly Scared; below 3 they are not.",
            RequiredBindings = ["none: this is not an action and cannot be chosen"],
            Preconditions = ["none"],
            TurnCost = "none: nerve changes as a consequence of other actions, never as an action of its own",
            RngRequirement = "none of its own; only an open threat rolls, and that roll belongs to the threat",
            Visibility = "the exact measure is the character's own. Crossing into or out of being Scared is public — it shows on a face",
            SuccessBehaviour = "rises by one: surviving a critical hit; one blow taking a quarter of maximum health; first becoming outnumbered; an enemy's threat telling. Falls by one: landing a critical hit; being steadied by a companion; being rallied. Being Scared inclines a character strongly toward survival, and no more than that",
            FailureBehaviour = "n/a",
            Exclusions = ["forcing a scared character to surrender, flee, defend or skip a turn — fear chooses for nobody, and the ordinary rules for yielding and leaving apply in full", "any penalty to hitting or defending", "a character stating their own fear as a number", "changing fear because somebody merely SAID something frightening or reassuring, outside the threat and steadying rules"]
        },
        new RuleCard
        {
            RuleId = RejectRuleId,
            Summary = "The generic refusal, for an intent that is no supported action at all.",
            RelatedRuleIds = [],
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
