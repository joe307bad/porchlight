#!/usr/bin/env bash
# Runs Porchlight locally: Keycloak and the game database (Docker) with a
# seeded user, the auth + game server, and the Vite dev server at
# http://localhost:5173/.
#
# Sign in as dev / dev. Without Docker, sign-in is switched off and the app
# opens straight away.
set -euo pipefail

if ! command -v node >/dev/null 2>&1; then
  echo "error: Node.js is not installed. Install Node 23.6+ (https://nodejs.org) and try again." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")" && pwd)"

for dir in app server; do
  if [ ! -d "$ROOT/$dir/node_modules" ]; then
    echo "Installing $dir dependencies..."
    npm --prefix "$ROOT/$dir" install
  fi
done

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
  # Process env beats app/.env.development.
  export VITE_AUTH_ENABLED=false
fi

echo "Starting Porchlight at http://localhost:5173/  (sign in as dev / dev)"
npm --prefix "$ROOT/app" run dev
