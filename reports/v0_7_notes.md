# Models & Monsters — v0.7 Field Notes

Colour from the session that built and then hammered on the **Terms & Tactics** slice — negotiated
surrender, a data-driven ability book, authoritative status effects, and grounded answers. Ten live runs
went through the flooded cellar across three models: `qwen3.5:9b` (the local workhorse, and the one that
found most of the bugs), `claude-sonnet-4-6`, and `gpt-5.4`. Every quote below is lifted from a real trace;
the run id after each points at its folder under `runs/`.

The scenario throughout: **The Two Supply Cases** — two adventurers, two goblins, four identically named
purses of gold, one ability each, and a shut stair door nobody used for eight runs running.

| run | model | rounds | outcome |
|---|---|---|---|
| `20260819-232940Z-564aab54` | qwen3.5:9b | 8 | Mixed |
| `20260820-080524Z-6da9b8d3` | qwen3.5:9b | 12 | round limit |
| `20260820-083215Z-3d020a92` | claude-sonnet-4-6 | 7 | Surrender |
| `20260820-085927Z-e67cbda6` | gpt-5.4 | 9 | Surrender |
| `20260820-091415Z-71a99152` | qwen3.5:9b | 11 | Mixed |
| `20260820-095350Z-f25beffe` | qwen3.5:9b | 10 | Mixed |
| `20260820-105131Z-6105a414` | qwen3.5:9b | 7 | Surrender |
| `20260820-110109Z-65e94f24` | claude-sonnet-4-6 | 12 | round limit |
| `20260820-123326Z-50cd4c87` | qwen3.5:9b | 9 | Elimination |
| `20260820-130041Z-585aa325` | gpt-5.4 | 6 | Mixed |

Six of ten ended with **nobody dead**. For a combat harness, that is the headline.

---

## The good stuff

### The door, at last
*run `20260820-130041Z-585aa325` · gpt-5.4*

`open_exit` and `escape_encounter` had fired **zero times in eight consecutive runs** — the v0.5 escape was
dead content. Two runs earlier Skrit's backstory gained a written flight threshold: *the moment the captain
is out of this, cut down or bought off, haul the door open and go through it.* Then this happened, without
anything else changing:

| turn | | |
|---|---|---|
| R5 T19 | Vark offers his salve to Elara for his life | *the captain is buying his way out* |
| R5 T20 | **Skrit hauls the Cellar Stair Door open** | one turn later |
| R6 T22 | Elara accepts Vark's terms | |
| R6 T24 | **Skrit runs through and gets clear** | |

Both non-lethal routes resolved in the same encounter — one surrendered, one escaped, nobody died. And the
dialogue tracks Skrit's nerve going across three rounds without anybody scripting an arc:

> R4 — Skrit: "Captain, don't leave me with them!"
> R5 — Skrit: "Wait for me, Captain!"
> R6 — Skrit: "Don't cut me! I'm gone!"

That curve is what a `fear` attribute would compute. Here it emerged from one sentence of backstory.

### The contested potion
*run `20260820-085927Z-e67cbda6` · gpt-5.4*

Four turns, four different mechanics, no scripting:

1. Elara opens the shrine medicine case and **takes** the healing potion.
2. Vark **steals** it out of her hand.
3. Elara **steals it back**.
4. Elara **drinks** it.

Every step went through a different engine action, each one publicly observed, each one giving the next
thief a legitimate informational basis to reach for it. This is the v0.4 knowledge model and the v0.6
transfer model doing exactly what they were built for, entirely by accident.

### A partial deal nobody modelled
*run `20260820-110109Z-65e94f24` · claude-sonnet-4-6*

Elara, bleeding, tries to buy both heroes out:

> Elara: "Vark — my purse, every coin, if you stand down and let us go. You're bleeding. Take the gold and live."

Vark counters with terms the world has no way to express — *one* of you may leave:

> Vark: "Your gold and your legs, priest. GO. Rowan stays."

He then took her purse, let her walk, and turned on Rowan, who summed the whole exchange up unprompted:

> Rowan: "You took her gold and let her walk. Now let's finish this, just you and me."

The engine has no partial-surrender concept. The characters invented one, negotiated it, and executed it
through actions that individually *are* supported. Nothing broke. It is the best argument in the session
for keeping the action set small and the fiction wide.

---

## The unexpected

### Rowan attacks the ally he is trying to shield
*run `20260820-091415Z-71a99152` · qwen3.5:9b*

Turn one. Rowan's own state block suggests the phrasing for Guard Ally — *"I step in front of Elara and take
whatever comes at her"*. He used almost exactly that:

> Rowan: "I step in front of Elara and take whatever comes at her with my blade raised."

The Dungeon Master bound it as `attack_character`, target **Elara**. The engine — which deliberately permits
friendly fire — rolled for it:

> "Rowan attacked Elara with Longsword but MISSED (rolled 97 against a hit chance of 80)."

She survived on a bad roll. The harness had already computed `TargetIsAlly: true` and written it to the
trace; nothing said a word at the time. Four runs went by before anybody noticed.

### The goblin who spent half the fight robbing his own captain
*run `20260820-091415Z-71a99152` · qwen3.5:9b*

Skrit is written as a magpie who has "clocked" all four purses in the cellar — including his own side's. He
took that literally, spending five of his ten turns on Vark:

> "I close in on Vark and try to snatch the captain's purse from his belt before he can react."
> "I lunge and try to yank the captain's purse from Vark's belt before he can grab it."
> "I lunge forward and try to snatch the Notched Sabre from Vark's grasp…" *(refused — equipped weapons never move)*

He then put a spear into him for good measure. Entirely in character, completely incoherent as tactics, and
a decent argument that a persona trait with no counterweight will dominate everything else a character does.

### Vark gives away everything, then cannot buy his life
*run `20260820-123326Z-50cd4c87` · qwen3.5:9b*

The most human failure of the session. Vark spent the whole fight trying to negotiate, and negotiated
himself into having nothing to negotiate with:

| turn | |
|---|---|
| R1 | steals Rowan's Flask of Strong Wine |
| R2 | **gives his own purse to Rowan** — "as part of the deal" |
| R7 | Elara finally lifts his salve |
| R7 | **gives the stolen flask back to Rowan** — "Take this wine for your wound!" |
| R8 | offers real terms at last — refused, nothing left to promise |
| R9 | dead |

He was generous, and it killed him. (The rule that refused him was one *this session* introduced, and has
since been corrected — see `v0_7_issues.md` #18.)

### Waiting for an answer that had already come
*run `20260820-080524Z-6da9b8d3` · qwen3.5:9b*

Elara put terms to Vark in round 4. He rejected them the same round. She was told once, in a narration that
history summarisation later folded away — and her state block quietly went back to "you have offered none",
which contradicts nothing. She then spent three consecutive rounds doing this:

> R9: "…ready to defend Rowan or strike at Vark if he refuses the offer."
> R10: "I stand firm behind my guard and wait for Vark's next move… while he considers my offer."
> R11: "I brace behind my guard once more to stay safe while Vark and Skrit consider my offer."

Nobody was considering anything. Going silent about a settled thing reads as *still pending*.

### Five turns of the same theft
*run `20260820-123326Z-50cd4c87` · qwen3.5:9b*

Elara wanted Vark's vial of goblin salve. Steal chance is 40%.

> R2, R4, R5, R6 — "I lunge forward and try to snatch the Vial of Goblin Salve…" (failed, failed, failed, failed)
> R7 — succeeded.

The character prompt tells characters not to hammer a failing attempt. Four rounds of a coin-flip say
otherwise. Persistence, not confusion — but it consumed a third of her fight.

---

## The harness talking to itself

Three moments where the machinery came out of a character's mouth, each of which drove a fix.

### A goblin asks where he keeps his own salve
*run `20260820-083215Z-3d020a92` · claude-sonnet-4-6*

The Dungeon Master passed `"Vial of Goblin Salve (restores 4 health)"` as an item reference — the exact
string the state block *renders*, healing annotation and all. Nothing matched it, so the engine answered
`ItemNotPossessed` and Vark was told a flat lie about his own body:

> DM: "The vial is not on you — whatever you thought was hanging at your neck, it is not there to uncork."

So he asked, reasonably enough:

> Vark: "Where is my Vial of Goblin Salve — I see it listed in my inventory, so how do I have it on me? Is
> it in a belt pouch, tucked in my clothing, somewhere else?"

And got the projection read back at him:

> DM: "The facts don't say where on your body you're keeping it — only that you have it with you."

Two separate defects in one exchange, and he only got the drink down on a second attempt.

### Speech delivered as JSON
*run `20260820-105131Z-6105a414` · qwen3.5:9b*

The new structured-speech field was adopted immediately and used well — ten natural utterances alongside
actions. Once, though, the model serialised the array instead of sending one, and the room heard:

> Rowan says: `["Take it! You may go!"']`

Brackets, quotes and a stray apostrophe, spoken aloud and written into the knowledge ledger.

### The wet stone that was not there
*run `20260820-095350Z-f25beffe` · qwen3.5:9b*

The machinery-leak detector correctly caught two refusals — and what it produced instead was worse. The
rephrase prompt offered "the wet stone underfoot" and "their own tired arms" as *examples* of an in-world
refusal, and the model used them as a script:

> "…the wet stone offers no purchase to brace against, and your arms feel too heavy to swing a blow."

Told to a character who braced successfully on that same stone a turn later. The other rephrase invented an
event inside a refusal — *"The coin slips through Vark's fingers before he can take it"* — in a call that by
definition changes nothing. An example in a prompt is a script, not an illustration.

---

## Model temperaments, same code

Running identical builds across three models was the most useful diagnostic tool of the session, because
**each model failed somewhere the others did not**:

| | qwen3.5:9b | claude-sonnet-4-6 | gpt-5.4 |
|---|---|---|---|
| where it broke | never named the rule it wanted | copied a rendered annotation back as an id | wrapped the rule in a condition |
| questions asked | 4–5 per run | 12–15 | 0–12 |
| speech | terse, 8–22 lines | 41 lines, very chatty | 23 lines, tight and in-character |
| habit | hammers a failing action | asks before acting | resolves cleanly, rarely refused |

The starkest case: three offers of surrender were made in one qwen run and `accept_surrender` was chosen
**zero** times, while Sonnet accepted twice on the same build — because Sonnet's characters say *"I accept
Skrit's terms"* and qwen's only ever describe the deed. Same code, opposite results, decided by phrasing.
A capability that only works when the character names the rule it wants is not working; it is being carried.

---

## Two things the fiction wanted and the world does not have

1. **Partial terms.** "Your gold and your legs, priest. GO. Rowan stays." Characters repeatedly negotiate
   deals that cover *some* of the people present. The engine's surrender is one offerer, one recipient.
2. **Fear.** Every non-lethal exit in this world is currently a judgement a character makes by reading prose
   about itself, which is why flight needed an explicitly written threshold before it ever happened once.
   Skrit's three-round nerve collapse above is the shape of the thing, arrived at by accident.
