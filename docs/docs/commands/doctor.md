---
sidebar_position: 3
title: kagura doctor
sidebar_label: doctor
description: Diagnose whether the current machine is set up to run Kagura — claude CLI, git, gh, state dir, port, and database.
---

# `kagura doctor`

`doctor` is the first thing to reach for when something is off. It runs
through every prerequisite Kagura cares about and prints one
`OK` / `FAIL` line per check. A non-zero exit code means at least one
check failed.

```bash
kagura doctor
```

Sample output:

```text
OK   claude: on PATH and authenticated
OK   git: on PATH (user.email=you@example.com)
OK   gh: on PATH
OK   state-dir: /Users/you/.kagura writable
OK   port: localhost:5253 free
OK   database: /Users/you/.kagura/kagura.db up to date
```

## Checks

| Check | What it verifies | Failure hint |
| --- | --- | --- |
| `claude` | `claude` binary on `$PATH` *and* a real `claude -p hi --max-turns 1` probe succeeds. This is the one place `doctor` is allowed to spend a token. | Install from [claude.com/code](https://claude.com/code) or run `claude login`. |
| `git` | `git` on `$PATH` and `user.email` set globally. Commits Kagura makes on your behalf need a valid author. | `git config --global user.email "you@example.com"` |
| `gh` | `gh` on `$PATH`. **Optional** — if it's missing, `doctor` still prints `OK` with a `(disabled — PR features disabled)` note rather than failing. | Install [`gh`](https://cli.github.com/) if you want Kagura to open pull requests for you. |
| `state-dir` | The state directory (`~/.kagura/`, or legacy `~/.devflow/` if it already exists) is writable. | Fix permissions on the directory, or remove it so Kagura can recreate it. |
| `port` | `localhost:5253` is not already bound. | Stop the offending process, or run Kagura on a different port via `kagura run --port <n>`. |
| `database` | A `kagura.db` exists in the state directory and has no pending EF Core migrations. | If missing: run `kagura run` once to create it. If pending migrations: `kagura run` again to apply them. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Every check passed. |
| `1` | At least one check failed. The failing check is printed to stderr. |

## When to run it

- After installing Kagura on a new machine — before your first `kagura run`.
- After a `dotnet tool update` — to confirm migrations on disk match the
  new binary.
- Any time `kagura run` or triage fails with a confusing error — `doctor`
  surfaces the underlying environment problem in plain language.

## See also

- [Setup](../setup.md) — the install guide that walks you through making
  every check above pass.
