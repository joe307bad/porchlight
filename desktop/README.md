# Porchlight on the desktop

Windows and macOS (Intel and Apple silicon) builds of the same Unity game as
the web version, signing in with Google and playing on the live server
(porchlight.joebad.com), installed and kept up to date by
[Velopack](https://velopack.io).

**Download:** the newest release on GitHub,
<https://github.com/joe307bad/porchlight/releases/latest>:
`Porchlight-win-Setup.exe` (Windows) or `Porchlight-osx-Setup.pkg` (macOS).

## How it's put together

- **Releases.** `.github/workflows/desktop.yml` runs on every push to main:
  - The game is built for Windows and macOS in Unity's Docker image
    (`Setup.BuildWindowsApp` / `BuildMacApp`, with `PORCHLIGHT_DESKTOP` defined).
  - Each is packed by `vpk` on its own system, as version `0.0.<run number>`,
    with a delta from the release before.
  - Both are published as one GitHub release, `v0.0.<n>`.
- **The launcher** (`Launcher/`, a small native .NET program) is what the app
  opens. In order, it:
  1. runs Velopack's startup, which applies an update downloaded last time and
     starts again on it;
  2. sends a copy of itself off to download the next release (only what
     changed, where it can);
  3. waits up to 12 seconds for it: a release downloaded by then is applied
     straight away and the app starts again on it, so one open is enough to be
     on the newest release;
  4. starts the game (macOS: in its own place, so there's one app in the Dock).

  A download slower than that carries on in the background while you play and
  is in place the next time you open the app. Velopack's log is `~/Library/Logs/velopack_Porchlight.log` (macOS) or
  `%LocalAppData%\Porchlight\velopack.log` (Windows).
- **Signing in** goes through the system browser:
  1. The game listens on a one-off port on 127.0.0.1.
  2. It opens `/auth/login?native=<port>&challenge=…`.
  3. After Google, the browser comes back to that port with a one-time code.
  4. The game trades the code, with a verifier only it has, at `POST /auth/native`
     (server/src/app.ts) for the session.
  5. It keeps the session in a file only the player can read
     (`~/Library/Application Support/Porchlight/Porchlight/session`, or
     `%UserProfile%\AppData\LocalLow\Porchlight\Porchlight\session`), and sends
     it as the cookie.

  Signing in or out starts the app afresh, as the web version reloads the page.
- **Burst** can only build a Mac or Windows player on that system, so the
  Linux-built players run its jobs as ordinary managed code (`Setup.NoBurst`).
  Moving the Unity builds onto macOS and Windows runners would bring Burst back.
- **The lantern** (icon and logo) is made from `branding/lantern.png` by
  `branding/make.py`.

## Not signed (yet)

There's no Apple Developer account or Windows code-signing certificate, so the
first install asks you to vouch for it:

- **Windows:** "Windows protected your PC" → *More info* → *Run anyway*.
- **macOS:** the installer "can't be opened" → System Settings → Privacy &
  Security → *Open Anyway*.

Updates after that apply without asking. To sign later:

- macOS: pass `--signAppIdentity`, `--signInstallIdentity` and `--notaryProfile`
  to `vpk pack` in `pack-mac.sh` (an Apple Developer account, $99 a year).
- Windows: pass `--signParams` or `--azureTrustedSignFile` (e.g. Azure Trusted
  Signing).

## Trying it locally (macOS)

```sh
dotnet tool install -g vpk --version 1.2.161
# build: Porchlight > Build desktop app (macOS) in the editor, or batch:
Unity -batchmode -projectPath unity -executeMethod Porchlight.EditorTools.Setup.BuildMacApp -porchlightVersion 0.0.1 -quit
desktop/pack-mac.sh unity/Builds/macOS-app/Porchlight.app 0.0.1 Releases
```

`PORCHLIGHT_UPDATES=<folder>` points an installed launcher at a folder of
releases instead of GitHub, to try an update. `PORCHLIGHT_DESKTOP=1` makes any
build sign in the desktop way, and `PORCHLIGHT_ORIGIN` sets the server.
