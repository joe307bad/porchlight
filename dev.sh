#!/usr/bin/env bash
# Runs Porchlight locally: Keycloak and the game database (Docker) with a
# seeded user, the auth + game server, and the game (the Unity WebGL build)
# at http://localhost:5173/.
#
# Sign in as dev / dev. Without Docker there's no sign-in (and so no
# multiplayer): the game opens straight away and plays alone.
#
# The game is built from unity/ if there's no build yet (or with --build);
# that needs Unity 6000.6.5f1 (set UNITY to its executable if it isn't in the
# Hub's usual place).
set -euo pipefail

if ! command -v node >/dev/null 2>&1; then
  echo "error: Node.js is not installed. Install Node 23.6+ (https://nodejs.org) and try again." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/unity/Builds/WebGL"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")/Unity.app/Contents/MacOS/Unity}"

if [ ! -d "$ROOT/server/node_modules" ]; then
  echo "Installing server dependencies..."
  npm --prefix "$ROOT/server" install
fi

if [ "${1:-}" = "--build" ] || [ ! -f "$BUILD/index.html" ]; then
  if [ ! -x "$UNITY" ]; then
    echo "error: no WebGL build in unity/Builds/WebGL, and no Unity at $UNITY to make one." >&2
    echo "Install Unity $(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt") with WebGL support, or set UNITY." >&2
    exit 1
  fi
  echo "Building the game (Unity WebGL; a few minutes the first time)..."
  "$UNITY" -batchmode -nographics -projectPath "$ROOT/unity" \
    -executeMethod Porchlight.EditorTools.Setup.BuildWebGL -logFile "$ROOT/unity/Logs/build-webgl.log" \
    || { echo "error: the build failed; see unity/Logs/build-webgl.log" >&2; exit 1; }
fi

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  export COMPOSE="docker compose -f $ROOT/infra/dev/docker-compose.yml"
  echo "Starting Keycloak at http://localhost:8080/ and the game database ..."
  $COMPOSE up -d --wait db
  $COMPOSE up -d
  "$ROOT/infra/keycloak/init-keycloak.sh" dev

  npm --prefix "$ROOT/server" run dev &
  AUTH_PID=$!
  trap 'kill $AUTH_PID 2>/dev/null || true' EXIT
else
  echo "warning: Docker isn't running, so there's no Keycloak; sign-in (and so multiplayer) is off for this run." >&2
fi

echo "Starting Porchlight at http://localhost:5173/  (sign in as dev / dev)"
node "$ROOT/unity/Tools~/serve.mjs" "$BUILD"
