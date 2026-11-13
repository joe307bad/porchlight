#!/bin/bash
# Deliver the presents, played headless in the native macOS player: builds
# the player (Setup.BuildMac, which rebuilds the model library first if it's
# missing a model: Santa, the sleigh, the tree and the present included),
# compiles server/missions/deliver-the-presents.ink as the server does, and
# runs the player's smoke run on it (PORCHLIGHT_SMOKE_SANTA): Santa in his
# sleigh high over the town, flown through the ring over each of five houses,
# the story bringing the sleigh down on the roof (down the chimney and back
# up) or in the street (in at the door and back out to the sleigh), a present
# under each tree, and yourself again at the end. Exits 0 if it passed.
#
#   unity/Tools~/santa-smoke.sh              build, then play
#   unity/Tools~/santa-smoke.sh --no-build   play the player already built
#
# Needs the Unity editor closed (a batch build can't share the project).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.6.5f1/Unity.app/Contents/MacOS/Unity}"
APP="$ROOT/unity/Builds/macOS/Porchlight.app/Contents/MacOS/Porchlight"
OUT="${TMPDIR:-/tmp}/porchlight-santa-smoke"
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

echo "compiling deliver-the-presents.ink..."
(cd "$ROOT/server" && node scripts/mission.ts deliver-the-presents 2>/dev/null) > "$OUT/deliver-the-presents.json"

echo "playing it (log: $OUT/player.log)..."
status=0
PORCHLIGHT_SMOKE=1 PORCHLIGHT_SMOKE_SANTA="$OUT/deliver-the-presents.json" \
  perl -e 'alarm 600; exec @ARGV' "$APP" -batchmode -logFile "$OUT/player.log" || status=$?
awk '/smoke: santa:|Exception|porchlight: mission/' "$OUT/player.log"
if [ "$status" -eq 0 ] && awk '/smoke: santa: PASS/ { found = 1 } END { exit !found }' "$OUT/player.log"; then
  echo "deliver the presents: passed"
else
  echo "deliver the presents: FAILED (player exit $status)"
  exit 1
fi
