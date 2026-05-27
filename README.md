# Kagura

A local devflow app that pulls issues from your trackers (GitHub, Azure DevOps, Beads, Markdown), triages each into smaller tasks via Claude, and runs them in parallel as real, attachable `claude` CLI sessions inside isolated git worktrees.

Single-user, local-only. No auth. Built for one developer's workstation.

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
                                       ~/.devflow/kagura.db
                                       ~/.devflow/keys/        (DPAPI/DataProtection)
                                       ~/.devflow/worktrees/<wi>/<task>/
                                       ~/.devflow/transcripts/
```

Per work item: a branch `devflow/<external-id>-<slug>` is cut from the repo's default branch. Each approved task gets a child branch `devflow/.../<order>-<slug>` and a worktree. A `claude` PTY runs in that worktree; its bytes stream over SignalR to an xterm.js tab the user can type into. When all tasks merge up, the work-item branch becomes a PR.

## Prerequisites

- **.NET 10 SDK** (also works with .NET 9 if you change `TargetFramework`)
- **Node 20+** (developed on Node 24)
- **`claude` CLI** on `$PATH`, already logged in — Kagura spawns this binary as the user
- **`git`** on `$PATH`
- **`gh`** on `$PATH` (only for opening PRs; not required for local use)
- Triage uses the same logged-in `claude` CLI (headless `claude -p`), so no Anthropic API key is required

## First run

```bash
# One command — the Aspire AppHost orchestrates the API + Vite frontend
# together, injects the API URL into the frontend, and opens a dashboard
# with logs, traces, and health for both processes.
dotnet run --project src/Kagura.AppHost
# → Aspire dashboard prints its own URL on startup
# → API at http://localhost:5253
# → Web at http://localhost:5173
# → DB + DataProtection keys auto-created under ~/.devflow/
```

Make sure the `claude` CLI on `$PATH` is logged in (run `claude` once interactively if not) — triage shells out to it.

To run pieces individually without Aspire: `dotnet watch --project src/Kagura.Api` and, in another terminal, `npm --prefix web/kagura-web run dev`.

In the UI:

1. **Sources** → Add a source pointing at a local repo clone.
2. Click **Sync** — Markdown sources scan `<repo>/.devflow/issues/*.md`. Each `.md` becomes a WorkItem.
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
  "Devflow": {
    "MaxConcurrentAgents": 3,             // SemaphoreSlim cap on parallel claude PTYs
    "WorktreesRoot": "~/.devflow/worktrees",
    "DbPath": "~/.devflow/kagura.db",
    "ClaudeBinary": "claude",             // anything resolvable on $PATH
    "TranscriptsRoot": "~/.devflow/transcripts"
  },
  "Triage": {
    "Model": null                         // optional; passes --model to `claude -p` when set
  }
}
```

Sources themselves (GitHub tokens, ADO PATs, Markdown paths, etc.) live in the **DB**, not in config. The whole `ConfigJson` column is encrypted with `Microsoft.AspNetCore.DataProtection` (keys under `~/.devflow/keys/`).

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

## Development

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
| `~/.devflow/kagura.db` | SQLite, all entities |
| `~/.devflow/keys/` | DataProtection master keys (do not commit, do not share) |
| `~/.devflow/worktrees/<wi-slug>/<task-slug>/` | Per-task git worktree where `claude` runs |
| `~/.devflow/transcripts/<wi-id>/<task-id>_<run-id>.log` | Raw PTY transcript for replay on reconnect |

The DB path, worktree root, and transcript root are all overridable in `appsettings.json`. Move them all together if you want a different "profile."

### Resetting

The app is stateless beyond `~/.devflow/`. To start over:

```bash
# nuke local state (will lose all sources, work items, transcripts, encryption keys)
rm -rf ~/.devflow
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
