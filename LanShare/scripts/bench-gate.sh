#!/usr/bin/env bash
# Speed gate for the protocol / TLS / CPU path.
#   scripts/bench-gate.sh --save              record a baseline (quiet machine, before your change)
#   scripts/bench-gate.sh                     run and compare; exit 2 if a scenario is >15% slower
# Default mode "ram" works in /dev/shm: stable numbers, no disk effects. This is the regression gate.
#   BENCH_MODE=disk scripts/bench-gate.sh     the same on the real disk (informational: disk speed varies a lot)
# Other: BENCH_SIZE_MB (default 512), BENCH_DIR, BENCH_BASELINE
set -euo pipefail
cd "$(dirname "$0")/.."

MODE=${BENCH_MODE:-ram}
SIZE=${BENCH_SIZE_MB:-512}
EXTRA=(--repeat 3 --size-mb "$SIZE")

if [ "$MODE" = "ram" ]; then
  DIR=${BENCH_DIR:-/dev/shm/lanshare-bench}
  BASE=${BENCH_BASELINE:-bench/baseline.shm.json}
  need=$((SIZE * 3 + 400))   # MB of RAM disk the data set needs
  avail=$(( $(df --output=avail -k /dev/shm | tail -1) / 1024 ))
  if [ "$avail" -lt "$need" ]; then
    echo "Need about ${need} MB free in /dev/shm (have ${avail} MB). Lower BENCH_SIZE_MB or use BENCH_MODE=disk."
    exit 1
  fi
  EXTRA+=(--dir "$DIR")
else
  BASE=${BENCH_BASELINE:-bench/baseline.local.json}
  [ -n "${BENCH_DIR:-}" ] && EXTRA+=(--dir "$BENCH_DIR")
fi
OUT=bench/last.json

if [ "${1:-}" = "--save" ]; then
  dotnet run -c Release --project bench/LanShare.Bench -- "${EXTRA[@]}" --out "$BASE"
  echo "Baseline saved to $BASE"
  exit 0
fi

if [ ! -f "$BASE" ]; then
  echo "No baseline yet. Run: scripts/bench-gate.sh --save"
  exit 1
fi

dotnet run -c Release --project bench/LanShare.Bench -- "${EXTRA[@]}" --out "$OUT" --baseline "$BASE" --tolerance 0.15