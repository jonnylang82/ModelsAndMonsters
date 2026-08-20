# Documents to read

Initital spec in docs/inital_spec.md

Phased build notes under docs/prompts/*.md

README.md has important context from previous sessions.

reports/v0_x_issues.md and repors/v0_x_notes.md are documents I will ask you to generate at the end of the sessions.
Don't generate them before this - put important context into README.md

There is a requirement that the model Qwen3.5:9b resides fully in memory with context size.
This means 8192 is what we want. 12288 is too high. There is a middle-ground somewhere.
But the answer to any context truncation/stop problems isn't to increase the context, its to work out why - can it be compacted/trimmed.
