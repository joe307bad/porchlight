#!/usr/bin/env bash
# The released desktop app on this Mac, exactly as a player gets it: erases
# every trace of Porchlight that could change how it runs (the app, its
# session, a server a dev build chose, its settings, Velopack's downloads and
# receipts, PORCHLIGHT_* set for apps), then installs the newest release from
# GitHub and opens it, playing on the live server.
#
#   desktop/prod-mac.sh           asks first, then erases, installs and opens
#   desktop/prod-mac.sh --yes     without asking
#   desktop/prod-mac.sh --erase   erase only: no install
#
# The released app can't be pointed at another server (only a dev build can:
# desktop/dev-mac.sh, "Porchlight Dev", whose own folder this leaves alone,
# though it does close it). Installing needs your password once (the .pkg goes
# in /Applications).
set -euo pipefail

REPO=joe307bad/porchlight
PKG_NAME=Porchlight-osx-Setup.pkg
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ASK=1
INSTALL=1
while [ $# -gt 0 ]; do
  case "$1" in
    -y|--yes) ASK=0 ;;
    --erase) INSTALL=0 ;;
    -h|--help) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1 (see desktop/prod-mac.sh --help)" >&2; exit 2 ;;
  esac
  shift
done

say() { printf '\033[1m%s\033[0m\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
[ "$(uname)" = Darwin ] || die "this is for a Mac."

# Where the released app keeps things. Unity's folders are company/product ("Porchlight/Porchlight");
# Velopack's are by its pack id ("Porchlight"); macOS's by bundle id: the release's (com.joebad.porchlight)
# and Unity's default, which builds from before dev-mac.sh had their own (mac-dev.sh's) carried.
BUNDLES=(com.joebad.porchlight com.Porchlight.Porchlight unity.Porchlight.Porchlight)
DATA=(
  "/Applications/Porchlight.app"
  "$HOME/Applications/Porchlight.app"
  "$HOME/Library/Application Support/Porchlight/Porchlight"
  "$HOME/Library/Logs/Porchlight/Porchlight"
  "$HOME/Library/Logs/velopack_Porchlight.log"
  "$HOME/Library/Caches/velopack/Porchlight"
  "$HOME/Library/Caches/Porchlight"
  "/tmp/velopack/Porchlight"
  # mac-dev.sh's build, which shared the released app's folder (the cause of a released app playing on a dev server).
  "$ROOT/unity/Builds/macOS-app/Porchlight.app"
)
for b in "${BUNDLES[@]}"; do
  DATA+=(
    "$HOME/Library/Preferences/$b.plist"
    "$HOME/Library/Saved Application State/$b.savedState"
    "$HOME/Library/Caches/$b"
    "$HOME/Library/HTTPStorages/$b"
    "$HOME/Library/HTTPStorages/$b.binarycookies"
    "$HOME/Library/WebKit/$b"
  )
done
VARS=(PORCHLIGHT_ORIGIN PORCHLIGHT_COOKIE PORCHLIGHT_UPDATES PORCHLIGHT_DESKTOP PORCHLIGHT_HASH PORCHLIGHT_SMOKE_SIGNIN)

found=()
for p in "${DATA[@]}"; do [ -e "$p" ] && found+=("$p"); done
receipts="$(pkgutil --pkgs 2>/dev/null | grep -i porchlight || true)"
set_vars=()
for v in "${VARS[@]}"; do [ -n "$(launchctl getenv "$v" 2>/dev/null)" ] && set_vars+=("$v"); done

say "Erasing Porchlight from this Mac:"
if [ ${#found[@]} -eq 0 ] && [ -z "$receipts" ] && [ ${#set_vars[@]} -eq 0 ]; then
  echo "  (nothing there)"
else
  for p in ${found[@]+"${found[@]}"}; do echo "  $p"; done
  for r in $receipts; do echo "  the installer's receipt $r"; done
  for v in ${set_vars[@]+"${set_vars[@]}"}; do echo "  $v, set for apps (launchctl)"; done
fi
[ "$INSTALL" = 1 ] && echo "then installing the newest release from github.com/$REPO and opening it."
if [ "$ASK" = 1 ]; then
  read -r -p "Go ahead? [y/N] " yes
  case "$yes" in y|Y|yes) ;; *) echo "Nothing done."; exit 1 ;; esac
fi

# The installer and anything under /Applications need root: asked for once, up front.
if [ "$INSTALL" = 1 ] || [ -n "$receipts" ] || [ -e /Applications/Porchlight.app ]; then sudo -v; fi

# 1. Closed, every Porchlight: the released app (its launcher and the game), a dev build, and Velopack updating it.
say "Closing Porchlight..."
for pattern in "/Porchlight.app/Contents/MacOS/" "/Porchlight Dev.app/Contents/MacOS/" "velopack.*Porchlight" "UpdateMac.*Porchlight"; do
  pkill -f "$pattern" 2>/dev/null || true
done
for _ in $(seq 1 20); do
  pgrep -f "/Porchlight( Dev)?\.app/Contents/MacOS/" >/dev/null 2>&1 || break
  sleep 0.5
done
pkill -9 -f "/Porchlight( Dev)?\.app/Contents/MacOS/" 2>/dev/null || true

# 2. Erased.
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
for p in ${found[@]+"${found[@]}"}; do
  case "$p" in *.app) [ -x "$LSREGISTER" ] && "$LSREGISTER" -u "$p" >/dev/null 2>&1 || true ;; esac
  case "$p" in /Applications/*|/tmp/*) sudo rm -rf "$p" ;; *) rm -rf "$p" ;; esac
done
# The preferences daemon keeps its own copy of a plist: told to forget it too.
for b in "${BUNDLES[@]}"; do defaults delete "$b" >/dev/null 2>&1 || true; done
for r in $receipts; do sudo pkgutil --forget "$r" >/dev/null; done
for v in ${set_vars[@]+"${set_vars[@]}"}; do launchctl unsetenv "$v"; done
# Set in this shell, it would only reach an app started from here, which open(1) doesn't do; worth knowing all the same.
for v in "${VARS[@]}"; do [ -n "${!v:-}" ] && echo "note: $v is set in this shell (unset it if you start the app from here)."; done
say "Erased."
[ "$INSTALL" = 1 ] || exit 0

# 3. The newest release, as a player downloads it.
TAG="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1 || true)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
say "Downloading Porchlight ${TAG:-(the newest release)}..."
curl -fL --progress-bar -o "$WORK/$PKG_NAME" "https://github.com/$REPO/releases/latest/download/$PKG_NAME" \
  || die "couldn't download $PKG_NAME from the newest release on github.com/$REPO."

say "Installing..."
sudo installer -pkg "$WORK/$PKG_NAME" -target / >/dev/null
APP=
for a in /Applications/Porchlight.app "$HOME/Applications/Porchlight.app"; do [ -d "$a" ] && APP="$a" && break; done
[ -n "$APP" ] || die "the installer finished, but there's no Porchlight.app in /Applications."
say "Installed $(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist" 2>/dev/null || echo "${TAG:-it}") at $APP."

# 4. Open (Velopack's installer may have opened it already: then this brings it to the front).
open "$APP"
echo "Its logs: ~/Library/Logs/Porchlight/Porchlight/Player.log and ~/Library/Logs/velopack_Porchlight.log"
