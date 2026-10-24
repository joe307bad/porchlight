#!/usr/bin/env bash
# Runs the Porchlight app locally with the Vite dev server.
set -euo pipefail

if ! command -v node >/dev/null 2>&1; then
  echo "error: Node.js is not installed. Install Node 20+ (https://nodejs.org) and try again." >&2
  exit 1
fi

cd "$(dirname "$0")/app"

if [ ! -d node_modules ]; then
  echo "Installing dependencies..."
  npm install
fi

echo "Starting Porchlight at http://localhost:5173/"
exec npm run dev
