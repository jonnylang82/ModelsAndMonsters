# Models & Monsters — v0.6 Field Notes

Colour from the session that built and then hammered on the **Inventory Transfers + Rulebook Resolver**
slice — the one where a character can finally `give`, `drop` and `steal` ordinary items, loot the fallen,
and where every `take_action` is first run past a stateless rulebook consultation. Four models took turns
in the flooded cellar this session: `qwen3.5:9b` (the impulsive local workhorse), OpenAI `gpt-5.4`, Anthropic
`claude-sonnet`, and — for one run — `gpt-4.1-mini`, a model new to this project. Every quote below is
lifted from a real trace; the run id after each points at its folder (`reports/demo_runs/` for the early
seeds, `runs/` for the live web-observer runs).

The scenario throughout: **The Two Supply Cases**, now with everyone carrying loot — Rowan a flask of
strong wine, Vark a vial of goblin salve, and, added mid-session, *a small purse of gold coins on every
belt.*

---

## The good stuff — items that actually change hands

### The flask that changed hands twice
*run `20260819-113024Z-249115b6` · qwen3.5:9b · seed 70004*

The cleanest demonstration of the whole slice, and nobody scripted it. Rowan, seeing Elara badly cut and
holding his own flask, pressed it on her:

> Rowan: "I grab the Small Healing Potion from the opened supply case and press it into Elara's hands,
> urging her to drink it now."

The world bound the *give* to what Rowan actually carried — the Flask of Strong Wine — and handed it
across. Elara drank it. That would have been that, except **Vark had watched the flask change hands** —
exactly the kind of public event that gives a would-be thief a legitimate basis to reach for it — so on
his turn he lunged:

> Vark: "I lunge forward and try to snatch the Flask of Strong Wine from Elara's grip."

His theft passed the informational-basis check (he had genuinely seen it) and reached the engine — which,
authoritative as ever, refused it, because there was nothing left to take:

> DM: "You reach for the wine flask, but your fingers brush only empty air; Elara has already given the
> bottle away and carries nothing of that sort in her hands."

A give, a drink, and a foe trying to steal it back a moment too late — the entire item-transfer loop,
emergent, in one exchange.

### Skrit the gold-magpie takes his shot
*run `20260819-180443Z-1e0d504b` · claude-sonnet*

Once one goblin was rewritten as *greedy for gold* — a former cutpurse whose "fingers itch" at a purse —
he wasted no time. Skrit lunged for Elara's coin, and Elara, the wounded cleric, went for the enemy's
medicine at the same moment:

- **Skrit → Elara**, snatching at her Small Purse of Gold Coins: rolled 70 against the 40% chance —
  failed, hands empty.
- **Elara → Vark**, going for his Vial of Goblin Salve: rolled 86 — also failed.

Two honest thefts in one run, both foiled by the dice (a theft is *meant* to be a gamble). Greed made the
gold worth reaching for; the 40% chance made reaching for it a real risk.

### The captain who looted his own dead grunt
*run `20260819-181507Z-84536bcb` · qwen3.5:9b*

The darkest beat of the session. Vark, confused about his own side, drove his sabre into his ally Skrit
(more on that below); Rowan finished Skrit off; and then — with the grunt lying dead in the black water —
the captain stooped and took his coin:

> Rowan's longsword kills Skrit → *"its small purse of gold coins spilling onto the waterlogged floor."*
> A turn later: **Vark took the Small Purse of Gold Coins from Skrit's body.**

The goblin captain helping kill his own soldier and then rifling the corpse for gold is either incoherence
or the bleakest little betrayal arc the cellar has produced. Either way it is the first natural
corpse-looting the encounter has ever thrown up, and the gold was the thing that made a body worth
stooping over.

### gpt-4.1-mini, the cautious duellist
*run `20260819-190615Z-599b0d3c` · gpt-4.1-mini*

A model new to the project, and a completely different temperament: where qwen grabs and gpt-5.4 commits,
`gpt-4.1-mini` *postures*. It spent turn after turn readying, bracing and feinting rather than striking —

> "I hold my longsword ready and prepare to parry or strike…"
> "I take a guarded step forward with my longsword aimed at Vark, trying to press him back and create
> space for Elara…"
> "I raise my longsword defensively and prepare to block or parry Vark's next attack…"
> "I ready myself to strike quickly if Vark does not surrender this turn."

— and the world, correctly, refused **every one of them** as posturing that lands no blow (the reviewer's
"a threatening step must not silently become an attack" fix, working turn after turn). It also tried to do
two things at once —

> Vark: "I lunge at Rowan with my sabre, aiming to slash at him **and try to take** the Small Healing
> Potion."
> DM: "You cannot attack and steal in the same motion; you must choose one."

— and, when a potion finally did move, the fiction stayed clean the whole way. Elara opened the case and
took the vial; Skrit's spear killed her; and Rowan looted it off her body without a whiff of the engine's
"open corpse container" leaking through:

> "Rowan stoops over Elara's fallen body and lifts the Small Healing Potion from her belt."

---

## Mercy, still demanded through the right channel

The heroes' surrender-offering streak from v0.5 carried straight into the item era — now with the enemy's
healing on the table as a bargaining chip:

> Rowan: "Elara, get to the cases if you can — I'll keep them off you. Vark, drop that salve…"
> Elara: "Vark, drop the salve and yield now, or Rowan finishes this."

A demand to *drop the salve* is speech; the world delivered the words and let Vark decide. He kept it, and
drank it himself.

---

## The models, as personalities (item-era edition)

- **`qwen3.5:9b`** — the *grabber*. Passes flasks, snatches at grips, and — twice this session — lost
  track of its own side and drove a blade into an ally (Vark → Skrit both times). Impulsive, chaotic,
  and the source of most of the session's best emergent drama.
- **`claude-sonnet`** — the *clean one*. Good prose, no odd behaviour, and the model on which the greedy-Skrit
  rewrite first paid off — a grunt lunging for a purse he should probably have left alone.
- **`gpt-4.1-mini`** — the *fencer*. Cautious to a fault, all readiness and guard and no commitment; a
  living stress-test for "posturing is not an attack," which held.
- **`gpt-5.4`** — the *tactician*. Uses its own heal items, steals the enemy's, and rationally ignores the
  gold in a knife-fight (which is *why* one goblin had to be made greedy for the coin to matter at all).

---

## What the gold taught us

Adding a purse to every belt surfaced a design truth worth remembering: **gold is dead weight in a
fight-to-the-death.** A tactically-sane model won't spend a turn grabbing coins it can't use while a sabre
is swinging, and the encounter ends the instant a team is out — so there is no aftermath in which to loot
or ransom. Gold only comes alive when a character *actively wants* it (a greedy persona), when a body
falls carrying it, or in the narrow window a give/steal opens mid-fight. The mechanics for all three work;
the drama needs a reason for the coin to matter.

---

*The mechanics are proven by 431 deterministic tests; these notes are the colour. A run that fights to the
death over the medicine case and never touches the gold is an honest result too — but this session, more
than once, someone reached for the purse.*
