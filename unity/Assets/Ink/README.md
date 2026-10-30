# ink runtime

The runtime engine of [ink](https://github.com/inkle/ink), inkle's narrative scripting language, as it is upstream (`ink-engine-runtime/`, version 1.2.1, commit 35c63e5), MIT licensed (LICENSE.txt). It plays the missions: the server compiles their `.ink` (`server/missions`) to ink's JSON, and `Porchlight.Shared`'s `Missions/` runs that JSON with this.

To update it, copy `ink-engine-runtime/*.cs` from a newer ink over `Runtime/` (keeping the `.meta` files), and check that the server's `inkjs` writes a story format (`inkVersion`) this runtime reads.
