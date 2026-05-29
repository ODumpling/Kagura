# Kagura

A local orchestrator that pulls issues from your trackers (GitHub, Azure DevOps, Beads, Markdown), triages each into smaller tasks via Claude, and runs them in parallel as real, attachable `claude` CLI sessions inside isolated git worktrees.

Single-user, local-only. No auth. Built for one developer's workstation.

## Documentation

Full setup and usage guides are published at **<https://ODumpling.github.io/Kagura/>**.

The site lives in [`docs/`](docs/) and is built with Docusaurus. To contribute, edit the Markdown under `docs/docs/` (e.g. `docs/docs/setup.md`, `docs/docs/usage.md`) and preview locally:

```bash
cd docs
npm install   # first time only
npm run start # serves the site at http://localhost:3000 with live reload
```

Open a PR with your changes — the docs site rebuilds and redeploys from `main` via GitHub Actions.

## Architecture

```
┌──────────────────────────┐         ┌──────────────────────────────────┐
│  React + xterm.js (5173) │ <─SR──> │  ASP.NET Core API (5253)         │
│  Sources / WorkItems UI  │ <─REST─>│  + SignalR /hubs/agent           │
└──────────────────────────┘         │  + EF Core (SQLite)              │
                                     │  + claude CLI (triage)            │
                                     │  + Porta.Pty (claude PTYs)       │
                                     │  + git CLI (worktrees, PRs)      │
                                     └──────────────────────────────────┘
                                                  │
                                                  ▼
                                       ~/.kagura/kagura.db
                                       ~/.kagura/keys/        (DPAPI/DataProtection)
                                       ~/.kagura/worktrees/<wi>/<task>/
                                       ~/.kagura/transcripts/
```

Per work item: a branch `kagura/<external-id>-<slug>` is cut from the repo's default branch. Each approved task gets a child branch `kagura/.../<order>-<slug>` and a worktree. A `claude` PTY runs in that worktree; its bytes stream over SignalR to an xterm.js tab the user can type into. When all tasks merge up, the work-item branch becomes a PR.

> Migrating from an older install? On first run Kagura moves `~/.devflow/` → `~/.kagura/` automatically. Any `Devflow:` overrides in `appsettings.json` should be renamed to `Kagura:`.

## Install

Kagura ships as a [.NET global tool](https://learn.microsoft.com/dotnet/core/tools/global-tools):

```bash
dotnet tool install -g Kagura.Cli
```

That puts a `kagura` binary on `$PATH`.

### Prerequisites

- **.NET 10 runtime** — required to run the tool. Get it from [dot.net](https://dotnet.microsoft.com/download).
- **`claude` CLI** on `$PATH`, already logged in. Kagura shells out to it for triage and runs it as the agent inside each task's worktree, so no Anthropic API key is needed.
- **`git`** on `$PATH`.
- **`gh`** on `$PATH` — optional, only needed if you want Kagura to open GitHub pull requests for you.

> Node is **not** required to use Kagura — the React UI is built into the package. You only need Node if you're contributing to the frontend (see [Local development](#local-development) below).

To verify your machine is set up, run:

```bash
kagura doctor
```

It checks every prerequisite and prints one OK / FAIL line per check. Exit zero means you're good.

## Quickstart

```bash
kagura run
# → Kagura running at http://localhost:5253/
```

Open that URL — the React UI loads served by the same Kestrel server. On first run, Kagura creates `~/.kagura/` (database, encryption keys, worktrees, transcripts) and prints a one-time banner letting you know.

## Subcommands

| Command | Notes |
|---|---|
| `kagura run` | Start the local server. `--port <n>` overrides the default `:5253`. `--verbose` (or `KAGURA_LOG_LEVEL=Debug`) restores the full ASP.NET host logs. `--no-update-check` (or `KAGURA_NO_UPDATE_CHECK=1`) suppresses the daily NuGet version check. |
| `kagura open` | Open the UI in your default browser. If nothing is running, spawn a server first; honours `--port`. |
| `kagura doctor` | Diagnose the local install: `claude` CLI, git, gh, state directory, port, database. Non-zero exit if anything fails. |
| `kagura version` | Print the installed Kagura version and exit. |

## Uninstall

```bash
dotnet tool uninstall -g Kagura.Cli
```

That removes the binary. `~/.kagura/` is **left behind** so reinstalling resumes where you left off. To fully remove Kagura, also delete its state directory:

```bash
rm -rf ~/.kagura
```

`dotnet tool` has no post-uninstall hook, which is why this last step has to be manual.

## Privacy / telemetry

Kagura sends **no telemetry**. The only outbound network request the tool ever makes on its own is an optional version check against `https://api.nuget.org/v3-flatcontainer/kagura.cli/index.json` — the same public, unauthenticated endpoint `dotnet restore` uses to look up packages. Disable it with `--no-update-check` or `KAGURA_NO_UPDATE_CHECK=1`.

The `claude` CLI you've already installed is what talks to Anthropic — Kagura just shells out to it.

## Walkthrough

In the UI:

1. **Sources** → Add a source pointing at a local repo clone.
2. Click **Sync** — Markdown sources scan `<repo>/.kagura/issues/*.md`. Each `.md` becomes a WorkItem.
3. **Work items** → open one → **Triage** (calls Claude, proposes tasks) → **Approve all**.
4. **Start agent** on a task → a terminal tab opens, attached to a live `claude` running in that task's worktree. Type into it normally.

### Markdown issue format

```markdown
---
id: ISSUE-001          # required if you want a stable external id
title: Something to do
labels: prio:high,feat
---

# Body in markdown

Whatever description you want.
```

If `id` is missing, the filename (without `.md`) is used.

## Configuration

`src/Kagura.Api/appsettings.json`:

```jsonc
{
  "Kagura": {
    "MaxConcurrentAgents": 3,             // SemaphoreSlim cap on parallel claude PTYs
    "ClaudeBinary": "claude"              // anything resolvable on $PATH
    // WorktreesRoot / DbPath / TranscriptsRoot / ScratchRoot default to ~/.kagura/*
    // — override them here if you want a different "profile".
  },
  "Triage": {
    "Model": null                         // optional; passes --model to `claude -p` when set
  }
}
```

Sources themselves (GitHub tokens, ADO PATs, Markdown paths, etc.) live in the **DB**, not in config. The whole `ConfigJson` column is encrypted with `Microsoft.AspNetCore.DataProtection` (keys under `~/.kagura/keys/`).

## Project layout

```
Kagura.sln
├─ src/
│  ├─ Kagura.Core/                  Domain + services (no ASP.NET deps)
│  │  ├─ Domain/                    Source, WorkItem, AgentTask, AgentRun + enums + SourceConfig records
│  │  ├─ Sources/                   IIssueProvider, IssueProviderFactory, MarkdownIssueProvider, stubs
│  │  ├─ Triage/                    ITriageService, ClaudeCliTriageService (shells out to `claude -p`)
│  │  ├─ Git/                       GitService (worktrees, branches, PR), ProcessRunner
│  │  └─ Agents/                    AgentRunner, AgentSession (PTY wrapper), IAgentBroadcaster
│  ├─ Kagura.Data/                  EF Core
│  │  ├─ KaguraDbContext.cs         SQLite, encrypted-string converter for Source.ConfigJson
│  │  ├─ EncryptedStringConverter.cs
│  │  ├─ Migrations/
│  │  └─ Services/SourceSyncService.cs    Calls provider, upserts WorkItems
│  └─ Kagura.Api/                   ASP.NET Core minimal API
│     ├─ Program.cs                 DI wiring, CORS, migrations, SignalR
│     ├─ Endpoints/                 Sources, WorkItems, Triage, Agents
│     └─ Hubs/AgentHub.cs           SignalR: Join/Leave/Input/Resize + SignalRAgentBroadcaster
└─ web/kagura-web/                  Vite + React + TS + Tailwind v4 + shadcn/ui
   ├─ src/
   │  ├─ types.ts                   Mirrors API DTOs/enums
   │  ├─ api.ts                     Typed fetch wrappers
   │  ├─ signalr.ts                 Shared HubConnection singleton + base64 helpers
   │  ├─ App.css                    Tailwind + shadcn theme tokens
   │  ├─ lib/utils.ts               cn() helper (clsx + tailwind-merge)
   │  ├─ contexts/SourcesContext.tsx   Shared source list + refresh
   │  ├─ components/
   │  │  ├─ AppSidebar.tsx          shadcn Sidebar listing sources + nav
   │  │  ├─ AgentTerminal.tsx       xterm.js + FitAddon + SignalR
   │  │  └─ ui/                     shadcn primitives (button, card, table, dialog, …)
   │  └─ pages/                     SourcesPage, WorkItemsPage, WorkItemDetailPage
   ├─ components.json               shadcn config (Nova preset, radix base)
   ├─ vite.config.ts                @tailwindcss/vite plugin + @/ alias
   └─ tsconfig.app.json             paths: "@/*" → src/*
```

## REST API

| Method | Path | Notes |
|---|---|---|
| GET    | `/api/sources` | List all sources |
| POST   | `/api/sources` | Create. Body: `{name, type, localRepoPath, config, enabled}` |
| PUT    | `/api/sources/{id}` | Update |
| DELETE | `/api/sources/{id}` | Cascades to WorkItems |
| POST   | `/api/sources/{id}/sync` | Pulls issues into WorkItems |
| POST   | `/api/sources/sync-all` | Syncs every enabled source |
| GET    | `/api/workitems?sourceId=&status=` | List with optional filters |
| GET    | `/api/workitems/{id}` | Detail including tasks |
| POST   | `/api/workitems/{id}/triage` | Calls Claude, persists Proposed tasks |
| POST   | `/api/workitems/{id}/triage/approve` | Flip Proposed → Approved |
| PUT    | `/api/workitems/{wi}/tasks/{id}` | Edit a task (title/description/order) |
| DELETE | `/api/workitems/{wi}/tasks/{id}` | Remove a task |
| GET    | `/api/agents` | Active PTY sessions |
| POST   | `/api/agents/start/{taskId}` | Spawn `claude` in a fresh worktree |
| POST   | `/api/agents/{runId}/stop` | Kill and dispose |

Hub: `/hubs/agent` (SignalR). Client → server: `Join(runId)`, `Leave(runId)`, `Input(runId, base64)`, `Resize(runId, cols, rows)`. Server → client: `data(runId, base64)`, `exit(runId, exitCode|null)`.

## Local development

This section is for contributors hacking on Kagura itself — install the global tool (above) if you just want to use it.

### Prerequisites for contributors

In addition to the user prereqs above:

- **.NET 10 SDK** (the runtime alone is enough for end users; the SDK is required to build).
- **Node 20+** (developed on Node 24) — for the React frontend.
- `dotnet dev-certs https --trust` — first time only, so the API can serve HTTPS without warnings.

### Dev flow

```bash
# Aspire AppHost orchestrates the API + Vite frontend together,
# injects the API URL into the frontend, and opens a dashboard with
# logs, traces, and health for both processes.
dotnet run --project src/Kagura.AppHost
# → Aspire dashboard prints its own URL on startup
# → API at http://localhost:5253
# → Web at http://localhost:5173 (Vite HMR)
# → DB + DataProtection keys auto-created under ~/.kagura/
```

To run pieces individually without Aspire: `dotnet watch --project src/Kagura.Api` and, in another terminal, `npm --prefix web/kagura-web run dev`.

### Build



```bash
dotnet build              # whole .NET solution
cd web/kagura-web && npm run build
```

### Watch loops

```bash
dotnet watch --project src/Kagura.Api    # backend hot-reload
cd web/kagura-web && npm run dev          # Vite HMR
```

### Migrations

```bash
# Add a migration after changing entities in Kagura.Core/Domain/
dotnet ef migrations add <Name> \
  --project src/Kagura.Data \
  --startup-project src/Kagura.Api

# Apply (also happens automatically on Api startup)
dotnet ef database update \
  --project src/Kagura.Data \
  --startup-project src/Kagura.Api
```

### Adding an issue provider

1. Implement `IIssueProvider` in `Kagura.Core/Sources/` — set `Type`, return `FetchedIssue[]`. Read your per-source config out of `source.ConfigJson` (use the matching `SourceConfig` record).
2. Register in `Program.cs`:
   ```csharp
   builder.Services.AddSingleton<IIssueProvider, MyNewProvider>();
   ```
   The `IssueProviderFactory` picks it up automatically via `SourceType`.
3. Add the matching enum value to `SourceType` (Core) and `SourceTypeLabel` (web `types.ts`). Add a `defaultConfigFor(...)` branch in `SourcesPage.tsx`.
4. If you need secrets in the config, just store them in `ConfigJson` — the whole blob is encrypted.

The four stubs to fill in are `GitHubIssueProvider`, `AzureDevOpsIssueProvider`, `BeadsIssueProvider` in `Kagura.Core/Sources/StubProviders.cs`. For GitHub use Octokit and `GitHubConfig.Token`; for ADO use the REST API + `AzureDevOpsConfig.Pat`; for Beads shell out to `bd` in `BeadsConfig`'s repo path.

### Adding a triage backend

`ITriageService` is the seam. Default impl is `ClaudeCliTriageService`, which shells out to `claude -p --output-format json --append-system-prompt …` against the user's logged-in CLI session. Swap it in `Program.cs` to plug in OpenAI, a direct Anthropic SDK call, a local model, or a deterministic rules engine.

### Editing the merge/PR flow

`GitService` already exposes:
- `EnsureWorkItemBranchAsync(repoPath, wi)`
- `CreateTaskWorktreeAsync(repoPath, wi, task)`
- `MergeTaskBranchAsync(repoPath, wi, task)`
- `RemoveWorktreeAsync(repoPath, worktreePath)`
- `OpenPullRequestAsync(repoPath, wi)` — pushes then calls `gh pr create`

A "Finish work item" endpoint that walks `task.Status == Merged`, then opens the PR, would live in `WorkItemEndpoints.cs`. Add a button on the detail page.

### Where state lives

| Path | What |
|---|---|
| `~/.kagura/kagura.db` | SQLite, all entities |
| `~/.kagura/keys/` | DataProtection master keys (do not commit, do not share) |
| `~/.kagura/worktrees/<wi-slug>/<task-slug>/` | Per-task git worktree where `claude` runs |
| `~/.kagura/transcripts/<wi-id>/<task-id>_<run-id>.log` | Raw PTY transcript for replay on reconnect |

The DB path, worktree root, and transcript root are all overridable in `appsettings.json`. Move them all together if you want a different "profile."

### Resetting

The app is stateless beyond `~/.kagura/`. To start over:

```bash
# nuke local state (will lose all sources, work items, transcripts, encryption keys)
rm -rf ~/.kagura
```

Worktrees registered with git but missing on disk can be cleaned with `git worktree prune` inside the source repo.

## Known gaps

- **GitHub / Azure DevOps / Beads providers** are stubs that return 501. Easiest first contribution.
- **No merge/PR UI yet** — `GitService` has the methods, just needs an endpoint + button.
- **Triage UI is read-only** after Claude proposes tasks. The API supports `PUT /tasks/{id}` so adding inline editing is small.
- **`Porta.Pty` cross-platform** — works on macOS; Linux should be fine; Windows path is untested.
- **No tests yet**. Worth wiring `xUnit` against `Kagura.Core` for the provider/triage/git logic, and Playwright for the React side.
- **No persistence of `claude` settings across sessions** — each task worktree is a fresh dir, so MCP servers etc. need to live in the user's global `~/.claude` config (which is exactly how the running `claude` binary discovers them).

## License

Personal/internal use. No license declared.
