# All Claude interactions are PTY Agents with a typed MCP result contract

## Status

Accepted

## Context

Kagura started with two execution models for `claude`: **Task Agents** running in attached PTYs (xterm.js + SignalR + `Porta.Pty`), and **everything else** — Triage, AutoReview, Grill, MergeResolver — running as one-shot `claude -p` invocations whose stdout was parsed for JSON. Two runners, two debug stories, two ways to surface "what is Claude doing right now", and the non-Task roles were invisible and un-steerable.

## Decision

Every `claude` invocation in Kagura is an **Agent**: a PTY-attachable session the user can observe, type into, and stop. Each Agent has a **Role** (`Triage`, `Task`, `AutoReview`, `Grill`, `MergeResolver`) that fixes its prompt and the structured result it must produce. Structured results are returned via a **Lean MCP server** that Kagura runs locally and attaches to every Claude session — one MCP submission tool per Role (e.g. `kagura.submit_triage`). The MCP tool call is both the completion signal and the structured payload; there is no stdout parsing, no result file, no ad-hoc HTTP endpoint. The MCP server exposes **only** submission tools — it does not wrap `gh`/`git`, it does not expose read-context tools, and it does not let Agents mutate Kagura state.

## Considered alternatives

- **Keep the split** (Task = PTY, others = one-shot). Cheaper in the short run, but the user explicitly asked for every role to be interactive, configurable, and visible — the split makes that a per-role retrofit forever.
- **Result file in the worktree** instead of MCP. Simpler infra, but doesn't give us a typed schema-validated contract, and conflates the agent's *work product* (files) with its *return value* (structured result).
- **HTTP POST submission** instead of MCP. Same shape as the existing `curl COMPLETE_URL` pattern. Works, but loses the schema-typed tool surface MCP provides — and we already need to ship MCP for future tools whether we like it or not.
- **Rich / Full MCP** (read tools, side-effect tools). Rejected to avoid duplicating `gh`/`git` and to keep Ralph Loop as the single orchestrator. Lean MCP can grow later; over-broad MCP can't easily shrink.

## Consequences

- One runner (`AgentRunner`), one transcript format, one "what's happening" surface (the sidebar tree).
- The MCP server is a hard dependency at every Agent spawn — failure to attach it means no Agent can complete.
- Every Role's prompt must teach Claude to call its specific submission tool. Prompts grow slightly; in exchange they become self-describing.
- The existing `ITriageService` / `IReviewService` / `IGrillService` / `IMergeConflictResolver` interfaces either disappear or become thin adapters over `AgentRunner` that block on the MCP submission.
- Reading context for an Agent stays via **prompt interpolation**, since MCP is deliberately submission-only.
