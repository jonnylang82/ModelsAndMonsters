# Models & Monsters / Council RPG — Voice Chat Handoff

Updated: September 3, 2026

## Purpose of the next chat

Jon wants to brainstorm by voice. Do not modify files, start runs, change settings, or operate the computer unless Jon explicitly asks in the new chat. The immediate subject is how to make the local-LLM D&D simulation feel like people inhabiting a scene instead of agents mechanically selecting actions.

## The larger idea

Jon is building **The Council**, a visual wrapper in which several AI villagers can talk, collaborate, play games, and use a shared world. The current experiment uses a fork of Spencer Clark's open-source **Models & Monsters** engine as a D&D sandbox. Jon can normally watch the agents play and can temporarily take control of Deacon as a guest/director rather than being a permanent player.

Repository:

`C:\Users\jonny\Documents\Codex\ModelsAndMonsters`

Local observer:

`http://localhost:5173/`

LM Studio API:

`http://localhost:1234/v1`

## Current scenario

The party is trying to rescue Deacon, who was wrongly detained in the Council archive.

Heroes:

- Nema Mistsong
- Mirabel Tipton
- Emma Nightveil
- Pipix Thistlegrin
- Scott Suncrest
- Deacon, normally AI-controlled but available for Jon to take over

Wardens:

- The Iron Curator
- Smut

## What went wrong in the observed runs

- Several characters repeatedly inspected the same containers. One run contained 18 box-related engine actions, including 11 inspections during the first two rounds.
- Characters often ignored spoken conversation even though it appeared in their prompts.
- When Jon controlled Deacon, he celebrated, joked with Emma, attempted a victory dance, and blew her a kiss. The spoken lines were recorded and delivered, but Emma ignored the invitation and inspected the floor instead.
- The victory dance and blown kiss were submitted separately as mechanical actions. The DM rejected them because the engine had no supported action for them.
- This revealed an important architectural split: the simulation supports **hearing speech**, but it does not reliably create **social attention or reaction**.
- The engine's private-knowledge rules also manufacture identical curiosity. Opening a container does not necessarily tell everyone its contents, so several agents independently decide they should inspect it themselves.
- Earlier runs also exposed allied attacks/theft, muddled item chronology, accidental surrender interpretations, ignored requests, and weak spell/item usage.

## Evidence about speech delivery

The trace for run `20260903-060643Z-f213f459` proves that Deacon's speech was not lost:

- `IT'S PARTY TIME NERDS! WE WON!!!!` was delivered publicly.
- `Hey Emma, now that that nerds gone ... What do you say ladies?` appeared verbatim in Emma's next-turn context.
- Emma received it but chose to ask the DM what was on the floor.

Therefore, this was not simply a broken chat pipe. The model saw the words, but the orchestration treated them as optional background beneath a large mechanical prompt.

## Recent engine safeguards already implemented

These changes passed 1,172 automated tests and are loaded in the local server, but a complete 20-round validation run has not yet been performed:

- Council configuration blocks allied attacks and allied theft before state changes or dice rolls.
- Requests and responses can be tracked separately from forced actions. A recipient can accept, refuse, counter, or ignore.
- Agreement does not automatically transfer an item or cause surrender.
- Offering one's own surrender requires explicit confirmation.
- Repeated inspection of an unchanged container can be rejected without spending the action.
- Recent rejected actions are carried into the next turn as reminders.
- Final encounter recaps can be rendered from chronological engine events instead of an invented narrative summary.
- Ordinary-speech request detection exists, but it remains model-based and can misidentify an ambiguous recipient.

These safeguards improve state integrity. They do **not** yet solve natural social play, personality, or repetitive tactical choices.

## Proposed architectural direction

Separate character behavior into three lanes:

1. **Mechanical action** — attacks, spells, theft, opening, taking, escaping, and other consequential changes that require engine adjudication.
2. **Observable expression** — dancing, laughing, waving, glaring, hugging, cheering, or blowing a kiss. These normally just happen, are shown to everyone, and do not require a roll or DM rejection.
3. **Directed interaction** — a question, joke, invitation, request, insult, or comment aimed at another character. It should enter a high-priority attention/reaction queue rather than being buried in the general transcript.

Conversation and expression should not consume or compete with the character's one mechanical action. A character should be able to answer Deacon and still cast a spell, leave the room, or inspect something.

For mundane party discoveries, one character should normally inspect and report what they found. Allies should use that report instead of all verifying the same box. Deliberate concealment should remain possible when secrecy matters.

## Context-window finding

The configured 8,192 value is a **token context window**, not 8 MB or 8 GB of VRAM.

In run `20260903-060643Z-f213f459`:

- Character calls averaged roughly 7,000 input tokens.
- The engine summarized histories 53 times.
- No character response ended because it hit the output-length limit.
- Emma's ignored conversation was still present near the bottom of her current context.

Increasing the window to 16,384 may preserve more history, but it will not by itself make characters prioritize social interaction. Prompt size and orchestration are currently larger problems.

## Local models currently exposed by LM Studio

- `google/gemma-4-12b`
- `google/gemma-4-12b-qat`
- `google/gemma-4-e2b`
- `google/gemma-4-e4b`
- `nvidia/nemotron-3-nano-4b`
- `qwen/qwen3.5-9b`
- `qwen/qwen3.8-27b`
- `openai/gpt-oss-20b`
- `dolphin3.0-llama3.1-8b`
- `llama-3.2-3b-instruct`
- `llama-3.2-3b-uncensored`
- `llama2-13b-tiefighter`
- Nomic embedding model

The current Models & Monsters setup uses Qwen 3.5 9B for the DM, characters, and support agents. In the earlier Village prototype, Jon felt Gemma models were more articulate and conversational. Pip's later Village configuration used Gemma 4 E4B; Nema used Nemotron 4B.

## Proposed model experiment

Use the same scenario and fixed seed, changing only one variable at a time:

1. Current Qwen characters and Qwen DM as baseline.
2. Change only the DM to Gemma 4 12B QAT.
3. Keep that DM and change only the characters to Gemma 4 E4B.
4. Compare Nemotron characters as the speed-focused condition.

Start with three or four rounds rather than full runs. Compare:

- response to directed speech
- distinct voices and action choices
- repeated inspection count
- rejected/unsupported actions
- spell and item usage
- progress toward rescuing Deacon
- model-call count and round duration

The leading DM candidate is **Gemma 4 12B QAT** because LM Studio identifies it as tool-trained and estimates about 7.4 GB minimum model memory, leaving more room on the GPU than GPT-OSS 20B or Qwen 27B. This is a hypothesis requiring an actual controlled run, not a confirmed conclusion.

## Hardware diagnostic result

Read-only diagnostics found:

- GPU: NVIDIA RTX 4070 SUPER, 12 GB VRAM
- CPU: AMD Ryzen 9 3900X, 12 cores / 24 threads
- Motherboard: ASRock X570M Pro4
- System RAM detected: 15.93 GB
- Physical memory detected: only one 16 GB Corsair module
- Detected part number: `CMK32GX4M2D3600C18`, which corresponds to a two-stick 32 GB kit
- Detected memory speed: 2133 MT/s rather than the kit's rated 3600
- Motherboard reports four memory slots

Jon's recollection that one RAM stick was missing in BIOS is consistent with Windows. This does not cause characters to obsess over boxes, but it seriously constrains larger models that spill beyond 12 GB VRAM. The missing stick should be investigated before testing GPT-OSS 20B or Qwen 27B. Hardware should be powered down and unplugged before reseating anything; the motherboard manual recommends A2 and B2 for a two-module configuration. Confirm both sticks are detected and stable before enabling XMP.

## Useful distinctions for brainstorming

- **Local versus hosted** is not the main intelligence distinction. Model training, architecture, size, quantization, prompts, tools, and orchestration matter.
- A larger download usually requires more memory, but file size alone does not determine speed. Quantization, active parameters, architecture, context length, GPU fit, memory bandwidth, and CPU offload also matter.
- A fluent role-play response and reliable tool-based state management are different abilities.
- A stronger model cannot perform an action that the engine itself rejects as nonexistent.
- More rules can reduce hallucination while simultaneously making characters less spontaneous. The goal is to keep strict adjudication for consequential mechanics while leaving ordinary human expression free.

## Best opening question for the voice chat

Given the evidence above, what is the smallest redesign that lets characters feel socially present—hearing, reacting, joking and expressing themselves—without sacrificing authoritative inventory, hidden knowledge, combat, and D&D rules?

