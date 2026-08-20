# v0.7 Session — Issues Found and Fixed

Problem → fix pairs actually resolved during this session, while building the **Terms & Tactics** slice and
then hammering on it through ten live runs across `qwen3.5:9b`, `claude-sonnet-4-6` and `gpt-5.4`. Anything
left unresolved, and anything that was a leftover from an earlier session's work, is deliberately excluded —
see the end of this file for what remains open.

Tests went **431 → 698** over the session.

---

## Negotiated surrender

### 1. A rule the release exists for was unreachable by its natural wording

**Problem:** The physical description of accepting terms *is* reaching out and taking the promised thing,
which reads exactly like a grab. In `20260819-232940Z-564aab54` a recipient was refused three turns running —
*"I take the Vial from Vark's hand and tell him he may live"*, then *"I reach forward and take the vial as
part of accepting his surrender terms"* — because nothing in `encounter.accept-surrender` described what an
acceptance looks like from the outside, and both grab rules said nothing about surrender.

**Fix:** the card now claims that phrasing explicitly ("taking it IS the acceptance"), and `inventory.steal`
and `container.take` carry exclusions pointing at it.
[`RuleCatalog.cs`](../src/ModelsAndMonsters/Rulebook/RuleCatalog.cs)

### 2. The stateless resolver cannot see state, and two rules depend on it

**Problem:** The Rulebook Resolver reads the intent and the cards and *no world state* — correct for rules
about the deed, and fatal for rules whose meaning is the situation. In `20260820-080524Z-6da9b8d3` three
offers were made and `accept_surrender` was chosen **zero** times. The recipient said *"I take Rowan's small
purse of gold coins, telling him he may live and leave"*; the resolver cited `inventory.steal` and
`encounter.offer-surrender`, so the DM was handed `[steal_item, offer_surrender, reject_action]` and could
not have accepted if it wanted to. The steal then counted as a hostile act that **destroyed the very offer it
was accepting**. Fix #1 had changed nothing, because the component choosing the card cannot know an offer
exists.

**Fix, in two layers:**
- **State-driven widening** — `WidenCandidateToolsForState` adds `accept_surrender` to whatever the resolver
  named whenever terms are pending for the acting character, with a note headed `STATE THE RULEBOOK COULD NOT
  SEE`. Same for `take_item` when the resolver says "theft" and a body lies in the room.
- **Deterministic redirect** before the engine — the named recipient reaching for what an offer promises *is*
  the acceptance, whatever tool the DM called. Narrow on purpose: only the promised items, only the named
  recipient; grabbing anything else is still a theft that kills the offer.
[`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs)

### 3. The fail-safe shut the door on acceptance

**Problem:** In `20260820-085927Z-e67cbda6` two acceptances carried an extra clause — *"I take Skrit's purse
and tell him he can live. Drop Elara's purse and get out"* and *"I take Vark's terms and tell him he can live
if he yields the salve and the captain's purse"* (the second correctly restating the standing terms). The
resolver read the condition and answered `supported: false`, so the DM got the rejection alone — and #2's
widening deliberately skipped reject-only sets. The character was told to wait for a yield that had already
happened.

**Fix:** the fail-safe is now opened for exactly one thing — `accept_surrender` is added even to a refused
intent when the state holds a pending offer naming the actor. The offer exists in state or it does not, the
DM still has to choose the tool, and the engine still validates the recipient. Everything else stays shut,
with a test on each half.

### 4. "Primary physical deed" excluded bargains

**Problem:** The compound-intent rule added earlier in the session told the resolver to find the *primary
physical deed*, which literally excludes taking somebody up on terms — the cause of #3.

**Fix:** generalised to the *primary act*, with an explicit paragraph that the rule "applies just as hard to
bargains": a demand, threat, condition or misremembered term riding along changes nothing about which rule
governs the intent.
[`rulebook.resolver.system.md`](../src/ModelsAndMonsters/Prompts/Templates/rulebook.resolver.system.md)

### 5. A demand for someone else's surrender was recorded as the speaker's own

**Problem:** `offer_surrender` binds the offerer to the acting character — the schema says so explicitly — so
a demand has no representable form and collapses into one shape: no items, `forfeit_weapon` true because a
weapon was mentioned somewhere. In `20260820-105131Z-6105a414` **all four** offers had `forfeit_weapon` true
and two promised nothing else. One was a character who was plainly winning: *"I hold out my hand to Vark and
offer him his life if he throws down his Notched Sabre"* — recorded as **that character** surrendering and
forfeiting his own longsword. Three of four characters surrendered that run.

**Fix:** terms must promise a carried item. Weapon-only offers are refused, which closes the shape the
misreading falls into. Engine, card and tool schema all agree, and `forfeit_weapon` now says it means the
offerer's **own** weapon.
[`GameEngine.cs`](../src/ModelsAndMonsters/Engine/GameEngine.cs)

### 6. …and that rule then trapped the character it should have protected

**Problem:** In `20260820-123326Z-50cd4c87` Vark spent the fight negotiating: stole a flask, gave his own
purse away "as part of the deal", handed the flask back "for your wound", had his salve stolen. When he
finally put real terms on the table he owned nothing but his sabre — and #5 refused him. He died the next
turn.

**Fix:** the rule is now **nothing held back**, not *at least one item*. Carrying items and promising none is
still the misread-demand shape; carrying nothing and promising your sword is giving everything you have, and
a stripped character can always yield. (A stripped character who promises *nothing* is still refused.)

### 7. An offer that came to nothing never said so

**Problem:** Only *pending* offers appeared in a character's state. When one lapsed or was rejected the state
silently reverted to "you have offered none", which contradicts nothing — so in `20260820-080524Z-6da9b8d3` a
character spent rounds 9, 10 and 11 bracing "while Vark and Skrit consider my offer", refused in round 4.

**Fix:** a character's own most recently settled offer stays on their state as `LAPSED` or `DEAD`, with "do
not wait for an answer" attached.
[`WorldStateFormatter.cs`](../src/ModelsAndMonsters/Prompts/WorldStateFormatter.cs)

### 8. "You cannot do that to yourself" for a muddled offer

**Problem:** A mis-bound offer hit `RecipientIsSelf` and rendered the generic self-targeting line, which
tells a character nothing about what went wrong with the terms they were trying to put.

**Fix:** its own line — "Terms have to be put to somebody on the other side; you cannot bargain with
yourself."
[`InWorldRefusal.cs`](../src/ModelsAndMonsters/Engine/InWorldRefusal.cs)

---

## Structure over heuristics

### 9. Growing regex heuristics replaced with explicit structure *(user-directed refactor)*

**Problem:** Two mechanisms were being maintained a synonym at a time, and each run found another phrasing
they lacked: `MachineryLanguage` (detecting rules-talk in text bound for a character) and speech detection
(quoted text near a growing list of speech verbs). The space of ways to describe a rule in English is
unbounded; the list could never converge. Worse, each catch triggered a *rephrasing model call*, and a
rewrite is free to be worse than what it replaced.

**Fix, structural rather than lexical:**
- **Speech is declared, not detected.** Every turn-taking tool gained an optional `utterances` field, so a
  character states what it says in the same call as its action — no extra model call, and quotation marks
  elsewhere are just punctuation. The old heuristic survives only for a reply carrying no tool call at all,
  narrowed by *shape* (first-person speaker, quote immediately after the verb) rather than vocabulary, and
  traced every time it fires.
- **Refusals originate from a code.** `InWorldRefusal` renders engine rejections deterministically from the
  rejection code and its bound names — no model call, so the common paths are in-world by construction and
  cannot leak, invent an obstacle, or narrate an event.
- **The detector is now a lint** over the one remaining prose path, matching only unmistakable machine
  vocabulary (tool names, stable ids, "rulebook", "engine", "hit chance"). Broad natural-language matches
  were removed.
- **Markdown is cleaned, not rewritten** — a presentation defect with a deterministic fix, split out of the
  semantic detector entirely.
[`ModelText.cs`](../src/ModelsAndMonsters/Agents/ModelText.cs),
[`MachineryLanguage.cs`](../src/ModelsAndMonsters/Agents/MachineryLanguage.cs),
[`InWorldRefusal.cs`](../src/ModelsAndMonsters/Engine/InWorldRefusal.cs)

### 10. Serialised speech spoken verbatim

**Problem:** In the first run of the new field, a model sent `utterances` as the literal text
`["Take it! You may go!"']` rather than an array, and the harness said exactly that aloud — brackets, quotes
and stray apostrophe — and wrote it into the knowledge ledger.

**Fix:** a bare string that looks like a JSON array is parsed, with the quoted spans recovered when the JSON
is malformed (which it usually is).
[`ToolArguments.cs`](../src/ModelsAndMonsters/Agents/ToolArguments.cs)

### 11. Commas in speech read as separators

**Problem:** A bare string supplied where a list was expected went through the comma-splitter built for id
lists, which would turn "Rowan, take the flank!" into a character saying the word "Rowan" and then an
orphaned command.

**Fix:** `GetStringList` takes a `splitLooseStrings` flag; prose arguments stay whole, id lists still split.

### 12. Speech silently halved

**Problem:** The once-per-turn speech limit counted each declared line as a separate speaking, so a reply
carrying two short sentences delivered the first and discarded the rest — **ten times in nine rounds** in
`20260820-123326Z-50cd4c87`.

**Fix:** declared lines are joined into one utterance. Two sentences are one breath; the limit itself is
unchanged, and a second separate `say` in the same turn is still refused.

---

## Context and truncation

### 13. "Output-token limit" was the wrong diagnosis

**Problem:** Seven replies in `20260820-091415Z-71a99152` finished with `Length` and every one was reported
as *"truncated at the output-token limit — consider raising MaxOutputTokens"*. The usage said otherwise:

```
InputTokenCount 8152   OutputTokenCount 40   TotalTokenCount 8192
```

Exactly `num_ctx`. The input had filled the window; the output budget was barely touched. The advised remedy
would have made it worse, since the window is shared.

**Fix:** `ContextTruncation.WasContextExhausted` tells the two apart, and the console gives the opposite
advice for the opposite cause.
[`ContextTruncation.cs`](../src/ModelsAndMonsters/AI/ContextTruncation.cs)

### 14. The retry loop made a full window fuller

**Problem:** The harness answers a truncated reply by appending a nudge and asking again. Against a full
window that enlarges the very request that had no room — one turn spent three consecutive retries this way,
each appending "your reply was cut off" to a request that could not fit.

**Fix:** on a context-full truncation the harness *reclaims* room first — sheds the turn's failed prose,
folds older turns into the summary, traces `ContextRoomReclaimed` — and says plainly when nothing can be shed.
Measured live afterwards: 6,708 → 4,091 estimated tokens, one truncation, recovered.

### 15. The character system prompt had grown 28%

**Problem:** `character.system.md` went from 9,656 chars at v0.6 to 12,326, and it is ~45% of every character
request, sent every turn to every character. `character.decide` was jammed against the 8,192 ceiling.

**Fix:** "Staying alive", "Acting" and "Speaking" compressed to 10,566 chars with no rule dropped. Verified
next run: zero `Length` finishes, `character.decide` peaking at 6,959.

### 16. A truncated fragment read as an attempt to speak

**Problem:** A reply cut off at *"barely standing against the wall with blood welling from the deep wound in
his torso."* was matched by the spoken-line extractor, so the console announced a prose speech nudge on a turn
where nobody had tried to speak.

**Fix:** truncated replies skip that extractor, for the same reason the intent parser already skipped them.

### 17. Truncation detection was provider-dependent

**Problem:** All 109 responses in `20260820-130041Z-585aa325` came back with `FinishReason` null — OpenAI
reports none through M.E.AI — leaving `WasTruncated` permanently false for that provider. A cut-off reply
would have gone to the intent parser as though complete, and none of #13–#16 would have run.

**Fix:** detection falls back to usage when no finish reason is given (output budget spent, or input+output
filling the window). A reported reason still wins where there is one.
[`ModelAgent.cs`](../src/ModelsAndMonsters/Agents/ModelAgent.cs)

---

## Items, knowledge and refusals

### 18. Identically named items were one item to a character

**Problem:** The scenario deliberately gives all four characters an identically named purse. A character
holding two had a self-state that read, in full:

```
- Small Purse of Gold Coins
- Small Purse of Gold Coins
```

Indistinguishable, unreferenceable and duly forgotten. Two identical purses on the floor were *literally*
unreferenceable — `ResolveContainedItem` refused any take as ambiguous however it was worded.

**Fix:** items carry an authored `Qualifier` ("Rowan's", "the runt's") and everything renders
`DisplayName`. Deliberately fixed for the life of the item, so it is a naming fact and not a second copy of
state. Lookup tries the qualified name first.
[`InventoryItem.cs`](../src/ModelsAndMonsters/Domain/InventoryItem.cs)

### 19. …and was only half-applied

**Problem:** Provenance showed one purse as `Small Purse of Gold Coins (Elara's)` when stolen and plain
`Small Purse of Gold Coins` as surrender tribute — the qualifier had only reached the paths changed by hand.
Two names for one purse is worse than no qualifier.

**Fix:** eight more engine call sites render `DisplayName`, plus a test that fails the build if `GameEngine`
ever reports a bare `item.Name` again.

### 20. Rendered decorations came back as item references

**Problem:** The DM passed `"Vial of Goblin Salve (restores 4 health)"` — the exact string the state block
renders — so nothing matched, the engine answered `ItemNotPossessed`, and a goblin was told the vial at his
own neck was not there. He spent a question asking where he kept it.

**Fix:** `MatchesReference` peels trailing parentheticals one at a time *after* exact matches fail, so the
annotation resolves while `Small Purse of Gold Coins (Rowan's)` still picks Rowan's purse.

### 21. Looting the fallen was refused three ways

**Problem:** Nothing can be stolen from a corpse — the belongings have already moved into the body — and the
resolver cannot know the target is dead. A goblin burned a whole turn on three rewordings of "loot the gold
from dead Rowan", one refusal conceding the belongings were "already within reach for anyone to take freely".

**Fix:** a theft naming a dead character is redirected to a take from their body, and the informational-basis
gate now exempts corpses alongside the ground — a body holds only what its owner carried openly, and everyone
watched them fall.

### 22. Refusals that narrated events, and a prompt that scripted them

**Problem:** A `reject_action` reason read *"Your blade strikes Rowan's shoulder, but you cannot force him to
drop the purse"* — a blow landing inside a call that changes nothing. The deeper cause was two-fold: the
tool's own summary still said "use this whenever the intent is not a direct weapon strike or an item use"
(true in v0.2, wrong for eleven of fourteen actions), and the *rephrase* prompt offered "the wet stone
underfoot" and "their own tired arms" as examples — which the model used as a script, in three separate
refusals across two runs, once telling a character the ground gave no purchase a turn before she braced on it.

**Fix:** `reject_action` is described as a last resort and its `reason` forbids narrating anything; both
refusal prompts name the honest alternative (nothing came of it) and forbid inventing an obstacle. A test
fails the build if a stock excuse reappears.

### 23. Knowledge facts were seeded with unqualified names

**Problem:** `KnowledgeSeeder` minted possession facts from `item.Name`, so a character's knowledge view
showed three separate lines reading "carrying the Small Purse of Gold Coins" with no way to tell them apart.

**Fix:** seeded from `DisplayName`.

---

## Scenario and observability

### 24. Gold obsession was self-inflicted

**Problem:** A run read as unusually money-fixated. v0.6 mentioned gold in exactly one persona (Skrit, a
cutpurse by design); v0.7 — needing every character to know it had something to trade — had added purse
language to all four personas **and the encounter summary**, which every character reads every turn.

**Fix:** the affordance kept, the currency cut. The summary now says each "carries something worth more to
them than pride"; Rowan and Elara say "whatever it carries"; Vark leads with the salve. Non-Skrit gold
mentions 8 → 3.

### 25. Friendly fire was invisible while it mattered

**Problem:** A guard-ally intent was bound as a strike on the guarded ally and resolved by the engine; she
survived on a missed roll. The relationship was already computed (`TargetIsAlly`) and the report has carried
an **Allied attacks** section since v0.2 — but nothing said a word at the time, so four runs went by unnoticed.

**Fix:** the console names it as it happens. The engine rule itself was deliberately **not** changed: a v0.2
test states that friendly fire is permitted and the world does not second-guess a confused actor.

### 26. The exit had never once been used

**Problem:** `open_exit` and `escape_encounter` fired **zero** times across eight runs and every model; in
three consecutive runs no character even mentioned the door, though it appears in every state block every
turn. v0.7's negotiated surrender had out-competed it — one turn instead of two, and you stay targetable in
between — and no persona carried a flight threshold where three now carried surrender thresholds.

**Fix:** Skrit's backstory gained an explicit written condition (he came down that stair; the moment the
captain is out of the fight he hauls the door open and goes through it). Two runs later it fired, exactly on
cue, in `20260820-130041Z-585aa325`.

---

## Left open

Recorded here so a later session does not mistake them for solved:

- **Stale item bindings in offers.** The DM promised an item the offerer had given away *the previous turn*,
  against a fresh snapshot showing he no longer had it. The engine refused correctly, but it cost a life.
  Whether terms may be silently narrowed to what the offerer actually holds is a design question.
- **`reject_action` producing factually wrong refusals.** It is the one remaining prose path, and it has
  refused a legitimate `inspect_object` and misidentified who was offering terms. Wrong facts, not just wrong
  register, so no detector helps.
- **Friendly fire.** Permitted by deliberate v0.2 design; now surfaced but not prevented.
- **Combat resolves slightly too slowly.** Sonnet's 12-round run ended on the cap with all four alive at
  1–5 health — ~2 damage per attacker per round against 8–14 health pools.
- **Partial terms.** Characters negotiate deals covering some of the people present; the engine models one
  offerer and one recipient.
- **A `fear` attribute.** The intended successor to hand-written flight thresholds — see the README and
  `v0_7_notes.md`.
