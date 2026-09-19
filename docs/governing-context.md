# Governing context

Post `/objective <text>` to pin the room's objective. Post `/correction <text>` to replace its latest correction. Each value may contain multiple lines and up to 6,000 UTF-16 characters. An oversized value is refused without saving a message or changing either slot.

Only a live message authenticated as a human participant can set these values. Both the local owner and owner-remote can use the commands. They must begin at the first character, use lower case, and end the command token with a space, tab, newline or end of message. Blockquotes, fenced examples, indented examples, imported history, model discussion and hub-generated notes never set context. A command inside the payload is not a second command. Quoted instructions remain quoted data.

| Command | Effect |
|---|---|
| `/objective <text>` | Replace the objective and retire every earlier correction. |
| `/correction <text>` | Replace only the latest correction. It can be set even before an objective. |
| `/objective` | Clear both slots. |
| `/correction` | Clear only the correction. |

These commands spawn nobody, invoke no skill, and do not steer or resume an active run. Mentions in their payload are plain text. A hub acknowledgement confirms the source message ID. Updates apply to launches that read context after the post commits. An already-running process keeps the prompt it received.

The current values appear in a separate section in every spawned prompt, outside the 60-message retrieval and 24,000-character transcript rendering limits. The section identifies the source message ID, authenticated author and server timestamp. Message IDs order versions. A clear is a versioned event too, so restarting cannot revive a superseded value. Ordinary conversation does not update the pins automatically. Set or clear them when the room's governing work changes.

The hub stores acceptance in `governing_updates`, atomically with the source message and read cursor. The source message stays unchanged in chat. Schema 14 creates an empty acceptance table after the normal verified database backup. No older messages become governing commands during upgrade, even if their bodies look exactly like commands. Retry keys return the original post without applying it again.

Each launch reads the transcript, total count and current context in one SQLite read snapshot. The prompt discloses how many messages were excluded by retrieval, how many additional messages were dropped during rendering, and the total. Counts include all source kinds and are room-scoped, so gaps in global message IDs do not inflate them. Rendering measures the actual message chunks, including headers and boundary lines. The newest message is retained whole even if it alone exceeds 24,000 characters, and this exception is stated. The separate governing section is bounded by its two value limits.

Imported history, live human messages, model discussion and hub notes receive distinct source labels. Message boundaries use a fresh spawn key, and governing payloads are JSON strings so embedded newlines cannot create provenance headers. These are prompt-format protections, not a guarantee that a model follows instructions correctly. Governing context does not override safety rules or the active skill.
