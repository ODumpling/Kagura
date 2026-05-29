# Kagura — Domain Context

Living glossary for Kagura. Add terms as they're pinned during design conversations. Keep entries short and tied to domain meaning, not implementation details.

## Agent

A **PTY-attachable Claude session**. Every interaction with Claude in Kagura is an Agent — there are no "one-shot" Claude invocations.

An Agent has:
- A **role** (see below) that determines its prompt and how its result is interpreted.
- A live PTY the user can attach to, type into, and stop at any time.
- Auto-permission mode from the moment it starts.
- A transcript on disk and a ring buffer in memory for replay.

## Role

The *kind of work* an Agent is doing. Each role has its own configurable prompt template and its own contract for how the Agent signals completion / returns structured results.

Roles: `Triage`, `Task`, `AutoReview`, `Grill`, `MergeResolver`.

The role is fixed at session start and never changes.

## Source tree (navigation)

The primary runtime view of Kagura is a **left sidebar tree** rooted on Sources. Every Source is an expandable node; each currently-running Agent appears as a sub-heading under the Source whose WorkItem it's working on. Clicking an Agent jumps directly to its terminal.

Sources with no active Agents collapse to a single line. The sidebar updates live over SignalR as Agents start and exit.

Each Agent's sub-heading carries a **status line set by the orchestrator** — the Ralph Loop (or whichever service spawned the Agent) writes a short description as it transitions stages, e.g. *"Triage — proposing tasks"*, *"Task gh:42 — merging into work-item branch"*. The status is set from outside the Agent; the MCP server has no `update_status` tool. This keeps MCP strictly submission-only at the cost of a coarser, stage-grained description rather than a live, second-by-second one.

Sources, WorkItems, and Agents form the hierarchy: `Source → WorkItem → Agent`. The sidebar flattens that to `Source → Agent`, with the WorkItem reference carried in the Agent's sub-heading text.

## Agent working directory

Where an Agent's PTY runs determines what context it has when the user attaches and steers it. Policy per Role:

- **Task** — its own worktree under `~/.devflow/worktrees/<wi>/<task>/` on the task branch.
- **MergeResolver** — the work-item's merge worktree (`WorkItemMergeWorktreePath`).
- **AutoReview** — the work-item's merge worktree (where the merged diff is).
- **Triage**, **Grill** — the **Source's scratch worktree** at `~/.devflow/scratch/<source>/`. One long-lived worktree per Source, on a detached HEAD at the default branch. Refreshed (`git fetch && git reset --hard origin/<default>`) before each Agent spawn so the snapshot is current. Survives across Agent runs; pruned when the Source is deleted.

The scratch worktree keeps Triage and Grill out of the user's real working copy. Detached HEAD makes "do not create branches" a stronger hint than a prompt instruction alone. Triage/Grill are still **read-only by convention** — the cwd is a real git checkout, just not the user's own.

## Concurrency

Only **Task** Agents are capped — they share `MaxConcurrentAgents` (default 3) because they run tests, edit files, and spin for tens of minutes. Triage, AutoReview, Grill, and MergeResolver are **unmetered**; they're either short-lived (~30s LLM-bound) or self-throttled by the orchestrator (AutoReview/MergeResolver only run after a specific task event). The system does not protect the user from spamming Triage on themselves.

## Auto-triage

A Source can opt in to spawning a Triage Agent automatically for every `New` WorkItem that arrives via sync. The setting (`AutoTriageOnImport`) lives on the Source, defaults off, and composes with the existing `WorkItem.AutoApproveTriage` field — together they enable a fully hands-off "sync → triage → tasks queued" pipeline per Source.

Without the toggle, Triage stays a manual user click on each new WorkItem.

## Agent collisions

Per WorkItem, at most **one Agent per Role** can be running, with **Task** scoped per-`AgentTask` (so a WorkItem can have multiple Task Agents — one per task). Different Roles may run concurrently on the same WorkItem (e.g. Grill refining the body while Tasks are coding from a body snapshot).

When the user requests an Agent that's already running, the request is refused and the UI links to the live session. The orchestrator (Ralph Loop) is the one entity allowed to chain Roles back-to-back on the same WorkItem — that's what it's *for*.

## Agent lifecycle

When an Agent's PTY exits:

- **Successful exit** — the Agent called its MCP submit tool *and* exited zero. The Agent is auto-dismissed from the sidebar. The transcript and `AgentRun` row persist on disk / in the DB as audit trail.
- **Failure** — non-zero exit, no MCP submission, or any unexpected termination. The Agent **lingers in the sidebar** with its in-memory ring buffer intact, until the user explicitly dismisses it. This is where the user goes to read the last terminal output and figure out what went wrong.
- **User-killed** — counts as successful exit for sidebar purposes (the user did it on purpose). Auto-dismissed.

The sidebar is the *live attention view*; the DB is the audit trail. The two have different jobs.

## Provider sync direction

Sync is **pull-only** for every Source type. Trackers (GitHub, Azure DevOps, Beads, Markdown) are read; nothing flows back. Work item closure is detected by *absence* on the next sync — if an issue stops appearing, the local WorkItem is marked `Closed`. Status writeback (posting PR links or comments back to the tracker) is intentionally deferred — it adds per-provider surface that has nothing to do with the Agent rewrite, and can land as a separate slice with a `WriteStatusAsync` extension to `IIssueProvider`.

For the stub providers:

- **AzureDevOps** — REST + WIQL against `dev.azure.com/{org}/{project}/_apis/wit/wiql`, PAT in `Basic` auth. `AzureDevOpsConfig.Query` is the WIQL hook; if null, defaults to "active work items assigned to me".
- **Beads** — shells out to `bd list --status {Status ?? "open"} --json` in `Source.LocalRepoPath` via the existing `ProcessRunner`. No auth — Beads is repo-local.

## Service interfaces

The existing typed service interfaces (`ITriageService`, `IReviewService`, `IGrillService`, `IMergeConflictResolver`) **survive** as the orchestrator's calling boundary, with their implementations rewritten to spawn an Agent via `AgentRunner` and await its MCP submission. Their signatures are the typed-result abstraction Ralph Loop cares about; the Agent-spawning lifecycle (PTY, sidebar entry, user steering, Stop semantics) is an implementation detail hidden behind them.

A user-initiated Stop on an orchestrated Agent surfaces to the orchestrator as `AgentInterruptedException` from the service call — mapped to Ralph Loop's existing "halted by user" path.

## Stop vs Cancel

Two distinct interruptions:

- **Stop an Agent** — kills that specific PTY. If the Agent was orchestrator-spawned, this **also halts the orchestrator** for that WorkItem (`RalphLoopActive` → false). Mental model: *"I'm taking control."* The user resumes orchestration with the explicit "Retry Ralph Loop" button.
- **Cancel Ralph Loop** — stops orchestration but lets in-flight Agents finish their current run. Mental model: *"Stop adding new work; let the current one finish."*

Stop is finer-grained (one Agent); Cancel is coarser-grained (the whole loop). They are not interchangeable.

Crashes (non-zero exit *without* a Stop click) still flow through the existing `HandleCrashedTasks` retry budget — the Stop-halts-Ralph rule is specifically for **user-initiated** interruption.

## Prompt

The instructions the Agent is launched with. Prompts are **configured per-Source, per-Role** — each Source owns its own copy of the prompt for each Role. There is a built-in default prompt per Role; an unmodified Source resolves that default at launch time (lazy lookup, not a one-time copy), so new built-in defaults flow through automatically until a Source explicitly customises a Role's prompt.

Every AgentRun snapshots the **resolved prompt** (post-interpolation, exactly what the Agent saw) onto the run record. Editing a prompt does not retroactively change the audit trail — past runs always show what they actually ran with.

## MCP transport

The MCP server is **hosted in-process with the API** and exposed over HTTP at `/mcp/{runId}` — one URL per Agent. Identity is structural: Claude is launched with `--mcp-config` pointing at the Agent's own URL, so the server cannot confuse one Agent's submission for another's. No per-Agent bridge process; no caller-asserted identity. A submission to a stale `runId` returns an MCP error, not an HTTP 404.

## Agent result contract

How an Agent returns structured data to Kagura. Kagura runs an **MCP server** exposing one tool per Role (e.g. `kagura.submit_triage`, `kagura.submit_review`). The Claude CLI is launched with the MCP server attached, so the Agent calls a typed, schema-validated tool to hand its result back. The completion signal and the structured payload are the same tool call.

There is no parsing of stdout, no result file on disk, no ad-hoc HTTP endpoint. The MCP tool *is* the contract.

The MCP server is **lean by design** — it only exposes submission tools. It does not wrap `gh`/`git`, does not expose read-context tools, and does not let Agents mutate Kagura state directly. Reading context is still done via prompt interpolation; doing work is still done via the `gh` and `git` CLIs. The MCP surface is the smallest thing that delivers a typed result contract.
