#!/usr/bin/env bash
# Runs Porchlight locally: Keycloak and the game database (Docker) with a
# seeded user, the auth + game server, and the game (the Unity WebGL build)
# at http://localhost:5173/.
#
# Sign in as dev / dev. Without Docker there's no sign-in (and so no
# multiplayer): the game opens straight away and plays alone.
#
# The game is built from unity/ if there's no build yet, if anything in the
# project is newer than the build, or with --build;
# that needs Unity 6000.6.5f1 (set UNITY to its executable if it isn't in the
# Hub's usual place).
#
#   ./dev.sh                          at http://localhost:5173/
#   ./dev.sh --host jbhs --no-game    at http://jbhs:5173/, for other machines (a desktop
#                                     app built on a Mac, set to that server), without the web game
#   ./dev.sh --build                  build the web game first, even if it looks up to date
#
# One address carries it all: the game, the server (/auth, /mp), Manage (/manage/)
# and Keycloak's sign-in pages (/realms/...), so that's all another machine needs.
set -euo pipefail

BUILD_GAME=auto
PUBLIC_HOST=localhost
while [ $# -gt 0 ]; do
  case "$1" in
    --build) BUILD_GAME=yes ;;
    --no-game) BUILD_GAME=no ;;
    --host) PUBLIC_HOST="${2:?--host needs a name, e.g. --host jbhs}"; shift ;;
    *) echo "unknown option: $1 (see the top of dev.sh)" >&2; exit 2 ;;
  esac
  shift
done
export PUBLIC_URL="http://$PUBLIC_HOST:5173"
# The server's addresses for this run (they take the place of server/.env.development's).
export APP_URL="$PUBLIC_URL"
export KEYCLOAK_ISSUER="$PUBLIC_URL/realms/porchlight"
export KEYCLOAK_INTERNAL_URL="http://localhost:8080/realms/porchlight"
export EDITORS="*"
# Anywhere but localhost: listen for other machines too.
if [ "$PUBLIC_HOST" != localhost ]; then export HOST=0.0.0.0; fi

if ! command -v node >/dev/null 2>&1; then
  echo "error: Node.js is not installed. Install Node 23.6+ (https://nodejs.org) and try again." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/unity/Builds/WebGL"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")/Unity.app/Contents/MacOS/Unity}"

if [ ! -d "$ROOT/server/node_modules" ]; then
  echo "Installing server dependencies..."
  npm --prefix "$ROOT/server" ci
fi

# The towns' traffic and townsfolk, run by the server (sim/, .NET): built when dotnet's here and it's
# missing or out of date. Without it the server lets each town's players run it, as before.
SIM_DLL="$ROOT/sim/bin/Release/net8.0/porchlight-sim.dll"
# Where .NET's installers put it when it isn't on the PATH (the Mac's .pkg, Homebrew, dotnet-install.sh).
for d in /usr/local/share/dotnet /opt/homebrew/bin /usr/local/bin "$HOME/.dotnet"; do
  if ! command -v dotnet >/dev/null 2>&1 && [ -x "$d/dotnet" ]; then export PATH="$d:$PATH"; export DOTNET_ROOT="${DOTNET_ROOT:-$d}"; fi
done
if command -v dotnet >/dev/null 2>&1 && [ -z "$(dotnet --list-sdks 2>/dev/null)" ]; then
  echo "warning: dotnet is here but has no SDK (only a runtime), so the town simulation can't be built." >&2
  echo "         Install the .NET SDK, 8 or newer (on a Mac: brew install --cask dotnet-sdk)." >&2
fi
if command -v dotnet >/dev/null 2>&1; then
  if [ ! -f "$SIM_DLL" ] || [ -n "$(find "$ROOT/sim/src" "$ROOT/sim/Porchlight.Sim.csproj" "$ROOT/unity/Assets/Porchlight/Runtime/Shared" -newer "$SIM_DLL" -name '*.cs*' -print -quit)" ]; then
    echo "Building the town simulation (sim/)..."
    dotnet build "$ROOT/sim/Porchlight.Sim.csproj" -c Release -nologo -v quiet \
      || echo "warning: the town simulation didn't build; the players will run their towns" >&2
  fi
else
  echo "" >&2
  echo "warning: no dotnet, so no town simulation on the server: in Manage there'll be no cars, townsfolk," >&2
  echo "         traffic lights, stop signs, people in buildings or clicking on things, and in the game each" >&2
  echo "         town's players run its traffic. Install the .NET SDK, 8 or newer (on a Mac:" >&2
  echo "         brew install --cask dotnet-sdk), then start ./dev.sh again." >&2
  echo "" >&2
fi

# Rebuild when there's no build, when asked, or when the project has changed since the last one.
stale() {
  [ ! -f "$BUILD/index.html" ] && return 0
  [ -n "$(find "$ROOT/unity/Assets" "$ROOT/unity/Packages" "$ROOT/unity/ProjectSettings" \
    -path "$ROOT/unity/Assets/StreamingAssets" -prune -o -type f -newer "$BUILD/index.html" -print -quit)" ]
}

# Manage Porchlight (manage/, at /manage/): built into the game's StreamingAssets when it's missing or out of date.
MANAGE="$ROOT/unity/Assets/StreamingAssets/manage/index.html"
if [ ! -d "$ROOT/manage/node_modules" ]; then
  echo "Installing Manage's dependencies..."
  npm --prefix "$ROOT/manage" ci
fi
if [ ! -f "$MANAGE" ] || [ -n "$(find "$ROOT/manage/src" "$ROOT/manage/index.html" "$ROOT/manage/package.json" -newer "$MANAGE" -print -quit)" ]; then
  echo "Building Manage Porchlight..."
  npm --prefix "$ROOT/manage" run build
fi

if [ "$BUILD_GAME" = no ]; then
  echo "Not serving the web game (--no-game): the server, sign-in and Manage only."
elif [ "$BUILD_GAME" = yes ] || stale; then
  if pgrep -f "Unity.app/Contents/MacOS/Unity.*-projectpath $ROOT/unity" >/dev/null 2>&1; then
    if [ ! -f "$BUILD/index.html" ]; then
      echo "error: there's no build yet, and the Unity editor has unity/ open so one can't be made; close it and run this again." >&2
      exit 1
    fi
    echo "warning: the build in unity/Builds/WebGL is older than the project, but the Unity editor has unity/ open" >&2
    echo "         so it can't be rebuilt; serving the OLD build. Close the editor and run this again for the latest." >&2
  else
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
  export NO_GAME_SERVER=1
fi

echo "Starting Porchlight at $PUBLIC_URL/  (sign in as dev / dev)"
node "$ROOT/unity/Tools~/serve.mjs" "$BUILD"
