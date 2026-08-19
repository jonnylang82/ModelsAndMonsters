# Models & Monsters — v0.2 Session Notes

Observations gathered on 2026-08-17 while building the unified reasoning-effort feature, from
reading run traces across Ollama (qwen3.5:9b), OpenAI (gpt-5.x), and Anthropic (sonnet/haiku). Most
of the vivid material comes from one qwen3.5 duel — **Aric vs Grik in the Guard Chamber**
(`runs/20260817-222815Z-970f0138`) — because turning reasoning on made the characters' private
thinking visible for the first time. Counts marked *(all runs)* are aggregated across the session's
traces.

---

## Characters developed personalities nobody scripted

**Grik, the cowardly goblin.** Given only a terse profile, qwen3.5 committed hard to playing a
coward — and its *reasoning* (now rendered in the report) reads like an internal monologue:

> "I am Grik... Aric just swung his sword at me but missed. Grik is cowardly, spiteful, and
> opportunistic — Grik wants to drive the intruder away without fair fighting."

> "I'm badly wounded now with 2/8 health... I need to drive him away or end this fight **before I die
> alone on the stones.**"

It never just "attacked." Its actual moves were all evasion and provocation:

> "I **shout insults at Aric to provoke him** into striking again, hoping to find an opening or make
> him reckless."
> "I step back and hold my rusty axe ready, **watching for Aric to make another move so I can
> dodge.**"
> "I swing at his legs, **trying to trip him or make him stumble** in this tight space."

**Aric, the methodical sell-sword.** The counterpoint — Aric's reasoning tracked his own health,
inventory, and tempo like a careful mercenary, and his prose had a nice grim flavor:

> "I **drink the small healing potion quickly, wiping away the taste of blood with a grim nod** as I
> prepare to finish this."
> "I thrust my sword upward into Grik's throat with all the strength in my body, **to end this once
> and for all.**"

The whole duel arced like a real fight: Aric wounds Grik, Grik counters, Aric heals at 6/10, Grik
gets desperate — an emergent three-act structure out of dice and two goblin-brained LLMs.

---

## The models desperately want to *move* — and the world won't let them

The single most common friction, by a mile: characters constantly attempt **tactical positioning**
in a world that has no distance. The engine can only resolve *a weapon strike* or *an item use*, so
everything else bounces. **277 "unsupported" rulings** *(all runs)*, almost all footwork:

> "You can't **dash towards the brazier**, Grik."
> "You can't **take a step back**, Aric."
> "You can **try to dodge**, but the world can only resolve a direct weapon strike or an item use."
> "You can **lunge forward**, but... your axe stays still."
> "You can **slash your axe sideways**, but... your blow stays [put]."

Every model brings trained-in combat instincts — flanking, footwork, feints, "back away to the
brazier" — to an abstract turn engine. It's the clearest signal of the mismatch between what LLMs
*think* a fight is and what this engine models. (Related: earlier in the project the DM once
**invented a passage behind the goblins** to satisfy this urge — hallucinating geography the world
didn't have.)

---

## Genuinely funny / odd moments

- **Swinging at a corpse.** "Skrit is already dead, so there is no one to strike." More than once a
  character kept attacking a target the engine had already killed.
- **Dying mid-swing.** A character committed to an attack and the DM had to narrate the body giving
  out first: "You brace for impact, but your body has given out, and you fall to the ground, unable
  to meet Aric's blow."
- **Friendly fire (early gemma test).** A small model (gemma4) had Skrit attack **its own ally
  Vark** — team lines meant nothing to it. Bigger models never did this once teams were in the
  prompt.
- **Misses narrated with dignity.** RNG whiffs came back as "the blade cuts through the air and
  strikes nothing" rather than "MISS" — the DM dressed up the dice nicely.

---

## Machinery leaks (a real bug worth watching)

The DM occasionally **narrated raw tool-call JSON as prose** — the model wrote the reject-action
*call* into the *narration* field. Caught **16 times** *(all runs)*, e.g.:

> `{"name": "reject_action", "parameters": {"category": "unsupported", "reason": "You cannot dodge to
> the side in this cramped room..."}}`

The harness classifies these as `DmProtocolFailure`; the detect-reask guard exists precisely because
prompt bans alone didn't stop it. Still the most common way the illusion breaks.

---

## Behavioral notes by model (from the reasoning-effort testing)

- **qwen3.5:9b** — strong roleplay and clean tool calls, but with reasoning on it sometimes **thinks
  on one call and acts on the next** (the action-retry absorbs it). Graduated `medium` didn't starve
  it *this* time only because the budget was 1500 tokens; at 700 it still would.
- **gemma4:e2b / e4b** — tended to **posture in prose instead of calling tools**, especially under
  the immersive character prompt. The prose-vs-tool-call divide is the main gate on which local
  models can play.
- **The reasoning is the payoff.** The best argument for rendering the thinking blocks: Grik's
  cowardice was *invisible in the narration* but obvious in its private reasoning. The internal
  monologue is where the character actually lives.

---

## Tool usage across the session *(all runs)*

| Tool | Calls | | Tool | Calls |
|---|--:|---|---|--:|
| `take_action` | 1140 | | `use_item` | 29 |
| `attack_character` (DM) | 740 | | `say` (public speech) | 20 |
| `ask_dm` | 493 | | `open_container` | 9 |
| `reject_action` | 296 | | `inspect_object` | 7 |
| `end_turn` | 88 | | `take_item` | 6 |

Characters lean overwhelmingly on **acting and asking**; the social/exploration verbs (`say`,
containers, `inspect_object` — the v0.3/v0.4 additions) see light but real use. The `reject_action`
count (296) is the flip side of the movement problem above: a lot of turns get bounced.
