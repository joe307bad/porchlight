#!/bin/bash
# Trash day, played headless in the native macOS player: builds the player
# (Setup.BuildMac, which rebuilds the model library first if it's missing a
# model, the wheelie bins included), compiles server/missions/trash-day.ink as
# the server does, and runs the player's smoke run on it
# (PORCHLIGHT_SMOKE_TRASH): the garbage truck parked by each of five bins, and
# the crew followed from hopping out to hopping back in. Exits 0 if it passed.
#
#   unity/Tools~/trash-smoke.sh              build, then play
#   unity/Tools~/trash-smoke.sh --no-build   play the player already built
#
# Needs the Unity editor closed (a batch build can't share the project).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.6.5f1/Unity.app/Contents/MacOS/Unity}"
APP="$ROOT/unity/Builds/macOS/Porchlight.app/Contents/MacOS/Porchlight"
OUT="${TMPDIR:-/tmp}/porchlight-trash-smoke"
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

echo "compiling trash-day.ink..."
(cd "$ROOT/server" && node scripts/mission.ts trash-day 2>/dev/null) > "$OUT/trash-day.json"

echo "playing it (log: $OUT/player.log)..."
status=0
PORCHLIGHT_SMOKE=1 PORCHLIGHT_SMOKE_TRASH="$OUT/trash-day.json" \
  perl -e 'alarm 300; exec @ARGV' "$APP" -batchmode -logFile "$OUT/player.log" || status=$?
awk '/smoke: trash:|Exception|porchlight: mission/' "$OUT/player.log"
if [ "$status" -eq 0 ] && awk '/smoke: trash: PASS/ { found = 1 } END { exit !found }' "$OUT/player.log"; then
  echo "trash day: passed"
else
  echo "trash day: FAILED (player exit $status)"
  exit 1
fi
