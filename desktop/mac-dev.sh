#!/usr/bin/env bash
# The desktop app on this Mac, from main as it is now, against a dev server:
# pulls, builds Manage and the app (Unity, this Mac's), and opens it set to
# play at the server.
#
#   desktop/mac-dev.sh                       pull, build, open at http://jbhs:5173
#   desktop/mac-dev.sh --server http://other:5173
#   desktop/mac-dev.sh --server live         the live server again
#   desktop/mac-dev.sh --no-pull             as the checkout is (your own changes)
#   desktop/mac-dev.sh --build               build even if this commit was built last time
#   desktop/mac-dev.sh --open                no pull, no build: open the last build
#
# The server: ./dev.sh --host jbhs --no-game on the machine called jbhs (on the
# tailnet), which serves the game server, sign-in (dev / dev) and Manage all at
# http://jbhs:5173. The app keeps the server it's given (it's also in its
# Settings, from the sign-in card or the sidebar) and a separate sign-in for each.
#
# Needs: Unity (the version in unity/ProjectSettings/ProjectVersion.txt, with
# Mac Build Support) from the Hub, Node 23.6+, and the editor not open on unity/.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SERVER="${PORCHLIGHT_SERVER:-http://jbhs:5173}"
PULL=1
BUILD=auto
while [ $# -gt 0 ]; do
  case "$1" in
    --server) SERVER="${2:?--server needs an address, e.g. http://jbhs:5173, or live}"; shift ;;
    --no-pull) PULL=0 ;;
    --build) BUILD=yes ;;
    --open) PULL=0; BUILD=no ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1 (see desktop/mac-dev.sh --help)" >&2; exit 2 ;;
  esac
  shift
done
[ "$SERVER" = live ] && SERVER=default

VERSION="$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity}"
APP="$ROOT/unity/Builds/macOS-app/Porchlight.app"
STAMP="$ROOT/unity/Builds/macOS-app/.commit"
LOG="$ROOT/unity/Logs/build-mac.log"

say() { printf '\033[1m%s\033[0m\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

[ "$(uname)" = Darwin ] || die "this is for a Mac (it builds the macOS app)."

# 1. The latest main.
if [ "$PULL" = 1 ]; then
  cd "$ROOT"
  if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    git status --short --untracked-files=no
    die "the checkout has changes of its own (above): commit or stash them, or run with --no-pull to build it as it is."
  fi
  say "Pulling..."
  git pull --ff-only
fi
COMMIT="$(git -C "$ROOT" rev-parse HEAD)"
say "At $(git -C "$ROOT" log -1 --format='%h %s')"

if [ "$BUILD" = auto ]; then
  if [ -d "$APP" ] && [ "$(cat "$STAMP" 2>/dev/null)" = "$COMMIT" ] && [ -z "$(git -C "$ROOT" status --porcelain --untracked-files=no)" ]; then
    BUILD=no
    say "Already built at this commit (--build to build again)."
  else
    BUILD=yes
  fi
fi

if [ "$BUILD" = yes ]; then
  # 2. Manage Porchlight, into the game's StreamingAssets, so the app carries it.
  command -v node >/dev/null 2>&1 || die "Node.js isn't installed (brew install node)."
  say "Building Manage..."
  if [ ! -d "$ROOT/manage/node_modules" ] || [ "$ROOT/manage/package-lock.json" -nt "$ROOT/manage/node_modules/.package-lock.json" ]; then
    npm --prefix "$ROOT/manage" ci
  fi
  npm --prefix "$ROOT/manage" run build

  # 3. The app.
  [ -x "$UNITY" ] || die "no Unity $VERSION at $UNITY: install it from the Hub (with Mac Build Support), or set UNITY."
  [ -d "$(dirname "$UNITY")/../../../PlaybackEngines/MacStandaloneSupport" ] \
    || echo "warning: Unity $VERSION may not have Mac Build Support (add it in the Hub: Installs > ⚙ > Add modules)." >&2
  if pgrep -f "Unity.*-projectpath $ROOT/unity" >/dev/null 2>&1 || pgrep -f "Unity.*-projectPath $ROOT/unity" >/dev/null 2>&1; then
    die "the Unity editor has unity/ open; close it (a build needs the project to itself)."
  fi
  say "Building the app (Unity; several minutes, longer the first time). Log: $LOG"
  mkdir -p "$(dirname "$LOG")"
  rm -f "$STAMP"
  # A build touches some of the project's own files (its icon and settings): put back any it changed, so the next pull goes through.
  before="$(git -C "$ROOT" diff --name-only)"
  if ! "$UNITY" -batchmode -projectPath "$ROOT/unity" \
      -executeMethod Porchlight.EditorTools.Setup.BuildMacApp \
      -porchlightCommit "$COMMIT" -logFile "$LOG"; then
    grep -E "error CS|Error|Exception" "$LOG" | tail -20 >&2 || true
    die "the build failed; the whole log is $LOG"
  fi
  touched="$(comm -13 <(printf '%s\n' "$before" | sort) <(git -C "$ROOT" diff --name-only | sort))"
  if [ -n "$touched" ]; then
    printf '%s\n' "$touched" | (cd "$ROOT" && xargs git checkout --)
    echo "(put back what the build changed: $(echo $touched))"
  fi
  [ -d "$APP" ] || die "the build said it worked, but there's no $APP; see $LOG"
  echo "$COMMIT" > "$STAMP"
fi
[ -d "$APP" ] || die "no app built yet at $APP (run without --open)."

# 4. Is the server there?
if [ "$SERVER" != default ]; then
  code="$(curl -s -o /dev/null -m 5 -w '%{http_code}' "${SERVER%/}/auth/me" || true)"
  case "$code" in
    200|401) say "The server at $SERVER is answering." ;;
    000) echo "warning: nothing answered at $SERVER; is ./dev.sh --host running there? Opening anyway." >&2 ;;
    *) echo "warning: $SERVER/auth/me answered $code, not a Porchlight server's 401. Opening anyway." >&2 ;;
  esac
fi

# 5. Open it (closing the last one first), set to play there.
if pgrep -f "$APP/Contents/MacOS/" >/dev/null 2>&1; then
  say "Closing the one already open..."
  pkill -f "$APP/Contents/MacOS/" || true
  for _ in 1 2 3 4 5 6 7 8 9 10; do pgrep -f "$APP/Contents/MacOS/" >/dev/null 2>&1 || break; sleep 0.5; done
fi
say "Opening Porchlight ($( [ "$SERVER" = default ] && echo "the live server" || echo "$SERVER"))..."
open -n "$APP" --args -porchlightServer "$SERVER"
echo "Its log: ~/Library/Logs/Porchlight/Porchlight/Player.log"
