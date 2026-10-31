#!/usr/bin/env bash
# Local speed gate. The baseline is specific to THIS machine, so it is not committed.
#   scripts/bench-gate.sh --save   record a baseline (run it on a quiet machine, before your change)
#   scripts/bench-gate.sh          run the benchmark and fail (exit 2) if any scenario is >15% slower
set -euo pipefail
cd "$(dirname "$0")/.."
BASE=bench/baseline.local.json
OUT=bench/last.json

echo "Running benchmarks..."
echo "  baseline: $BASE"
echo "  output:   $OUT"

if [ "${1:-}" = "--save" ]; then
  dotnet run -c Release --project bench/LanShare.Bench -- --out "$BASE"
  echo "Baseline saved to $BASE"
  exit 0
fi

if [ ! -f "$BASE" ]; then
  echo "No baseline yet. Run: scripts/bench-gate.sh --save"
  exit 1
fi

dotnet run -c Release --project bench/LanShare.Bench -- --out "$OUT" --baseline "$BASE" --tolerance 0.15