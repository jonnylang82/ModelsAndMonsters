# v0.6 Session — Issues Found and Fixed

Problem → fix pairs actually resolved during this session, while building the **Inventory Transfers +
Rulebook Resolver** slice and then hammering on it with four different models (`qwen3.5:9b`, `gpt-5.4`,
`claude-sonnet`, `gpt-4.1-mini`). Anything left unresolved, or that was a leftover from an earlier
session's work, is deliberately excluded.

---

## 1. Rulebook retrieval silently dropped the one relevant card

**Problem:** The first `RuleRetriever` scored rule cards by keyword/tag overlap and sent only a bounded
subset. Two live runs showed the failure mode. A goblin trying to flee — *"I run through the open cellar
doorway and get clear"* (`20260819-132058Z-215a0302`) — never had `encounter.escape` sent to the
resolver, because the escape card's leaf verb "escape" appears in almost no natural flight phrasing and
"open"/"door" lit up the wrong cards; the resolver truthfully reported "no rule supports that" and the
goblins were refused an escape through an open door. The same class bit `container.take`: *"I seize the
potion from the open medicine case"* (`20260819-152505Z-b15b9ca3`) scored `container.take` at **0**
("seize" isn't a tag, and its leaf "take" doesn't appear), so Elara couldn't pick up a potion sitting
in an open case she'd opened herself.

**Fix (in the end, radical — at the user's steer):** the keyword index was doing the resolver's semantic
job, badly. So the semantic routing was removed entirely. `RuleRetriever` now sends the **whole rulebook**
(12 small cards, ~10KB, constant per round) to the stateless resolver on every call and lets the model
decide — no `IntentTags` (removed from `RuleCard`), no scoring, no families, no `score > 0` filter. The
`RulebookMaxCards` / `RulebookMaxInputChars` settings became a **hard ceiling that throws at construction**
if the catalog ever outgrows a bounded request, rather than a budget that silently trims (a trimmed card
is exactly the hidden-action failure above).
[`RuleRetriever.cs`](../src/ModelsAndMonsters/Rulebook/RuleRetriever.cs),
[`RuleCatalog.cs`](../src/ModelsAndMonsters/Rulebook/RuleCatalog.cs)

---

## 2. The transient retry could not rescue a temperature-0 agent

**Problem:** A qwen run (`20260819-112601Z-d15c5786`) aborted on three consecutive `500`s from the
`IntentParser` (`character.parse-intent`). The Ollama log showed the cause was a malformed tool-call XML
the model emitted (`element <function> closed by </parameter>`), which Ollama's own parser rejects with a
hard 500 rather than degrading. The existing transient-retry re-sent the *same* request — but the
IntentParser runs at **temperature 0**, where sampling is greedy and the seed is ignored, so all three
retries reproduced the byte-identical bad reply and the run died. (Confirmed both in `server.log` and the
app trace.)

**Fix:** a retry now genuinely re-samples. `WithRetrySampling` floors the temperature to **0.1** (never
lowering an agent already above it) and offsets the **seed** by the attempt number — both are needed,
since at temp 0 the seed does nothing and near-greedy sampling can still repeat. The first attempt still
uses the exact profile, so a failure-free run replays identically. Covered by `TransientRetryTests`.
[`ModelAgent.cs`](../src/ModelsAndMonsters/Agents/ModelAgent.cs)

---

## 3. History summarisation was calibrated against the wrong number, so context still truncated

**Problem:** A qwen run (`20260819-164625Z-8a875319`) truncated Elara's replies five times — replies
cut off at 58–179 tokens — even though history summarisation fired six times for her. The summariser
trims until an *estimate* of the history is under `HistoryTokenBudget` (5500), but the estimate counts
only the **messages**; every `character.decide` also ships the tool schemas and the model's chat-template
scaffolding — ~2000 tokens the estimate never sees. So "trimmed to 5500 estimated" left ~7600 **real**
tokens in an 8192 window, with almost no room to generate. Confirmed by comparing `HistorySummarised`
(estimate ≈ 5100–5550) against `ModelResponse.InputTokenCount` (≈ 7000–8134).

**Fix:** the budget is now **derived from the context window**, not a fixed number:
`ContextTruncation.EffectiveHistoryBudget(configured, ctx, maxOutput, overhead)` returns
`min(configured, ctx − maxOutput − overhead − safety)` (≈ 4436 for the qwen case). `ModelAgent` learns
the overhead per model as the largest gap between the provider's reported input and the message-only
estimate, so it self-calibrates the tool/template cost rather than hard-coding it. Re-run: truncations
**5 → 0**, max character input **8134 → 6588**. Hosted models (no per-request window) keep the configured
budget unchanged.
[`ContextTruncation.cs`](../src/ModelsAndMonsters/AI/ContextTruncation.cs),
[`ModelAgent.cs`](../src/ModelsAndMonsters/Agents/ModelAgent.cs),
[`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs)

---

## 4. A Dungeon Master leak could become a valid state mutation (take_item had no basis check)

**Problem (a reviewer's finding, the most serious):** After Rowan privately opened a case and saw the
potion, Elara — who had *not* looked inside — asked about it, and a weak DM answered from authoritative
hidden state ("it holds a healing draught"). She then named and took the potion, and `take_item`
**accepted** it. Two protections were needed and only one existed: `steal_item` had an informational-basis
gate, but `take_item` did not, so a DM slip turned into a real ownership change.

**Fix:** `take_item` now has the same authoritative, deterministic, pre-engine gate
(`TurnCoordinator.TakeLacksKnowledgeBasis`). A character can only take an item it has a legitimate basis
to identify: it opened/inspected the container or knew its contents from backstory (checked against a
`ContainerContents` fact, which now carries structured `ItemIds`), or knows the item in the open, or was
told of it (heard its name). The floor is exempt (dropped in plain sight); corpse items pass via the
openly-carried possession facts. Even if the DM leaks, the take is refused with no mutation. The
reviewer's exact scenario is now a regression test.
[`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs),
[`KnowledgeLedger.cs`](../src/ModelsAndMonsters/Knowledge/KnowledgeLedger.cs)

---

## 5. Intimidation and posturing silently became an attack

**Problem (reviewer's finding):** *"I grip my crude spear tighter and take a threatening step forward at
Rowan"* was adjudicated as `attack_character` and rolled for damage — a strike the character never
declared.

**Fix:** the `combat.attack` rule card the resolver reads now states plainly that an attack **must make
contact**, and its exclusions explicitly cover a threatening step, an advance, brandishing/readying a
weapon, and posturing "with no blow described" ("this is speech or nothing, never attack_character").
Validated live: on `gpt-4.1-mini` (`20260819-190615Z-599b0d3c`), which postures constantly ("hold my
longsword ready and prepare to parry", "a guarded step forward… press him back"), **none** of it became
an attack — every one refused in-world. Pinned so the exclusion can't regress.
[`RuleCatalog.cs`](../src/ModelsAndMonsters/Rulebook/RuleCatalog.cs)

---

## 6. The corpse's container representation leaked into the fiction

**Problem (reviewer's finding):** looting a body was narrated as *"Vark reaches into the open corpse of
Skrit…"* — the engine's death-loot container ("an open container you take from") bled into the prose. A
body is not a chest.

**Fix:** corpse containers are now flagged `IsCorpse`. The state formatter and the take-narration hint
describe **a body, not a container** ("never call it 'open' or a 'container', never say it is 'reached
into', never mention a lid"). Validated: on `gpt-4.1-mini` the same loot narrated cleanly — *"Rowan
stoops over Elara's fallen body and lifts the Small Healing Potion from her belt."*
[`WorldObject.cs`](../src/ModelsAndMonsters/Domain/WorldObject.cs),
[`GameEngine.cs`](../src/ModelsAndMonsters/Engine/GameEngine.cs),
[`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs),
[`WorldStateFormatter.cs`](../src/ModelsAndMonsters/Prompts/WorldStateFormatter.cs)

---

## 7. Openly-carried items were recorded as prior "backstory" knowledge, not a public observation

**Problem (reviewer's finding):** every character was seeded knowing that Rowan carried wine and Vark
carried salve — correct, since in this no-distance room what you wear on your person is visible to all —
but it was recorded with `KnowledgeSource.Backstory`, so the DM's knowledge view read it as "known from
before the fight began." That is the wrong provenance: they *see* it now, they didn't know it beforehand.

**Fix:** seeded carried inventory is now recorded as an **initial public observation**
(`KnowledgeSource.PublicEvent`) that every *other* present character makes (the owner is no longer
recorded as "observing" their own item — it is simply in their self-state). The DM view renders it as
"plainly visible — carried openly on the person."
[`KnowledgeSeeder.cs`](../src/ModelsAndMonsters/Knowledge/KnowledgeSeeder.cs),
[`SimulationRunner.cs`](../src/ModelsAndMonsters/Orchestration/SimulationRunner.cs),
[`CharacterKnowledgeView.cs`](../src/ModelsAndMonsters/Orchestration/CharacterKnowledgeView.cs)

---

## 8. A rejected theft was traced as having rolled dice when it hadn't

**Problem:** a live run showed an `InventoryInteraction` for a rejected `steal_item` (the item was
already gone) with `RngConsulted: true`, though the engine rejects on validation *before* any roll — an
internal trace inconsistency (the flag was set for any steal that reached the engine, not for one that
actually drew).

**Fix:** `RngConsulted` now reflects whether a draw actually happened (`engineResult.RngDraws.Count > 0`),
so a validation-rejected theft correctly records no RNG.
[`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs)

---

*The full slice — give/drop/steal, the ground and corpse loot, the informational-basis gates for both
take and steal, the stateless whole-rulebook resolver, and every fix above — is pinned by the
deterministic test suite (431 tests). Every live observation here points at a specific run folder whose
`trace.jsonl` and `report.md` hold the evidence.*
