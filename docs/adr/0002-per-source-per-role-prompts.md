# Prompts are configured per-Source, per-Role, with lazy default lookup

## Status

Accepted

## Context

Before this decision the prompt template for Task Agents lived as a single static string on `AgentRunnerOptions.PromptTemplate`, and Triage / AutoReview / Grill each had a hardcoded `SystemPrompt` constant inside their service. Customising any of them meant editing C# and redeploying. With every Role becoming an Agent (ADR 0001), the question of *where* a Role's prompt lives — and at what scope it can be overridden — needed an answer.

Different `Source`s have legitimately different needs: a frontend React repo's notion of "small enough for one agent" is not the same as a backend .NET service's, and the right review/merge discipline varies by codebase too. Global-only prompts force a lowest-common-denominator wording; per-WorkItem prompts add UI surface for marginal value in a single-user app.

## Decision

Prompts are configured **per-Source, per-Role**. Each Source carries an optional custom prompt text for each Role; if unset, Kagura resolves the **current built-in default** for that Role at Agent spawn time (lazy lookup, not a one-time copy). New built-in defaults therefore flow through automatically to every Source that hasn't customised that Role.

Every `AgentRun` snapshots the **resolved post-interpolation prompt** onto the run record, so the audit trail of past runs is unaffected by later prompt edits.

## Considered alternatives

- **Global per-Role.** One Triage prompt site-wide. Too rigid — different repos genuinely want different rules.
- **Per-WorkItem.** Per-item overrides on top of source. Overkill for single-user; the `Grill` role already exists for refining individual items.
- **Hierarchical (Global → Source → WorkItem).** Three layers of "where did this prompt come from?" Debug nightmare.
- **Eager copy of defaults at Source creation** instead of lazy lookup. Loses the ability to evolve built-in defaults — every Source becomes a frozen snapshot the moment it was created.

## Consequences

- Adding a new Role later only requires shipping a new built-in default; existing Sources pick it up automatically without migration.
- A Source's "Prompts" tab in the UI shows each Role with a "Using default" badge until customised, and a "Reset to default" affordance returns to the lazy-lookup state.
- The prompt-snapshot on `AgentRun` is the source of truth for "what actually ran" — editing a Source's prompt has no effect on past runs.
- A misedited Source-level prompt only affects that Source's future Agents, which limits blast radius vs a global edit.
