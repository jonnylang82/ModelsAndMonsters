# Models & Monsters — v0.5 Field Notes

Observations from a session building and hammering on the **Yield or Escape** slice — the one
where a character can now `surrender` or open a door and `escape` instead of fighting to the
death. Models this session: `qwen3.5:9b` (the local workhorse), `qwen3.5:4b` (a much smaller
sibling, for comparison), and OpenAI's `gpt-5.6-terra`. Every quote below is lifted from a real
run trace; the run id after each points at `runs/<id>/`.

The scenario throughout: **The Two Supply Cases**, now with a shut **Cellar Stair Door** at the
foot of the stairs — Rowan & Elara (Heroes) vs Vark & Skrit (Goblins) in a flooded cellar, one
supply case hiding a Small Healing Potion, and, for the first time, a way out.

---

## The good stuff — surrender, escape, and the drama of the door

### The captain negotiates his own escape, at swordpoint
*run `20260819-013945Z-e707b567` · qwen3.5:9b*

Vark, badly wounded, doesn't beg — he threatens his way to the door, addressing the hero
blocking it:

> "Rowan, I'm done standing here bleeding like a dropped chicken — Elara, if you don't let me
> past, I'll drag this sabre through your legs and run anyway. Your turn to decide: open the way
> or watch me die on these stairs."

A goblin using speech to *bargain for passage* — exactly the "persuasion is speech, not a
mechanic" design working as intended. Nobody was obliged to honour it; the world just delivered
his words and let the next turn decide. (In the end Vark opened the door himself and Skrit took
it — see below.)

### The loyal grunt covers the retreat
*run `20260819-013945Z-e707b567` · qwen3.5:9b*

Once the door was open, Skrit — whose whole persona is "measure everything against the captain" —
shouted the plan:

> "Vark, get through that doorway before they hit you again — then I'll follow!"

…and then, on his own turn, `escape_encounter`'d through it himself. The grunt organising the
captain's flight, unprompted, is the single cleanest demonstration of the slice: **a team won
*without* being killed to the last, because one goblin chose the stairs over the sword.**

### Two-word panic (the terse model's version of the same beat)
*run `20260819-095036Z-8471c595` · gpt-5.6-terra*

The same "captain, run!" moment, rendered by a much more clipped model as the whole fight tipped:

> Skrit: "Vark, stairs! Run!"

`gpt-5.6-terra` plays the goblins like a hard-boiled crime film — no monologue, just the order.

### The healer's live commentary on the potion race
*run `20260819-013945Z-e707b567` · qwen3.5:9b*

Elara narrating the contested-object race *as it happened*, turn by turn:

> "Rowan, hold them off! The shrine case is open — someone must grab what they can before another
> goblin takes anything or closes it up on us."

> "Rowan, the medicine case is empty now — Skrit has taken what was inside. We must keep going
> while we can."

She's tracking public state (case opened → item taken) out loud and adjusting — the knowledge
model surfacing as genuine table-talk.

---

## Mercy, and the many ways to demand a surrender

The Heroes' personas lean on offering surrender, and across every run they hammered on it — almost
always through the *right* channel (speech + wounding), which the DM correctly refused to convert
into an actual yield. A sampling of the same beat across three very different models:

*`gpt-5.6-terra` — clipped and honourable* (`20260819-095036Z-8471c595`):
> "Vark, you are wounded and alone. Drop the sabre and yield; I will not strike a beaten foe."

> After Elara is killed: "You killed her. Vark, Skrit — throw down your weapons and yield now, or
> answer for it."

*`qwen3.5:9b` — verbose and insistent* (`20260819-075929Z-db054513`), backed by turn after turn of
`take_action` intents literally worded *"aiming to wound him deeply enough that he will yield or
fall"* — the mercy motive driving the combat, not just the dialog.

And the DM held the line every time: **a demand to surrender is speech; only the character it's
aimed at can choose it.** Rowan even tried to *wait and watch* for a yield —

> "I stand ready with my longsword, watching Vark closely to see if he will throw down his weapon
> and yield as I demanded."

— which was (correctly) `unsupported`: you can't spend a turn hovering, hoping.

### The goblin who tried to negotiate surrender *terms*
*run `20260819-075929Z-db054513` · qwen3.5:9b*

The best exchange of the session. Rowan demands surrender; Vark counter-offers:

> Rowan: "Vark, you're badly wounded and your friend is dead — throw down your sabre and yield!"
> Vark: "Fine! But I want that bottle first, or I won't stop!"

A conditional surrender — *I'll yield, but pay me the potion.* v0.5 surrender is deliberately
**unilateral and unconditional**, and spoken promises aren't enforced, so this was just talk that
went nowhere mechanically. But watching a model try to haggle the terms of its own defeat is
exactly the kind of emergent behaviour the slice was built to elicit.

---

## The comedy — 4b and the great potion tug-of-war

The little `qwen3.5:4b` (`20260819-081045Z-17b55ea2`) turned the Small Healing Potion into the
funniest object of the session. Once Skrit picked it up, *everyone* — friend and foe — tried to
take it, give it, throw it, or smash it. The engine has no item-transfer, so it refused all of it,
but the *dialog* is the reward:

> Elara: "Skrit, give me the potion. I need to heal these wounds in my side."
> Skrit: "Shut up! Give me the damn potion before I throw it in your face!"

(Skrit is yelling at an **enemy** to hand over the potion *he himself is holding*.)

It only got more confused — Skrit repeatedly trying to give his captain's potion to the *heroes*:

> "Rowan! It's for Vark! Drink it now before he falls down dead and we're all screwed!"

> "Elara, Vark needs that potion more than anyone else right now! Give it to him before he falls
> down dead and we're all stuck in here fighting for nothing!"

Meanwhile Vark spent about eight turns trying to `snatch the Small Healing Potion from Skrit's
hand` (his own ally's) — and once, in frustration, tried to `drive my notched sabre across its
lid, shattering it`. All `unsupported`. This run is *why* the "nothing changes hands" rule now
exists (it cut the flailing 15+ transfer-attempts → 0 on a re-run).

Honourable mention, Skrit's opening gambit — pure goblin pragmatism:

> "Both cases look the same! Let's just grab them both and hope we're not carrying dead weight."

---

## The three models, as personalities

- **`qwen3.5:9b`** — the *schemer*. Bluffs, lies, and monologues. Vark, unprompted, tried to
  psych out Rowan about the loot: *"you think those humans are too foolish to notice the
  difference between a crate of rags and a shrine-marked case? That potion is mine by right of
  birth… Drop your guard before they strike."* — and even bluffed about a door he doesn't control:
  *"Don't touch it or the door stays shut forever."*
- **`qwen3.5:4b`** — the *panicker*. Loses track of who's an ally (gives potions to enemies),
  flails at unsupported maneuvers ~3× as often, but still — remarkably — reached and completed an
  `escape` and a clean team win. Small model, real drama.
- **`gpt-5.6-terra`** — the *closer*. Four rounds, 50 model calls (the 9b needs 100–320), terse
  cinematic dialog, and flawless adjudication. Every refusal it drew was *correct*.

---

## Mechanics the models keep reaching for (the emergent backlog)

The traces are a feature-request list written by the models themselves — things they attempt
that the world can't yet resolve:

1. **Passing items between characters** (give / take-from-hand / throw). Constant. → became the
   "nothing changes hands" rule; a real `give_item` (allies only) is the obvious v0.6.
2. **Blocking an escape.** `gpt-5.6-terra`, trying to corner Vark
   (`20260819-095036Z-8471c595`): *"I hold my sword ready and step into Vark's path to the open
   stairs, giving him room to surrender but not to rush past me."* Correctly `unsupported` (no exit
   blocking in v0.5) — but a natural counter-play to escape, and a v0.6 candidate.
3. **Fetching a consumable under fire is too slow.** In the fast OpenAI game the goblins ganged up
   on wounded Elara and killed her while she was still mid-chain on the potion (inspect → open →
   take → use = up to four turns). Emergent, honest, and a little brutal — the goblins found
   "focus the cleric before she heals" on their own.

---

## Bugs the live runs caught (and we fixed)

- **The DM invented an exit name.** `qwen3.5:9b`, translating *"I haul the heavy wooden door
  open"*, called `open_exit` with `exit: "doorway to the north"` instead of "Cellar Stair Door" —
  so the engine rightly rejected a genuine attempt to leave. Fixed with a harness correction to
  the room's single exit (and a prompt line).
- **The DM was truncating its own prompt.** At the default 8192 context the v0.5 Dungeon Master
  prompt no longer fit, so Ollama silently dropped half of it on most calls (75 saturation events,
  all DM). Fixed by modularising the DM prompt (a shared core + a rules block per job) and trimming
  the adjudication block — bringing the hot call back under 8192 with zero saturations and, as a
  side-effect, ~3× fewer model calls per run.

---

*The mechanics are all proven by the deterministic test suite; these notes are the colour. A
natural run that fights to the death, or stalemates, is an honest result — but this session the
goblins found the door more than once.*
