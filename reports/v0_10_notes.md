# v0.10 field notes — what the models actually did

Pulled from live runs under `src/ModelsAndMonsters.Web/runs/` and one scratch smoke-test run this session.
Everything quoted here is verbatim; nothing is reconstructed.

---

## A goblin captain buys his way out of a fight he is winning

`20260821-171039Z-7e4ea447`, round 6: the battle state at the moment reads `Vark (unhurt), Skrit (lightly
wounded)` against `Rowan (badly wounded), Elara (barely standing)` — the goblins are dominating. Vark's own
model call decides, unprompted, to offer his purse for safe passage anyway:

> **Vark's own reasoning:** *"I ask the Dungeon Master about the healing draught in the shrine-marked case,
> then offer Elara the purse of gold coins, telling her to take them and let me live."*
>
> **Vark says:** *"Elara, take my purse of coins and let me live."*

This is a direct, word-for-word contradiction of Vark's own character prompt: *"while the fight still looks
winnable you would never stoop to buying your life — that would be weakness in front of the runt... Once you
are badly cut and plainly beaten... pride is worth nothing and you deal."* Nothing in his own reasoning even
mentions his health. Elara accepted. Vark surrendered, unhurt, mid-victory, and there was nothing wrong with
the machinery — the offer/accept protocol worked exactly as designed. The model just reached for the "goblin
buys his way out" beat without checking it against the one condition his own prompt gave him. Diagnosed as a
small-model instruction-following limit, not a bug — the conditional trait lived in one paragraph of a long
prompt, competing against a much simpler, always-available narrative move. Left as-is, on request: too funny
to fix.

## The knight who would rather die guarding a healthy friend than drink his own wine

Same run, same knight: across 11 successful turns, Rowan spent 7 of them on `guard-ally`, protecting Elara —
including one moment where Elara was at full health, Rowan himself was down to 2, visibly scared, and
carrying an unused Flask of Strong Wine that would have restored 3 of his own health. He guarded her anyway.
Rowan's own `Fears` field lists exactly one thing: *"Failing to protect a companion."* Nothing about dying,
losing, or ever needing a drink. (The fix for the missing counter-pressure this exposed is in `v0_10_issues.md`.)

## The story that refused to declare a winner it didn't have

A scratch smoke-test run (`20260821-193307Z-ea299467`, local Ollama, `qwen3.5:9b`, 6-round cap) hit the round
limit with all four combatants still standing. Rather than invent a resolution, the model's own account —
grounded in nothing but the engine's actual record — ended the fight exactly where the record did:

> *"With Rowan's guard finally spent after absorbing a barrage of attacks meant for his partner, the
> encounter hung suspended mid-fight, no victor declared and no surrender spoken as the lantern light danced
> wildly on the dripping walls."*

The harness's own deterministically appended Ending agreed, independently: *"The conflict remained unresolved
when observation stopped after 6 round(s). Round limit reached (6 rounds). Still standing and still
fighting: Rowan, Elara, Vark, Skrit."* Two completely different mechanisms — one a model asked not to guess,
one code that never asked the model anything — landed on the same honest answer. The same story also
narrated Rowan's guarding habit faithfully, unprompted: *"he took this stance three times in quick
succession"* — the model reporting back, accurately, on the exact behavioural quirk noted above.

## A degenerate story that briefly switched languages, then recovered on its own

`20260821-175615Z-8cee99e9`: mid-meltdown (see `v0_10_issues.md` #3), the Encounter Summariser's Backstory
section drifted from an unpunctuated cascade of English abstractions into several sentences of Mandarin —
unprompted, mid-paragraph, on a request that never mentioned Chinese anywhere — reading roughly as a
philosophical monologue on mercy being cruelty to oneself, then further degraded into two-letter
abbreviations, Roman-numeral-style tokens, and Chinese numeral/calendar characters. And then, with no
external correction of any kind, the very next section — Setting — came out as a perfectly ordinary,
well-formed paragraph of scene description. Whatever had gone wrong resolved itself the moment the model
moved on to a new heading.

## Three attempts, three flavours of gibberish

Worth reading `v0_10_issues.md` #3 as its own small case study in how a single tuning knob (`RepeatLastN`)
produced entirely different failure shapes at three different values: a verbatim-repeated ~1000-token
sentence at the default; an endless, punctuation-starved cascade of increasingly obscure English vocabulary
at the maximum; and the English-into-Chinese-into-symbols collapse above at a middling value. None of the
three looked anything like the other two.
