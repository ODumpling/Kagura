---
sidebar_position: 2
title: Setup
---

# Setup

This guide walks a new developer from a fresh machine to a running Kagura instance.

## Prerequisites

Install these before cloning the repo:

- **.NET 10 SDK** — required for the API and the Aspire AppHost. .NET 9 works too if you change `TargetFramework` in the project files. Download from [dot.net](https://dotnet.microsoft.com/download).
- **Node.js 20+** — required for the Vite-based frontend. Developed on Node 24. Download from [nodejs.org](https://nodejs.org/).
- **`claude` CLI** on your `$PATH`, already logged in. Kagura spawns this binary directly and triage shells out to `claude -p`, so no Anthropic API key is needed.
- **`git`** on your `$PATH`.
- **`gh`** on your `$PATH` — optional, only needed if you want Kagura to open GitHub pull requests for you.

### Trust the local HTTPS dev certificate

The first time you run .NET on a machine, trust the local dev cert so the API can serve HTTPS without warnings:

```bash
dotnet dev-certs https --trust
```

You only need to run this once per machine.

## Clone the repo

```bash
git clone https://github.com/ODumpling/Kagura.git
cd Kagura
```

## Run the app

Kagura is orchestrated by [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/). A single command boots the API, the Vite frontend, and an Aspire dashboard that aggregates logs, traces, and health for both:

```bash
dotnet run --project src/Kagura.AppHost
```

The first run restores NuGet packages, installs npm dependencies under `web/kagura-web/`, and applies EF Core migrations against a fresh SQLite database at `~/.devflow/kagura.db`. Subsequent starts are fast.

### What you should see

Once startup finishes, three URLs are available:

| Service | URL | Notes |
| --- | --- | --- |
| Aspire dashboard | printed in the terminal on startup | Logs, traces, and health for every Kagura process |
| Kagura API | http://localhost:5253 | ASP.NET Core minimal API + SignalR hub at `/hubs/agent` |
| Kagura web UI | http://localhost:5173 | Vite + React frontend — start here |

The Aspire AppHost injects `VITE_API` into the Vite dev server so the frontend always knows where the API is. Open the web URL in your browser to start using Kagura.

## Verify the install

You're set up correctly when:

1. The Aspire dashboard loads in your browser and both `Kagura.Api` and `kagura-web` show as **Running**.
2. http://localhost:5173 renders the Kagura UI with no console errors.
3. http://localhost:5253/api/sources returns `[]` (an empty JSON array) — the API and database are wired up.

## Troubleshooting

- **`dotnet` command not found** — install the .NET SDK and reopen your terminal.
- **HTTPS warnings in the browser** — re-run `dotnet dev-certs https --trust` and restart the browser.
- **Vite can't reach the API** — make sure you started the app via `src/Kagura.AppHost`, not `src/Kagura.Api` on its own. Aspire is responsible for wiring the two together.
- **Triage fails immediately** — run `claude` once in any terminal to confirm the CLI is on `$PATH` and logged in.

## Next steps

With the app running, head to the usage guide to add your first source and triage a work item.
