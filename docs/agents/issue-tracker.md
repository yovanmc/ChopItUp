# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues on `yovanmc/ChopItUp`. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --comments`.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments`.
- **Comment**: `gh issue comment <number> --body "..."`.
- **Labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`.
- **Close**: `gh issue close <number> --comment "..."`.

Infer the repo from `git remote -v`; `gh` does this automatically inside the clone.

## Pull requests as a triage surface

**PRs as a request surface: no.**

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`.

## Roadmap milestone tickets

The roadmap workflow cuts per-milestone build tickets under gitignored `.scratch/m<row#>-<slug>/issues/`, not on GitHub. GitHub issues hold owner-filed defects, decisions and wayfinder maps.

## Wayfinding operations

Map = one issue labelled `wayfinder:map`; tickets = child issues labelled `wayfinder:<type>`; blocking = GitHub native issue dependencies (`gh api --method POST repos/yovanmc/ChopItUp/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`); claim = `gh issue edit <n> --add-assignee @me`; resolve = comment, close, append a pointer to the map's Decisions-so-far.
