# server/sim

Where the deployed server finds the town simulation worker (`porchlight-sim`,
built from `sim/` at the repo's root). CI publishes it here, self-contained for
the box (linux-musl-arm64), before the server is copied over; the image copies
this folder in (see the Dockerfile and `.github/workflows/deploy.yml`).

Nothing here is checked in but this note. In development the server runs
`sim/bin/Release/net8.0/porchlight-sim.dll` with `dotnet` instead (`dev.sh`
builds it), or whatever `SIM_COMMAND` says; `SIM=off` runs without one (each
town's players run its traffic, as before).
