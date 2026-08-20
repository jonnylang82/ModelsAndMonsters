# v0.8 field notes — what the models actually did

Pulled from the run traces in `src/ModelsAndMonsters.Web/runs/20260820-*`. Everything quoted here is
verbatim from a real run; nothing is reconstructed. Three models drove v0.8: `qwen3.5:9b` on Ollama,
`gpt-5.4`, and `claude-haiku-4-5`.

---

## The goblin who mugged his own captain

The best emergent moment of the release, and nobody wrote a rule for it.

In `20260820-154542Z-185858ad`, Skrit — the warren's last grunt, whose persona wants coin — decided the
easiest purse in the room belonged to **his own captain**. He attacked Vark on four consecutive rounds:

> r5 *"I use my Dirty Strike on Vark — a kick to his knee followed by a hard thrust of my crude Spear into
> his shoulder — and shout at him to drop the potion before he takes another."*
>
> r6 *"I thrust my Crude Spear at Vark's legs to knock him off balance, and shout for him to drop the potion
> or hand over one of his purses."*
>
> r7 *"...trying to knock him off balance so he can't defend properly and I can grab one of his purses."*
>
> r8 *"I sweep my Crude Spear low at Vark's legs to make him stumble in the ankle-deep water and try to knock
> one of his purses from his belt."*

He missed all four times. Then in r9 he turned on Rowan instead and immediately started landing blows.

Two things worth noticing. First, **the engine has no concept of a team**, so a goblin attacking a goblin is
just an attack, and this resolved as ordinary combat — the friendly fire was a *persona* outcome, not a bug.
Second, look at what he keeps asking for: *knock a purse from his belt*. There is no disarm and no
knock-loose action, so the engine resolved the attack and silently dropped the interesting half of the
intent. **Character intent is consistently richer than the action set**, and the harness gives no signal
when the surplus is discarded.

## Skrit does not recognise his own purse

Also from that run, after the v0.6 qualifier work made every purse distinguishable:

> **Skrit:** *"The runt's purse is on the floor! Take it, you fool, before I do!"*

Skrit **is** the runt. The qualifier `(the runt's)` is how the state block labels his own coin, and he read
it straight back out as though it belonged to someone else. The fix that stopped four identical purses being
indistinguishable handed the models a new way to be confused — in the fiction rather than the machinery,
which is the better failure, but worth knowing.

The same qualifier surfaces, correctly, in DM narration:

> *"Elara stoops over the fallen Skrit and lifts the Small Purse of Gold Coins (the runt's) from his body,
> now holding it in plain sight while the corpse remains empty."*

## Surrendering to one enemy while threatening the other

`20260820-190215Z-5f73ad04`, Vark, in a single breath:

> *"Elara, take this purse! I give you all of it and my life for mine — just let me walk away! But Rowan...
> put back what you stole or die trying!"*

A concrete offer to the one named recipient the rules require, and an open threat to the other hero, in one
utterance. The negotiated-surrender machinery handled it exactly right — the offer binds only to Elara — but
it is a good illustration of why speech had to become a *declared field* rather than something detected: no
amount of parsing would have split that sentence correctly.

## The character who said the quiet part out loud

From the same run, Skrit's declared speech in r5:

> *"I'll move in now," I mutter to myself, mentally preparing to strike when the time is right.*

He put his **internal monologue** in the `utterances` field, so the whole room heard him announce that he was
muttering to himself. The field is authoritative by design — what is in `utterances` is spoken — so the
harness did precisely what it was told.

## Haiku negotiates before it fights

`claude-haiku-4-5` opened `20260820-204809Z-6803966f` with a parley in **round one, before a single blow**:

> **Rowan:** *"You two have one chance to walk out of here alive. Drop your steel, hand over what you're
> carrying, and you can leave this cellar. Fight us, and you won't."*
>
> **Elara:** *"We mean what we say. You can walk out of here. That's a mercy most wouldn't offer. Take it."*

Two heroes running a coordinated good-cop routine, unprompted. Vark's refusal is worth quoting in full for
the voice:

> *"You walk into* my *mill, make demands of* me*, and expect me to fold like a beggar? I've cut down better
> than you both."*

He folded two rounds later, after taking a serious wound — and the grunt read the room instantly:

> **Skrit:** *"Captain's buying us out! I'm getting the door!"*

Skrit opened the cellar door and escaped **while the surrender was still being negotiated**. That is the
v0.5 exit and the v0.7 negotiation firing in the same round, from a goblin correctly inferring that his
captain's deal did not include him.

## The first clean withdrawal

`20260820-211409Z-9df11a76` produced an outcome classification we had never seen live: **`Withdrawal`** —
both goblins left through the door and nobody died.

> *"Vark bolts for the cellar stairs, yanking the heavy door wide and disappearing into the darkness above,
> the sound of footsteps echoing up and away into the mill."*
>
> *"Skrit bolts for the Cellar Stair Door and wrenches it open, disappearing up the dark stairs into the
> gloom above. The heavy door swings shut behind them with a dull thud."*

Read against the v0.7 notes — where `open_exit` and `escape_encounter` fired **zero times in eight
consecutive runs** — this is the clearest sign that the exit is now a live option rather than a theoretical
one. Across this session it fired in four separate runs.

## Vark reads the mechanics

Haiku's Vark, on Rowan interposing himself:

> *"You think standing in front of her saves her? It just makes you easier to cut."*

That is a correct tactical reading of `guard_ally` — the guard redirects the blow to the guardian — expressed
entirely in-world with no mechanical vocabulary. And later, after Rowan drank the Flask of Strong Wine:

> *"Your wine won't save you twice, human!"*

A taunt built out of an item use the goblin publicly observed. Both are exactly what the knowledge model is
for: he taunts about what he saw, and never about what he could not have seen.

## gpt-5.4 negotiates like a metronome

`20260820-192134Z-9330757c`, Elara across four consecutive rounds:

> r2 *"Throw down the salve and yield, Vark, and you may yet leave here alive."*
> r3 *"Throw it down, Vark, and walk out alive."*
> r4 *"Yield now, Vark, and drop the salve if you want to walk out alive."*
> r5 *"Drop the salve and your blade, Vark, and you live."*

Vark's refusals escalate in step, and they are the better half of the exchange:

> *"You want my salve, girl? Come and take it."* → *"You hide behind him now, girl? Good. I'll gut him
> first."* → *"You'll die for her, then."*

Meanwhile Rowan said a variant of *"Stay behind me, Elara"* on every single turn. gpt-5.4 is consistent to
the point of being mechanical; the demand is barely reworded round to round.

## Model voices, side by side

The same scenario, the same prompts, three very different registers:

| | speech per run | character |
| --- | --- | --- |
| **qwen3.5:9b** | ~22 lines | Short, loud, repetitive. Re-states the same warning for four rounds (*"Skrit! The shrine-marked one holds the draught — don't mix it up with this crate!"*). Loops on an obsession — one run had Vark defending a supply case he never opened. |
| **gpt-5.4** | ~17 lines | Terse and clipped (*"Come on, then."* / *"Behind me, Elara."*). Escalates deliberately, almost scripted. Never wastes a line. |
| **claude-haiku-4-5** | ~10–21 lines | Longest and most varied. Argues, appeals, reads the room. The only model that opened with diplomacy and the only one whose grunt made his own decision. |

One qwen habit worth flagging: **hearsay propagating as fact through dialogue.**

> **Vark:** *"Skrit! That shrine-marked case holds a healing draught — don't touch it!"*
> **Skrit (next round):** *"Elara, back away from that shrine-marked case! Vark says it's for medicinal
> supplies — don't touch it or you'll get us all killed!"*

Skrit correctly attributes it (*"Vark says"*) — which is the knowledge model working as designed: he heard it,
he did not see it, and he says so.

---

## Two things the models got wrong that the harness let through

**The Dungeon Master narrates the arithmetic.** Defend outcomes are consistently narrated with the number
attached:

> *"Elara sets her feet in the ankle-deep water and braces behind her iron mace, covering herself rather than
> striking, so that the next blow to land will deal less damage."*
>
> *"...covering herself as the next blow that lands will deal one less damage to her."*

The machinery-leak detector catches *vocabulary* ("the world cannot resolve", "not supported") and this slips
past it, because every word is in-world. It is still the mechanic read aloud to the reader. Not fixed in
v0.8 — recorded here because it is the kind of thing that is invisible until you go looking at narration in
bulk.

**Rowan's pronouns drift within a single run.** No persona in `scenario.json` states a gender for anybody, so
each model picks — and does not stay consistent:

> *"Rowan uncorks the flask with a sharp twist and drinks deep, the strong wine burning down **her** throat.
> Color returns to **her** face..."*  (r4)
>
> *"Rowan grips **his** Longsword..."*  (a different run, same character)

and it reaches dialogue too — *"Rowan drank from that flask and her wounds closed—I saw her"*. Either the
scenario should say, or it should be deliberately neutral; leaving it unstated means the fiction quietly
contradicts itself. Not fixed in v0.8.
