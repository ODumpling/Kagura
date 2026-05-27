#!/usr/bin/env bash
set -euo pipefail
trap 'kill 0' INT TERM
dotnet watch --project src/Kagura.Api &
npm --prefix web/kagura-web run dev &
wait
