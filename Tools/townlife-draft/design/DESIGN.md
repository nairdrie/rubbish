# Town Life: design brief (Acres + Meadows)

Lead-authored. Every agent on this job reads this first. It wins over anything in the research reports where they disagree.

## 0. The owner's complaint, verbatim

> "I'm trying to fill up the town and give it life. The houses in the city are too disconnected, the backyard fences don't
> connect to each other (between houses), the park is very sad. Really try to get some cohesive real town feeling. Focus on
> the first 2 unlocked neighborhoods."

The previous agent validated by counts and validators and the owner still called the town "extremely empty and barren".
**The only acceptance test that matters is what the place looks like at eye level and from a truck cab.** Tone: derpy, cute,
"friendslop", Mario-esque. Bright, readable, a bit silly. Never grim, never photoreal.

Districts in scope: Mildly Pleasant Acres (`mildly_pleasant`) and Minivan Meadows (`minivan_meadows`). Do not touch the
other four districts' content.

## 1. What is wrong (measured; see `understand/*.md`, `understand/geometry/blocks.md`, `photos/before/*.png`)

- 57% of every Acres block and 69% of the Meadows ring interior is unowned flat grass. Buildings cover 14%.
- Fences are 42 separate three-sided pens hugging one house footprint each. Only 15 of 41 neighbour pairs share a divider.
  A bare 16-38 m band runs down the middle of every block.
- Pickle Park: 9% of its 112 x 78 m block is designed. No edge, no centre, you see straight through it.
- Zero street trees. 50 trees in 786 x 393 m. 41% of street frontage has nothing behind the sidewalk.
- No town edge: vistas end in a flat horizon. Riverbank and coast strip are bare.
- Cul-de-sac islands are bare 28 m discs. Depot Row (the spawn street) is the emptiest street.
- Acres is monotone: 30 of 37 houses dark green-grey, every roof the same near-black.
- Four Meadows houses (mm-juicebox-210/224/240/254) hang over the river.

## 2. The design in one paragraph

Every block is **tiled into parcels** (lots), one per house, with nothing left over except deliberately designed common
land. Lot lines carry a **connected boundary network**: one shared divider between neighbours, one shared rear line down
the middle of each block, closed to the house by short wing fences with an open gate gap. Back yards are **lived in**
(trees, sheds, veg beds, patios, washing lines, play stuff). Streets get **rhythm and enclosure** (street trees both sides,
front hedges and beds, corner planting). **Pickle Park** becomes a real park filling its whole block: hedge-and-rail
perimeter with four gateways, tree avenue promenade, central plaza with a giant googly-eyed pickle monument, pond with
ducks, the playground, picnic lawn, bandstand, sports court. Meadows gets **planted court islands** and a **Meadows
Common** (kids' soccer pitch, paths linking the two courts) in the ring interior. The town gets an **edge**: woodland belts
and low wooded hills closing every vista, a riverside promenade along Birch, a coast strip. Acres houses get a warm varied
**paint palette**. The four river houses move to the empty coast-side frontage of the Mile.

## 3. Decisions already made (do not re-litigate)

1. **No new households, no removed households, no renamed stopIds.** 98 identities stay. Density comes from parcels,
   boundaries, planting and outbuildings, not extra collection stops.
2. **Road network, gates, crossings, landmarks, infill sites and everything else in `CoastalCity.json` stay untouched.**
   Editing them trips `CityBlueprintStamp` and forces a non-transactional full rebuild. New recipe data lives in code or in
   new files.
3. **No full rebuild.** Everything is applied to the existing saved scene through an incremental, transactional path.
4. **New generated layer = `NeighborhoodGen/TownLife`**, built by new self-contained static classes (`TownLife*.cs`) that
   use only public API of the game assemblies. It is never parented under `House_*` holders, `Park` aside (see 6).
5. **The boundary network for the two districts is planned by TownLife (`TownLifePlan` + `TownLifeFences`) and realised
   inside `CoastalYardAuthoring.Rebuild`** (it must live there: Rebuild destroys and regenerates every
   `CoastalYardBoundary` on every publish). Hills/Bluffs lots and lots with no road context (the test fixtures at
   (5000, 5000)) keep the legacy algorithm untouched.
6. **Pickle Park is rebuilt in place** around the existing landmark (256.75, 213.2). The `Park` group, the two crossings,
   the `playground` KidPen (left exactly where it is, world-space fields untouched), `ParkPath_NS`, two `ParkSign`s and
   one `SM_lemonade_shop*` keep their names and semantics. The street graph is NOT changed in this pass, so a 3 m wide
   collider-free, prop-free corridor must stay open along the straight lines landmark -> each crossing.
7. **Boundaries are solid static colliders, not knockable.** Existing Meadows front pickets (241 knockables) stay as they are.
8. **Every fenced back yard has an open 1.8 m gap** (posts, no leaf) in a wing fence. Nothing is sealed.
9. **No runtime scripts are added.** Existing components may be used if they suit (e.g. `DerpyWobble`). Ambient motion that
   needs new runtime code is out of scope for this pass.
10. **Four river houses are relocated** to meadow-loop side +1 on the free coast-side frontage (stations ~236-400) via
    `ResidentialBlocks.json` placements (identities preserved). A separate agent does this first; treat the live scene as
    truth for their positions.
11. **Palette**: bright, saturated-but-soft, toy-like. Foliage in several greens plus blossom pink and a little autumn
    orange. New materials live in TownLife's own asset folder; do not recolour existing `NB_*` materials.
12. `NetVersion` is not bumped by agents. The lead reports it to the owner.

## 4. Hard rules for generated content (digest; details and code refs in `understand/runtime.md`, `yards.md`, `pipeline.md`)

- **Pure and deterministic.** Plan from typed data read off scene components, never from `Physics` queries. No
  `UnityEngine.Random`, no shared RNG. Use `new System.Random(StableHash(key))` per feature, keyed by stopId/road/feature name.
- **Never place anything solid**: on a carriageway or within 3.4 m of a road centreline; on a sidewalk (0..3 m beyond the
  asphalt edge; NPC walk lane is 2.4 m beyond it); within 2.6 m of a driveway line `ramp -> top` or on a `Driveway` slab;
  within 1.4 m of a garden path `door -> bends -> pathEnd`; within 3 m of a curb stop (lot origin), bin or mailbox; inside a
  `CoastalInfillSite` footprint (publish fails on any foreign collider there); on a depot pad; inside a KidPen; within
  12 m of a junction centre on the verge (sight lines); on the two zebra crossings' sidewalk forecourts; in the park corridor (3.6).
- **Layers**: decorative solids on Default (0). Anything the player must walk ON needs layer Ground (6) and a collider.
  Nothing scenic on Interactable (3). Tall thin things do not go on Walls (7).
- **Colliders are scarce.** Trunks get one capsule. Sheds, gazebo posts, walls, hedges-as-walls get boxes. Foliage, flowers,
  small props, flat paving, ponds: none. Never keep Unity's default collider on a flattened cylinder (it becomes a dome).
- **No scene-embedded meshes or materials.** Every mesh and material is an asset under
  `Assets/Materials/Generated/TownLife/`. Shared meshes per prop type; one GameObject per prop (parts merged into one mesh
  with submeshes per material). Set `StaticEditorFlags.BatchingStatic | OccludeeStatic` (plus `OccluderStatic` on big
  solids) yourself. Shadow casting off on small props and ground decals.
- **Names with meaning are forbidden for new objects**: `Lot_*`, `SM_house*`, `Hall`, `Driveway`, `Drive`, `DriveLink`,
  `Turnaround`, `Path`, `SM_path_garden`, `SM_whirlpool`, `Fountain`, `SM_garden_fence_long*`, `Nature`, `Ground`,
  `Trampoline`, `TennisCourt`, `InfinityPool`, `SM_swimming_pool*`, `SM_clothes_drying*`, `SM_streetlamp*` (unless you
  want it lit at night with moths, which for park lamps you do), `Building body`. Prefix TownLife objects `TL_`.
- **Heights**: terrain grass y = -0.08, asphalt 0.02, sidewalk top 0.055, house holders 0.15. Sit things ON the ground they
  stand on: props at -0.08 on grass, paving decals a few cm above grass (z-fight safe: >= 0.03 apart), walkable slabs
  need a Ground collider.
- **Budget**: the scene is at ~10.8k GameObjects. Aim to add no more than ~4,000 for both districts. Prefer clump meshes
  for woodland (3-5 trees per GameObject) and long panels for runs.
- **TownAuthored** is the owner's hand-painted layer: never write into it; treat its renderers as obstacles (empty today).
- **CoastalYardBoundary markers**: one marker per frontage chain, parented under the first owner's holder with identity
  local transform; all owners share one `roadId`; each stopId in exactly one marker; BoxCollider count under the marker ==
  `boundaryPoints.Length / 2`; no stored segment within 2.6 m of `ramp -> top`; nothing else parented under it.
- **Tests reach code by reflection strings.** Do not rename, re-type or overload: `CoastalYardAuthoring.Rebuild /
  LoadSettings / IsLegacyBackFence / RemoveLegacyBackFences`, `CoastalYardRules.*`, `NeighborhoodBuilder.CoastalLotSites /
  ValidateResidentialHouseholds / CoastalBackdropNorthStart / FitCoastalParkEntrances`, `CoastalResidentialLayout.Resolve
  / Configure`, `Setup_TownMap.ScanCoastal`, `CoastalCityGraphBuilder.Build`. Rebuild's report must keep the substrings
  `"N homes"` and `"M connected garden rows"`.

## 5. Architecture and file ownership

All new source is drafted OUTSIDE the project in `scratchpad/draft/`, mirroring project paths, so the owner's Editor never
sees half-written code. New files go under `draft/Assets/Scripts/Editor/TownLife/`. Edited existing files are full copies at
their mirrored path. Integration into `Assets/` happens once, later, by the integrator only.

| file | owner | content |
|---|---|---|
| `TownLifePlan.cs` | plan agent | Pure parcel plan. Public API frozen in section 7. |
| `TownLifeFences.cs` | fences agent | Turns plan edges into boundary runs (style, gaps, wings). |
| `Assets/Scripts/Editor/CoastalYardAuthoring.cs` (copy) | fences agent | Starter-district branch consuming `TownLifeFences`; legacy path kept. |
| `TownLifeKit.cs`, `TownLifeKit.Props.cs` | kit agent | Materials, mesh assets, prop recipes, runs, ribbons, polygons. |
| `TownLifeScene.cs` | core agent | Scene -> typed data (houses, obstacles, courts, park block, water) + occupancy/reservations. |
| `TownLifeBuilder.cs` | core agent | Clear/Build orchestration, module order, standalone transactional command. |
| `Assets/Scripts/Editor/NeighborhoodBuilder.Coastal.cs` (copy) | core agent | Hooks only. |
| `TownLifePreview.cs` + `tools/preview.sh` | architect, then core agent | No-recompile, no-save preview harness. |
| `TownLifePark.cs` | park agent | Pickle Park. |
| `TownLifeYards.cs` | yards agent | Back and front yard dressing, outbuildings. |
| `TownLifeStreets.cs` | streets agent | Street trees, verge and corner planting, street-facing returns. |
| `TownLifeMeadows.cs` | meadows agent | Court islands, Meadows Common, ring interior. |
| `TownLifeEdges.cs` | edges agent | Woodland belts, ridgelines, riverside, coast, Depot Row, district seam. |
| `TownLifePaint.cs` | paint agent | Acres house palette. |

Module entry points are `public static void Build(TownLifeContext ctx)`. Module order is fixed: Paint, Park, Meadows,
Streets, Yards, Edges. Each module reserves what it places in `ctx.occupancy` so later modules avoid it.

## 6. Preview loop (how everyone sees their own work)

`unity command run_script` compiles ONE C# file in memory with no domain reload and runs a public static entry. The
harness concatenates every `TownLife*.cs` draft into one file (usings hoisted), runs `TownLifePreview.Run`, which reads the
live scene, builds the requested modules into a temporary `HideFlags.HideAndDontSave` root with transient (non-asset)
meshes and materials, photographs fixed camera poses with `PreviewRenderUtility` (hiding old yard fences, the legacy park
contents and anything else the real integration will replace), writes PNGs, destroys everything, and leaves the scene
clean (not dirty, nothing saved). All Editor access is serialised by the mkdir lock. **Look at your own PNGs. If it looks
dead, sparse, floating, clipping, mis-scaled or mis-rotated, it is not done.**

## 7. `TownLifePlan` public API (FROZEN: you may add members, never rename or remove)

```csharp
/// Pure geometry: no UnityEditor, no Physics, no scene access, no statics that survive a call. Deterministic.
public static class TownLifePlan
{
    public sealed class Road { public string id, districtId; public Vector3[] points; public float halfWidth; public bool bridge, service; }
    /// World-space convex quad, corners in order, y ignored.
    public struct Quad { public Vector3 a, b, c, d; public string tag; }
    public sealed class House
    {
        public string stopId, roadId, districtId;
        public int side;                 // +1 = right of the road's travel direction, -1 = left (sign of CityRoadAnchor.lateral)
        public float station;            // metres along the road polyline of the lot origin
        public Vector3 origin;           // holder world position (the curb stop)
        public Vector3 right, inward;    // unit, y = 0: along the street (holder +X) and from the street into the lot (holder -Z)
        public Quad footprint;           // building footprint
        public bool hasDrive; public Vector3 driveA, driveB; public float driveHalfWidth;  // driveway centre line; set whenever a slab exists
        public Vector3[] walk;           // door, bends..., pathEnd
        public Quad[] keepIn;            // rear-yard items that must end up inside this parcel (pool, trampoline, kid pen, drying rack)
        public Quad[] frontItems;        // front-yard items (pickets, signs, pens); informational
    }
    public sealed class Settings
    {
        public float cell = 0.5f, sidewalk = 3.3f;
        public float depthAcres = 46f, depthMeadows = 36f, depthCourt = 30f;   // max parcel depth behind the sidewalk
        public float endCap = 20f, endSnap = 13f;        // row ends: extend up to endCap past the last house, snap to the corner if less than endSnap would be left
        public float riverSetback = 27f, coastSetback = 15f, reservedMargin = 1f;
        public float thinStrip = 12f;                    // unassigned land narrower than this is absorbed by the adjacent parcel
        public float simplify = 0.8f;                    // Douglas-Peucker tolerance for edges and outlines
    }
    public sealed class Input { public Road[] roads; public House[] houses; public Vector3[] river, coast; public Quad[] reserved; public Rect bounds; public Settings settings = new Settings(); }

    public const int Common = -1, RoadReserve = -2, Water = -3, Reserved = -4, Outside = -5;

    public sealed class Parcel
    {
        public int index; public House house;
        public string chain;             // roadId + "|" + side + "|" + groupIndex : one frontage chain = one future yard marker
        public bool court;               // frontage road is a closed loop (cul-de-sac bulb)
        public Vector3[] outline;        // simplified closed polygon, y = 0
        public float area, backYardArea;
        public float frontLine, rearLine;// metres from origin along `inward` to the building's front wall / rear wall
        public Vector3 centroid, backYardCentroid;
        public int[] neighbours;
    }
    public enum EdgeKind { ParcelParcel, ParcelCommon, ParcelRoad, ParcelWater, ParcelReserved }
    public sealed class Edge
    {
        public int a, b;                 // a = parcel index; b = other parcel index or a negative code
        public EdgeKind kind;
        public Vector3[] points;         // open polyline, y = 0, simplified, >= 2 points
        public string ownerChain;        // which chain's marker owns it (cross-chain parcel-parcel edges: ordinal-smaller chain)
        public bool frontage;            // parcel a's own street frontage
    }
    public sealed class CommonRegion
    {
        public int id; public float area; public Rect bounds; public Vector3 centroid;
        public bool touchesWater, touchesBounds;
        public int[] adjacentParcels; public string[] adjacentRoads;
        public Vector3[] outline;        // simplified outer ring
    }
    public sealed class Result
    {
        public Input input; public Parcel[] parcels; public Edge[] edges; public CommonRegion[] commons;
        public int LabelAt(Vector3 world);             // parcel index or negative code
        public int CommonAt(Vector3 world);            // common region id or -1
        public float RoadEdgeDistance(Vector3 world);  // metres beyond the nearest carriageway edge (negative on asphalt; sidewalk is 0..3)
        public float WaterDistance(Vector3 world);     // metres to the river/sea reserve (<= 0 inside it)
        public float DepthInParcel(Vector3 world, int parcel); // metres along the parcel's `inward` from its origin
        public bool InBackYard(Vector3 world, out int parcel); // in a parcel and behind its building's rear wall line
        public Parcel ParcelOf(string stopId);
    }
    public static Result Build(Input input);
    public static List<string> SelfCheck(Result result);
}
```

Algorithm ("frontage Voronoi", prototyped and proven in `proto/parcels2.py` + `proto/parcels2.png`):
rasterise at 0.5 m; mask carriageway + sidewalk, water setbacks, reserved quads; group houses into frontage chains (same
road + side, consecutive stations closer than `2*endCap + endSnap`); each chain claims cells on its side of its road within
max depth whose projected station lies inside the chain's extent, the nearer frontage winning (this gives the straight
shared rear spine between back-to-back rows and a mitre at corners); within a chain, cells go to houses by station with
dividers at the **midpoint of the free gap between the two buildings' facing side walls** (NOT the midpoint of the lot
origins: origins sit 2.8-11 m off-centre; fall back to origin midpoint only if the buildings overlap in station); keep only
the connected component touching the house; absorb thin leftover strips; force `keepIn` quads into their parcel; then
vectorise label boundaries as a planar graph (junction vertices, one chain per label pair, simplified once so shared edges
are identical for both neighbours).

## 8. Module briefs (vision, not pixel specs: you are the designer of your module)

Read `understand/geometry/blocks.md` for exact coordinates and `understand/geometry/acres_meadows.json` for raw data.

**Fences.** Per parcel: side dividers from the building FRONT line back to the rear line, the rear line, and two short
wing runs from the house's front corners out to the side dividers, one of them with a 1.8 m open gap framed by posts.
Shared edges are built once. Style is consistent per chain so streets read as planned: Acres = warm wooden board fence
~1.5 m (visually heavier than today's 5 cm pickets: it must read from 100 m and from above) with a capped top rail and
posts every panel; Meadows = white picket ~1.2 m; rear spines get the tallest, plainest style. Where a parcel's side or rear
faces a street (corner lots, double-frontage lots backing onto Depot Row or the Mile) the run sits 1 m behind the
sidewalk and the Streets module plants a hedge in front of it. Skip any stretch that would violate section 4; never leave
a run shorter than 1.5 m; never cross a driveway, garden path, kid pen, pool or infill footprint.

**Park (Pickle Park, block interior x 192..304, z 174..252.25).** Perimeter: clipped hedge with a low rail, open at four
gateways (S on Maple and N on Birch at the existing crossings, x = 256.75, each a 14 m wide open forecourt with a timber
arch set back clear of the sidewalk; W on Elm, E on Pickle Park Ave, both joining the block's own sidewalk). Tree avenue
promenade N-S on the corridor line, paved 5 m wide, lamps and benches each side, never anything in the 3 m corridor.
E-W path through a central plaza; a looping stroll path ~10 m inside the perimeter. Centrepiece: **the Pickle**, a giant
upright pickle with googly eyes on a plinth in a round shallow fountain basin, sited beside the promenade so it is the
first thing you see from both entrances but stands off the corridor. Pond (irregular, reeds, lily pads, a few derpy ducks,
a little jetty, a willow) in the north-west. Playground: the existing sealed KidPen stays put; build the play AREA around
it (safety-surface patch, extra equipment outside the pen from the pack's Playground prefabs, benches for parents, low
planting). Picnic lawn with tables and a BBQ under big trees. Bandstand/gazebo on a low stepped base in the east half.
Half basketball court. Flower beds at the gates and plaza. Park bins and benches from the pack prefabs. Around 60-90
trees of mixed kinds in groups, not a grid, leaving two or three open lawns so it is a park and not a forest.
Lemonade stand stays by the south gate.

**Yards.** Each back yard gets a theme chosen by stable hash, sized to the yard: kitchen garden + shed; patio + BBQ +
parasol; orchard; kids' yard (swing/sandbox from the pack, playhouse); washing day; greenhouse; dog yard (kennel, bone);
lazy lawn (deckchairs, paddling pool). All get 1-4 trees toward the rear, shrubs and flower borders along the dividers,
maybe a bird bath or gnome. Respect existing pools, trampolines and kid pens (keep them the hero of that yard). Front
yards: foundation planting under the front wall, flower beds flanking the door path, one feature tree where it fits,
low front hedge or bed along the sidewalk edge broken at the path and driveway (Acres only; Meadows keeps its pickets).
Where a driveway ends at open grass beside the house, a small detached garage or carport can terminate it if there is
room inside the parcel.

**Streets.** Street trees on both sides of every non-service street, in a line ~1.2 m behind the sidewalk, 12-16 m
apart, one species per street (Elm = tall round elms, Maple = orange-tinted maples, Oak = big oaks, Birch = white
birches, Home Run = blossom, Pickle Park Ave = something pickle-green and pointy, Minivan Mile = alternating blossom and
round), skipping everything in section 4. Corner planting at junctions (low beds, outside the sight triangle). Hedges
in front of street-facing side/rear boundaries. Re-add a few hydrants and the odd road-works site only if the existing
knockable recipes can be used without new runtime code (optional).

**Meadows.** Court islands (radius 14 m, inside the inner sidewalk): each a planted roundabout: low stone kerb ring, flower
ring, one hero feature (Carpool Court: a huge old oak with a circular bench; Juice Box Court: a kids' fort or a
giant juice-box sculpture), lamp. Meadows Common in the ring interior between the courts: a kids' soccer pitch with
goals, mown stripes, a little bleacher and a row of traffic cones; gravel paths linking both courts, the two lanes and
the Mile; picnic spots; groves. Buffer planting between court back fences and the Mile. Seaview row (the four relocated
houses) gets the same care as any other row.

**Edges.** South of Home Run and the Mile, west of Depot Row, east of the Mile to the coast, the seam between Oak and
the Mile's west leg: woodland belts (dense clump meshes, understorey shrubs, a few boulders, wildflower patches), getting
denser away from town, plus low rolling wooded hills (non-walkable, far enough out) that close every street vista.
Riverside along Birch's north side: a promenade strip with a post-and-rail fence at the bank top, willows and birches,
benches facing the water, reeds, two little fishing jetties, lamps. Coast strip: dune grass tufts, a few pines, a
bench or two, beach-fence. Depot Row: tree row and planting strips between and behind the depot pads (never on a pad),
hedge along the east side backing the Elm lots. District seam: hedge and tree belt between the three Meadows gates.

**Paint.** Acres houses get a wall colour from a warm toy palette (cream, butter, sage, sky, brick, terracotta, dusty
pink, pale teal), trim stays light, roofs vary across 3-4 tones (charcoal, brown, slate blue, terracotta). Deterministic
per stopId, neighbours never share a wall colour, window/emissive material slots untouched (night lighting depends on
their names). Material variants are assets in the TownLife folder. Idempotent.

## 9. Working rules

- Draft in `scratchpad/draft/` only. Do not write under `/Users/nick/Dev/Rubbish/` unless your brief says you are the
  integrator or the relocation agent.
- Compile-check offline with `tools/compile.sh` (Unity's bundled csc against the Editor response file; no Editor contact).
- Editor access only under the lock (`scratchpad/editor.lock`, mkdir protocol, unique owner name, release only your own),
  only after `unity command editor_status --project-path /Users/nick/Dev/Rubbish --json` shows `playMode: "stopped"` and
  not compiling. If the owner is playing: release the lock, sleep, retry. Never stop play mode, never trigger a recompile,
  never `save_all`, never save `Bootstrap.unity`, never use git checkout/stash/clean.
- `eval`/`eval_file`/`run_script` need explicit generous timeouts (the default 5 s main-thread limit kills long evals).
- Prefix scratch files with your agent name. Do not edit another agent's file; if you need something from it, say so in
  your final report.

## 10. Addenda (after the foundation stage; these override earlier sections where they differ)

**Read `design/CONTRACT.md` next.** It is the exact API. Signatures are frozen.

**Relocation is done and published.** mm-juicebox-240 / -254 / -210 / -224 now sit on meadow-loop side +1 at stations
225.5 / 261.5 / 305.0 / 364.0 (the "Seaview row", continuing the south row round the south-east corner).
`understand/geometry/acres_meadows.json`, `blocks.md` and the RELOCATION section of `understand/geometry.md` are
regenerated; `photos/before2/` is the new baseline photo set; `photos/relocation/` shows the row. The old river-side
frontage of the Mile (stations ~462..612, flat strip 12-21 m deep, bank top z ~284) is now empty public riverside.
Accepted as is: 210 and 224 have shallower land behind them; Meadows parcels are capped 36 m behind the sidewalk, which
ends 16 m or more from the coastal bank top. The bank top steps with the 8 m terrain grid: x 815.75 for z 20..100,
823.75 for z 116..148, 831.75 for z 156..220. Keep solids west of that.

**The parcel plan is real** (`TownLifePlan.cs` in the shared draft): 59 parcels, 14 chains, SelfCheck clean, ~1.2 s.
Picture: `draft-plan/out/plan.png` (+ `plan_acres_west/east.png`, `plan_meadows.png`), numbers: `draft-plan/out/plan.json`.
Things to know: (1) mm-juicebox-224 is its own chain; a ~20 m wide strip of common land at about x 769-804, z 142-163
separates its lot from the row to the south: make it a deliberate beach-access path, not a leftover. (2) Court lots on
the south sides run through to the Mile's sidewalk (double-frontage): their Mile side is a non-frontage ParcelRoad edge.
(3) The two Acres infill gardens inside block A3 eat most of two corner lots' back yards; the 1.7 m strips beside them are
verge. (4) Home Run's south row and the Mile's south/east rows have a parcel-common rear edge onto the big outside
region 0 (touchesBounds). (5) `parcels[i]` belongs to `scene.houses[i]`; outlines are counter-clockwise; shared edge
polylines are bit-identical for both neighbours.

**Stock kit library (frozen ids; the kit agent delivers them, everyone else uses them and does not redefine them).**
Until an id exists (`TownLifeKit.HasProp(id)`), fall back to `kit.tree.round` / `kit.shrub.round` through a tiny local
helper so your module always runs, and make your FINAL previews with the real ones.
- Trees (suffix = size: s ~3.5 m, m ~6 m, l ~9 m, xl ~12 m tall; trunk capsule collider; each id has baked-in shape so
  vary by yaw and 0.85-1.2 scale): `kit.tree.round.s/m/l`, `kit.tree.elm.m/l` (tall vase), `kit.tree.oak.l/xl` (broad,
  dark), `kit.tree.maple.m/l` (orange-red), `kit.tree.birch.m/l` (white trunk, lime), `kit.tree.blossom.s/m` (pink),
  `kit.tree.pickle.m/l` (columnar, pointy, pickle green), `kit.tree.pine.m/l`, `kit.tree.willow.l` (weeping),
  `kit.tree.fruit.s` (apple, red dots).
- Woodland clumps (several trees in ONE object, one collider-free canopy mass with 1-2 trunk capsules; for belts):
  `kit.clump.mixed.3`, `kit.clump.mixed.5`, `kit.clump.pine.3`, `kit.clump.pine.5`, `kit.clump.birch.3`.
- Shrubs and ground plants (no colliders): `kit.shrub.round.s/m`, `kit.shrub.flower.pink/yellow/white/purple`,
  `kit.shrub.cone` (topiary), `kit.grass.tuft`, `kit.reeds`, `kit.flowers.red/yellow/pink/purple/white/mixed` (a ~1.2 m
  patch of flower heads), `kit.lilypad`.
- Run panels (for `TownLifeKit.Run`; modelled along +Z, default panelLength in brackets): `kit.panel.hedge.low` [2]
  (0.7 high), `kit.panel.hedge.mid` [2] (1.2), `kit.panel.hedge.tall` [2] (1.8), `kit.panel.flowerborder.red/yellow/
  pink/purple/white/mixed` [2] (0.7 m deep bed with flowers), `kit.panel.picket.white` [2], `kit.panel.rail.knee` [2]
  (park knee rail 0.45), `kit.panel.rail.post` [2.5] (two-rail timber 1.0), `kit.panel.wall.stone` [2] (0.6),
  `kit.panel.wall.brick` [2] (1.0), `kit.panel.kerb.stone` [2] (0.2), `kit.panel.beachfence` [2]; posts
  `kit.post.wood`, `kit.post.white`, `kit.post.stone`, `kit.post.gate`.
- Furniture: `kit.bench.park`, `kit.bench.simple`, `kit.picnic.table`, `kit.bin.park`, `kit.lamp.park` (decorative; for
  lamps that light at night use `SpawnPrefab` of `Props/SM_streetlamp_02`/`_03` with `keepName`), `kit.boulder.s/m/l`,
  `kit.planter.tub`, `kit.bollard`, `kit.signpost`, `kit.birdbath`, `kit.gnome`, `kit.duck`, `kit.googly` (a pair of
  googly eyes to stick on anything, ~0.5 m).
Everything else (sheds, gazebo, the Pickle, goals, jetties, garages...) is defined by the module that needs it, with
module-prefixed ids.

**Private draft protocol (mandatory: a compile error in the shared tree blocks everyone's preview).**
```
SP=/private/tmp/claude-501/-Users-nick-Dev-Rubbish/89149967-39e5-422b-a7f2-9cb2938effd6/scratchpad
$SP/tools/sync.sh <you> <YourFile.cs> [...]        # create/refresh your private tree work/<you>/draft from the shared one (never overwrites your own files), compile-checks it
$SP/tools/compile.sh --draft $SP/work/<you>/draft  # compile your private tree
PREVIEW_DRAFT=$SP/work/<you>/draft PREVIEW_OWNER=<you> $SP/tools/preview.sh <modules> $SP/work/<you>/shots/<n> <shots> "<opts>"
$SP/tools/publish.sh <you> Assets/Scripts/Editor/TownLife/<YourFile.cs> [...]   # copy YOUR files to the shared tree if it still compiles with them
```
Edit only your own files, only in your private tree. Sync before each preview session to pick up other agents' published
work. Publish early and often once it compiles and looks right (others build on you), and publish your final version
before you report. Never edit a file in `draft/` directly.

**Looking at your work is the job.** Minimum for a content module: a top-down, an oblique and three eye-level shots of
your area (fixed shot names are in CONTRACT section 8 and `photos/before2/shots.tsv`; add custom shots with
`shotsFile=`), at least three look-fix rounds, and a final honest comparison against the matching `photos/before2/*.png`.
Check specifically for: floating or sunk objects, things on roads/sidewalks/driveways/paths, objects inside houses,
mis-rotated runs, z-fighting flat layers, trees too small to matter (a street tree must be visibly taller than the
eaves), anything that still reads as "empty lawn with dots on it". If a human standing there would not think "nice
neighbourhood", keep going.
