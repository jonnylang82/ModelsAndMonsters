# 🎲 Models & Monsters 🧌

Original post: <https://spencerclark.dev/blog/models-and-monsters/>

## Council RPG fork: local setup

This branch adds a first-class LM Studio provider for the Council RPG adaptation while preserving the
original provider abstraction. The default configuration connects to LM Studio at
`http://localhost:1234/v1` and uses `qwen/qwen3.5-9b` with an 8,192-token context boundary.

LM Studio must have its local server running. The connector sends `reasoning_effort: "none"` for the default
profile, preventing hidden reasoning from consuming the response allowance before narration or a tool call is
emitted. Per-model reasoning-budget workarounds in the LM Studio UI are therefore not required by this fork.

Run the observer with two terminals:

```powershell
dotnet run --project src/ModelsAndMonsters.Web/ModelsAndMonsters.Web.csproj
```

```powershell
cd src/ModelsAndMonsters.Web/client
npm run dev
```

Then open <http://localhost:5173/>. Ollama and the hosted providers remain available through configuration.

### Private phone access (prepared; device setup pending)

The web host can now serve the built interface and API together at `http://localhost:5170/`.
Browser requests are relative to that address, including the live event stream and guest controls.
The development interface on port 5173 proxies `/api` to the same backend.

Build in this order from the repository root, then start the web host:

```powershell
npm --prefix src/ModelsAndMonsters.Web/client run build
dotnet build src/ModelsAndMonsters.Web/ModelsAndMonsters.Web.csproj
dotnet src/ModelsAndMonsters.Web/bin/Debug/net10.0/ModelsAndMonsters.Web.dll
```

Use the local .NET 10 executable described below if needed. Keep the PC awake, LM Studio running,
and the web host running. Only the built client goes into `wwwroot`; configuration, prompts and run
files are outside the public static-file directory.

For off-site phone access, install Tailscale on both devices and sign into the same personal tailnet.
After Windows installation approval and PC sign-in, inspect `tailscale serve status` for existing routes,
then configure **Serve**, not public **Funnel**:

```powershell
& 'C:\Program Files\Tailscale\tailscale.exe' serve --bg http://127.0.0.1:5170
```

Follow any HTTPS-enablement prompt and open the resulting private HTTPS address on the phone with
Tailscale connected. Do not forward router ports or expose LM Studio, Vite, or the repository.
The game has no separate user authentication: anyone allowed to reach this service through the tailnet
can view and control it. Keep access restricted to trusted personal devices; this is not a public-hosting setup.
Tailscale's background route does not itself start the game or LM Studio after reboot.

Verification: 1,141 tests passed; a 390px browser viewport loaded portraits, started a real run,
received live state, reserved Deacon control, and cancelled successfully using one origin with no
page errors or horizontal overflow. Configuration/source/run-file probes returned 404.
Actual off-site phone/HTTPS testing is pending: the Tailscale installer was waiting on Windows
administrator approval while Jon was away. No tailnet sign-in or Serve route has been configured.
Next session: finish installation/sign-in, configure the private route, then test from Jon's phone.

The branch now boots into **The Archive Wakes**, the first Council-native encounter. Nema Mistsong, Mirabel
Tipton, Emma Nightveil, Pipix Thistlegrin and Scott Suncrest are independent hero agents facing two archive wardens. Their
names, equipment, concise behavioural grounding and supported abilities live in
`src/ModelsAndMonsters/scenario_council.json`. Character appearance is separate from game logic: the web client
loads `public/characters/manifest.json`, whose stable character ids point to copied portrait sheets. This lets
future D&D skins and emotion variants replace art without changing the engine or character prompts.

The complete corrected character sheets—including Deacon's future Guest character—are preserved at
`characters/all-characters-corrected.json`. Scenario values translate those sheets into this engine's smaller
mechanical vocabulary; for example, D&D armour class is mapped to the engine's 0–3 damage-reduction scale rather
than copied as literal armour.

### Rulebook-informed spells and encounter tuning — September 2, 2026

The default cap is now **20 rounds**. Characters are no longer instructed to treat everyone outside
their party as a target, and can ask one question before committing to an action. Scott's self-defense
lookup also supplies Guard Ally, so protecting a named companion can be distinguished from self-defense.
An attack doing zero damage now feeds the actual engine result back to the party instead of an invented
wound; harmless criticals no longer generate injury-related morale changes.

New playable options: **Nema — Sleep; Pipix — Healing Word and Faerie Fire; Scott — Divine Favor**.
Everyone can wake a sleeping ally. Healing consumables can be used on companions, spending the user's
item and healing only the recipient. New spell rolls, saves, concentration, expiry and charges are
engine-owned and traced. Ordinary equipment still has no effect unless one is implemented.

Read [the rules adapter](docs/council-rules.md) for the supplied PDF/page references and exact differences
from D&D: single targets, one turn per action, per-spell encounter charges instead of shared slots,
the existing percent-hit/armour system, and unchanged public perception while sleeping. No full D&D
conversion, movement hazards, second room, or campaign persistence is claimed. Context remains 8192 tokens.

Verification: **1,157 automated tests pass**, including 16 new spell/item/routing cases. A real Qwen/LM Studio
one-round smoke run (`20260903-041857Z-e914832c`) completed, generated its trace/report, and exposed all new
spell labels and charges in the browser. It was deliberately capped at one round for testing; the saved
default is 20. Nema used Rally Grunt after an initial misclassification; the character prompt now asks
agents to name a chosen ability directly. Other compound-action and surrender mistakes still occurred.
The new spells' mechanics were exercised by deterministic tests, not all cast in that live round.

Next check: start a fresh 20-round rescue and review spell choices, item use, Scott's protection targets,
repetitive actions, and whether Deacon actually escapes. Better options and more rounds do not guarantee
better model decisions; a full live rescue with these changes is not yet confirmed by Jon.

### Rescue and guest control — September 2, 2026

The current encounter is a single-room rescue. Deacon starts alive and detained, hears the public record,
and cannot act or be attacked while confined. Once neither warden is active (defeated, surrendered, or gone),
the engine unlocks detention. The encounter continues until Deacon escapes alive through the Village Stair,
the rescue fails, or a run limit stops play. Who framed him is an unresolved story hook, not a completed objective.

Jon remains an observer unless he chooses **Take control of Deacon**. Control can be reserved while Deacon
is detained. At his next decision after release, enter an action and optional spoken words, pass, or choose
**Return to AI**. Both controllers use the same character conversation, DM adjudication and engine. An AI
decision already in flight finishes before takeover; cancellation and returning control release any input wait.
Guest commands have per-request identifiers so stale or duplicate submissions cannot take a later turn.

Memory persists across rounds and controller changes within one run, with existing summarisation limits.
Starting another run is still a fresh start: campaign saves, a second room, director world edits, and speaking
while detained are not implemented. The gate's automatic unlock is an explicit first-slice rule, not a new
arbitrary lock-picking or movement system.

The .NET 10 SDK used on Jon's machine lives at `C:\Users\jonny\Documents\Codex\.dotnet10\dotnet.exe`.
Use that executable if plain `dotnet` resolves to the older system .NET 6 SDK. This branch does not require
changing the global SDK installation. New regression coverage exercises rescue outcomes, detainee turn skips,
human-to-AI history continuity, stale commands, cancellation, passive witness memory, and a complete scripted rescue run.
Browser checks verified real server startup, reserving guest control, cancellation, and the action form's
request payload. Jon has not yet playtested a complete live-model rescue or the post-release guest experience.
Next check: play the rescue once with Deacon on AI, then reserve control before release on a second run.

Most of you will have heard of Dungeons & Dragons, or seen it on _Stranger Things_: a group of friends sat round a table, rolling dice and taking on imaginary adventures.

I wanted to play with that idea. What would happen if I recreated it with every role played by an AI agent? Would the agents stick to the rules? Would they work together? Would their personalities affect their decisions? Would they even remember whose side they were on?

## The engine

Firstly, Models & Monsters is an experimental harness, not an attempt to create a complete game or a faithful recreation of D&D rules.

I also decided to make things harder for myself by requiring it to run well on a local LLM using only 8 GB of VRAM. As my previous blog post showed, that first requires finding a model that can reliably call tools; running an ongoing simulation where context built up quickly, all while keeping it inside an 8k token window, was a real challenge.

Dropping several agents into a conversation and letting them decide what was happening would be chaos. They could invent new locations, actions and outcomes as it suited them. The experiment needed a persistent world and a rules-based engine that could act as the single source of truth.

The agents could decide what they _wanted_ to do but they could not decide what was _true_.

I mirrored the basic structure of a tabletop game by placing a Dungeon Master between the characters and the engine. A character describes its intent to the Dungeon Master in natural language. The Dungeon Master interprets that intent and selects an appropriate engine action. The game engine then validates it, rolls any dice, updates the game state and reports back what happened. The Dungeon Master then adds his flourish to turn a mechanical answer into an answer fitting the game style.

In short:

```text
Character chooses intent
        ↓
Dungeon Master interprets it
        ↓
Game engine decides result
        ↓
Dungeon Master describes the result
```

The initial world was deliberately tiny: one room, heroes against monsters, and a very basic combat system.

Each hero and monster was an independent agent with its own backstory, personality, wants, fears and goals. On its turn, a character received only the information it should know, along with the relevant history of what it had previously seen and heard.

It could then use four tools:

- `say` allows the character to speak publicly to everyone in the room. Speaking does not consume its main action.
- `ask_dm` allows the character to ask the Dungeon Master a private question and receive a private answer. That answer is based only on information the character should legitimately have access to.
- `end_turn` allows the character to deliberately do nothing.
- `take_action` allows the character to attempt one concrete action. The character is not shown the actions supported by the engine or the structure of its internal API. It simply expresses what it wants to do in natural language.

That last constraint was important. I did not want the characters behaving like clients of a software API:

```text
attack_character(target: "goblin-1")
```

I wanted them behaving like inhabitants of the world:

> I raise my shield, step between Elara and the goblin, and swing my sword at its head.

The Dungeon Master’s job was to translate between those two worlds without being allowed to determine the outcome itself.

The first version supported little more than attacking someone with the weapon in your hand. I wanted to begin with the smallest world that could prove the idea worked, then observe what the agents tried to do.

It did not take long for the goblin to start filing feature requests.

## How the characters grew the world

The most interesting part of building Models & Monsters was watching the characters attempt actions the engine did not yet support.

Those rejected intentions became useful research. Rather than guessing which mechanics an autonomous character might need, I could observe what the models repeatedly tried to do and decide whether supporting it would enable meaningful or interesting behaviour.

The first example was unexpectedly bleak. A goblin, mortally wounded but not yet dead, repeatedly tried to give up. Before `end_turn` existed, the harness treated “lie down and stop” as a failed action and asked it to try something else:

> _“I accept my fate and remain motionless, knowing there is nothing left to do.”_
>
> _“I accept my fate and remain still, knowing there is nothing left to do.”_
>
> _“I accept my fate and let myself rest, knowing there is nothing left to do.”_

The result was a goblin trying, and failing, to be allowed to die, round after round.

That behaviour directly produced two features. The `end_turn` tool allowed a character to deliberately do nothing, while stalemate detection allowed the encounter itself to stop when nobody was willing or able to change anything.

The same process continued throughout the project.

Characters tried to speak to allies, threaten enemies and bargain for their lives, so public speech became a real channel that every character in the room could hear. This allowed the agents to coordinate, lie, negotiate and react to one another’s words without speech itself changing the game state.

When the room contained chests, characters naturally tried to examine and open them. That led to containers with hidden contents that could be inspected, revealed and emptied.

Once characters could take items from a chest, they began trying to pass those items to allies, steal them from enemies and loot them from fallen bodies. That required persistent ownership, transfers, theft rolls and rules governing whether a character had a legitimate reason to know an item existed.

Characters repeatedly tried to bargain for safe passage. The first surrender mechanic allowed someone to yield, but the models immediately wanted to negotiate conditions: _take my gold and let me live_. Surrender therefore grew into a proper offer-and-acceptance system. One character could put concrete terms on the table, but nothing moved and nobody became safe until the named recipient chose to accept.

Characters also kept trying to leave. A real exit was added to the room, but escaping required commitment: opening the door consumed one turn and passing through it consumed another. This created opportunities for allies to cover a retreat, or for an enemy to strike before the escape was complete.

Attempts to brace, block and protect companions led to defence and guard abilities. Threats, panic and rallying cries eventually led to an authoritative fear system. Fear could influence the situation, but it could not choose on a character’s behalf: even a terrified character still had to decide whether to fight, flee or offer terms.

Furniture followed the same pattern. From the earliest runs, characters wanted to hide behind overturned tables, shove obstacles into an attacker’s path or smash the scenery. Rather than adding a complete movement and physics system, I eventually added environmental cover. A character could shelter behind a particular object, attacks could be intercepted by it, and another character could spend a turn destroying it.

Not every attempted action became a feature. Characters also wanted to throw weapons, disarm opponents, perform elaborate combinations and improvise arbitrary physics. Supporting all of that would have turned the experiment into an attempt to build a complete role-playing system.

The models were never allowed to invent what happened inside the world, but their attempts had a substantial influence on what the world eventually allowed.

## Interesting emergent behaviour

Once the world contained enough mechanics for the characters to express themselves, they began combining those mechanics in ways I had never explicitly designed.

One of the best examples was a deal the engine had no direct way to represent.

Elara, badly wounded, attempted to buy safe passage for both herself and Rowan:

> “Vark - my purse, every coin, if you stand down and let us go. You’re bleeding. Take the gold and live.”

Vark did not accept those terms. Instead, he made a counter-offer:

> “Your gold and your legs, priest. Go. Rowan stays.”

The surrender system only understood an offer between one character and one named recipient. It had no concept of allowing one member of a group to leave while holding another behind.

The characters nevertheless assembled the deal themselves. Vark took Elara’s purse, allowed her to leave, and then turned back towards Rowan. Rowan recognised what had happened and responded:

> “You took her gold and let her walk. Now let’s finish this, just you and me.”

No single mechanic represented that agreement. It emerged from several smaller capabilities working together: public speech, transferring an item, choosing not to attack, and leaving through the door. The engine did not need to understand the entire bargain. It only needed to resolve each concrete action honestly.

This became one of the strongest arguments for keeping the mechanical vocabulary relatively small while allowing the fiction around it to remain wide.

Another interesting result came from the character knowledge system.

Vark knew from his backstory that the shrine-marked supply case contained a healing draught. During the fight, he shouted:

> “The shrine-marked case holds the healing draught - that is what we must protect at all costs!”

The other characters heard him, but the system did not immediately record the contents of the case as something they knew to be true. They only knew that Vark had made the claim.

When Skrit referred to the case later, he attributed the information correctly:

> “Vark says it’s for medicinal supplies.”

In another turn, the Dungeon Master described the situation to him by saying that Vark _claimed_ the case contained a healing draught. Skrit only learned the truth later, after inspecting the open case himself.

That distinction between knowledge and hearsay was small mechanically, but it made the characters’ conversations feel much more believable. They could lie, bluff, warn one another or repeat rumours without the engine silently converting everything said aloud into objective truth.

The largest behavioural change, however, was visible across whole encounters.

Models & Monsters began as a combat harness in which a fight normally ended when one side was killed. After adding escape, surrender and negotiated terms, that stopped being the default outcome. In one set of ten full encounters across Qwen3.5 9B, Claude Sonnet and GPT-5.4, six ended with nobody dead.

Characters surrendered, bought their lives, opened escape routes and withdrew from fights they might otherwise have continued. In one encounter, a goblin captain offered terms while his surviving grunt opened the cellar door; the captain surrendered and the grunt escaped. Two separate non-lethal systems combined naturally in the same scene.

Not all emergent behaviour was sensible.

In one later run, Vark offered to buy his life while completely unhurt and visibly winning. His side was dominating: Rowan was badly wounded and Elara was barely standing. Vark’s persona explicitly said that he would never surrender while the fight remained winnable, but the model ignored that condition and reached for the familiar dramatic beat of a goblin offering gold for mercy.

Elara accepted, the surrender system resolved correctly, and Vark left a fight he was probably going to win.

Nothing in the machinery had failed. The model had simply made a poor decision.

That was an important distinction throughout the project. Emergent behaviour did not automatically mean intelligent behaviour. Sometimes it produced a convincing bargain or an unexpected tactical combination. Sometimes it produced cowardice, obsession, betrayal or obvious mistakes.

The engine’s responsibility was not to make every character wise. It was to ensure that whatever they chose, the world remained consistent about what actually happened.

## What each agent was allowed to know

While the interface allowed me to see everything, the agents deliberately could not.

A character received:

- Its persona, motivations and goals.
- Exact information about its own condition, abilities and possessions.
- Recent public events it had witnessed.
- Information it had personally discovered.
- Claims it had heard from other characters, recorded as hearsay rather than fact.
- A history of its previous turns.

It did not receive:

- The exact health, fear or private state of another character.
- Private questions and answers involving other characters.
- Hidden information it had no legitimate reason to know.
- The contents of a container it had not opened, inspected or otherwise learned about.

Characters could still perceive that someone looked badly wounded or frightened, but they were not given the numbers behind that description.

The Dungeon Master worked differently. It could receive authoritative state, but it did not carry one enormous conversation containing everything that had happened.

Instead, each of its jobs used a fresh, purpose-specific context:

- **Adjudicating** received the current state, the acting character’s intent, that character’s knowledge and the rules from the rulebook.
- **Narrating** received the authoritative outcome that had already been decided by the engine.
- **Answering a question** received only the facts that the asking character was allowed to know.

> Prompt engineering determined what an agent was asked to do. Context engineering determined what it was capable of knowing while doing it.

## Making 8,192 tokens enough

The local model had a context window of 8,192 tokens, shared between the input and the model’s reply. Filling the window with context left no space for the output, so every request needed a deliberate budget.

Character history was the first obvious problem. As an encounter continued, every question, action, response and narration accumulated in the character’s conversation.

The first solution was to remove context that was no longer useful. Failed replies, correction messages and retry nudges were discarded once a turn had resolved. The complete record remained in the experiment trace, but the character did not need to carry those failed attempts forever.

Eventually, even the legitimate history became too large. This was where context compaction came in.

Once a character’s history crossed a threshold, the harness made a separate model call to summarise the older turns into a short, first-person memory. That summary replaced the older messages, while the most recent turn remained available in full.

As increasing the context window was a self-imposed restriction, the useful question was: _what information in this request is still relevant?_

## Selecting the right rules

The other growing source of context was the rulebook.

As more actions and abilities were added, the rules could no longer remain whole in the Dungeon Master’s prompt. They were moved into versioned rule cards and given to a separate, stateless Rulebook Resolver.

Initially, the resolver received the whole rulebook. This was safe, but by the end of the project the 25 cards consumed an average of 6,386 input tokens per consultation.

I experimented with several RAG-like ways of selecting a smaller set.

### Embedding retrieval

The first approach used a conventional embedding model. Each rule card was embedded as a vector, as was the character’s intent, and the closest cards were selected by vector similarity.

This reduced the average input substantially, but achieved only 87.8% required-rule recall on a labelled set of 45 test cases. Five cases were incorrectly judged unsupported because the rule needed to make the decision had not been retrieved.

The problem was that the most important card was not always the one most similar to the request. Sometimes it was a contrasting rule or an exclusion.

An intent such as:

> “I take the purse and let him live.”

looks like taking or stealing an item, but in the presence of a surrender offer it may actually describe accepting terms.

### Compact Index

The second approach gave a model a compact, one-line-per-card index of the whole rulebook and asked:

> Which rules would you need to read to decide this?

This raised required-rule recall to 96.7%. However, including enough exclusions to distinguish similar rules made the selection call itself expensive. Once that extra call was included, the total token reduction was only 19%.

It worked much better than embeddings, but occasionally still omitted a rule on an awkward phrasing.

### Action Routing

The most successful approach routed through the engine’s action surface instead of selecting rule cards directly.

The model read approximately 19 action descriptions - one per engine action - and returned:

- The primary action it thought the character was attempting.
- A short list of actions it had considered and ruled out.

Code then expanded those action labels into the relevant rule cards using relationships declared in the rulebook metadata.

This had two advantages:

- The routing index was bounded by the number of engine actions rather than growing once per rule card.
- Asking what an intent was _not_, helped surface contrasting rules that similarity retrieval tended to miss.

Action Routing achieved 98.9% required-rule recall, full agreement on all 45 final decisions, zero wrongly unsupported cases and a 42.3% reduction in total input tokens.

Interestingly, the model selected the correct primary action label only 77% of the time. The final recall was much higher because the declared relationships also supplied the rules for plausible competing actions. The surrounding structure compensated for an imperfect model decision.

That was the most useful RAG lesson from the project.

## A window into the game world

Watching events scroll past in a terminal quickly became confusing. Who was speaking to whom? Who was dead? Where had the potion gone? I decided to put a simple web interface over the engine.

It gave me a live, god-view of the experiment: every character’s condition, possessions and injuries; who was hiding behind cover; which containers were open; and any active surrender offers or status effects. Beneath it, a transcript showed the encounter unfolding in real time.

The interface did not control the characters or alter the world. It was simply a looking glass into the authoritative state behind the story.

![Game in action](models-and-monsters-1.png) ![End of game](models-and-monsters-2.png)

In order to debug, chase context overflows and judge parameter changes, _everything_ that goes to and from a model and all the decisions and rolls made by the game engine are logged to JSONL files.

Chasing down the foibles of each model, and deciding what information was missing from their context, or was conversely too much information, would have been very difficult without this excessive logging.

## Different models, different results

This article is not intended to be a model benchmark, but running the same system across different models produced differences worth mentioning.

I built an abstraction over the model calls so I could switch between local models running through Ollama, hosted frontier models from OpenAI and Anthropic, and larger open-weight models - beyond the reach of my 8 GB GPU - through OpenRouter and Ollama Cloud.

These results should be treated as field notes rather than rankings. The sample sizes were small, game events included seeded randomness, and provider infrastructure, quantisation and inference speed all affected the experience.

### Models that did not complete the scenario

- **Qwen3.8 Max**, through OpenRouter>Alibaba, failed in the first round with `HTTP 400: reasoning cannot be disabled`. This was a provider-contract mismatch rather than a model-capability failure: the harness requested `Effort=None`, while the model required reasoning to remain enabled. It was fixable through provider detection, but long agent runs with mandatory reasoning would be slower and more expensive, so I did not pursue it.
- **Nemotron 3.5 Lightning**, through OpenRouter, remained technically functional but stalled in an interrogation loop. One character made 65 `ask_dm` calls and hit the questions-per-turn limit 17 times before I cancelled the encounter.
- **The smaller local group** - Llama 3.2 3B, uncensored Llama 3.1 8B variants, Granite 4.1 8B and Qwen3.8 27B at the extremely compressed IQ2_XXS quantisation - struggled with malformed rule guidance, tactical blindness and protocol errors. Some could technically progress, but none was reliable enough for the full scenario.

The Qwen3.8 result is a useful warning against judging a model by parameter count alone: the heavily compressed local 27B version struggled, while a better-quality hosted version of the same model performed extremely well.

### Models that completed with limitations

- **Qwen3.5 9B IQ4_XS (Unsloth finetune)**, running locally, was the usable baseline. It completed full encounters and generally maintained tool calling, but sometimes made poor tactical decisions or narrated events that had not happened.
- **GPT-5.4 Mini** completed the scenario but produced two malformed rule-guidance responses and comparatively flat prose. It was the weakest of the otherwise capable hosted group.

### The strongest group

| Model                 | What I observed                                                                                                                                                                      |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **GPT-5.6 Terra**     | The most effective overall. Fast, decisive and mechanically flawless, with excellent tactics but functional rather than atmospheric prose.                                           |
| **Grok 4.20**         | Very close to Terra, with perfect tool discipline. The best story telling of all models.                                                                                             |
| **Qwen3.8 27B**       | The value result: an open-weight 27B model producing a clean sheet comparable to the frontier models at a much lower cost.                                                           |
| **Claude Sonnet 4.6** | The best overall balance of coherent decisions, reliable tool use and good prose.                                                                                                    |
| **Claude Sonnet 5**   | Capable but slower and chattier. It was also the only strong model to narrate an event that had not occurred, referring to “another firebolt” when the character had cast its first. |
| **DeepSeek V4 Flash** | Produced the rich prose and used the mechanics extensively, but took much longer and suffered one decoding-degeneration incident.                                                    |
| **Gemma 4 Cloud**     | Handled the complete scenario cleanly through Ollama Cloud, including tactics that required planning across multiple turns.                                                          |

For the constraint that started the project, the most important result remained **Qwen3.5 9B using an Unsloth IQ4_XS quantisation**. It was not the strongest model tested, but it was the best model I found that could run the complete experiment locally on an 8 GB GPU.

## What was not built

A lot more could have been added given time, though I don't think it has legs to become more than an interesting tech demo. Examples include:

- Full positional movement and distance.
- Throwing and disarming.
- Weapon switching.
- Reactions and readied actions.
- Arbitrary improvised physics.
- A complete D&D ruleset.
- Multiple rooms and persistent campaigns.
- A human-controlled character using exactly the same pathway as an AI character.

## A new scenario

To prove the viability of the engine itself, and that nothing was hard-coded, I swapped out the scenario (controlled via JSON) for a whole new one - different setting, different heroes, different enemies (3 vs 2), different containers and furniture - and it dropped in and played through first time.

This proves that the agents really were operating freely but in a world constrained by the rules I had coded.

## Conclusion

Models & Monsters began with a simple question: if models supplied the creativity, personality and intent, could a the engine keep their shared world coherent? The answer was yes - provided no model was allowed to decide what was true.

The interesting result was not merely that the agents could imitate a game of Dungeons & Dragons. It was that a relatively small action set was enough to support bargains, retreats, rumours, tactical cooperation and spectacularly poor decisions.

>Context engineering determined what each character could know.
>
>The harness constrained what each could attempt.
>
>The models made the choices
>
>The engine remained the single source of truth for the world.
