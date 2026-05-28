---
sidebar_position: 2
title: Setup
---

# Setup

This guide walks a new user from a clean machine to a running Kagura instance.

## Prerequisites

Install these before running Kagura:

- **.NET 10 runtime** — required to run the tool. Download from [dot.net](https://dotnet.microsoft.com/download).
- **`claude` CLI** on your `$PATH`, already logged in. Kagura spawns this binary directly and triage shells out to `claude -p`, so no Anthropic API key is needed.
- **`git`** on your `$PATH`.
- **`gh`** on your `$PATH` — optional, only needed if you want Kagura to open GitHub pull requests for you.

Node is **not** required to use Kagura — the React UI is built into the released package. You only need Node if you're contributing to the frontend (see [Local development](#local-development) below).

## Install

```bash
dotnet tool install -g Kagura.Cli
```

That puts a `kagura` binary on `$PATH`.

### Verify

```bash
kagura doctor
```

`doctor` checks every prerequisite (claude on PATH and authenticated, git, gh, state directory writable, port 5253 free, database at the current migration) and prints one OK / FAIL line per check. Exit zero means you're good.

## Run

```bash
kagura run
# → Kagura running at http://localhost:5253/
```

Open that URL in your browser — the React UI loads served by the same Kestrel server. On first run, Kagura creates `~/.devflow/` (database, encryption keys, worktrees, transcripts) and prints a one-time banner letting you know.

### Subcommands

Each subcommand has its own page with the full set of flags, environment
variables, and behaviour notes:

- [`kagura run`](./commands/run.md) — start the local server.
- [`kagura open`](./commands/open.md) — open the UI in your browser (spawns
  a server first if nothing is listening).
- [`kagura doctor`](./commands/doctor.md) — diagnose the local install.
- [`kagura version`](./commands/version.md) — print the installed version.

## Uninstall

```bash
dotnet tool uninstall -g Kagura.Cli
```

That removes the binary. `~/.devflow/` is **left behind** so reinstalling resumes where you left off. To fully remove Kagura, also delete its state directory:

```bash
rm -rf ~/.devflow
```

`dotnet tool` has no post-uninstall hook, which is why this last step has to be manual.

## Privacy / telemetry

Kagura sends **no telemetry**. The only outbound network request the tool ever makes on its own is an optional version check against `https://api.nuget.org/v3-flatcontainer/kagura.cli/index.json` — the same public, unauthenticated endpoint `dotnet restore` uses to look up packages. Disable it with `--no-update-check` or `KAGURA_NO_UPDATE_CHECK=1`.

The `claude` CLI you've already installed is what talks to Anthropic — Kagura just shells out to it.

## Troubleshooting

Run `kagura doctor` first — most common first-run failures are diagnosed there.

- **`kagura: command not found`** — make sure `~/.dotnet/tools` is on your `$PATH`. The .NET SDK installer usually does this; reopen your terminal after a fresh install.
- **`port 5253 in use`** — another process is on that port. Pass `--port <n>` to `kagura run` to pick a different one, or stop the offending process.
- **Triage fails immediately** — run `claude` once in any terminal to confirm the CLI is on `$PATH` and logged in.

## Local development

This section is for contributors hacking on Kagura itself.

### Additional prereqs

- **.NET 10 SDK** (the runtime alone is enough for end users; the SDK is required to build).
- **Node 20+** (developed on Node 24) — for the React frontend.
- `dotnet dev-certs https --trust` — first time only, so the local HTTPS dev cert works without warnings.

### Run from source

```bash
git clone https://github.com/ODumpling/Kagura.git
cd Kagura
dotnet run --project src/Kagura.AppHost
```

The Aspire AppHost orchestrates the API + Vite frontend together, injects the API URL into the frontend, and opens a dashboard that aggregates logs, traces, and health for both:

| Service | URL | Notes |
| --- | --- | --- |
| Aspire dashboard | printed in the terminal on startup | Logs, traces, and health for every Kagura process |
| Kagura API | http://localhost:5253 | ASP.NET Core minimal API + SignalR hub at `/hubs/agent` |
| Kagura web UI | http://localhost:5173 | Vite + React frontend with HMR |

To run pieces individually without Aspire: `dotnet watch --project src/Kagura.Api` and, in another terminal, `npm --prefix web/kagura-web run dev`.

## Next steps

With the app running, head to the usage guide to add your first source and triage a work item.
