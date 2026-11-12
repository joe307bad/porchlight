#!/bin/bash
# The turkey run, played headless in the native macOS player: builds the
# player (Setup.BuildMac, which rebuilds the model library first if it's
# missing a model, the turkeys and the feast included), compiles
# server/missions/turkey-run.ink as the server does, and runs the player's
# smoke run on it (PORCHLIGHT_SMOKE_TURKEY): the pickup's bed piled with
# frozen turkeys, parked outside each of five houses, the helper followed from
# hopping out, taking a turkey off the pile and carrying it in, to hopping
# back in, and the feast on every house's dining table at the end. Exits 0 if
# it passed.
#
#   unity/Tools~/turkey-smoke.sh              build, then play
#   unity/Tools~/turkey-smoke.sh --no-build   play the player already built
#
# Needs the Unity editor closed (a batch build can't share the project).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.6.5f1/Unity.app/Contents/MacOS/Unity}"
APP="$ROOT/unity/Builds/macOS/Porchlight.app/Contents/MacOS/Porchlight"
OUT="${TMPDIR:-/tmp}/porchlight-turkey-smoke"
mkdir -p "$OUT"

if [ "${1:-}" != "--no-build" ]; then
  echo "building the macOS player (log: $OUT/build.log)..."
  "$UNITY" -batchmode -nographics -projectPath "$ROOT/unity" -executeMethod Porchlight.EditorTools.Setup.BuildMac -quit -logFile "$OUT/build.log" || true
  if ! awk '/Porchlight: build Succeeded/ { found = 1 } END { exit !found }' "$OUT/build.log"; then
    echo "the build failed:"
    awk '/error CS|Porchlight: build|BuildFailedException|Exception:/' "$OUT/build.log" | head -20
    exit 1
  fi
fi

echo "compiling turkey-run.ink..."
(cd "$ROOT/server" && node scripts/mission.ts turkey-run 2>/dev/null) > "$OUT/turkey-run.json"

echo "playing it (log: $OUT/player.log)..."
status=0
PORCHLIGHT_SMOKE=1 PORCHLIGHT_SMOKE_TURKEY="$OUT/turkey-run.json" \
  perl -e 'alarm 300; exec @ARGV' "$APP" -batchmode -logFile "$OUT/player.log" || status=$?
awk '/smoke: turkey:|Exception|porchlight: mission/' "$OUT/player.log"
if [ "$status" -eq 0 ] && awk '/smoke: turkey: PASS/ { found = 1 } END { exit !found }' "$OUT/player.log"; then
  echo "turkey run: passed"
else
  echo "turkey run: FAILED (player exit $status)"
  exit 1
fi
