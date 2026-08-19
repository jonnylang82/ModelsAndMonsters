# v0.5 Session — Issues Found and Fixed

Problem → fix pairs actually resolved during this session, while building and then live-testing
the **Yield or Escape** slice. Anything left unresolved, or that was a leftover from an earlier
session's work, is deliberately excluded.

---

## 1. The Dungeon Master invented an exit name, rejecting a genuine escape attempt

**Problem:** A live run (`qwen3.5:9b`) had a goblin say *"I haul the heavy wooden door open"*.
The DM correctly recognised this as `open_exit` — but filled in the exit argument as
`"doorway to the north"` instead of the snapshot's actual "Cellar Stair Door". The engine
rightly refused it (`UnknownExit`), so a straightforward attempt to leave silently failed.

**Fix:** `TurnCoordinator.ResolveExitReference` mirrors the existing actor/weapon correction:
when the room has exactly one exit and the DM's reference matches none of them, the harness
substitutes the room's sole exit and records an `AdjudicationCorrected` trace event, so the
DM's original (wrong) argument is never silently discarded. With two or more exits nothing is
guessed — the engine still refuses an unmatched or ambiguous reference in-world. Also hardened
the adjudication prompt: *"never invent an exit name."*
[`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs),
[`dungeon-master.rules-adjudicate.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.rules-adjudicate.md)

---

## 2. The Dungeon Master silently truncated its own prompt at the 8192 context window

**Problem:** The v0.5 additions (exit/disposition rules, three new decision steps) grew the DM's
system prompt to ~24KB (~6,050 tokens). Combined with the per-call authoritative state block, a
single adjudication call ran ~8,500+ tokens — over the default 8192 `num_ctx` — so Ollama
silently discarded the oldest part of the DM's *own system prompt* on nearly every call. A
12-round natural run logged **75 `ContextWindowSaturated` events, all on the DungeonMaster**,
degrading late-game adjudication exactly where the slice's deliberate surrender/escape decisions
were supposed to happen.

**Fix, in two parts:**
- **Modularised the DM prompt.** Split the one monolithic `dungeon-master.system.md` into a
  shared `dungeon-master.core.md` plus a rules block per job
  (`rules-adjudicate` / `rules-narrate` / `rules-answer`); `DungeonMasterAgent` now composes
  core + that job's rules per projection, so narration and answering calls no longer carry the
  ~17KB adjudication procedure they never use (those calls dropped from ~6,100 to ~1,400 tokens
  and stopped saturating entirely).
- **Trimmed the adjudication rules block itself** (17.3KB → 11.4KB, ~4,329 → ~2,838 tokens):
  condensed repeated examples and merged overlapping "hold these lines" bullets, with every
  decision step and invariant preserved.

Verified with a same-seed A/B at 8192 (`qwen3.5:9b`, seed 7): **43 saturations → 0**,
`DmProtocolFailure` 8 → 0, `EngineRejected` 13 → 0, total model calls 178 → 62, same 15 accepted
actions, same clean `Mixed` outcome. Context window reverted to 8192 (full GPU, no CPU
spillover from the interim 12288 workaround).
[`DungeonMasterAgent.cs`](../src/ModelsAndMonsters/Agents/DungeonMasterAgent.cs),
[`dungeon-master.core.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.core.md),
[`dungeon-master.rules-adjudicate.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.rules-adjudicate.md),
[`appsettings.json`](../src/ModelsAndMonsters/appsettings.json)

---

## 3. Characters burned turns trying to pass items hand-to-hand, which the engine has never supported

**Problem:** A live run (`qwen3.5:4b`) turned a contested healing potion into a farce: once one
goblin picked it up, three other characters — allies and enemies alike — spent turn after turn
trying to snatch, give, throw or smash it out of each other's hands. None of it was ever
supported (items only ever moved through containers), so every attempt was refused, and each
refusal risked a machinery-leaking explanation. One run logged **~15+ transfer attempts**, **35
`DmUnsupported` adjudications**, and **12 machinery-leak rephrases** driven almost entirely by
this one gap.

**Fix:** Made the restriction explicit rather than something the model had to discover by
failing repeatedly — a state-note rule ("items move ONLY through containers"), an explicit
`unsupported` example list in the adjudication rules, and an in-world line in the character
prompt ("nothing changes hands"). A same-seed A/B (`qwen3.5:4b`) confirmed it: potion-transfer
attempts **15+ → 0**, `DmUnsupported` **35 → 4**, machinery-leak rephrases **12 → 0**, total
model calls **321 → 229**.
[`WorldStateFormatter.cs`](../src/ModelsAndMonsters/Prompts/WorldStateFormatter.cs),
[`dungeon-master.rules-adjudicate.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.rules-adjudicate.md),
[`character.system.md`](../src/ModelsAndMonsters/Prompts/Templates/character.system.md)

---

## 4. The adjudication prompt's closing instruction still listed only the old six tools

**Problem:** While inspecting a real adjudication request, the final directive line —
*"call exactly one tool: `attack_character`, `use_item`, `open_container`, `take_item`,
`inspect_object`, or `reject_action`"* — never mentioned `open_exit`, `escape_encounter` or
`surrender`. They were fully wired (real tool declarations, full decision steps earlier in the
same prompt) and worked anyway, but the single most immediate instruction under-listed them,
risking a bias back toward the old six.

**Fix:** Updated the closing directive to list all eight resolvable tools plus `reject_action`,
with a one-line reminder not to overlook the non-combat outcomes.
[`dungeon-master.adjudicate.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.adjudicate.md)

---

## 5. A closed exit was answered as "locked", contradicting the no-lock design

**Problem:** A live run had the DM answer a character's question about the cellar door with
"shut and locked in place, barring your exit" — but v0.5 deliberately has no lock: a closed
exit can always be pulled open. That wording could wrongly discourage a character from ever
attempting to open it.

**Fix:** Hardened both the DM system prompt and the per-turn authoritative state block to state
explicitly that an exit is never locked, barred, jammed or held shut — only shut, and always
openable.
[`dungeon-master.core.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.core.md),
[`WorldStateFormatter.cs`](../src/ModelsAndMonsters/Prompts/WorldStateFormatter.cs)

---

## 6. The report's per-character resolution summary rendered as a single flattened bullet

**Problem:** The new "How each character left active combat" report section was built from a
newline-split string, but the JSON reader's `Flatten` helper strips newlines for table safety
(a pre-existing, correct behaviour elsewhere in the report). The result: a Mixed-resolution run
with two departures ("Vark was killed." and "Skrit escaped through the Cellar Stair Door.")
rendered both facts squashed onto one bullet instead of one each.

**Fix:** `RunReportWriter.WriteOutcome` now iterates the trace's structured `Resolutions` array
directly (one JSON object per character) instead of splitting a flattened summary string, so
each character gets its own bullet regardless of how the text-flattening helper behaves.
[`RunReportWriter.cs`](../src/ModelsAndMonsters/Tracing/RunReportWriter.cs)
