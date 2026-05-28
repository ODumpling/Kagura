---
sidebar_position: 4
title: kagura version
sidebar_label: version
description: Print the installed Kagura version and exit.
---

# `kagura version`

Print the installed Kagura version and exit.

```bash
kagura version
# → 0.7.3
```

The version is the package's `AssemblyInformationalVersion`, with any
`+<sha>` build-metadata suffix stripped so the output is a clean
semver — useful for piping into other tools or bug reports.

## When to use it

- Confirming a `dotnet tool update -g Kagura.Cli` actually took effect.
- Filing an issue — paste the output so we know which build you're on.
- Comparing against the latest release on
  [NuGet](https://www.nuget.org/packages/Kagura.Cli) when the daily
  update banner is off.

## See also

- [`kagura run`](./run.md) — the daily startup banner also prints a one-line
  upgrade prompt if a newer version is on NuGet (unless
  `--no-update-check` / `KAGURA_NO_UPDATE_CHECK=1` is set).
