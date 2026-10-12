#!/usr/bin/env bash
# Starts the Porchlight browser dev stack: Postgres in Docker, then the API server, which also
# serves the Unity WebGL build from browser/dist at http://localhost:5080.
#
#   ./browser-dev.sh            start Postgres and the server
#   ./browser-dev.sh --build    also build the WebGL player with Unity first (close the Unity editor)
#
# Day to day, iterate in the Unity editor's Play mode, which talks to the same server. WebGL
# builds take minutes, so only rebuild when you want to check the browser build itself.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$ROOT/infra/dev/docker-compose.yml"
BUILD=0

for arg in "$@"; do
  case "$arg" in
    --build) BUILD=1 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 1 ;;
  esac
done

find_unity() {
  if [[ -n "${UNITY_PATH:-}" ]]; then echo "$UNITY_PATH"; return; fi

  local version_file="$ROOT/unity/ProjectSettings/ProjectVersion.txt"
  if [[ -f "$version_file" ]]; then
    local version
    version="$(awk '/m_EditorVersion:/ {print $2}' "$version_file")"
    local candidate="/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
    if [[ -x "$candidate" ]]; then echo "$candidate"; return; fi
  fi

  local newest
  newest="$(ls -d /Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity 2>/dev/null | sort -V | tail -n 1 || true)"
  if [[ -n "$newest" ]]; then echo "$newest"; return; fi

  echo "Couldn't find Unity. Set UNITY_PATH to the Unity executable." >&2
  exit 1
}

echo "==> Starting Postgres"
docker compose -f "$COMPOSE_FILE" up -d --wait

if [[ "$BUILD" == 1 ]]; then
  UNITY="$(find_unity)"
  echo "==> Building WebGL player with $UNITY (this takes a while)"
  "$UNITY" -batchmode -nographics -quit \
    -projectPath "$ROOT/unity" \
    -executeMethod Porchlight.EditorTools.BuildScripts.BuildWebGL \
    -buildOutput "$ROOT/browser/dist" \
    -logFile -
fi

if [[ ! -f "$ROOT/browser/dist/index.html" ]]; then
  echo "==> No WebGL build in browser/dist yet. The API will run, but there's no page to open."
  echo "    Build with: ./browser-dev.sh --build   (or Porchlight > Build > WebGL in the Unity editor)"
fi

echo "==> Starting server on http://localhost:5080 (Ctrl+C to stop; Postgres keeps running)"
echo "    Stop Postgres with: docker compose -f infra/dev/docker-compose.yml down"
exec dotnet watch --non-interactive --project "$ROOT/server/Porchlight.Server" run
