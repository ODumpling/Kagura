---
sidebar_position: 1
title: kagura run
sidebar_label: run
description: Start the Kagura server (Kestrel on :5253) and serve the embedded React UI.
---

# `kagura run`

Start the Kagura server. Kestrel binds to `localhost:5253` by default and
serves the embedded React UI on the same port, so once the command prints
its `running at` line you can open the URL in any browser.

```bash
kagura run
# → Kagura running at http://localhost:5253/
```

On first boot Kagura creates the `~/.kagura/` state directory (database,
encryption keys, worktrees, transcripts) and prints a one-time banner
pointing at it. There is no daemon — the process runs in the foreground
and Ctrl-C stops it cleanly.

## Options

| Flag | Default | Notes |
| --- | --- | --- |
| `--port <n>` / `-p <n>` | `5253` | TCP port to listen on. Pick a different one if `:5253` is in use, or if you want to run several Kaguras side-by-side. |
| `--verbose` / `-v` | off | Show the full ASP.NET host logs. Without this, only warnings and errors print, so the terminal stays readable. |
| `--no-update-check` | off | Skip the daily NuGet version check that prints an upgrade banner on startup. |

## Environment variables

| Variable | Equivalent to | Notes |
| --- | --- | --- |
| `KAGURA_LOG_LEVEL=Debug` (or `Trace` / `Information`) | `--verbose` | Anything debug/trace/info promotes the host to verbose logging. |
| `KAGURA_NO_UPDATE_CHECK=1` | `--no-update-check` | Set this in your shell rc to suppress update checks permanently. |

## Behaviour notes

- The port is probed before Kestrel binds, so an in-use port fails with a
  friendly `port 5253 in use — pass --port <n> to override` message rather
  than a raw socket stack trace.
- The update check fires two seconds after startup so the upgrade banner
  appears below Kestrel's `running at` line. Network failure is silent.
- Migrations run on every boot — if you see a `pending migrations` warning
  in [`kagura doctor`](./doctor.md), a `kagura run` is what applies them.

## See also

- [`kagura open`](./open.md) — launches a server if needed *and* opens the
  browser in one step.
- [`kagura doctor`](./doctor.md) — run this first if `kagura run` fails on
  a fresh machine.
