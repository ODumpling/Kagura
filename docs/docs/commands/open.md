---
sidebar_position: 2
title: kagura open
sidebar_label: open
description: Open the Kagura UI in your default browser, spawning the server first if nothing is listening.
---

# `kagura open`

Open the Kagura UI in your default browser. If a server is already
listening on the target port, the browser just opens; if nothing is
listening, `open` spawns a detached `kagura run` first, waits for the port
to come up, then opens the browser.

```bash
kagura open
# spawns `kagura run` if needed, waits up to 10s, then opens http://localhost:5253/
```

Use this when you want one command that "just shows me Kagura" without
caring whether the server is already running.

## Options

| Flag | Default | Notes |
| --- | --- | --- |
| `--port <n>` / `-p <n>` | `5253` | Port the server is on (or will be spawned on). |

## How it works

1. Probe `localhost:<port>` with a 250 ms TCP connect. If something is
   already listening, skip to step 3.
2. Otherwise, spawn `kagura run --port <port>` as a detached child
   process. The spawned server is the same `kagura` binary on your
   `$PATH` — so `kagura open` only works while the global tool is
   installed.
3. Poll the port every 200 ms for up to 10 seconds. If it never comes up,
   exit with `server did not become ready on :<port> within 10s`.
4. Open the URL using `open` (macOS), `xdg-open` (Linux), or
   `cmd /c start` (Windows).

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Browser launched successfully. |
| `1` | Could not spawn `kagura run` (binary missing from PATH), the server never bound to the port, or the browser launcher failed. |

## See also

- [`kagura run`](./run.md) — the underlying server command that `open`
  spawns for you.
