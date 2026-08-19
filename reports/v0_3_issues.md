# v0.3 — Issues Found and Fixed This Session

Problems that were both **encountered and resolved** while building and hardening v0.3 (public speech +
the contested chest) in this session. Carry-over issues from earlier sessions, and anything found but
left unresolved, are intentionally excluded.

---

## 1. Demo runs 500'd — misdiagnosed as OOM, actually a model-reload race

**Encountered:** Autonomous demo runs against local Ollama (`qwen3.5:9b`) intermittently failed with
`HttpRequestException: 500 Internal Server Error`, aborting the run. First guess was VRAM exhaustion on
the 8 GB card.

**Resolved:** Reading `server.log` showed zero CUDA/OOM errors anywhere. The actual cause: changing
`ContextWindow` between successive runs (8192 → 4096 → 8192) forced Ollama to tear down and reload the
`llama-server` process each time (`num_ctx` can't be changed on a live runner). The reload raced the
app's next `/api/chat` call, hitting a not-yet-ready runner → 500. Fix: stop changing `num_ctx` between
runs; keep one runner resident (pre-warm + `keep_alive`). Runs at a constant context size never 500'd.

## 2. Chest scenery described as "behind the goblins" discouraged natural use

**Encountered:** In the first clean demo run, no character showed any interest in the chest — five
rounds of pure combat.

**Resolved:** The room/chest description said the chest was "against the wall **behind the goblins**,"
which reads as a positional barrier — but the engine world has no distance or position, everyone is
already within reach. Reworded to "within easy reach of everyone in the room." A subsequent seed
produced a fully natural open → take sequence.

## 3. Immersive speech prompt made the model write tool calls as prose

**Encountered:** One agent (Vark) repeatedly replied with an in-character monologue ending in
`say "..."` or `ask_dm "..."` — no parentheses — which the harness's tool-call recovery regex (paren-only)
didn't catch. Each miss triggered a nudge, bloating that agent's history until it exceeded `num_ctx`,
producing cascading output-truncations. Root cause traced to the new Speaking section's evocative
language ("shout or mutter... a warning, a threat, a taunt").

**Resolved:** Two changes: (a) tightened `character.system.md` into a firm output contract — "every
reply is one of the four tool calls; whatever you say/ask/do goes inside the call, never loose prose" —
and reworded Speaking to "call `say`, put your words in `message`"; (b) widened
`ModelText.TryRecoverToolCall` to also catch the quoted, paren-free form as a backstop. Measured on the
same seed before/after: no-tool-call replies 62→0, output truncations 18→0, context-saturation events
30→0.

*(While widening the recovery regex, the new quoted-form matcher briefly broke JSON tool-call recovery
by grabbing a stray quoted fragment out of JSON punctuation. Caught immediately by the existing
`A_tool_call_written_as_json_is_recovered` test; fixed by trying JSON recovery first and gating the
quoted-form matcher to non-JSON text.)*

## 4. Grab/snatch on an already-open container wasn't recognised as `take_item`

**Encountered:** Live trace showed Skrit saying "I reach into the open chest and grab the potion," which
the Dungeon Master twice misrouted — once claiming the chest needed opening first, when it was already
open.

**Resolved:** Extended the DM adjudication prompt to explicitly recognise "reach into/grab from/snatch
from" an container marked **OPEN** in the snapshot as `take_item` directly, and to never claim an
already-open container needs opening.

## 5. Object-outcome narration contradicted the actual state transition

**Encountered:** After a `take_item` from a container that was already open, the DM still narrated the
character "lifting the lid" — describing an opening that never happened.

**Resolved:** The object-outcome prompt now receives an explicit, pre-computed transition string (e.g.
"already OPEN and stays open — no lid is lifted; only the item moved") instead of inferring the change
from the engine summary, so the narration can't invent an opening.

## 6. An item on a dead character's body became permanently unobtainable

**Encountered:** A goblin who had just looted the potion was then killed; the potion was gone with no
way for any other character to retrieve it.

**Resolved:** `GameEngine.ResolveAttack` now moves a lethal target's inventory into a new, already-open
corpse `Container` (`"{Name}'s body"`) in the room when they die carrying anything, reusing the existing
`take_item` action to loot it. Verified live: Skrit died holding the potion mid-theft and it ended up
sitting in `Skrit's body`, still recoverable.

## 7. A valid action on the attempt-limit boundary was discarded instead of resolved

**Encountered:** A character's third (and cap-reaching) attack attempt was a legitimate, well-formed
intent, but the harness refused it outright as "you hesitate too long" and ended the turn
`AbandonedAtLimit` — discarding a decision it had already asked for and received.

**Resolved:** Reordered `TurnCoordinator`'s take-action handling so every attempt actually requested is
always adjudicated; the cap now only governs whether a *further* decision is requested, checked after
resolving the current one. Added a regression test asserting a valid action on the last allowed attempt
is resolved, not refused.

## 8. Stale report metadata and duplicated terminal condition

**Encountered:** Generated reports showed application version `0.2.0.0` (unbumped since v0.2) and
printed the run's terminal condition twice — once in the header, once again verbatim in the Final State
section.

**Resolved:** Bumped `<Version>` to `0.3.1` in the project file; removed the redundant scalar block from
`RunReportWriter`'s Final State section (the header already carries it).

## 9. Characters lost track of their own side mid-fight

**Encountered:** Live traces showed the goblin captain Vark attacking his own grunt Skrit in multiple
runs — a friendly-fire confusion the engine permits and the DM faithfully translates.

**Resolved:** Added an explicit, per-turn "Your allies (never strike these): … / Your enemies (aim your
blows here): …" roster to each character's self-state block, computed fresh from who is currently alive
— not just a static ally list from character creation.

## 10. Dungeon Master rejections leaked the game's machinery, and a prompt ban wasn't enough

**Encountered:** Refusal text repeatedly broke the fiction — *"that is not an action the world can
resolve here,"* and in one case an outright recitation of the supported-action list to a goblin — despite
the system prompt explicitly forbidding it. A seed batch showed the leak in 4 of 5 runs.

**Resolved:** A prompt instruction alone couldn't reliably bind this content, so added a deterministic
guard: `MachineryLanguage.IsLeak` detects framing-specific phrasing in a rejection reason before it
reaches a character; if it fires, the DM is asked once to rephrase in-world (falling back to a neutral
line if the rephrase still leaks). Verified live: 0 leaked reasons reached a character afterward, with
the original leak preserved in the trace via an `AdjudicationCorrected` event.
