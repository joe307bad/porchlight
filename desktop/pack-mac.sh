#!/usr/bin/env bash
# Package the macOS desktop app for Velopack (desktop.yml runs this; it works
# on any Mac too). Takes the Unity player's Porchlight.app, puts the launcher
# in it as the app's own executable (the game becomes its neighbour), and has
# vpk pack it, signed ad hoc (no Apple account: Gatekeeper asks once), with a
# delta from whatever release is already in the output folder.
#
#   desktop/pack-mac.sh <Porchlight.app> <version> <output dir>
#
# Needs: the .NET 10 SDK, Xcode's command line tools, and vpk on PATH
# (dotnet tool install -g vpk --version 1.2.161).
set -euo pipefail
APP_IN=$1 VERSION=$2 OUT=$3
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

for rid in osx-arm64 osx-x64; do
  dotnet publish "$HERE/Launcher" -c Release -r "$rid" -o "$WORK/$rid" -p:Version="$VERSION" --nologo -v quiet
done

APP="$WORK/Porchlight.app"
cp -R "$APP_IN" "$APP"
lipo -create -output "$APP/Contents/MacOS/PorchlightLauncher" "$WORK/osx-arm64/PorchlightLauncher" "$WORK/osx-x64/PorchlightLauncher"
chmod +x "$APP/Contents/MacOS/PorchlightLauncher" "$APP/Contents/MacOS/Porchlight"
# Opening the app starts the launcher, which starts the game in its place.
/usr/libexec/PlistBuddy -c 'Set :CFBundleExecutable PorchlightLauncher' "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist"
# No App Nap: hidden behind another window, the game still keeps the room's town running and its connection up.
/usr/libexec/PlistBuddy -c 'Delete :NSAppSleepDisabled' "$APP/Contents/Info.plist" 2>/dev/null || true
/usr/libexec/PlistBuddy -c 'Add :NSAppSleepDisabled bool true' "$APP/Contents/Info.plist"

vpk pack --packId Porchlight --packVersion "$VERSION" --packTitle Porchlight --packAuthors Porchlight \
  --packDir "$APP" --mainExe PorchlightLauncher --bundleId com.joebad.porchlight \
  --signAppIdentity - --channel osx --outputDir "$OUT"
