# 🏁 Claude Session Search

A local dashboard for your [Claude Code](https://claude.com/claude-code) sessions. Search every transcript and memory note, give sessions real names, pin the ones you keep coming back to, and see where you left off — without opening a single `.jsonl`.

Runs entirely on your machine. Your transcripts are mounted read-only and never leave it.

## What you get

- **Dashboard** — sessions, what's active this week, open threads, tickets, days of work, spend.
- **Session cards** — a real name, ticket and repo chips, PR links, a 90-day activity strip, and a health dot:
  green = active this week, amber = gone quiet but its memory has a `START HERE` waiting, grey = idle.
- **Pinned / Recent / Archived** sections.
- **Ticket & repo views** — click a chip to see every session that touched it, plus related memory notes and PRs.
- **Detail drawer** — activity heatmap, the session's `START HERE` note, your last few messages, linked memory files, files edited, cost and lines changed.
- **Edit in place** — rename, pin, archive, curate tickets, add a note.
- **⌘K** jump to any session (↵ copies `claude --resume <id>`, ⇧↵ opens it). Keys: `/` search, `j`/`k` move, `↵` open, `c` copy, `esc` close.
- **Full-text search** (SQLite FTS5) across transcripts and memory.
- **🏁 Race-weekend theme**, because why not. Toggle it, or open `/?race`.

## Run it

Needs Docker (Docker Desktop, Colima, OrbStack… anything with `docker compose`).

```bash
cp .env.example .env      # then set SESSION_DATA_DIR (and optionally REPOS_ROOT)
set -a; . ./.env; set +a  # load .env into this shell
[ -f "$SESSION_DATA_DIR/memory/session_names.json" ] || echo "{}" > "$SESSION_DATA_DIR/memory/session_names.json"   # first run only
docker compose up -d --build
open http://localhost:5299
```

`SESSION_DATA_DIR` is your Claude Code project folder: `~/.claude/projects/<working directory with "/" replaced by "-">`. It contains the `*.jsonl` transcripts and the `memory/` folder.

The site indexes at startup. After new sessions, hit **reindex** in the header.

## Where the data comes from

| Shown | Source |
|---|---|
| Session name | `session_names.json` → your `/rename` name → Claude Code's auto title |
| Tickets | `session_names.json` override, else `memory/reference_sessions.md` ticket column + Jira-style keys you typed in the session (prefixes allow-listed) |
| Repos, active days, files edited, last messages, cost | parsed from the transcript |
| Start here / linked memory | `memory/*.md` files that mention the session's id |

### `memory/session_names.json`

The only file the app writes to (everything else is mounted read-only). The UI's edit controls maintain it; you can also edit it by hand and reindex. Keys can be a full session id or its first 8 characters.

```json
{
  "ticketPrefixes": ["ABC", "PROJ"],
  "sessions": { "09d77af1": "Search sync — the original" },
  "pinned": ["09d77af1"],
  "archived": [],
  "tickets": { "09d77af1": ["ABC-123", "ABC-140"] },
  "notes": { "09d77af1": "waiting on the infra ticket" }
}
```

## Security

The port is bound to `127.0.0.1` on purpose. Transcripts can contain anything you pasted into a session — including secrets — and the edit endpoints have no authentication. Don't expose it to a network.

## Stack

.NET 10 minimal API · SQLite FTS5 · a single static `index.html` (no build step, no framework).
