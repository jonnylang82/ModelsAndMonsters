# Models & Monsters — v0.1 Field Notes

Observations from the v0.1 build sessions: one hero (**Aric**, brave/direct sell-sword) versus one
cornered goblin (**Grik**, cowardly/spiteful) in a single bare stone room, resolved through an LLM
Dungeon Master. These are the moments that were surprising, revealing, or just funny — pulled from the
run traces, verbatim.

The engine here is tiny on purpose: the only things it can resolve are *hit someone with the weapon
you hold* and *use an item on yourself*. Almost everything interesting below is the models pushing
against that wall.

---

## 1. Personality actually landed

The single most encouraging finding: the two characters behaved like different people, and it matched
their prompts, without either ever being told a "combat style".

**Grik is a coward and it shows.** Cornered and hurt, the goblin almost never fought cleanly — it
schemed, fled, and grovelled:

> *"I try to bargain with Aric, offering to share ancient secrets he might not know, hoping he will
> spare me out of greed or curiosity."*

> *"I fall forward onto my knees, clutching my side in pain, and try to crawl backward toward the door
> while pleading for mercy or trying to slip past him unseen."*

> *"I try to thrust my rusty axe toward Aric's face again, this time with less strength but all of my
> spite in it. I'll go for his eyes or mouth if the steel will let me."*

That last one is the persona ("cowardly, spiteful and opportunistic") rendered almost perfectly: a
weak, nasty, desperate jab. Nobody wrote "go for the eyes."

**Aric is boring on purpose, and that's correct.** His persona says he doesn't gloat and doesn't waste
words, and he doesn't:

> *"I swing my sword at the goblin's head, trying to end this quickly."*

Two clean fighters would have been a dull transcript. One coward and one professional made it a story.

---

## 2. The goblin is a tiny inventor

Denied any mechanic but "swing your axe," Grik kept trying to *engineer* an advantage out of the room.
My favourite is the goblin reaching for improvised fire:

> *"I slam my axe down hard on the stone floor between us, trying to spark a fire or create some danger
> that will make Aric think twice about staying here."*

A MacGyver goblin striking sparks off flagstones. Also in the "clever things the engine can't do yet"
pile:

- *"I hurl the rusty axe at Aric's head, hoping the blow will stagger him."* (throwing a weapon)
- *"I push the toppled bench into Aric's path, hoping the impact will force him to stumble."*
- *"I slip silently past Aric and open the door to the dim corridor, hoping to escape while he is distracted."*
- *"I leap sideways... and strike the brazier with my axe to create a distracting flare."*

This is exactly the "useful data" the design hoped for. Tallying Grik's rejected intents, the engine's
missing verbs practically name themselves: **flee/move, dodge/defend, shove/trip, throw, intimidate,
negotiate.** The goblin is basically filing feature requests.

---

## 3. The two loops nobody scripted

Two emergent behaviours came purely from the gap between what a character *wanted* and what the world
*could resolve* — and both were oddly affecting.

**Grik trying to die.** Mortally wounded but not yet at 0 HP, the goblin repeatedly tried to simply
give up — and the harness, before `end_turn` existed, kept treating "lie down and stop" as an action
attempt and asking it to try again:

> *"I accept my fate and remain motionless, knowing there is nothing left to do."*
> *"I accept my fate and remain still, knowing there is nothing left to do."*
> *"I accept my fate and let myself rest, knowing there is nothing left to do."*

A goblin trying, and failing, to be allowed to die, round after round. This is what drove two real
features: the `end_turn` tool (a character can now *choose* to do nothing) and stalemate detection (the
run ends instead of grinding to the round cap while two characters refuse to move).

**Aric trying to go home.** The mirror image, once Grik was dead: the job was done and the hero just
wanted to leave — but there is no "leave the room":

> *"I carefully step over Grik's body and continue towards the door."*
> *"I carefully lower my sword, pick up the goblin's rusty axe, and head towards the door."*

Rejected every time. The victorious hero, standing over the corpse, politely trying every phrasing of
"can I go now?" and being told no.

---

## 4. Things that broke, that were funny

Every one of these became a fix, but they were good while they lasted.

- **The goblin attacked itself.** An identity-confusion bug had Grik describe itself by Aric's name
  *and* Aric's weapon; the DM faithfully translated it, and the engine received
  `attack_character(attacker: Grik, target: Grik, weapon: Iron Sword)` — the goblin swinging a
  borrowed sword at its own head. The engine caught it (`TargetIsSelf`). Fix: the harness now forces
  the acting character as the attacker.

- **The DM kept crediting the wrong fighter.** Grik would land a blow — engine confirms *Grik hit
  Aric* — and the DM would narrate *"Aric's Iron Sword flashes as he strikes Grik."* A model with a
  strong "the hero does the hitting" prior, rewriting the goblin's victories as the hero's. Fixed by
  naming the actor, loudly, in the narration prompt.

- **The DM measured a world with no distances.** Asked how close the goblin was, the DM confidently
  reported *"about 10 feet away"* — in a world that has no coordinates, range, or movement at all. It
  invented a tape measure.

- **The DM went quiet because it was thinking too hard.** Pointed at a reasoning model, the Dungeon
  Master spent its *entire* output budget on a private `<think>` block and emitted no actual prose, so
  the transcript just read *"(The Dungeon Master said nothing.)"* A DM lost in thought.

- **The DM broke character to read the rulebook aloud.** The most persistent immersion-breaker was the
  DM explaining the *mechanics* to a character in-world:

  > *"You can't dash towards the brazier, Grik. You can only try to strike Aric or use an item on yourself."*
  > *"Slamming your axe into the floor does not strike another character, and this world can only resolve a direct weapon strike or an item use."*

  A goblin being told, mid-lunge, the list of supported API calls. (Reined in by prompt, though the
  smaller/reasoning models still slip occasionally.)

---

## 5. When the prose worked, it really worked

The counterpoint: with the actor named and misses/glancing wired in, the RNG made combat read well.
A clean miss:

> *"Grik's axe swings wide against Aric, the heavy blade missing its mark and clattering harmlessly off
> the stone wall as Aric stands unharmed save for his minor cut."*

A solid hit vs. the same goblin, one exchange later:

> *"Aric drives his Iron Sword home into Grik's chest, the steel meeting flesh with a sickening crunch."*

The engine only said "roll 97 vs hit chance 70 → miss" and "3 damage, badly wounded"; the DM did the
rest, and kept the numbers invisible.

---

## 6. What v0.1 taught us

- **A one-line persona is enough to differentiate behaviour.** The coward and the professional diverged
  immediately and consistently. That's the whole thesis of the project, and it held on the first
  scenario.
- **A deliberately tiny action surface is a feature, not a limitation.** The rejected actions are a
  ranked backlog of what to build next, written by the players themselves. Movement and social actions
  (flee, shove, intimidate, negotiate, surrender) dominate the list.
- **Most "bugs" were the DM model imposing narrative common sense** the world doesn't have yet —
  distance, the hero landing the blows, someone being allowed to give up or leave. The friction between
  the models' storytelling instincts and the engine's flat little rules is where all the interesting
  behaviour — and all the interesting work — lives.

*Every quote above is verbatim from a run trace in `runs/`. Reproduce any of them by setting
`ModelsAndMonsters:Harness:Seed` to the master seed recorded in that run's `run.json`.*
