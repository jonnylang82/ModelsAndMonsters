# v0.9 issues — found and fixed

Problems discovered **and** resolved while building and live-testing v0.9. Anything still open — including the
gap survey in `reports/action-set-gaps.md` — and anything that was already understood from an earlier release
is deliberately not here.

Each entry is what went wrong, why, and what changed. Where a live run is named it is under
`src/ModelsAndMonsters.Web/runs/`.

---

## Rulebook and cards

### 1. "Brace behind the workbench" routed to the wrong card

**Symptom.** Live probing during cover development showed intents like *"I brace behind the workbench"* or
*"I defend, keeping the crate between us"* being resolved as `combat.defend` — the character's own guard —
instead of `environment.take-cover`.

**Cause.** Both cards describe a defensive posture, and `combat.defend`'s vocabulary list (bracing, standing
one's ground, holding the guard up, covering oneself) is generic enough to match a sentence that also happens
to name a real object. Nothing on either card said which one wins when both kinds of language appear together.

**Fix.** `combat.defend`'s exclusions now name the cover case explicitly: *"bracing, guarding, defending or
covering oneself BEHIND a real environmental object named in the room ... however the character phrases the
defensive posture, naming a real object to get behind is environment.take-cover, not this card, because it is
the object doing the protecting rather than the character's own stance."* `environment.take-cover`'s
description states the same rule from the other side: naming the object is what decides it, "whatever
defensive word accompanies it." Re-probed clean after the edit.

### 2. Looting the dead was invisible to the card the resolver actually reads

**Symptom.** Across two live runs, an intent like *"I pick up the flask that spilled from Rowan's body"* was
answered unsupported — eight refused attempts total, five of them inside a single round from two different
characters.

**Cause.** `container.take`'s one-line summary already mentioned "or off the floor or a body," but the
**description** — the field the resolver actually reads to decide what an intent is — only ever described
containers and the floor. A body was never named where it needed to be.

**Fix.** `container.take`'s description, required bindings, and preconditions were rewritten to explicitly
claim a fallen character's body as something items can be taken from, "exactly like a chest standing open" —
with an explicit note that a body does not need to be opened first, unlike a container.

### 3. The two grab cards agreed on the same corpse

**Symptom.** Once #2 named bodies, `steal_item` — the theft card — could also plausibly claim the same intent,
since both describe taking something that is not yours. A live run had a goblin burn a whole turn on three
rewordings of *"loot the gold from dead Rowan,"* every one routed to theft, which the engine correctly refuses
because a dead character cannot be stolen from.

**Cause.** Nothing said which card owns a corpse. Two cards that can both plausibly claim the same intent leave
the resolver to pick whichever it happens to see first.

**Fix.** `container.take`'s exclusions now explicitly claim anything taken from someone **DEAD**; `steal_item`
is narrowed in the same breath to the **LIVING**. The two no longer overlap on a body.

---

## Engine and refusals

### 4. A theft refusal named the thief, not the person who actually had it

**Symptom.** `20260821-110442Z-1667ed2a`: Rowan gave his purse to Elara in round 3, in full view, and every
character learned it. In round 7, Skrit tried three times in one turn to steal that same purse *from Rowan*:

> *"I lunge forward and try to snatch the Small Purse of Gold Coins from Rowan's belt."* (repeated twice more,
> unchanged)

The engine refused correctly each time — Rowan wasn't carrying it — but the line delivered to Skrit was *"You
are not carrying the Small Purse of Gold Coins (Rowan's)."* True, and about the wrong person entirely.
`20260821-095322Z-45a78d65` shows the same pattern with a different purse. Nothing in either refusal
contradicted the belief that was actually wrong (that Rowan/Vark still had the purse), so both characters
repeated the identical attempt until the attempt limit ended their turn.

**Cause.** Theft refusals were rendered in the second person — "you are not carrying" — which is exactly right
for a character reaching into their *own* pockets, but a theft is about someone else's pockets, and the
refusal needs to say whose.

**Fix.** `InWorldRefusal.PossessorFor` decides, per action and rejection reason, whether a refusal should name
a third party: a theft names the true holder; use/drop/give — all about the actor's own belongings — keep the
second person, since naming someone there would be wrong. The possessor binding only ever applies to the
rejection reason it belongs to (`ItemNotPossessed`), never to unrelated refusals like a fled target or an
equipped weapon that cannot be transferred.

### 5. Accepting a surrender nobody had actually offered

**Symptom.** `20260821-101224Z-6247a3c7`, round 12: Elara had a real, standing offer on the table and tried to
accept it four times, wording it differently each attempt, plus one clarifying question confirming the offer
was genuine — and still hit the round limit with the surrender unresolved. Every attempt failed the same way:
`accept_surrender rejected: UnknownOffer — There is no offer of surrender called 'offer-1' on the table.`

**Cause.** The rulebook resolver reads words, not state — an intent that says "I accept his offer" is
correctly classified as an acceptance whether or not real terms are on the table. With `accept_surrender`
handed to it as a candidate tool, the Dungeon Master had to invent an offer id to fill the required binding,
and it invented the same one every time.

**Fix.** `TurnCoordinator.WidenCandidateToolsForState` — the existing mechanism for adding state-dependent
tools the resolver can't see — now also **removes** `accept_surrender` when the target character has no real
pending offer in state, and appends an explanatory note for the DM: *"STATE THE RULEBOOK COULD NOT SEE: nobody
has offered..."* so its refusal can be honest instead of a guess. Verified both directions: withdrawn when
nothing is on the table, and still offered — with a real offer id — when terms genuinely stand
(`Acceptance_survives_when_terms_really_do_stand`).

---

## Narration

### 6. The cover object was mechanically real but never once mentioned in the opening scene

**Symptom.** `20260821-090056Z-b2fe2e94` ended in a stalemate; nobody ever took cover. The user's own
diagnosis, reading the run: the workbench was never mentioned in the scene-setting paragraph the encounter
opens with, so there was no reason for a player to know it existed.

**Cause.** The opening-narration prompt told the Dungeon Master to establish the room but gave the cover
object no more weight than any other piece of scenery, competing for space against four characters, two
containers, and a door in one paragraph. Mechanically the object was always visible to a character who asked
— confirmed by 135 occurrences of cover-related state in the user's own run trace — but a player who never
thinks to ask never gets told.

**Fix — attempt 1.** Folded a cover-mention instruction into the existing "establish the room" sentence.
Tested live at **0/1** — still not mentioned. Rejected.

**Fix — attempt 2.** A dedicated, bolded bullet in `dungeon-master.rules-narrate.md`, given the same standing
as a door or a container rather than folded into general description: *"If the snapshot lists any
environmental cover, name it by name in the opening scene, every time... It is real, physical state a
character could use, not flavour, and unlike a container or a door it is easy to leave out by never once being
asked about."* Tested live at **2/3**. Accepted as a real, measurable improvement — not a guarantee, and not
tuned further, since the underlying cover-awareness was never mechanically broken, only its introduction to
the scene.

---

## Model and provider behaviour

### 7. Every local call silently inherited a sampling penalty that fights tool-calling

**Symptom.** No single incident — found by checking the Ollama request log rather than a run transcript. Every
one of 605 calls in a v0.8 run carried `presence_penalty = 1.5`, a value the harness never set and had no
record of anywhere. Separately, a live run closed a spoken line with `】` (U+3011), a CJK lenticular bracket,
where `]` belonged — a symptom consistent with the language-mixing Qwen's own documentation warns a high
presence penalty can cause.

**Cause.** `presence_penalty` exists in Ollama's (and Qwen's) chat defaults to stop a model repeating itself —
useful for conversation, actively hostile to tool calling, which depends on the model reproducing tool names,
stable ids, and item names *exactly*. Nothing in `AgentModelProfile` or `ChatOptionsFactory` set the value, so
every agent silently inherited whatever the model shipped with, and the harness believed it was configuring
sampling completely.

**Fix.** `AgentModelProfile.PresencePenalty`/`FrequencyPenalty` (nullable, so "unset" and "explicitly zero" stay
distinguishable), `ProviderCapabilities.SupportsPenalties` (true for Ollama/OpenAI, false for Anthropic, which
has no equivalent and now drops-and-reports rather than silently ignoring), and `appsettings.json`'s
`Agents:Default` now sets `PresencePenalty: 0` explicitly. The run manifest records whatever was actually
configured, including `null`, so two runs that differ only in this no longer produce identical-looking
artefacts.

### 8. The intent parser's first attempt still decoded greedily

**Symptom.** Found while confirming #7's fix didn't leave a neighbouring gap: the v0.8 fix for the Ollama-500
crash (session `v0_8_issues.md` #11) escalated the intent parser's **retry** temperature (0.1 → 0.4 → 0.8) but
left its **first** attempt at a hardcoded, greedy `0`. A greedy draw reproduces itself exactly on a bad input —
which is precisely the failure #11 fixed for retries, just left standing on attempt one. Qwen's own guidance
never recommends a temperature below 0.6 for any task.

**Fix.** `SimulationRunner.IntentParserTemperature = 0.3f` — above zero so a bad first draw isn't guaranteed to
repeat, and kept below the first retry floor (0.4) so escalation on retry still means something. The run's
master seed still makes a clean run reproducible; only a genuinely malformed draw now has room to land
differently on its own first attempt.
