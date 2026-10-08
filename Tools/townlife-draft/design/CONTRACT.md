# TownLife contract (read this instead of the stub sources)

Architect-authored. `DESIGN.md` wins on intent; this file is the exact API every TownLife agent codes against.
Sources: `scratchpad/draft/Assets/Scripts/Editor/TownLife/*.cs` (13 files, all compile; XML docs on every member).
Signatures here are FROZEN: add members if you must, never rename, re-type or remove. If you need something from a
file you do not own, say so in your final report; do not edit it.

```
SP=/private/tmp/claude-501/-Users-nick-Dev-Rubbish/89149967-39e5-422b-a7f2-9cb2938effd6/scratchpad
$SP/tools/compile.sh                                   # offline compile check, ~2 s, no Editor contact
PREVIEW_OWNER=<your-agent-name> $SP/tools/preview.sh <modules|all> <abs-out-dir> [shots|all] ["k=v;k=v"]
```

## 1. Ground rules

- **Assembly**: Editor-only (`Assembly-CSharp-Editor`), global namespace, `public static` classes + public data classes.
  Use ONLY public API of the game assemblies (the preview DLL is compiled separately against them; `compile.sh` pass 2
  fails on anything private, e.g. NeighborhoodBuilder helpers). C# 9.
- **Frames and units**: world metres. +X east (sea), +Z north (river), +Y up. Almost every API ignores y (XZ only).
  `Rect` always means `x = world x`, `y = world z`. Yaw is degrees about +Y; yaw 0 faces +Z
  (`TownLifeGeo.Dir(yaw)`, `TownLifeGeo.Yaw(dir)`). "Right of travel" along a polyline is `TownLifeGeo.Right(dir)`
  (Right(+Z) = +X).
- **Lot frame** (per house, from `TownLifePlan.House`): `origin` = holder position (curb stop, 0.8 m outside the asphalt),
  `right` = along the street (holder +X), `inward` = from the street into the lot (holder -Z). "Depth" = metres along
  `inward`. `HouseInfo.LotPoint(x, depth)` converts. Sidewalk back edge is at depth 2.2.
- **Heights** (`TownLifeContext` consts): `GrassY -0.08`, `AsphaltY 0.02`, `SidewalkY 0.055`, `HolderY 0.15`,
  `DepotPadY 0.025`, `WaterY -1.1`. Props stand at `ctx.scene.GroundY(p)` (= GrassY inside town). Flat layers use the fixed
  levels so modules never z-fight: `LawnY -0.05` (mown stripes, pitch grass), `SoilY -0.02` (beds, sand, safety surface,
  water surfaces), `PavingY +0.01` (paths, plazas, patios), `MarkingY +0.04` (lines on paving). `DecalLift 0.03` is the
  minimum gap between stacked layers.
- **Names**: every object the kit makes is `TL_<something>`. Never create objects with the reserved names of DESIGN
  section 4. Module top groups: `ctx.Group("Park")` -> `TL_Park` etc. Prop ids and material keys are GLOBAL: stock ones
  are `kit.*` / un-prefixed palette keys; yours are `<module>.<name>` (`park.bench`, `yards.shed.red`).
  Ground/run names (they name unique mesh assets) must be unique town-wide and deterministic:
  `<module>_<feature>_<identity>` (`yards_patio_birch-46`); a duplicate throws.
- **RNG rule**: the only randomness is `ctx.Rng(key)` = `new System.Random(TownLifeContext.StableHash(key))` (FNV-1a).
  One instance per feature, keyed `module/feature/identity` (`"yards/theme/" + stopId`, `"streets/" + roadId + "/" + side`).
  Never `UnityEngine.Random`, never `string.GetHashCode`, never a shared instance, never iterate a `Dictionary`/`HashSet`
  to decide placement order (sort first; `scene.houses` is already sorted by stopId).
- **Physics-free**: no `Physics.*`, no `Collider.Raycast`. Read `ctx.scene`, `ctx.plan`, `ctx.fences`, `ctx.occupancy`.
- **Budget**: `TownLifeBuilder.ObjectBudget = 4000` GameObjects for everything (reported as an error when exceeded).
  Rough split: park 600, yards 1500, streets 500, meadows 400, edges 600, fences live elsewhere. One prop = one
  GameObject; merge (`RunSpec.merge`, `Recipe.Add`, `MeshData`) rather than spawn many small things.
- **Transient mode** (`ctx.transient`, preview): the root is a HideAndDontSave object in a preview scene; the kit hands out
  in-memory meshes/materials; NOTHING may touch `AssetDatabase` writes, `Undo`, the real scene, `EditorUtility.SetDirty`
  on scene objects, or `PrefabUtility` (use `TownLifeKit.SpawnPrefab`). Modules must not branch on it except Paint
  (which only fills `ctx.materialOverrides` in both modes anyway). Loading assets read-only is fine.

## 2. Files, owners, call rules

| file | owner | may be called by |
|---|---|---|
| `TownLifePlan.cs` | plan agent | anyone (pure). Result is `ctx.plan`. |
| `TownLifeFences.cs` | fences agent | builder (`Plan`, `Reserve`, `DefineStyles`), preview (`BuildPreview`), `CoastalYardAuthoring` (`PlanForTown`, `BuildRunVisual`, `ColliderSize`); modules READ `ctx.fences` |
| `TownLifeKit.cs`, `TownLifeKit.Props.cs` | kit agent | every module. Only the builder/preview call `Begin/End/DestroyTransient` and set `BeforeAssetWrite`. |
| `TownLifeScene.cs` (`TownLifeGeo`, `TownLifeTags`, `TownLifeScene`, `TownLifeOccupancy`) | core agent | everyone reads; modules call occupancy `Free*/Reserve*/Within`; only the builder calls `Capture` and `SeedKeepOuts` (fences agent: `PlanForTown` captures too) |
| `TownLifeBuilder.cs` (`TownLifeOptions`, `TownLifeContext`, `TownLifeBuilder`) | core agent | preview, `NeighborhoodBuilder.Coastal.cs` hooks. Modules never call the builder. |
| `TownLifePreview.cs`, `tools/preview.sh`, `tools/compile.sh` | architect, then core agent | tools only |
| `TownLifePark/Yards/Streets/Meadows/Edges/Paint.cs` | one module agent each | builder only. A module never calls another module. |

Module order (fixed): **[fences reserved] -> paint -> park -> meadows -> streets -> yards -> edges**. Preview-only
pseudo-modules: `fences` (runs first, draws the planned network) and `smoke`. A module sees the reservations of all
earlier modules and must reserve what it places.

Module shape (already stubbed): `public static class TownLifeX { public const string Name = "x"; public static void Build(TownLifeContext ctx) }`.
Skip districts with `ctx.options.District(TownLifeScene.Acres|Meadows)`. End with one `ctx.Log(Name, summary)`.
Throwing is allowed for bugs (preview reports it and carries on; the real pipeline aborts the transaction).

## 3. `TownLifeContext` / `TownLifeOptions` (TownLifeBuilder.cs)

| member | semantics |
|---|---|
| `Transform town` | `NeighborhoodGen`. Read only. |
| `Transform root` | parent of all TownLife objects (`NeighborhoodGen/TownLife` or the preview root). |
| `Transform Group(string name)` | find-or-create top group `TL_<name>` under root. Sub-groups: `TownLifeKit.Group(parent, "TL_...")`. |
| `bool transient, continueOnError` | see section 1. |
| `TownLifeOptions options` | `Module(name)`, `District(id)`, `Get(key, fallback)`, `Flag(key, fallback)`, `Number(key, fallback)`; `Parse(modulesCsv, "k=v;k=v")`. Unknown keys reach modules, so you can add tuning switches for your own previews. |
| `TownLifeScene scene`, `TownLifePlan.Result plan`, `TownLifeFences.Result fences`, `TownLifeOccupancy occupancy` | never null after `Prepare`. |
| `System.Random Rng(string key)`, `static int StableHash(string)`, `static float Range(rng, min, max)`, `static T Pick<T>(rng, list)` | RNG rule above. |
| `StringBuilder report`, `List<string> errors`, `Log(module, msg)`, `Error(module, msg)` | `Error` records and logs; does not throw. |
| `Dictionary<Renderer, Material[]> materialOverrides` | Paint only: scene renderer -> full new `sharedMaterials` array. Real pipeline applies to the scene (`TownLifeBuilder.ApplyMaterialOverrides`), preview applies to its clones. Never assign scene renderers yourself. |

`TownLifeBuilder`: `GroupName = "TownLife"`; `Modules` (ordered list); `AddPreviewModule(name, fn)`;
`Prepare(town, root, options, transient) -> ctx` (kit Begin, scene capture, plan, fence plan, occupancy seeded);
`Run(ctx) -> report` (modules in order, kit End); `BuildInto(town, root, options, transient, out ctx)`;
`ClearGenerated(town) -> int`; **pipeline hooks** `PipelineClear(town)` (before any yard planning) and
`PipelineBuild(town) -> string` (after yards/infill/pads; creates the group, watches assets through
`CityEditTransaction.Current.WatchAsset`, applies material overrides, throws on errors); `ApplyMaterialOverrides(ctx)`;
`RebuildStandalone() -> string` (TODO core agent).

## 4. `TownLifeScene` (snapshot; all arrays non-null, deterministic order)

`static FindTown()`, `static Capture(town)`, `ToPlanInput()`, `Road(id)`, `House(stopId)`, `Court(roadId)`,
`DistrictRect(id)`, `DistrictAt(p)`, `RoadEdgeDistance(p, out road)` (negative on asphalt, sidewalk 0..3), `GroundY(p)`.
Consts `Acres = "mildly_pleasant"`, `Meadows = "minivan_meadows"`.

| field | content |
|---|---|
| `Rect acresRect, meadowsRect, area` | district rects; `area` = union + 60 m = everything TownLife may touch (x 53.75..840, z -50.25..342.75) |
| `RoadInfo[] roads` | `id, name, districtId, points[] (world, y 0), halfWidth (5), length, bridge, service, loop, plan (TownLifePlan.Road), group` for every road with a point in `area` (16 today) |
| `HouseInfo[] houses` | 59, sorted by stopId. `plan` (TownLifePlan.House: stopId, roadId, districtId, side, station, origin, right, inward, footprint, hasDrive/driveA/driveB/driveHalfWidth, walk[], keepIn[], frontItems[]), `holder, building, stop, route, anchor`, `buildingBounds` (world AABB), `buildingRenderers`, `model`, `frontDepth, rearDepth` (front/rear wall, metres along inward), `leftExtent, rightExtent` (min/max along right), `height`, `door, mailbox, bin`, `items[]`, `LotPoint(x, depth)` |
| `LotItem` | `name, kind (Other, Driveway, Path, Picket, KidPen, Pool, Trampoline, DryingRack, Car, Sign, Hoop, Flamingo, Shrub, Tree, Mailbox, Bin, Lemonade), transform, rect (oriented Quad, world), height, depth, hasCollider, rear` |
| `Vector3[] river, coast, riverWalk` | smoothed blueprint polylines (river centre line z ~305-316; water ribbon half-width 19.5; bank drops from 24 m) |
| `InfillInfo[] infill` | `id, kind, quad, transform` (6 in `area`: 4 in the districts + the two depot works) |
| `Rect[] depotPads`, `Vector3[] depotDoors` | 4 each |
| `GateInfo[] gates`, `CrossingInfo[] crossings` (`roadId, position, forward, halfWidth`; the two park zebras at x 256.75), `Vector3[] lamps` |
| `ParkInfo park` | `block` (inside the sidewalks: x 192..304, z 173.75..252), `kerb` (x 189..307, z 170.75..255), `landmark` (256.75, 0, 213.2), `corridors[]` (`Segment a, b, halfWidth 1.5`: landmark -> each crossing; stays solid-free), `playground` (the KidPen), `group` (`NeighborhoodGen/Park`), `kept[]` (ParkPath_NS, ParkSign x2, SM_lemonade_shop*) |
| `CourtInfo[] courts` | `roadId, name, centre, radius 22, islandRadius 14, innerKerb 17, outerKerb 27, outerRadius 30, entrance` (unit vector centre -> entrance lane) |
| `KidPenInfo[] kidPens` | `penId, stopId, center, yaw, half, quad, transform` (6) |
| `Vector3[] natureTrees`, `TownLifePlan.Quad[] authoredObstacles`, `List<string> notes` |

`TownLifeGeo` (pure XZ helpers): `Flat, Right, Yaw, Dir, Dist, ClosestOnSegment, DistToSegment, PolylineLength,
PointAt(pts, station, out tangent), Project(pts, p, out station, out lateral) (+lateral = right of travel),
Offset(pts, lateral), Stations(length, spacing, start, end), Quad(centre, right, forward, halfW, halfD) /
Quad(centre, yaw, halfW, halfD) / Quad(Rect), Centre, Corners, QuadDistance, QuadContains(q, p, margin),
PolygonContains, PolygonArea, RectContains(r, p, margin), SegmentsCross`.

## 5. `TownLifeOccupancy` (ctx.occupancy)

Tagged shapes (circle, capsule, convex quad) in XZ. `What { Solid, Flat }`: Solid = anything with height or a
collider; Flat = ground paint and very low planting. `Block { Marker, Solids, Everything }`: what a reserved shape
forbids (`Marker` forbids nothing, it only answers `Within`).

| member | semantics |
|---|---|
| `bool Free(p, radius, what = Solid)` | disc touches nothing that blocks `what`, and is inside `area` |
| `string Blocker(p, radius, what)` | tag of a blocker or null (debugging / reports) |
| `bool FreeLine(a, b, halfWidth, what)`, `FreeQuad(q, what, margin)`, `FreeBox(centre, yaw, halfW, halfD, what, margin)` | sampled (~0.5 m) versions |
| `ReserveCircle(c, r, tag, block = Solids)`, `ReserveLine(a, b, halfWidth, tag, block)`, `ReservePolyline(pts, halfWidth, tag, block, closed)`, `ReserveQuad(q, tag, block, margin)`, `ReserveRect(rect, ...)`, `ReserveBox(centre, yaw, halfW, halfD, tag, block, margin)` | reserve what you place |
| `bool TryReserve(p, radius, tag, what = Solid, block = Solids)` | `Free` then `ReserveCircle`: the normal way to place one prop |
| `bool Within(p, distance, tag)`, `float DistanceTo(p, tag, max = 50)` | spacing queries; a tag matches itself and `tag.<anything>` (`"tree"` matches `"tree.street"`) |
| `bool TryFindFree(near, radius, what, searchRadius, out found, step = 1)` | deterministic ring search |
| `int Count`, `CountOf(tag)` |

Seeded keep-outs (`SeedKeepOuts`, tags in `TownLifeTags`):

| tag | shape | blocks |
|---|---|---|
| `road` | carriageway (centre line capsule, r = halfWidth) | Everything |
| `sidewalk` | r = halfWidth + 3 (non-bridge roads) | Everything |
| `junction` | 12 m disc at every node shared by two roads | Solids |
| `stop` | 3 m discs at lot origin, bin, mailbox | Solids |
| `building` | footprint + 0.3 | Everything |
| `driveway.clear` / `driveway` | 2.6 m capsule ramp -> top / slab + 0.2 | Solids / Everything |
| `path.clear` / `path` | 1.4 m / 0.8 m capsule along door -> bends -> pathEnd | Solids / Everything |
| `lotitem` | every other existing lot object + 0.4 (pool, trampoline: Everything) | Solids |
| `kidpen` | pen quad + 0.6 | Everything |
| `infill` | footprint + 0.5 | Everything |
| `depot` | pad + 0.5 | Everything |
| `crossing` | 3.5 m capsule across each zebra and its two forecourts | Solids |
| `corridor` | 1.5 m capsule landmark -> crossing | Solids |
| `water` | river centre line r 24; coast line r 3 (TODO: sea side) | Everything |
| `lamp` 1.2, `gate` 9, `tree.nature` 2 (discs) | | Solids |
| `authored` | TownAuthored renderer AABB + 0.5 | Everything |
| `fence` (reserved by the builder from `ctx.fences`) | 0.6 m capsule per run | Solids |

Module vocabulary (use these so later modules can ask about your work): `tree[.x]`, `shrub`, `hedge`, `bed`,
`paving`, `structure`, `furniture`, `pond`, `lawn` (keep-open lawns: reserve with `Block.Solids` to keep later modules
off them), `feature`. Suffix with your module (`tree.street`, `tree.yard`).

Typical radii: tree trunk reserve 1.2 (and `Within(p, 6-8, "tree")` for spacing), shrub 0.6, bench 1.0.
Fences are solid colliders later scanned by `CoastalYardAuthoring.ClearLine` (0.35 m): the 0.6 m `fence` reservation is
what keeps your trunks and sheds from silently opening a yard. Respect it.

## 6. `TownLifeKit`

Session (builder/preview only): `Begin(bool transient)`, `End()`, `DestroyTransient()`, `Transient`, `Active`,
`BeforeAssetWrite` (hook, called with the path before every asset create/modify), `UsedAssets`, `WrittenAssets`,
`SpawnCount`, `TransientObjectCount`. Assets: `AssetDir = Assets/Materials/Generated/TownLife` with `Mat/TL_<key>.mat`,
`Props/TL_P_<id>.asset`, `Ground/TL_G_<name>.asset`, `Ground/TL_R_<name>.asset` (merged runs). Existing assets are
reused and rewritten in place only when content differs (same inputs -> no write, no GUID churn).

**Materials** (URP Lit, flat `_BaseColor`, `_Smoothness`, `_Metallic`, GPU instancing):

- `Define(key, Color, smoothness = 0.08, metallic = 0)`, `DefineEmissive(key, color, emission, smoothness = 0.3)`:
  declare; idempotent for identical values, throws when a key is redefined differently. Call at the top of `Build`.
- `Mat(key)`: the material. Keys starting `NB_` return the existing Neighborhood material read-only (`Mat("NB_Hedge")`).
- `HasMat(key)`; `Variant(key, Material source, Color)`: recoloured copy of an existing material (Paint).
- Stock palette (always defined): `leaf.mid, leaf.dark, leaf.lime, leaf.blossom, leaf.autumn, bark, bark.birch,
  wood.warm, wood.dark, paint.white, paint.red, paint.yellow, paint.blue, stone.light, stone.dark, paving.warm, gravel,
  soil, water, metal.dark, eye.white, eye.black`. The kit agent extends it; ask in your report for shared colours.

**Props** (`DefineProp(id)` returns a chainable `Recipe`; frame: origin = ground contact, +Y up, +Z front, metres;
frozen at first spawn; one merged mesh, a submesh per material):

| part | arguments |
|---|---|
| `Box(mat, center, size, euler = 0)` | centred box |
| `Cyl(mat, baseCenter, radius, height, sides = 10, topRadius = -1, euler = 0, smooth = false)` | cylinder / frustum / n-gon prism standing on its base; `sides 4` = axis-aligned square post with `radius` = half side; 6 hexagon, 8 octagon |
| `Cone(mat, baseCenter, radius, height, sides = 10, euler = 0)` | |
| `Sphere(mat, center, radii, lon = 10, lat = 6, smooth = false, euler = 0)` | ellipsoid |
| `Dome(mat, baseCenter, radii, lon = 10, lat = 4, smooth = false)` | half ellipsoid on its base |
| `Blob(mat, center, radii, seed, jitter = 0.18, lon = 8, lat = 5, euler = 0)` | faceted lumpy crown / bush / boulder |
| `Gable(mat, baseCenter, size, euler = 0)` | triangular prism: x span, y rise, z ridge length |
| `Wedge(mat, baseCenter, size, euler = 0)` | ramp, high edge at -X |
| `Torus(mat, center, ringRadius, tubeRadius, segments = 14, sides = 6, euler = 0)` | in the XZ plane before euler |
| `Bar(mat, a, b, thickness)` | square beam between two points (rails, ropes, branches) |
| `Sheet(mat, a, b, c, d)` | two-sided flat quad (flags, laundry, bunting) |
| `Add(otherPropId, offset, yawDeg = 0, scale = 1)` | bake another prop in (no colliders copied) |
| `Geometry(Action<MeshData>)` | free-form triangles (`MeshData.Tri/Quad/TriOut/QuadOut/Append`) |
| `ColliderBox(center, size)`, `ColliderFit(shrink = 0)`, `ColliderTrunk(radius, height)` | colliders per instance; none by default |
| `NoShadows()`, `Occluder()`, `Walkable()` | small/flat props; big solids; layer Ground |

`HasProp(id)`, `PropBounds(id)`, `PropIds`. Stock props today: `kit.tree.round`, `kit.shrub.round` (kit agent grows the
library: species trees, shrubs, hedge/fence/rail panels, benches, lamps, flowers, boulders).

**Spawning**: `Spawn(parent, propId, position, yawDeg = 0, scale = 1)`, `Spawn(parent, propId, position, yawDeg, Vector3 scale)`,
`SpawnRotated(parent, propId, position, Quaternion, Vector3 scale)`: one GameObject `TL_<id>`, static flags + shadows
set. `SpawnPrefab(parent, prefabPath, position, yawDeg = 0, scale = 1, colliders = false, keepName = false, castShadows = true)`:
pack prefabs (`Assets/Suburbs Town Pack/PREFABS/...`); renamed `TL_<prefab>` unless `keepName` (park lamps keep
`SM_streetlamp*`); colliders disabled unless asked; returns null when the prefab is missing.
`Group(parent, name)`; `Finish(go, castShadows, occluder = false)` for hand-built objects.

**Runs**: `Run(parent, name, IList<Vector3> polyline, RunSpec spec) -> GameObject` (root `TL_<name>` at world identity).
`RunSpec { panel, panelLength = 3, post, postAtEveryJoint = true, postAtStart = true, postAtEnd = true,
collider = None|Box, colliderHeight = 1.4, colliderThickness = 0.25, y = GrassY, merge = true, castShadows = true,
minSegment = 0.4 }`. The panel prop is modelled along local +Z from z 0 to `panelLength`, centred on x 0, base y 0.
Each straight piece gets `round(len / panelLength)` panels (min 1) stretched to fit exactly. `merge = true` (default)
bakes the whole run into ONE GameObject + one unique mesh; `Box` adds one `Col` child BoxCollider per straight piece.

**Ground** (world-space geometry, unique mesh per call, never casts shadows; `walkable = true` -> layer Ground +
MeshCollider, else a collider-free decal on Default):
`Ribbon(parent, name, centre, width, material, y, walkable = false, closed = false)`,
`Polygon(parent, name, outline, material, y, walkable = false)` (any winding, concave ok, no holes),
`Disc(parent, name, centre, radius, material, y, walkable = false, segments = 32)`,
`Ring(parent, name, centre, innerRadius, outerRadius, material, y, walkable = false, segments = 40)`,
`Rect(parent, name, centre, Vector2 size, yawDeg, material, y, walkable = false)`,
`Extrude(parent, name, outline, yBottom, yTop, topMaterial, sideMaterial, walkable = false, castShadows = false)` (plinths, kerbs, stages, raised beds),
`Mound(parent, name, centre, Vector2 radii, height, material, baseY, seed = 0, walkable = false, segments = 14, rings = 4)` (hills),
`Custom(parent, name, MeshData, walkable = false, castShadows = false)`, helpers `Circle(centre, radius, segments)`,
`AddPolygon(MeshData, material, outline, y)`, `Sanitize(string)`.

**Text**: `Label(parent, name, text, localPos, localRot, Vector2 size, Color, FontStyles = Bold) -> TextMeshPro`
(reads from local -Z at identity; same settings as `NeighborhoodBuilder.Label`);
`SignBoard(parent, name, center, yawDeg, Vector3 size, material, text, textColor, twoSided = false, collider = false) -> GameObject`
(pivot `TL_<name>` at the board CENTRE; text on the +Z face; posts are yours).

Collider policy (DESIGN 4): trunks one capsule (`ColliderTrunk`), sheds/walls/hedges-as-walls boxes, everything else
none. Anything the player walks ON: `walkable`. Nothing on Interactable (3) or Walls (7).

## 7. `TownLifeFences` (signatures; fences agent implements)

`Style*` ids (`"acres-board"`, `"meadows-picket"`, `"rear-spine"`), `GateGap = 1.8`, `MinRun = 1.5`,
`enum Role { Side, Rear, Wing, StreetSide, StreetRear }`.
`Run { a, b (world, y 0), style, role, chain, roadId, stopIds[] (owner first), streetFacing, gatePostA, gatePostB, height, Length }`,
`Chain { id (== Parcel.chain), roadId, side, stopIds[] (station order; [0] parents the marker), style, runs[] }`,
`Result { chains[], runs[], skipped, ChainOf(stopId), RunsOf(stopId) }`.
`Plan(scene, plan) -> Result` (pure), `PlanForTown(town) -> Result|null` (for `CoastalYardAuthoring.Rebuild`; null =
keep the legacy algorithm), `DefineStyles()`, `ColliderSize(style) -> (thickness, height)`,
`BuildRunVisual(parent, run, baseY, uniqueName) -> GameObject` (visual only, no collider),
`BuildPreview(ctx)` (preview module `fences`, group `TL_Fences`), `Reserve(result, occupancy)`.
Readers: Yards plants along `ctx.fences.RunsOf(stopId)`; Streets hedges runs with `streetFacing` (1 m in front,
street side).

## 8. Preview loop

```
preview.sh <modules-csv|all> <abs-out-dir> [shots-csv|all] ["key=value;key=value"]
```
- modules: `paint,park,meadows,streets,yards,edges` + preview-only `fences`, `smoke`, and any `foo` ->
  `TownLifeFoo.Build` (section 9). `all` = the six real ones (add `fences` explicitly: `all` alone does not draw them;
  write `paint,park,meadows,streets,yards,edges,fences`).
- outputs: `<shot>.png` (1600x1000), `shots.tsv`, `report.txt` (ctx.report), `result.txt` (first line `OK`,
  `OK-WITH-MODULE-ERRORS` or `FAILED`; counts, timings, dirty flags, leak check), `compile.txt`, `cli.txt`.
  Exit 0 / 3 / 1; 2 = compile error (nothing ran in the Editor); 4 = editor busy or lock never freed.
- shots: the 34 fixed poses of the "before" set (same names as `photos/before/*.png`, same lighting) plus custom shots
  from `shotsFile=<abs path>`: one per line `name eyeX eyeY eyeZ lookX lookY lookZ ortho fov` (`ortho > 0` = top-down
  half height in metres; else perspective, vertical fov, default 65; `#` comments).
- options: `districts=acres|meadows|acres+meadows`; `hideFences=0|1` (default 1: old yard fences under
  `CoastalYardBoundary` are not photographed); `hidePark=0|1` (default 1 when `park`, `smoke` or `all` runs: legacy
  `NeighborhoodGen/Park` contents are hidden except the playground KidPen and whatever stands inside it);
  `parkKeep=ParkPath_NS,ParkSign` (name prefixes that stay); `hide=NameA,NameB` (exact object names anywhere);
  `noScene=1` (only TownLife objects: prop sheets); `size=WxH`. Other keys go to `ctx.options`.
- It takes the editor lock itself (`PREVIEW_OWNER` = your agent name), refuses while the Editor plays or compiles
  (releases, sleeps 30 s, retries), and holds the lock about 3 s for a few shots, 7 s for all 36. The CLI may print a
  bogus "Main thread operation timed out after 5000ms": the script trusts `result.txt`.
- Mechanism: `compile.sh` builds the draft `TownLife*.cs` into a uniquely named scratch DLL (pass 2), an `eval_file`
  loads it with `Assembly.Load(bytes, pdb)` and calls `TownLifePreview.Run` by reflection. No domain reload, no import,
  nothing saved. Chosen over a concatenated `run_script` bundle (which also compiles: 575 ms dry run) because it keeps
  real file names and line numbers in diagnostics and stack traces, needs no using-hoisting (alias/ambiguity clashes
  between files), compiles before the lock is taken, and a fresh assembly name per run cannot clash with earlier runs or
  with the integrated copy later.
- `compile.sh [--draft <root>] [--quiet] [extra.cs ...]`: pass 1 `[editor]` = the whole Editor assembly with every draft
  file substituted/appended; pass 2 `[townlife]` = TownLife files alone against the compiled game assemblies (public-API
  proof + preview DLL). Prints errors from anywhere, warnings for draft files only, then `DLL:`, `PDB:`,
  `COMPILE OK|FAILED`. Exit 0 only when clean. Outputs under `tools/out/` (auto-pruned after 90 min).

## 9. Worked example: a tiny content module

```csharp
using System.Collections.Generic;
using UnityEngine;

/// Bollards with a flower tub between them along the west side of Elm Avenue.
public static class TownLifeExample
{
    public const string Name = "example";

    public static void Build(TownLifeContext ctx)
    {
        if (!ctx.options.District(TownLifeScene.Acres)) return;
        var road = ctx.scene.Road("elm"); if (road == null) { ctx.Error(Name, "no elm"); return; }

        // 1. palette + props (module-prefixed ids; stock colours need no Define)
        TownLifeKit.Define("example.petal", new Color(1f, 0.45f, 0.6f));
        TownLifeKit.DefineProp("example.bollard")
            .Cyl("stone.dark", Vector3.zero, 0.14f, 0.9f, 8)
            .Dome("paint.white", new Vector3(0f, 0.9f, 0f), new Vector3(0.16f, 0.12f, 0.16f))
            .ColliderBox(new Vector3(0f, 0.45f, 0f), new Vector3(0.3f, 0.9f, 0.3f))
            .NoShadows();
        TownLifeKit.DefineProp("example.tub")
            .Cyl("wood.warm", Vector3.zero, 0.5f, 0.45f, 8, 0.6f)
            .Blob("example.petal", new Vector3(0f, 0.6f, 0f), new Vector3(0.55f, 0.3f, 0.55f), 3)
            .NoShadows();

        // 2. walk the road: left side (-1), 1.2 m behind the sidewalk, every ~10 m, clear of the end junctions
        var group = ctx.Group("Example"); var occ = ctx.occupancy;
        float lateral = -(road.halfWidth + 3f + 1.2f);
        int placed = 0, skipped = 0, i = 0;
        foreach (float station in TownLifeGeo.Stations(road.length, 10f, 14f, 14f))
        {
            var rng = ctx.Rng("example/elm/" + i++);                       // one RNG per feature instance
            var centre = TownLifeGeo.PointAt(road.points, station, out var tangent);
            var p = centre + TownLifeGeo.Right(tangent) * lateral;
            p.y = ctx.scene.GroundY(p);
            string prop = rng.Next(3) == 0 ? "example.tub" : "example.bollard";
            float radius = prop == "example.tub" ? 0.7f : 0.3f;

            // 3. keep-outs and earlier modules: test + reserve in one call
            if (!occ.TryReserve(p, radius, TownLifeTags.Furniture + ".example")) { skipped++; continue; }
            TownLifeKit.Spawn(group, prop, p, TownLifeGeo.Yaw(tangent) + TownLifeContext.Range(rng, -8f, 8f));
            placed++;
        }

        // 4. a flat decal strip under them (Flat things may sit where solids may not)
        var strip = TownLifeGeo.Offset(road.points, lateral);
        if (occ.FreeLine(strip[0], strip[strip.Length - 1], 0.4f, TownLifeOccupancy.What.Flat))
        {
            TownLifeKit.Ribbon(group, "example_strip_elm", strip, 0.8f, "gravel", TownLifeContext.SoilY);
            occ.ReservePolyline(strip, 0.4f, TownLifeTags.Bed + ".example", TownLifeOccupancy.Block.Marker);
        }

        // 5. report
        ctx.Log(Name, placed + " placed, " + skipped + " skipped on Elm");
    }
}
```
Try it without touching the builder: any module name the builder does not know resolves, in the preview only, to
`TownLife<Name>.Build(TownLifeContext)` from the draft sources and runs after the fixed modules. So with the file above
saved as `draft/.../TownLife/TownLifeExample.cs` (or in a private copy of the draft tree, `PREVIEW_DRAFT=<root>`):
`preview.sh example <out> street-elm` (verified: 8 placed, 10 skipped by keep-outs, strip skipped because it would
cross driveways). Use this for experiments and prop sheets; never leave such files in the shared draft tree.
The architect's `smoke` module in `TownLifePreview.cs` is a second, larger example (ribbon, disc, ring, mound, merged
fence run with colliders, sign board, RNG scatter with spacing).

## 10. Known gaps (owners)

- Kit, non-transient path (asset writes, in-place reuse, `Variant`) compiles but has NEVER been executed: nothing may
  be written under `Assets/` before integration. Kit + core agents must exercise it at integration time.
- `TownLifeScene.GroundY` is flat (no river-bank profile); coast keep-out is a 3 m line, not the sea half-plane;
  nothing models the edge of the ground slab (x -39..903.5, z -52..955.5) (core agent).
- `TownLifeBuilder.RebuildStandalone` is a TODO; `PipelineBuild` does not yet delete stale TownLife assets (core agent).
- `TownLifeFences` bodies are placeholders (empty network); `TownLifePlan` is a stub returning no parcels until the
  plan agent's file replaces it (same path).
- Stock palette / props are a seed, not the library (kit agent).
