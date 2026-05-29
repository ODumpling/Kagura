---
id: usage
title: Usage guide
sidebar_label: Usage
sidebar_position: 2
description: Day-to-day flows in Kagura — filtering, triage, the Closed status, and Ralph Loop.
---

# Usage guide

Kagura's day-to-day surface is two pages: **Work items** (the list) and the
**work-item detail** page (one item, its tasks, and the actions that drive it
toward a PR). This guide walks the four flows you'll touch most often.

> Prerequisite: a source is configured and synced. See the [Setup guide](./setup.md)
> if you haven't done that yet — without at least one synced source the Work
> items page will be empty.

## Filter work items by status

The work-items list defaults to **Active**, which hides anything in the
`Closed` state. That keeps the list focused on items you can still act on.

**Steps**

1. Open **Work items** from the sidebar.
2. Use the **Status** dropdown in the top-right of the page.
3. Pick one of:
   - **Active (hide closed)** — default. Shows every status except `Closed`.
   - **All statuses** — shows everything, including `Closed` items.
   - A specific status (`New`, `Triaged`, `In Progress`, `Merged`, `PR Open`,
     `Done`, `Cancelled`, `Closed`) — scopes the list to that single status.
4. The choice is reflected in the URL (`?status=…`) so the view is
   bookmarkable and shareable.

The same filter is available from the API:

```text
GET /api/workitems              # default: hides Closed
GET /api/workitems?status=2     # only InProgress (enum value)
GET /api/workitems?includeClosed=true   # all statuses, including Closed
```

## Triage an issue into tasks

Triage is what turns a freshly-imported `New` work item into a set of small,
parallelisable `AgentTask`s. It shells out to the `claude` CLI under the
hood, so the call takes a few seconds.

**Steps**

1. From **Work items**, click the title of a `New` item to open its detail
   page.
2. Read the **Body** tab to refresh your memory on the request.
3. Switch to the **Board** tab and click **Triage** in the top-right.
4. Wait for the "Running triage…" banner to disappear (typically 5–30s).
   Proposed tasks appear in the `Proposed` column of the board.
5. Review the proposed tasks. You can drag a card between columns, open a
   card to edit its title/description, or delete a task you don't want.
6. When the set looks right, click **Approve all** (top-right) to move every
   `Proposed` task to `Approved`. Approved tasks are eligible to start.
7. From here you can either:
   - Click **Start all** to spawn `claude` PTYs in each task's worktree
     (up to `MaxConcurrentAgents` at a time), or
   - Hand the whole pipeline over to **Ralph Loop** (see below).

After triage the work item's status moves from `New` → `Triaged`, and the
work-item branch `kagura/<external-id>-<slug>` is created off the repo's
default branch.

## Closed work items

A work item becomes `Closed` automatically when its upstream issue or PR
disappears from the source's next sync (e.g. the GitHub issue was closed,
or the Markdown file was deleted). The transition stamps `ClosedAt`, and a
background job deletes the row seven days later.

**What changes in the UI when an item is Closed**

- The **Closed** badge renders on both the list row and the detail header.
- On the detail page the **Triage** and **Finish / Open PR** buttons are
  disabled — there is nothing more to do.
- The **Ralph Loop** button is hidden entirely.
- The item is filtered out of the default **Active** list view (see above).

**Steps to view Closed items again**

1. Open **Work items**.
2. Set the **Status** dropdown to **All statuses** or **Closed**.
3. Click into any Closed item to read its body and task history — every
   field is still there; only the action buttons are gated off.

If a Closed item appears unexpectedly, run the source's **Sync** again — the
status is recomputed from the upstream provider each sync.

## Drive a work item to PR with Ralph Loop

**Ralph Loop** is the one-click "carry this to PR" automation. After triage
it walks the tasks in `Order`: starts each agent, waits for it to land in
`AwaitingReview`, merges it into the work-item branch, then opens the PR
once every task is merged. It retries a failing task up to three times,
injecting the previous failure into the next prompt, before halting for a
human.

**Preconditions**

- Work item must be `Triaged` or `In Progress` (not `Closed`, not
  `PR Open`).
- 1–3 tasks on the work item. The button is disabled with a tooltip if
  there are more than 3.
- Not every task is already `Merged`.

**Steps**

1. Triage the work item and approve the tasks (see the previous flow).
2. Click **Ralph Loop** in the top-right of the detail page.
3. A blue banner appears showing the current stage, e.g.:
   - "Running task '…'"
   - "Merging task '…'"
   - "Opening pull request…"
   - The `attempt N/3` counter on the right increments on retries.
4. While the loop is active, the other action buttons (Triage, Start all,
   Approve all, Finish) are replaced by a single red **Cancel Ralph Loop**
   button. Cancel does **not** kill in-flight agents — they finish their
   current attempt; the loop just stops advancing.
5. On success, the work item moves to `PR Open` and a **View PR** button
   appears.
6. On failure (three exhausted retries, or a non-recoverable error) the
   banner turns red and shows the halt reason. The task that failed is left
   in `Failed`. Investigate, push fixes manually if needed, then click
   **Retry Ralph Loop** to resume — Failed tasks are reset to `Approved`
   with a fresh retry budget.

> Ralph Loop ticks every 5 seconds. There's no need to refresh the page —
> the banner and task board update over SignalR as the state machine
> advances.

## Where to go next

- The [Setup guide](./setup.md) covers installing prerequisites, adding a
  source, and the markdown issue format.
- The project [`README.md`](https://github.com/ODumpling/Kagura/blob/main/README.md)
  has the REST API reference and the on-disk layout under `~/.kagura/`.
