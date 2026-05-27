#!/usr/bin/env bash
set -euo pipefail
exec dotnet run --project src/Kagura.AppHost "$@"
