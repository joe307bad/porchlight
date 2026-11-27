# Technical approaches for player home building and customization (Porchlight: Unity + WebGL client, Colyseus server, account server)

Research note: done November 2026. Fetching full pages failed for docs.colyseus.io and gamedeveloper.com because the session's network allowlist blocks those hosts, so their findings come from search-result summaries, not full reads. Claims labelled "Inference" are engineering judgement and have no source.

## Data models for placed furniture/decor (grid vs float, catalog IDs, variants, parent/child, wall items, serialization, versioning)

### Takeaway
Shipped games use two families of model. Palette-indexed dense grids (Minecraft) suit voxel and tile worlds. Sparse lists of entities with typed key/value fields (Valheim ZDOs, Fortnite props attached to building actors) suit freely placed items. For a decor system the best fit is a sparse list of `{catalogId, variant, quantized transform, optional parentId/anchor}` entries in a versioned envelope. Use a schema/version field from day one, as Minecraft's `DataVersion` does.

### Cited Findings
- Minecraft stores world data in NBT, a tree-structured binary format that holds arbitrary data in many of its save files. Java Edition is big-endian and Bedrock is little-endian. — [Minecraft Wiki: NBT format](https://breezewiki.discard.no/minecraft/wiki/NBT_format); [Kaitai NBT spec](https://formats.kaitai.io/minecraft_nbt/graphviz.html)
- A Minecraft chunk's root tag carries a `DataVersion` field, which the game uses to version and migrate saves. Each section stores a palette of block states plus packed indices (`palette.get(block_indices[xzy_index])`), and the bits per index are derived from palette length. — [Lodestone Anvil chunk format 1.16+](https://team-lodestone.github.io/Documentation/Java/Anvil_Docs/1.16+/Chunk_Format); [1.13+](https://team-lodestone.github.io/Documentation/Java/Anvil_Docs/1.13+/Chunk_Format)
- The Sponge Schematic v2 spec, a community format for copy/paste structures, requires GZip-compressed NBT and recommends a palette "to tightly pack the block ids". — [Sponge Schematic v2](https://github.com/SpongePowered/Schematic-Specification/blob/master/versions/schematic-2.md)
- Bedrock packs `floor(32 / bitsPerBlock)` block indices into each 32-bit word. — [Bedrock level format history](https://wiki.ronlab.site/content/minecraftwiki_en_all_maxi_2025-11/Bedrock_Edition_level_format/History)
- Valheim mods that inspect networking show ZDO updates carrying Vector3/Quaternion and other typed fields, with delta compression ("only the fields that changed are sent") and lower update rates for distant structures (beyond about 500 m). These are mod descriptions, not official docs. — [FiresGhettoNetworking](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresGhettoNetworking/v/1.3.2/); [SkadiNet](https://thunderstore.io/c/valheim/p/sighsorry/SkadiNet/)
- Valheim blueprint and structure mods save selections of placed pieces that rebuild from the original piece prefabs, which suggests that a piece-prefab ID plus a transform is enough to persist a build. — [SaveStructure](https://thunderstore.io/c/valheim/p/Dyls_123/SaveStructure/); [PlanBuild](https://thunderstore.io/c/valheim/p/MathiasDecrock/PlanBuild/v/0.14.5)
- In UEFN, props placed on building pieces use an attachment system. The prop's pivot must touch or embed in the building actor so the prop is destroyed along with it, which is a parent/child relationship tied to the host's lifecycle. — [Epic: Grid snapping in UEFN](https://dev.epicgames.com/documentation/en-us/fortnite/using-grid-snapping-in-unreal-editor-for-fortnite)
- Roblox caps a single DataStore key at about 4 MB (2^22-1 = 4,194,303 per a forum answer). This forces large builds to be split across keys or tightly serialized. — [DevForum: DataStore limits](https://devforum.roblox.com/t/game-resourcesdatastore-limits/856634); [Roblox error codes and limits](https://create.roblox.com/docs/cloud-services/data-stores/error-codes-and-limits.md)

### Inferences
- Recommended item record (Inference): `id:u32 (house-local), cat:u16 (catalog index), var:u8 (colour/material variant), pos: i16×3 quantized (e.g. 1 cm or 1/64 grid units relative to house origin), rotY:u8 (0–255 → 1.4° steps) or u2 for 90° snapping, parent:u32? , anchor:u8? (floor/wall/surface slot), flags:u8`. Fully quantized, an item is about 14–18 bytes in binary or about 60–100 bytes in JSON. 300 items is then about 5 KB binary or about 25 KB JSON. Both are small, so choose a format for convenience and validation, not size.
- Hybrid grid/float (Inference): store float or quantized positions for flexibility, and run validation and occupancy on a derived grid built from each catalog item's footprint. This keeps a grid's simple checks without locking in grid-only aesthetics.
- Wall items (Inference): store them as `{wallSegmentId or (roomId, edgeIndex), u along wall, v height, side}` rather than a world transform. They then survive procedural regeneration that changes wall geometry slightly, and validation becomes one-dimensional.
- Items on surfaces (Inference): give catalog items "surface sockets" (for example, a table top at height h with a 2×1 footprint). Children store `parent` plus local offset. Deleting a parent either cascades or drops its children to the floor, the same lifecycle pattern as Fortnite's attachments.
- Catalog IDs: use stable string keys (`"chair_wood_01"`) in the authoring catalog and map them to compact numeric IDs through a versioned table, so that renumbering does not break saves. This is the same idea as Minecraft's palette, which resolves IDs to names.
- Serialization: MessagePack or a custom binary format reduces size by 2–4× (Inference). However, Colyseus already does binary delta sync, and the persisted size is small. JSON with a `schemaVersion` plus chained migration functions (`v1→v2→v3`) run on load is the lowest-risk start. Move to binary only if profiling shows it is needed.

### Gaps
- The Sims lot file format: no source found in this session.
- Official Valheim ZDO save layout (`.db`): only mod-level evidence was found. There is no first-party documentation.

## Modular building kits: snapping, sockets, wall/floor/roof modules, WFC / Townscaper marching, parametric houses, straight skeleton

### Takeaway
Socket-based kits (Valheim snap points, Rust entity sockets, Fortnite's 512-unit tile grid) are the proven approach for player-authored structure. Townscaper-style dual-grid "marching" (corner-occupancy lookup into a module set) and WFC are the proven ways to turn simple player input (fill or clear a cell) into attractive rule-generated architecture. Straight-skeleton roof generators are available as open-source Unity C# libraries.

### Cited Findings
- Rust (Facepunch devblogs, about 2014–15): entities "link up with other entities via sockets in a generic and extendable way". Stability was moved onto those entity links because the earlier conditional-model and stability checks used slow, unreliable physics queries that ran only on the client. — [Rust Devblog 85](https://rust.facepunch.com/news/devblog-85)
- Rust added stability serialization to speed up server restarts. Conditional models needed prefab pooling and server-side colliders. — [Rust Devblog 96](https://rust.facepunch.com/news/devblog-96)
- Rust "conditional models" infer corners where two edges meet at 90° and hide themselves when a connecting model is present, using modular, extensible rule checks. Corner pieces were not placed by hand. — [Rust Friday Devblog 20](https://rust.facepunch.com/blog/friday-devblog-20)
- One Fortnite tile in UEFN equals a 512-unit grid snap. — [Epic UEFN grid snapping](https://dev.epicgames.com/documentation/en-us/fortnite/using-grid-snapping-in-unreal-editor-for-fortnite)
- A developer recreating Fortnite-style edits in Unreal (not Epic's code) describes edits as a tile bitmask ("each bit corresponds to a tile being active or not"). Edits are predicted client-side, and the server checks neighbours for disconnected structures. Replicating ground checks in full had overloaded the network, so now only key destruction events replicate. — [LinkedIn post (A. Kabirov)](https://rs.linkedin.com/in/artkab)
- Valheim snap points are per-prefab transforms. A mod derives extra snap points (midpoints, edge points, centre) automatically from a piece's existing points, depending on whether the piece is a line or a rectangle. — [Extra Snap Points Made Easy](https://thunderstore.io/c/valheim/p/Searica/Extra_Snap_Points_Made_Easy)
- Townscaper: Stålberg's earlier prototype Brick Block placed blocks on a fixed grid, using a tool "markedly similar to what is commonly known as Marching Cubes". Townscaper extends this with irregular grids and WFC. — [Game Developer: How Townscaper works](https://www.gamedeveloper.com/game-platforms/how-townscaper-works-a-story-four-games-in-the-making) (search summary only); [Sylves Townscaper grid tutorial](https://boristhebrave.com/docs/sylves/1/articles/tutorials/townscaper.html)
- Marching squares and cubes work on an irregular quad grid because they only use the four corners of each quad, which an irregular quad grid always has. — [r/gamedev reimplementation](https://redlib.hbubli.cc/r/gamedev/comments/jvsp7k/i_recreated_oskar_stålbergs_irregular_grid/idwiw0a/?context=3)
- marian42/wavefunctioncollapse is an MIT-licensed Unity project that builds an infinite city from blocks using WFC with backtracking. Module sets (enabled modules, probabilities, connectors, neighbour rules) are editable in the editor without code, and a Slot Inspector shows which modules can go at each position. — [marian42 article](https://marian42.github.io/article/wfc/); [GitHub](https://www.github.com/marian42/wavefunctioncollapse)
- Briganti-Games/Straight-Skeleton-Generator (C#, Unity Mathematics) was used for roof generation in Dungeon Alchemist. Its mesh example converts skeleton edges to polygons and triangulates them into roofs. It is tested on Unity 2022.3. — [GitHub](https://github.com/Briganti-Games/Straight-Skeleton-Generator)
- AggroBird/StraightSkeleton is a brute-force generator with a building-generator sample. It accepts simple polygons only, with no holes. — [GitHub](https://github.com/AggroBird/StraightSkeleton)
- Procedural Toolkit's straight skeleton is based on Tom Kelly's "Unwritten procedural modeling with the straight skeleton" (2014). The toolkit also includes a building pipeline with roof planner and constructor classes. — [ProceduralToolkit fork](https://github.com/xiongxingplus/ProceduralToolkit)

### Inferences
- For Porchlight's existing MeshBuilder and lot-based houses, the lightest rule-based option is Townscaper-style marching on the house's existing room/floor grid. Each corner of a dual cell has a 2-bit or 3-bit state (empty, room, roof). The 8-corner configuration indexes a module table, and each module is emitted by MeshBuilder rather than authored as a prefab. Player edits then mean toggling cells, and the server only has to validate a bitfield.
- Use WFC only if marching's deterministic lookup is not varied enough. WFC needs a seeded RNG and is order-sensitive, so for client/server determinism run it with a fixed iteration order and an integer RNG (Inference). Better still, compute it on one side and sync the result.
- Use sockets (Rust/Valheim) if players should freely compose wall, floor and roof pieces beyond the house's base shape. Each catalog module declares typed sockets (`wall_bottom`, `floor_edge`, `roof_eave`), and placement snaps to the nearest compatible free socket.

### Gaps
- The primary sources (Stålberg's talks, such as the EPFL/Konstanz lectures and the "Beyond Townscaper" content) could not be fetched or confirmed in this session.
- Valheim's internal snapping algorithm: only mod-level evidence was found.

## Making procedural houses customizable (parameter overrides vs player-authored geometry)

### Takeaway
Porchlight's houses already regenerate deterministically from seeds. The cheapest and most robust approach is to persist a small override map on top of the seed (`{roofType, palette, doorStyle, extraRooms[], …}`) and regenerate on both sides. Player-authored geometry should be confined to an additive layer (decor and socketed modules), not used to replace the generator's output.

### Cited Findings
- WFC module sets can be changed without code (enabling or disabling modules, adjusting probabilities and neighbour rules), and individual slots can be manually pinned to a module. Pinning is the override-on-generator pattern. — [marian42 WFC](https://www.github.com/marian42/wavefunctioncollapse)
- Rust's conditional models show how to derive extra geometry (corners, trims) automatically from neighbouring player-placed parts, so player inputs stay minimal. — [Rust Friday Devblog 20](https://rust.facepunch.com/blog/friday-devblog-20)

### Inferences
- Model: `HouseDoc = { seed, generatorVersion, overrides: {roof:"gable", wallColor:3, trim:1, door:"arched", windows:{edgeId:style}}, roomEdits:[{op:"addRoom", cell:[x,y], floor:1}], decor:[…] }`. Regeneration is `Generate(seed, overrides, generatorVersion)`.
- Pin `generatorVersion`. When the generator changes, either keep the old code path (as with Minecraft's DataVersion upgraders) or "bake" existing houses into explicit parameters before the upgrade, so player houses do not shift. This is the main risk of seed-based customization.
- Anchor decor to semantic handles (room ID, wall edge ID, floor cell), not world coordinates, so that regeneration from overrides doesn't orphan items. Validate on load and move invalid items to inventory or storage.
- Fully replacing the house with player geometry costs much more in storage, validation, LOD and art consistency. Do it only if it is a core pillar.

### Gaps
- No published postmortem was found that describes a seeded-generator-plus-overrides housing system in a shipped multiplayer game.

## Networking: authoritative validation, op logs vs snapshots, co-op editing, conflict resolution, syncing large decor sets, interest management, rate limiting

### Takeaway
Use a server-authoritative edit protocol: clients send intent ops (`place`, `move`, `remove`, `recolor`), the Colyseus room validates and applies them, and the result syncs through schema state. Use per-item last-writer-wins, with ordering defined by the server, as in Figma. Load house interiors lazily, either through a separate per-house room or through StateView filtering. Note that StateView is documented as not yet optimized for large datasets.

### Cited Findings
- Colyseus 0.16 introduced StateView for per-client visibility. Fields are tagged with `@view()`, and schema instances are added or removed per client (`client.view.add/remove/has`). Documented use cases include area-based visibility and level of detail. — [Colyseus docs: StateView](https://docs.colyseus.io/state/view) (search summary); [DeepWiki summary](https://deepwiki.com/colyseus/docs/2.3-state-management)
- Each StateView adds an encoding step during serialization, and the 0.16 announcement says it is not yet optimized for large datasets. — [Colyseus 0.16 blog](https://colyseus.io/blog/colyseus-016-is-here/)
- The older `@filter()` decorators were experimental and consumed "way too much CPU" (2022 forum). — [Colyseus forum: Filter data](https://discuss.colyseus.io/topic/666/filter-data)
- A Schema class can have at most 64 synced fields; nest schemas or use a MapSchema/ArraySchema to go beyond that. View-, unreliable- or fullStateOnly-tagged fields at index ≥32 take a slower path. — [Colyseus 0.16 schema docs](https://0-16-x.docs.colyseus.io/state/schema); [@colyseus/schema README](https://cdn.jsdelivr.net/npm/@colyseus/schema@4.0.28/README.md)
- Figma's multiplayer design: the server keeps the latest value per (object, property), and conflicts go to whichever value reached the server last. It is a last-writer-wins register without timestamps, because the server defines the order. It is CRDT-inspired, not a true CRDT. — [Evan Wallace: How Figma's multiplayer technology works](https://madebyevan.com/figma/how-figmas-multiplayer-technology-works/); [Liveblocks analysis](https://www.liveblocks.io/blog/understanding-sync-engines-how-figma-linear-and-google-docs-work)
- Some third-party summaries claim Figma uses Lamport timestamps, which contradicts Figma's own post. — [techinterview.org](https://www.techinterview.org/post/3233474360/system-design-design-figma-collaborative-design-tool-real-time-multiplayer-vector-rendering-component-library-version-control/) vs [Figma post](https://madebyevan.com/figma/how-figmas-multiplayer-technology-works/)
- Roblox DataStore throttling uses a sliding 60-second quota per key. UpdateAsync draws from both the read and write budgets, and write queues hold about 30 requests before throwing (forum). This is a reference design for rate-limited persistence. — [Roblox limits](https://create.roblox.com/docs/cloud-services/data-stores/error-codes-and-limits.md); [DevForum advanced DataStore](https://devforum.roblox.com/t/details-on-datastoreservice-for-advanced-developers/175804/38)
- Valheim network mods throttle distant structures and delta-compress ZDO field changes, which is evidence that large structure counts are a bandwidth concern. — [FiresGhettoNetworking](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresGhettoNetworking/v/1.3.2/)
- In the Fortnite-style edit reimplementation, replicating everything (server ground checks) overloaded the network. Only key events now replicate, and clients simulate the rest. — [LinkedIn post](https://rs.linkedin.com/in/artkab)

### Inferences
- Room topology options for Colyseus:
  1. **Per-house interior room** (`HouseRoom` keyed by houseId): created on entry and disposed when empty, holding `MapSchema<DecorItem>`. Visitors only download a house's decor when they enter. This is the simplest form of interest management, and it avoids StateView's large-dataset caveat. The town room only needs exterior parameters.
  2. **StateView within the town room**: add a house's decor items to a client's view when they enter the house trigger. Suitable for a few hundred items, but each view adds an encode step per patch.
  3. **Non-state message channel**: send decor as a one-time binary blob (`room.send` / `onMessage`) when the player enters, followed by op deltas. This keeps very large sets out of schema patching.
- Edit protocol: `client → {op, clientSeq, payload}`. The server validates ownership or permission (owner or co-op editors list), checks that the catalog item is owned or unlocked (via an account-server cache), checks bounds, footprint occupancy, parent validity and the per-house item cap, then applies the change and acks with `clientSeq`. The client keeps optimistic ghosts and reconciles on ack or reject.
- Conflicts: per-item LWW in server order (Figma model) is enough for co-op decorating. Add soft "item locks" while someone is dragging an item, broadcast as `heldBy` (Inference), to avoid most conflicts at the UX level. Real CRDTs (Yjs/Automerge) are unnecessary with an authoritative server.
- Rate limiting: use a token bucket per client per room (for example 10 ops/s with a burst of 30), plus a cap on items per house. Debounce persistence to the account server (dirty flag plus a 5–30 s flush, and a flush on room dispose).
- Op log vs snapshot: keep the Colyseus room state as the live snapshot. Append ops to a short server-side journal so undo and crash recovery work between flushes, and persist snapshots to the account server.

### Gaps
- Full Colyseus docs for StateView and room patterns (for example, recommended limits on view size) could not be fetched because docs.colyseus.io is blocked in this session's network.
- No public benchmark was found for StateView with thousands of items.

## Persistence: storage schema, size budgets, backups/undo

### Takeaway
A JSON(B) blob per house is simplest and fits Porchlight's sizes (tens of KB). However, every Postgres update rewrites the whole TOASTed value, which causes churn and bloat for frequent small edits. Debounced snapshot writes, or a row-per-item table, avoid that cost. Item caps per house, used industry-wide (FFXIV: about 200/300/400 by house size), keep both storage and rendering bounded.

### Cited Findings
- Postgres moves rows larger than about 2 KB (TOAST_TUPLE_THRESHOLD) to compression and out-of-line storage. — [Cybrosys: oversized data](https://www.cybrosys.com/research-and-development/postgres/how-does-postgresql-deal-with-oversized-data-silently)
- Updating any part of a large TOASTed JSONB value duplicates the whole value, which causes churn, and the TOAST table bloats as fast as the main table. — [pganalyze: JSONB TOAST](https://pganalyze.com/blog/5mins-postgres-jsonb-toast); [Franck Pachot, DEV](https://dev.to/franckpachot/postgresql-jsonb-size-limits-to-prevent-toast-slicing-9e8)
- Reading one key from a large JSONB value cannot seek within the TOAST data; the whole value must be detoasted. — [Franck Pachot, DEV](https://dev.to/franckpachot/postgresql-jsonb-size-limits-to-prevent-toast-slicing-9e8)
- FFXIV furnishing caps, according to fan and community sources: about 200 for small houses, 300 for medium and 400 for large. The rationale (rendering, server load) is speculative, and Square Enix reportedly tied any increase to new housing servers. — [FFXIV housing fan guide](https://ffxiv-housing.carrd.co); [SE forums](https://forum.square-enix.com/ffxiv/showthread.php?p=6011734); [SE forums](https://forum.square-enix.com/ffxiv/showthread.php?p=5429380)
- ESO has run furnishing-limit status updates as an ongoing engineering topic. — [ESO forums Feb 2020](https://forums.elderscrollsonline.com/en/discussion/comment/7456407/)
- Roblox caps a key at about 4 MB, a common size budget for whole-build blobs. — [DevForum](https://devforum.roblox.com/t/game-resourcesdatastore-limits/856634)

### Inferences
- Recommended schema (Inference): `houses(id, town_id, lot_id, owner_id, seed, generator_version, overrides JSONB, decor_version INT, decor BYTEA/JSONB, updated_at)` plus `house_snapshots(house_id, version, decor, created_at)` for backups and rollback (keep the last N, or daily copies). Writes are debounced from the Colyseus room, so blob rewrite cost is paid every few seconds at most, not per op.
- Switch to row-per-item (`house_items(house_id, item_id, cat, var, x,y,z, rot, parent_id, anchor)`) if you need server-side queries (for example "how many of catalog X are placed" for inventory reconciliation), per-item audit, or very high edit rates. The cost is many row writes per save and more complex transactional diffs.
- Use optimistic concurrency (`WHERE decor_version = ?`) to prevent two server processes clobbering each other's writes when a room migrates.
- Budget suggestion: 300–1000 items per house is about 10–100 KB in JSON. Keep the item cap in shared config enforced by both server and client.
- Undo history: keep it session-only in memory (client command stack plus a server journal). Persistent backups use snapshot versions.

### Gaps
- No direct benchmark comparing a JSONB blob with row-per-item for game save workloads was found.

## Rendering performance: instancing/batching, WebGL constraints, LOD, thumbnails

### Takeaway
Many identical decor meshes are best drawn with GPU instancing (same mesh and material, per-instance colour through instanced properties) or with runtime mesh combining per house. In URP, static batching takes precedence over and disables the SRP Batcher and instancing. On WebGL, instancing requires WebGL 2. The GPU Resident Drawer needs compute shaders, so it cannot be relied on for WebGL 2. Generate catalog thumbnails at build time or at runtime with an off-screen camera rendering to a RenderTexture.

### Cited Findings
- GPU instancing on WebGL requires the WebGL 2.0 API. — [Unity 2017.1 manual](https://docs.unity3d.com/es/2017.1/Manual/GPUInstancing.html)
- Instancing only batches objects that share both mesh and material. Putting non-instanced properties in a MaterialPropertyBlock disables instancing. Script APIs: `Graphics.DrawMeshInstanced` / `DrawMeshInstancedIndirect` (newer: `RenderMeshInstanced`). — [Unity manual GPU instancing](https://docs.unity.cn/es/2019.2/Manual/GPUInstancing.html)
- The GPU Resident Drawer uses BatchRendererGroup and requires Forward+, compute-shader support and MeshRenderers. Otherwise it falls back to normal drawing. Unity suggests disabling static batching to help it. — [URP 17 docs: GPU Resident Drawer](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/gpu-resident-drawer.html)
- Unity's roadmap marks SRP Batcher compatibility for WebGPU as released (2 April 2025). — [Unity roadmap 2417](https://activation.unity3d.com/roadmap/2417-srp-batcher-compatibility-for-webgpu)
- The SRP Batcher keeps material data persistent on the GPU and avoids render-state changes. — [Unity SRP Batcher manual](https://docs.unity.cn/Manual/SRPBatcher.html)
- Community report: enabling static batching on an object disables GPU instancing and the SRP Batcher for it. Priority order is static batching, then SRP Batcher, then instancing. Anecdotal. — [Unity Discussions](https://discussions.unity.com/t/should-you-enable-static-batching-in-an-srp-project/272849)
- `Editor.RenderStaticPreview` is editor-only, for Inspector previews. Runtime thumbnailing uses a camera rendering to a RenderTexture. UnityPreviewGenerator offers an in-game component that creates sprites from a RenderTexture. yasirkula's RuntimePreviewGenerator is another option. — [Unity Editor.RenderStaticPreview](https://docs.unity3d.com/ScriptReference/Editor.RenderStaticPreview.html); [UnityPreviewGenerator](https://github.com/Super-Genius/UnityPreviewGenerator); [Gitee mirror crediting yasirkula](https://gitee.com/cuizhiqiang_802/UnityPreviewGenerator)

### Inferences
- Porchlight-specific (Inference): because meshes are generated in code, the house shell can be one combined mesh per house per material, which works on every platform. Static batching cannot be applied to objects spawned at runtime unless you call `StaticBatchingUtility.Combine`, which is expensive and blocks moving items. Use these strategies instead:
  - Use a single shared atlas or palette texture plus vertex colours or UV-offset variants, so most decor shares one material. That makes SRP Batcher and instancing effective, and on WebGL a low-poly palette texture is the biggest win.
  - Per-house "baked" combined mesh when nobody is editing. Rebuild the combined chunk on edit, or re-bake only the edited item's chunk.
  - `Graphics.RenderMeshInstanced` per catalog mesh, with per-instance colour in an instanced buffer (WebGL 2 supported). Test the per-call instance cap on WebGL. It was not confirmed here.
- LOD from outside: render only the generated shell plus a few exterior-visible items, or a combined impostor of window-visible interior items. Load interior decor only on entry, which matches the per-house room approach.
- Unity 6's WebGPU backend (still experimental at last check, Inference) may enable compute-based paths later. Do not depend on it for the WebGL 2 baseline.
- Thumbnails: generate them in an editor or batch step into an atlas shipped with the build, which is cheapest for WebGL. Generate at runtime only for recoloured variants (one off-screen camera, a 128 px RenderTexture, `ReadPixels`, then cache).

### Gaps
- Exact WebGL limits on instance count per draw call and on uniform buffer size in current Unity 6 were not confirmed.
- Whether GPU Resident Drawer works on Unity's WebGPU backend was not confirmed.

## Placement mechanics: raycasts, occupancy grids, overlap checks, ghost previews, rotation, undo/redo, gamepad cursor

### Takeaway
The standard pipeline runs as follows. Raycast from the cursor or camera centre to the placement layers (floor, wall, surface sockets). Snap to the grid or socket. Test the footprint against an occupancy grid, with a physics `OverlapBox` as fallback. Tint a ghost green or red. Commit through a command object that both the undo stack and the network op channel consume. Sourced evidence for this section is thin. Most of it is established practice and is marked as inference.

### Cited Findings
- UEFN grid snapping ties prop placement to building actors with a tile-sized snap (512). The attachment pivot must touch the host. — [Epic UEFN grid snapping](https://dev.epicgames.com/documentation/en-us/fortnite/using-grid-snapping-in-unreal-editor-for-fortnite)
- Valheim snap-point systems (vanilla plus mods) snap a ghost piece's snap points to the nearest compatible points on existing pieces. Mods can add points automatically per piece type. — [Extra Snap Points Made Easy](https://thunderstore.io/c/valheim/p/Searica/Extra_Snap_Points_Made_Easy)
- The Fortnite-style edit reimplementation uses a client-predicted edit that is confirmed locally, with a server-side neighbour check afterwards. — [LinkedIn post](https://rs.linkedin.com/in/artkab)

### Inferences
- Occupancy: each catalog item has a footprint mask (cells at 0.25–0.5 m resolution on the floor plane, plus height layers for stacking). The house keeps a `Dictionary<int3, itemId>` or a flat `ushort[]` per floor. Rotation rotates the mask in 90° steps. For free rotation, fall back to oriented-box tests (`Physics.OverlapBox` on the client; on the server, a simple OBB/SAT check in TypeScript, because Colyseus has no Unity physics).
- Server validation must not depend on Unity physics. Keep catalog footprints as data (JSON exported from Unity in an editor step) shared by the client and the Node server.
- Ghost preview: instantiate the mesh with a shared transparent "ghost" material. Swap tint by validity. Disable colliders.
- Undo/redo: an `IEditCommand { Do(); Undo(); ToOp(); }` stack on the client. Undo sends the inverse op to the server, which validates it like any other edit. Collapse drag sequences into one command. Clear redo on new commands. In co-op, undo should only revert your own commands, and if a command's item was changed by someone else since, skip it or fail.
- Gamepad cursor: move a virtual cursor in screen space with the stick (with acceleration and snapping to valid cells), or use camera-centre raycast with D-pad cell stepping. Unity Input System's `VirtualMouseInput` component provides a stick-driven virtual mouse (not verified this session). Grid-step movement is usually better for gamepads: a tap moves one cell, holding repeats, and the shoulder buttons rotate.

### Gaps
- No source fetched in this session documented Unity Input System's VirtualMouseInput or specific gamepad build-cursor postmortems.
- No Unity Asset Store building-system assets (e.g. Easy Build System) were verified this session.

## Relevant Unity assets/tools and Colyseus patterns (summary)

### Takeaway
Concrete reusable pieces include straight-skeleton libraries (Briganti, AggroBird, Procedural Toolkit), marian42's WFC, Sylves for irregular grids, preview generators, Colyseus StateView and MapSchema, and `@colyseus/schema` nesting rules.

### Cited Findings
- Sylves (BorisTheBrave) has a Townscaper-grid tutorial for building irregular grids. — [Sylves Townscaper tutorial](https://boristhebrave.com/docs/sylves/1/articles/tutorials/townscaper.html)
- Straight skeleton libraries: [Briganti-Games](https://github.com/Briganti-Games/Straight-Skeleton-Generator), [AggroBird](https://github.com/AggroBird/StraightSkeleton), [Procedural Toolkit](https://github.com/xiongxingplus/ProceduralToolkit)
- WFC Unity reference implementation (MIT): [marian42](https://www.github.com/marian42/wavefunctioncollapse)
- Preview/thumbnail generator: [UnityPreviewGenerator](https://github.com/Super-Genius/UnityPreviewGenerator)
- Colyseus: StateView ([docs](https://docs.colyseus.io/state/view)), the 64-field schema limit ([0.16 schema docs](https://0-16-x.docs.colyseus.io/state/schema)), and the 0.16 announcement ([blog](https://colyseus.io/blog/colyseus-016-is-here/))

### Inferences
- Check the licences of the straight-skeleton repos before use. The search did not show them.

### Gaps
- Commercial assets (Easy Build System, Grid Building System, etc.) were not researched because of the tool-call budget and blocked hosts.
