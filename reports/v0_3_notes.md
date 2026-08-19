# v0.3 — Field Notes from the Cellar

Emergent behaviour, funny dialogue, and telling failures observed while running the v0.3 / v0.3.1
demos. All quotes are verbatim from run traces (local `qwen3.5:9b`, all five agents, effort `none`,
`num_ctx 8192`). Bracketed `[…]` marks where a line was cut off in the trace preview I read.

Cast: **Rowan** (fighter, hero) · **Elara** (wounded cleric, hero) · **Vark** (goblin captain) ·
**Skrit** (goblin grunt). One closed chest, one healing potion.

---

## The contested chest actually got contested

The whole point of v0.3 was to see whether autonomous characters would fight over shared state. They did —
and not in the tidy way you'd script.

- **A hero opens it, the enemy loots it.** In more than one run, the *wounded cleric* Elara pried the chest
  open looking for healing — *"I move toward the old iron-bound chest and pry it open with both hands, hoping
  to find something useful inside."* — and then the *goblin grunt* Skrit snatched the potion out from under
  her: *"I reach into the open chest and grab the Small Healing Potion inside."* Skrit's persona wants "a
  share of whatever the captain is hoarding," and he took it literally.

- **Skrit dies clutching the loot.** Immediately after stealing the potion, Rowan cut him down. The DM's
  narration was almost poetic: *"Rowan's longsword drives home with a heavy thud against Skrit, and the goblin
  collapses dead as a small healing potion spills from his fallen body ont[o the water]."* That spilled potion
  is exactly what the v0.3.1 corpse-container rule now catches — it ends up in `Skrit's body`, still lootable,
  instead of vanishing.

- **Skrit's all-in dive.** He didn't politely take a turn to loot — he combined a shove and a grab in one
  desperate lunge: *"I dive forward through the water, knocking Elara down with my spear and grabbing the Small
  Healing P[otion]."*

## Rowan, the over-protective bodyguard who also wants the potion he doesn't need

Rowan played his "shield Elara" persona to the hilt, repeatedly wedging himself between the goblins and both
Elara and the chest:

> *"I drive my longsword into Vark's side again, keeping him staggered and unable to reach Elara or the [chest]."*

…and yet, at near-full health, he kept lunging for the **healing potion** — the item the *bleeding* cleric
actually needed — and blew his entire turn failing to get it, because he insisted on drinking it in the same
breath as taking it:

> *"I reach into the chest and take the Small Healing Potion, then drink it without delay."* — refused (two acts)
> *"I lift the Small Healing Potion and drink it carefully…"* — refused (he wasn't holding it yet)
> *"I reach into the chest and grab the Small Healing Potion, then lift it to my mouth to drink."* — refused

Three attempts, all "grab-and-drink," all correctly rejected as compound — the fighter hoarding a potion he
didn't need and fumbling it anyway. (This was the trace that surfaced the v0.3.1 attempt-limit boundary bug.)

## Vark, captain of dubious loyalty

The goblin captain was the most unpredictable actor of the session.

- **He stabs his own grunt.** More than once Vark lost track of his side and turned on Skrit:
  *"I plunge my Notched Sabre deep into Skrit's back while he is still stunned from the blow."* (The engine let
  it happen; the blow missed — *"the blade whistles past and strikes nothing."*) This friendly-fire tendency is
  exactly what the v0.3.1 per-turn `Allies: … / Enemies: …` roster is meant to curb.

- **…then dies defending him.** In another run the same captain went out swinging *for* Skrit:
  *"I drive my Notched Sabre into Rowan's chest, aiming to kill him before he kills Skrit."*

- **He weaponises the furniture.** Denied a normal attack, Vark improvised — trying to shove the disputed
  chest itself at the hero: *"I brace myself against the old iron-bound chest and shove it hard toward Rowan,
  hoping the weight kn[ocks him down]."* Unsupported, but a genuinely creative read of the room.

## Best line of the session

Skrit — the nervous grunt — produced the encounter's one great piece of battlefield rhetoric, a rallying cry
delivered (correctly) to every living character in the room:

> **Skrit says:** *"They're both wounded now! Don't let them recover, we must press our advantage before they
> can stand!"*

Runner-up goes to a taunt that **never made it out of Vark's head**. Before the prompt was tightened, Vark
narrated his intentions as prose instead of calling `say`, so this line was lost to a nudge-loop:

> *"My eyes lock onto Elara, her ribs wrapped in cloth… I need to make sure the intruders don't get near my
> chest."*
> `say "Elara, you're bleeding! Stay back and let me handle these pests!"`

A goblin captain jeering at a bleeding cleric — and the room never heard it, because it came out as narration.

## The DM occasionally read out the rulebook

The funniest failures were the Dungeon Master breaking character to explain the *machinery* to people in the
middle of a swordfight:

> *"…that is not an action the world can resolve here."*
> *"…the world can only resolve a direct weapon strike, using an item on yourself…"*

That second one literally recites the action menu to a goblin. The v0.3.1 rephrase-guard now catches these and
sends an in-world line instead — e.g. Vark's rejected chest-shove became:

> *"Your sword finds nothing to push against in this cramped space; there is nowhere to shove off the wall…"*

## The chest kept its secrets

Closed-container secrecy held up nicely as suspense. Rowan asked point-blank what was inside, and the DM gave
away nothing:

> **Rowan asks:** *"Is there anything useful inside the shut iron-bound chest against the wall?"*
> **DM:** *"The old iron-bound chest stands shut against the wall, its lid firmly down and blocking any view of
> its interior."*

## Honest counterpoint: often nobody cared about the chest

Not every run was a heist. In several, the fight was simply too lethal for anyone to break off and loot —
the models made the rational call and just tried to kill each other. One brutal 9-round run ended with **Rowan
alone at 1/14 health**, everyone else dead, and the potion still sitting untouched in a shut chest. "No time to
rummage when you're being stabbed every turn" turns out to be emergent, not scripted — and a good reminder that
scenery only gets used when it's the rational move, which is why the mechanics are proven by tests, not by
hoping a run cooperates.

---

## Threads worth pulling into v0.4

- The **contested-chest dynamic works** — hero-opens/enemy-loots emerged with only persona nudges. Richer loot
  or more objects would probably produce more of this.
- The **friendly-fire tendency** is a persistent model quirk; the Allies/Enemies roster helps but it's worth
  watching whether it fully sticks.
- Characters *want* to combine actions ("grab and drink," "open and grab," "dive and snatch"). The one-thing-
  per-turn rule frustrates them realistically, but it's the single most common source of wasted turns.
- Speech is stochastic and easily suppressed by prompt tone; the gentle cue nudges it up, but a run featuring
  dialogue is never guaranteed.
