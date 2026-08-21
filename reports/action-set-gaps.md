# Where characters reach past the action set

A survey of every intent the world refused across the v0.8 session's live runs, and what that says about
which actions are missing, which absences are deliberate, and which refusals are simply correct.

**Why now.** Through v0.8 most refusals were *harness* faults — a stale window budget, a resolver that could
not see state, a knowledge gate misfiring. Those are fixed, and the last few runs show what is underneath:
adjudication acceptance on `IQ4_XS` went 77% → 94% once the corpse-loot and terms gaps were closed, and the
refusals that remain are almost entirely characters asking for things the world has no way to express.

**Method and its limits.** 135 refused intents from the `20260820`–`20260821` runs (`DmUnsupported`,
`DmImpossible`, and engine rejections), bucketed by keyword. Keyword bucketing is fine for *reading a trace*
and would be forbidden for deciding game meaning — the counts below are indicative, not exact, and the
"other" bucket was large enough that I sampled it by hand rather than trusting the split.

---

## v0.10 closure batch — disposition of every item below

The whole survey below is kept as it was written (v0.8/v0.9 runs, v0.9-era refusals). This section says what
actually happened to each item in the v0.10 closure batch, so the survey does not have to be re-read to find
out. See `docs/prompts/build_v0_10.md` for the batch's own scope statement and exclusions list.

**Closed:**

- **A1, weapon half only — bilateral weapon-only surrender.** Not the unilateral `yield` A1 originally asked
  for (deliberately excluded — see below); instead the existing `offer_surrender`/`accept_surrender`
  handshake now accepts a weapon-only concession unconditionally, not only from a character carrying nothing
  else. "I hold my sabre out by the flat and offer to lay it down if you spare me" now creates a real
  pending offer.
- **A2, taking a weapon off the floor.** Already implemented in the engine before this batch
  (`Weapon.AsForfeitedItem` places a forfeited weapon on the ground as an ordinary, takeable item); v0.10
  closed the remaining truthfulness and discoverability gaps — `InventoryItem.IsWeaponTrophy` so state
  formatting never lets a looted weapon read as equipped, direct engine tests for giving/dropping/stealing a
  trophy and for the attack rejection, and README documentation. The item as A2 originally described it
  ("Rowan's longsword" specifically) was never reachable because a character's weapon on death is scenery,
  not a forfeiture — only a *surrendered* weapon becomes a trophy. Death-loot of an equipped weapon remains
  future work (see below).
- **A3, environmental destruction half only.** `damage_environmental_object` (built in v0.9) is now proven
  reachable through the *real* rulebook-resolver → Dungeon Master → engine pipeline, not only unit-tested at
  the engine level (`CoverOrchestrationTests`), and a character facing cover an enemy occupies is now told
  directly that striking the object is an option. Container-forcing (the other half of A3) remains a
  deliberate exclusion — see below.
- **B1, compound intents.** The resolver's "primary act" rule already existed; v0.10 adds the actual
  character-facing contract ("One deed, not several", with allowed/not-allowed examples) so the rule is
  stated to the party burning the attempts, not only followed silently by the components downstream of them.
- **B3, demands.** Not built as a new action (deliberately excluded — see below). `combat.attack`'s adjacent
  intimidation card, the surrender cards, and the character prompt now state explicitly that a demand from
  anyone other than the one giving up their own fight is ordinary, non-binding speech — closing the
  ambiguity that made characters try to express one *through* an action.
- **B4, movement framing.** The character prompt now states directly that everything in the room is already
  within reach and that repositioning is not a deed, instead of only being enforced by silent refusal
  downstream.

**Deliberately excluded (unchanged from the original survey's read, reaffirmed by `docs/prompts/build_v0_10.md`):**

- **A1, the unilateral half.** No `yield` action was added. Giving up the fight still requires a concrete
  offer and an opponent's acceptance — see [README § Negotiated surrender](../README.md#negotiated-surrender-terms-not-declarations).
- **A3, container forcing.** An unlocked container still opens for free via `open_container`; there is still
  no way to "pry" or "smash" one open, and the engine still refuses a container as a `damage_environmental_object`
  target (`ObjectCannotBeDamaged`).
- **A4, throwing; A5, disarming.** Neither was touched. Both remain genuinely new mechanics, deferred as the
  original table suggested.
- **Demand *contracts*.** A demand still cannot carry enforceable terms of its own; the only binding,
  negotiated outcome in the game remains surrender.

**Future possibilities, not started:**

- Looting an equipped weapon directly off a **dead** (not surrendered) character's body — the surrender
  path now handles the surrendered case; death does not currently turn an equipped weapon into ground loot.
- A demand that carries structured terms of its own (a negotiation-contract action), rather than always
  resolving to plain, non-binding speech.
- Throwing, disarming, and any positional system — all remain out of scope for this project's stated design.

---

## A. Genuine gaps — a player would expect these to work

### A1. Yielding as a gesture, not a transaction (5+ occurrences)

The most-repeated dead end in the session, seen on **qwen and Haiku alike**.

> *"I reach down and unsheathe my sabre completely, holding it by the flat of the blade, and I lay it in the
> black water at my feet. Then I raise my empty hands, palms forward…"* → `DmImpossible`
>
> *"I hurl my sabre away from me into the black water… Then I raise both empty hands"* →
> `EquippedWeaponCannotBeTransferred`
>
> *"I reach for the vial of goblin salve at my neck and hold it out to Rowan, offering it as a trade for my
> life."* → `DmImpossible`

Surrender exists only as a **contract**: a concrete offer of goods to one named opponent, and their
acceptance. There is no way to simply *stop fighting*. A character who has already given away everything —
which happens, because they hand things over first — has nothing left to promise and therefore no way to
yield at all. In one Haiku run Vark spent two of his three attempts on this and fled on the third.

The equipped-weapon rule is right for combat and should stay. What is missing is a unilateral `yield` (or a
weapon-forfeiture gesture) that changes disposition without requiring goods to trade.

### A2. Taking a weapon off the floor (2 occurrences)

> *"I try to snatch Rowan's longsword from the floor before anyone else can get it."*
> *"I bend forward and snatch Rowan's Longsword from the floor."*

Weapons cannot be given, dropped, taken or equipped. Once a fallen character's blade is on the ground it is
scenery. Reasonable enough while there is no equipping system — but the models do not know that, and the
refusal reads as arbitrary to them.

### A3. Forcing a container (3 occurrences)

> *"I pry open the Faded Shrine Medicine Case with my hands to see what's inside."*
> *"I lunge past the stunned Skrit, slashing at the Faded Shrine Medicine Case to grab the healing draught."*
> *"I strike the Overturned Mill Workbench with my iron mace to batter it down."*

`damage_environmental_object` exists and covers cover objects; containers are not damageable. Since `open` is
already free and unopposed this is close to cosmetic — but it is the natural phrasing under pressure, and the
third example is a character trying to destroy *cover*, which arguably should work.

### A4. Throwing an object (2 occurrences)

> *"I grab the empty shrine case and hurl it at Elara, hoping to buy myself a moment."*
> *"I grab the Small Purse of Gold Coins lying on the floor and throw it to Elara, telling her to take it."*

Two different actions sharing a verb: throwing *at* someone (an improvised attack) and throwing *to* someone
(a give at distance). The rulebook already excludes the second explicitly — *"throwing an item to a specific
person to catch (that is not a supported action)"* — so this is a known absence, but it recurs.

### A5. Disarming (2 occurrences)

> *"…trying to knock the healing draught from her grasp before she can finish drinking it."*
> *"…swing my sabre at Rowan's braced defense to try to knock his wine flask loose."*

Consistently phrased as knocking something *loose* rather than taking it, which `steal_item` does not model.
Resolves as a plain attack and the interesting half of the intent is silently discarded.

---

## B. Deliberate exclusions the models keep walking into

These are working as designed. The cost is not correctness, it is **turns burned** — so the fix is guidance
(cards, personas, prompts), never new actions.

### B1. Compound intents — roughly 23 of the 82 unclassified refusals

> *"I use my Dirty Strike, kicking the knee behind my thrust at Rowan to make him stumble **and try to snatch
> his Small Purse**…"*
> *"I take the captain's purse from Vark's belt **and hand it to Rowan**, **then** I take the Iron Mace off
> the floor **and give it to him** as well."*

One turn, one deed. Models naturally chain two or three. The rulebook states the rule (*"an intent that both
strikes AND offers terms is the striking action"*) but characters are not told it in a way that changes their
behaviour.

### B2. Speech carried alongside a deed — ~38 of 82

The largest single group. Usually harmless (the deed resolves, the words are spoken), but it is how demands
and terms get mangled — see B3.

### B3. Demands and extortion (several)

> *"I take a small purse of gold coins from Skrit and tell him he can live if he gives them to me."*
> *"I hold out both of my purses to Vark and tell him again: give me the salve too, drop your weapons, and
> you and Skrit will walk out alive."*

The rulebook is explicit and correct that a demand *binds nobody* — only the person giving up their own fight
can make an enforceable offer. But characters have **no way to make a demand at all**, so they keep trying to
express one through an action. `intimidate_character` is the nearest thing and does not carry terms.

### B4. Movement and repositioning — 27 occurrences, the single largest bucket

> *"I step closer to Elara, lowering my guard for just a moment to see how badly she's holding up."*
> *"I shift my footing away from Elara, raising my sabre in case she lunges again."*

The world has **no distance model** — everything is within reach — so movement can never resolve. This is a
long-standing design choice (README: *positional framing fights the no-distance world*), and the state block
was already reworded once to describe objects as reachable. Twenty-seven attempts says the framing still
invites it.

---

## C. Correct refusals, no action needed

- **Watchful waiting** — *"I stand ready… and wait for the moment to act"*. `defend` and `end_turn` both
  cover this; the character just did not pick either.
- **Stale beliefs** — reaching for an item that moved earlier. The engine refuses correctly; the fix already
  landed is that the refusal now names *who* actually has it.
- **Ability misuse** — an ability already spent, or a self-only ability aimed at an ally.

---

## Suggested ordering

| | Change | Why first |
| --- | --- | --- |
| 1 | **Unilateral yield / weapon forfeiture** (A1) | Most frequent, seen on every model, and it blocks the non-lethal outcomes the project is built to produce. A character who wants to stop fighting currently cannot. |
| 2 | **Demands as a first-class act** (B3) | Would absorb a large share of the compound-intent refusals, since most compounds are *deed + terms*. Possibly an extension of `intimidate_character` carrying terms rather than a new action. |
| 3 | **Take a weapon from the floor / a body** (A2) | Small, and completes the looting work already done in v0.8. |
| 4 | **Movement framing** (B4) | Not an action — a prompt and state-block problem. Cheapest of the four and the largest single bucket. |
| 5 | Throwing, disarming, forcing containers (A3–A5) | Genuinely new mechanics; defer unless a scenario needs them. |

**One caution.** Every action added is another rule card, and card count is what pushes the whole-rulebook
resolver request toward the context window — the ceiling that `RulebookRequestBudget` now guards. Two or
three more cards is comfortable; a wishlist of ten is not, and would force the card-selection work in
`reports/rulebook-efficiency.md` to stop being experimental.
