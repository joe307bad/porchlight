# Porchlight

A low-poly town builder. Build towns in the browser, then explore them on the Nintendo Switch.

## Built with

- **Unity 6** (URP): one project that builds the WebGL town builder and the Switch viewer
- **C# shared library** (`shared/`): town model and placement rules used by Unity and the server
- **ASP.NET Core** (.NET 10) + **PostgreSQL**: API that stores towns (`server/`)
- **Fly.io** + **GitHub Actions**: production hosting and deploys (`infra/prod/`)

## Run it locally

Requires Docker, the .NET 10 SDK and Unity 6000.6.5f1 with WebGL Build Support.

```sh
./browser-dev.sh            # Postgres + API on http://localhost:5080
./browser-dev.sh --build    # also builds the WebGL builder first (close the Unity editor)
```

Then either open http://localhost:5080 for the browser builder, or open `unity/` in Unity Hub and press **Play**.

In the editor, **Porchlight > Viewer Mode** previews the Switch experience, and
**Porchlight > Use Production Server** loads towns from production.

**Controls:** arrow keys move, WASD looks around, Space/C go up and down, Shift is faster.

## Tests

```sh
dotnet test shared/tests/Porchlight.Shared.Tests
```

## Deploy

Pushing to `main` deploys to https://porchlight-server.fly.dev via `.github/workflows/deploy.yml`.
It needs the repo secret `FLY_API_TOKEN`. To include the browser builder, it also needs
`UNITY_LICENSE`, `UNITY_EMAIL` and `UNITY_PASSWORD`.
