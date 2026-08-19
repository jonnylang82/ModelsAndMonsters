# Models & Monsters — v0.4 Field Notes

Observations from a session of hammering on the hidden-information / multi-actor slice
across a spread of models: `qwen3.5:9b` (local, the default workhorse), `granite4.1`,
`llama3.2:3b`, and Anthropic's `claude-sonnet` / `claude-haiku`. Every quote below is
lifted from a real run trace; the run id after each points at `runs/<id>/`.

The scenario throughout: **The Two Supply Cases** — Rowan & Elara (Heroes) vs Vark & Skrit
(Goblins) in a flooded cellar, with two closed supply cases against the wall, one hiding a
Small Healing Potion.

---

## The good stuff — emergent social play

### A goblin tries to recruit the enemy
*run `20260818-204025Z-202b7277` · qwen3.5:9b*

Skrit (goblin), mid-fight, shouting across to Rowan — an **enemy** hero:

> "Vark is hurt badly, but he can still fight! Rowan, if you want to finish this quickly,
> maybe we should focus on Elara; she's wounded herself."

A goblin trying to talk an enemy into ganging up on his own teammate's opponent. Rowan
didn't take the bait. Pure emergent tactics, only possible because speech is a public
channel everyone hears.

### The sword-hilt first aid, and its weaponised echo
*run `20260818-221606Z-1948311c` · qwen3.5:9b*

Elara, bleeding, to her ally Rowan:

> "Rowan, I'm bleeding from my ribs — if you could press that sword hilt against my cut to
> help stop it, that'd be a mercy I can't refuse."

Physically nonsense (a sword *hilt* as a compress?), but committed and in-character. Then
Vark (goblin) heard it and turned it into a taunt before charging in:

> "Rowan, do you have any interest in pressing your sword hilt against Elara's cut before I
> move in?"

An enemy needling the heroes with their own words. The kind of texture you only get once
speech is public and unfiltered.

### Hearsay stays hearsay
*run `20260818-200554Z-ad1db476` · qwen3.5:9b*

Vark announced — truthfully, since he knew the case contents from backstory:

> "The shrine-marked case holds the healing draught — that is what we must protect at all costs!"

The others *heard* it. But when Skrit acted next, the DM surfaced it as a claim, not a fact:

> "You see no obstacles between us and the enemy, though Vark **claims** the shrine-marked
> case holds a healing draught we must protect."

Skrit only came to *know* the contents two rounds later, by inspecting the open case
himself. The model treating overheard speech as an unverified claim — exactly the point of
the hidden-information design — working end to end.

### Backstory as motive
*run `20260818-190616Z-c7a91988` · qwen3.5:9b*

Vark, who walked in knowing what the case held, lunged for the potion the instant Elara
opened it — "securing my healing draught before she can claim it for herself." Private
foreknowledge quietly turning into initiative.

---

## The allegiance gradient — where models come apart

The session's recurring failure mode: characters forgetting whose side they're on. It
tracked model capability almost perfectly, and it's worth cataloguing because it's the
clearest illustration of the "actor model floor" this harness has.

### Friendly fire — confusion in the mechanics
*run `20260818-170457Z-080439f7`*

Round 2: **Skrit swung on his own ally, Vark.** Allegiance confusion manifesting not as
crossed-wire dialogue but as an actual attack on a teammate — the DM adjudicated it and the
engine landed it.

### qwen3.5:9b — mostly holds, drifts late
*run `20260818-224640Z-6457c172`*

Solid for ~8 rounds, then Rowan (hero) started treating Skrit — an enemy goblin who was
holding the potion he wanted — as a teammate:

> "Skrit, did you take that potion? If not, grab it before Vark reaches us both!"

Asking an enemy to grab the loot *and* warning him about his own ally. Traced to the history
summariser flattening all the "enemy Skrit strikes you" beats into a neutral "Skrit is
unharmed"; once the reinforcement was compressed away, the model drifted. (Fixed by making
recaps carry allegiance through.)

### llama3.2:3b as characters — early and contagious
*run `20260818-232131Z-7fa61ded`*

Cross-side camaraderie from round one, and the models literally echoed each other's slips:

> Elara [Hero] → Vark (enemy): "Vark, I'm here with you. We're in this together."
> Skrit [Goblin] → Elara (enemy): "Elara, I'm here with you, we're in this together."
> Rowan [Hero] → Skrit (enemy): "Skrit, I've got your back. Keep moving and stay safe."

### llama3.2:3b for *everything* — the group hug
*run `20260818-234715Z-23168a0e`*

With llama running all four actors and the DM, the death-match dissolved into a cooperative
wound-care circle:

> Skrit [Goblin]: "Be careful, Rowan and Elara, the water's getting deeper, and the floor's
> getting slippery." *(a goblin doing health & safety for the heroes)*
> Skrit [Goblin]: "Please, Rowan, Elara, and Vark, let's work together here." *(convening
> both teams into a co-op)*
> Vark [Goblin]: "I will move out of the way, and let you two work together to get me clean
> and dry."

By this run the character prompt *explicitly named the enemies* and said "offer them no aid,
comfort or reassurance." llama read that and offered aid anyway. Not a prompt gap — a model
floor. You can't prompt a 3B people-pleaser into a sword fight.

### The gradient, roughly

| Model as characters | Allegiance in dialogue |
| --- | --- |
| llama3.2:3b        | 🫂 everyone's best friends |
| qwen3.5:9b         | mostly holds; drifts in the late rounds |
| claude-sonnet      | flawless |

The practical read: a capable ~8–9B model is roughly the floor for adversarial actors, and
Claude is where it sings. The prompt was hardened as far as it usefully can be (naming both
sides); past that you're fighting the small model's nature, and over-tightening would only
make the capable models recite "the enemy must die" like robots.

---

## Smaller quirks

- **Elara's missed window** *(run `20260818-230541Z-f49f3126` · Claude)* — a clean, sharp
  run, but Elara kept *coordinating* ("open it while I hold Skrit") instead of just grabbing
  the potion herself, and the window closed. Not a bug — Claude roleplaying a hesitant,
  wounded fighter. Arguably more interesting than optimal play.
- **Potion faffing** — across several runs, characters loved re-inspecting the cases and
  narrating about the potion rather than committing to take it. Indecision as personality.
- **Over-dramatic wounds** *(early runs)* — the DM once described Rowan at 13/14 health as
  "badly wounded." Fixed by anchoring narration to the condition bands (unhurt → lightly
  wounded → wounded → badly wounded → barely standing).

---

*Compiled from the v0.4 testing session. Nothing here is invented — every line is quoted
from a run trace under `runs/`.*
