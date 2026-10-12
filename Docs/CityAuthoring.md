# City authoring

Use **Rubbish → Map Editor** to build directly in Unity's real Scene view. The window is named **Town Builder**. It places working properties and scenery, draws connected streets, and publishes collection/navigation/map updates together; it is not a separate mockup of the city.

The underlying coastal street blueprint remains `Assets/Resources/City/CoastalCity.json`. The visual editor handles ordinary layout changes without hand-editing that file. Advanced blueprint editing is documented below.

## Quick start: fill the town

1. Exit Play mode and open **Rubbish → Map Editor**. Click **Open town**, then **Depot**, **Top view** or **3D view** to frame the area you want to fill.
2. In **Assets**, choose a palette card. Click in Scene view to **Stamp** it. A green preview means the preliminary placement checks pass; red previews explain the obstruction. The final placement checks the complete object, not only its pivot.
3. Use **Brush** for clusters of trees, shrubs and other scenery. Ctrl/Cmd-click several cards to mix them; adjust brush radius, spacing, density, rotation and size variation. Buildings are deliberately excluded from scatter.
4. Use **Line**, clicking its two endpoints, for repeated fences, hedges, lamps or rows of working properties. Adjust spacing to the asset's actual width; a line is not a new navigable road.
5. Click **Save & bake** before Play or distributing a build. This validates placements and refreshes household collection, street navigation and the map. If you also have an unpublished street draft, the button publishes that draft and your placed assets together.

The built-in palette contains 100 entries across **Trees & greenery, Houses, Shops & buildings, Cars, Street furniture, Parks, Industrial, Ground & boundaries**, plus a **Custom** category. It combines the game's existing low-poly assets with dimensional kits: shops/apartments/workshops, groves and pocket parks, garden boundaries, planters, street furniture and service details. Cards state whether an asset is **PROPERTY + TRASH**, **KNOCKABLE**, or **SCENERY**. Search filters the catalog; the star saves a favorite.

### Scene-view controls

| Action | Control |
| --- | --- |
| Stamp / Brush / Line / Erase | P / B / L / X |
| Rotate scenery | Q/E in 15° steps; Shift+Q/E in 90° steps |
| Cancel current tool or unfinished line | Esc |
| Undo one complete brush/line stroke | Ctrl/Cmd+Z, or the window's Undo button |
| Fly / orbit / zoom | Right mouse + WASD / Alt-drag / scroll wheel |
| Focus selected object | F |

**Erase** only affects your `TownAuthored` layer, never the original generated town. Its **Also erase working properties** option is off by default. **Scenery overlap** permits intentional overlapping foliage, not blocked roads or household access. Undo restores removed stamps.

## Working houses and buildings

House cards place a complete property recipe, not a bare facade. They snap to a nearby land road, face its curb, and receive a fresh stable collection identity, district rules, garbage placement, household route metadata, driveway/car information and the appropriate original property details. Working shop and apartment cards likewise add a real curb collection stop and walking route. Collection is integrated when the town is published; adding properties does add real work to the town.

Place a working property within 60 m of a land road, with enough space for the complete frontage and driveway. Its size stays life-sized; scenery scale and rotation do not distort its collection setup. For a new street, publish the street first so the property can attach to the actual road. The tool refuses houses floating in unconnected countryside or colliding with roads, nearby buildings or required access.

To relocate a placed property, select it and choose **Move property**, then click its new street frontage. This preserves its collection/save identity. Use **Stamp a copy** to make another property with a new identity; do not use native duplication to copy household IDs. Do not move just the house mesh away from its bin or edit its `stopId` to change its address. Selecting a placed object also exposes **Remove**; publish after a removal so the directory and map no longer reference it.

Trees, gardens, boundaries and clutter do not generate garbage. A parked car remains a parked physical object, not a new traffic spawn. A bus shelter, market stall or decorative crosswalk does not add bus AI, a trading menu or a pedestrian crossing route. Street-prop cards use the existing knockable behavior. These distinctions are explicit on the palette cards instead of guessing gameplay from how a model looks.

### Add your own models

Expand **Add your own asset**, drag in a prefab/imported model from the Project window, name it, and choose **Decorative scenery**, **Working property + trash**, **Knockable street prop**, or **Parked car**. Custom models must be visual-only assets without embedded gameplay scripts or physics simulation; the editor adds the selected supported game role. Custom working properties get a bin and path automatically. Set their **Front correction** to 0° for a local +Z front or 180° for a local −Z front, then inspect the preview. Custom entries survive rebuilding the built-in catalog.

## Visual street editing

In **Streets**, use **Move junctions** to select a dot and drag its position handle. Shared junction IDs move every connected street together. The selected-road settings edit its name, full width, smooth bends and service-street setting. **Frame selected road** zooms to it.

Use **Draw street** to name a road, select its district and width, then click its route in Scene view. Existing roads and junctions attract the cursor; a new branch inserts a shared junction into the old road, and crossings connect both road polylines. Enter finishes the route, Backspace removes the last point, and Esc cancels it. Alt/right mouse retain normal camera control. A street must connect to the existing network; overlapping a second street directly onto existing asphalt is rejected.

Road edits are an unpublished, Undoable draft. **Validate draft** checks its references and connectivity; **Publish road changes** also checks the real town's clearance and rebakes dependent data. Finish or cancel an unfinished street before publishing; publishing never silently drops its drawing points. The draft, including unfinished points and their name/district/width/settings, is recovered across script reloads, window closes and editor restarts. **Reload published** asks before discarding any unpublished work, including a points-only stroke. If another author changes the JSON while your draft is open, publishing stops rather than overwriting their work; coordinate and reload the published version.

Branching from an existing curved road preserves its sampled shape and length, so existing normalized road-relative property placements do not slide around. Internally that affected road becomes an explicit polyline rather than resmoothing a changed control-point list. Existing road, district and household identities stay stable.

Road surfaces are generated as a joined network: asphalt stays continuous through junctions, sidewalks stop at adjoining carriageways, and corners have filled joins. Publishing a road edit refreshes neighbouring surface meshes and lane-paint gaps too. Surface-only updates preserve existing lamps and their knockable identities; they do not move households or change road connections.

The street tool does not edit the river, coast, terrain bounds or district progression. It rejects new cross-district attachments, junctions within 8 m of a permit gate, and direct detours connecting opposite sides of gated approaches—even if both approaches have the same road district label. For each new street it also compares depot reachability with every combination of existing district gates closed: a second street cannot join an earlier dead-end to sneak around a gate. These checks protect the authored road graph, not off-road walking or arbitrary manual scene edits. New inter-district access and broader gate/topology changes need explicit permit-gate authoring and a progression review. Original roads are not casually deleted or renamed by ID. Use the lower-level blueprint workflow for those broader changes, with backups and full validation.

## Save, test and share

Placed objects live under a separate `TownAuthored` root in Neighborhood and survive a full generated-town rebuild. **Save & bake** supports the dirty authored draft and publishes the scenes, pickup directory, graph and map transactionally. A validation/bake failure retains the editable draft while reverting partially published output. Normal Unity scene Save alone is not a substitute for the bake. Save the project in Git as well; Undo and recovered editor drafts are not source-control backups.

After publishing, enter Play and drive past the changed area. Check a newly added property's bags, sorting/collection flow, curb and doorway access, traffic turns, large-truck clearance and the map. After a topology or collection change, test the same published build as both host and guest. Every participant must use the same town; the multiplayer approval checks its published-town hash.

Unity's **Window → General → Test Runner → EditMode** includes `TownRoadAuthoringTests`, `TownGameplayAuthoringTests`, `TownPlacementToolsTests` and the town-asset tests. Run the full EditMode suite before sharing a build. The read-only `Tools/bots/eval/town_road_probe.cs` additionally exercises a connected branch on a copied blueprint and checks identity/pose preservation without writing the real town. These checks complement, but do not replace, visual/driving/multiplayer testing.

This is an Edit Mode authoring tool, not an in-game editor, live multiplayer map synchronization, terrain sculptor (the land's shape is code, see "Terrain relief"), arbitrary gameplay-script importer, or traffic-spawn editor. Placement caps and collision checks are safety limits, not performance guarantees for a town filled with thousands of objects.

## Advanced: edit a road or junction in the blueprint

1. Save or commit the current scene and blueprint before editing. Exit Play mode.
2. Use **Rubbish → City → Open Blueprint**. Find a junction in `nodes` and change its `x` and `z` values. Coordinates are plan units; `scale` converts them to world metres.
3. Keep its `id` unchanged. Every road listing that junction in its `nodes` array follows the same new position, so connecting roads stay joined.
4. Use **Rubbish → City → Apply Blueprint Changes**. This rebuilds changed road groups, moves their anchored lots and decorative parcels, regenerates backyard boundaries, refits park entrance paths/signs, updates gates and all four depot exteriors (opening DepotRow if necessary), and rebakes navigation, the pickup directory and the map. Neighborhood and DepotRow are saved by this operation. Unsaved changes in either scene must be saved or reverted first.
5. Run **Rubbish → City → Validate Current City** with Neighborhood open. Fix reported issues, then inspect the moved junction in Scene view and drive it in Play mode. Check entrances, driveways, sidewalk crossings and large-truck turning space.

For the first coastal city build, use **Rubbish → Setup → Neighborhood**. Treat a full setup build as regeneration of generated world content, not the normal way to make a small road edit. Keep authored changes backed up before using it.

## What belongs in the blueprint

`nodes` define stable junction and terrain-point identities. `roads` reference junction IDs in order, with road names, district IDs, curve/bridge/service flags and road half-widths. Shared junction IDs express connections; two differently named points at the same position are not a durable connection.

`gates` reference a road and a normalized distance `u` from 0 to 1. `depot` similarly identifies its frontage road and four station positions. `collectionSites` identify commercial and condo collection stops. `lotStrips` define placement candidates used during a full layout build. An optional positive `count` reserves an exact number of frontage positions; otherwise `spacing` determines the count. Strips are consumed in order up to the existing household count; later strips are repair alternatives, not extra households. `terrain` and `landmarks` supply the river, coast, walking path and named destinations.

`infillSites` define decorative parcels by stable `id`, `roadId`, normalized `u`, `side`, `setback`, `width`, `depth` and `type`. Dimensions and setbacks are world metres. Types are `storefront`, `workyard`, `courtyard`, `garden`, `estate` and `grove`. They reserve a footprint and follow their road anchor during incremental edits. They do not add pickup stops, household simulation, loot or trash generation. A full build reports any skipped parcel and the obstruction; incremental edits reject conflicts instead of silently deleting scenery.

The generated layout starts with six houses facing Depot Row, distributes homes onto interior crescents, and uses attached shops, shared gardens/courtyards and woodland pockets between active destinations. Keep buildings, roads and truck clearances life-sized when adjusting density. Preserve the original 98 collection identities. Use scenery to add visual density without workload; deliberately placed working properties add new collection identities rather than changing those originals.

## Town Life layer (Acres and Meadows)

Mildly Pleasant Acres and Minivan Meadows carry a generated dressing layer, built by the `TownLife*.cs` editor classes in `Assets/Scripts/Editor/TownLife/`. It adds no households, roads, graph nodes, knockables or runtime scripts (the one exception is the data-only `TownReliefPad` marker, see "Terrain relief").

- **Lots and fences.** `TownLifePlan` tiles every block into one lot per house. `TownLifeFences` turns the lot lines into a connected boundary network: one shared divider between neighbours, one shared rear line through each block, wing fences to the house with an open 1.8 m gate gap. `CoastalYardAuthoring.Rebuild` realises it as one `CoastalYardBoundary` marker per frontage chain (14 markers for the 59 starter households). The other districts, and any lot with no road context, keep the footprint-based yards described below.
- **Dressing.** `NeighborhoodGen/TownLife` holds `TL_Streets` (street trees, hedges in front of street-facing fences, corner beds, name posts), `TL_Yards` (front and back yards), `TL_Meadows` (court islands, the common inside the Mile, beach path) and `TL_Edges` (woodland belts, hills, riverside, coast strip, Depot Row planting). Pickle Park is `NeighborhoodGen/Park/TL_Park`; of the old park only the playground KidPen and the lemonade stand remain. Acres houses are repainted through `TL_paint_*` material variants.
- **Assets.** Meshes and materials live in `Assets/Materials/Generated/TownLife/` (container assets with sub-meshes, one `.mat` per colour). Nothing is embedded in the scene. Do not hand-edit them; they are rewritten only when content changes.
- **Rebuilding.** Every city publish (**Save & bake**, **Apply Blueprint Changes**, **Apply Residential Blocks**) clears and rebuilds the layer inside the same transaction, so it follows moved houses and roads. **Rubbish → City → Rebuild Town Life** runs that publish on its own. The result is deterministic: an unchanged town rebuilds to the same objects and writes no assets. **Rubbish → City → Town Life Enabled** is an emergency switch: when unticked, the next publish clears the layer and builds nothing.
- **Hand-placed objects** in `TownAuthored` are treated as obstacles; the layer plants around them. Do not edit objects under `TownLife` or `TL_Park` by hand: the next publish replaces them.
- **Previewing changes to the generator** without recompiling or saving: `Tools/townlife-draft/tools/preview.sh` (design notes and the API contract are in `Tools/townlife-draft/design/`).

## Cohesive backyards

The two starter districts use the Town Life boundary network above. This section describes the other districts.

`Assets/Resources/City/CoastalYards.json` controls generated residential boundaries. They are derived from actual house footprints and road-relative neighbors, with a connected rear boundary and side runs, shared dividers where compatible, and open front access. Driveways, walking routes, parked cars, neighboring buildings and roads remain clear. Awkward corner parcels may remain open; isolated fence fragments are not a fallback.

### Authored residential blocks

`Assets/Resources/City/ResidentialBlocks.json` records deliberate residential rows by stable household ID. Each row identifies its district, road, two consecutive junction IDs in forward road order, curb side (`1` right / `-1` left), first/last corner insets, and the ordered homes. Insets are **world metres**, not scaled plan coordinates. Homes are evenly spaced between the two inset stations; a one-home row uses their midpoint. The authoring resolver rejects unknown identities, reversed/cross-junction rows, duplicate homes, competing rows on the same curb, and pitches below 18 m. Physical lot/driveway validation remains necessary: the spacing minimum alone does not guarantee every house model fits.

The same schema optionally accepts `placements`: individual `{ stopId, districtId, roadId, u, side }` attachments for curved courts and irregular parcels. `u` is the normalized distance along the resolved road, from 0 to 1; `side` uses the same curb convention. Rows and explicit placements can coexist, or a recipe can contain only explicit placements. Household IDs must be unique across both lists, and their district must match the road. Bridges are excluded; service streets are allowed. Explicit placements do **not** apply the 18 m linear-row pitch rule, because outside-facing homes fan apart around turning bulbs. This is not permission to overlap properties: inspect and run physical house, driveway, road and yard clearance validation before publishing. Changing a road's shape or length moves these normalized stations, so recheck curved parcels after road edits.

Full generation and **Rubbish → City → Apply Residential Blocks** use the same recipe. Reapplying intentionally updates the listed complete properties while preserving their collection identities and household metadata. Ordinary **Save & bake** does **not** reapply these rows or overwrite hand-edited house attachments. Use reapply only when you want the recipe to replace the listed homes' current placements; then inspect boundaries, driveways, corner clearances and the published map. Unlisted properties are unaffected by the recipe. Full generation refuses an obstructed recipe home instead of silently moving it to an old fallback strip.

Explicit reapplication refreshes generated decorative parcels only on changed roads or roads whose listed homes move. As in full generation, a decorative parcel that no longer fits is omitted and named in the report; houses, pickup spaces and driving lanes take precedence. Other roads' decorative parcels and `TownAuthored` objects are preserved. Backyard boundaries are regenerated after the new property and decoration positions settle.

Yards rebuild after lot placement and after incremental road changes. Their boundary markers retain ownership and geometry information for validation and transactional rollback. Do not move a fence panel independently of its parcel; edit the lot attachment or yard settings instead.

Moving junctions, road widths, names and existing scene anchor offsets are supported incremental edits. Town Builder's new working properties are authored scene objects and publish incrementally. Changing the blueprint's generated `collectionSites` recipe, terrain, scale, landmarks or initial lot-placement recipe instead needs a full setup build. `CityBlueprintStamp` detects those structural changes and cancels road-only apply before mutation, rather than leaving the town partially updated.

## Keep complete lots attached

Each preserved house or collection-site root has a `CityRoadAnchor`. Its `roadId` and normalized `u` identify the attachment point. `lateral` and `height` are world metres, and `yawOffset` is rotation relative to the road tangent. These offsets do not scale the house or its contents.

The anchor moves the complete root and its children. It also transforms world-coordinate household metadata—doors, paths, parked-car poses, butler positions and kid-pen points—so navigation and interaction anchors move with the visible building. New lots use `lateralFromRoadEdge`: signed `lateral` is the clearance beyond the curb, so widening a street keeps its bins and lots outside the traffic lane. Gates remain centreline-relative. Change the anchor fields when adjusting a lot's relation to the road. Avoid dragging only the visible building away from its collection stop.

Applying layout changes is explicit. Loading a save, enabling a depot or joining a session does not automatically move authored city objects. A missing referenced road is an error, not permission to assign a lot to another road or delete it.

## Preserve identities and progression

Never rename existing collection `stopId`s just because their road or address moves. Bag records, household identity and saved work orders rely on stable identifiers. Likewise, preserve existing district IDs and road/anchor IDs when the same place is being repositioned.

When distributing authored-system changes, bump `NetVersion.Content` as appropriate; bump `Protocol` when replicated message layouts change. The connection approval also checks a SHA-256 of the published town, so differently edited towns cannot silently join each other. All players in a playtest need the same build; the current format is protocol 11 / content 10.

The unlock route is Acres → Meadows → Coupon Commons → Snootington Hills → Stackington Heights → Billionaire Bluffs. The serialized district index order is deliberately different: Acres, Meadows, Hills and Bluffs retain indices 0–3; Commons and Heights append at 4–5. Display progression uses `Districts.ProgressionOrder`; saves and WorldLife use `Districts.All`. Do not reorder the latter.

Permits are business-owned. A current employee can use their online employer's access without receiving permanent ownership. Town trash generation uses the online businesses' combined open districts and best subscriber trust for each district, while purchases and career ownership remain local to the business. Test both host and guest permits after changing gates or district connections.

## Validation and hand testing

The validator is read-only. It checks blueprint references, road reachability from Depot Row, connected bridge endpoints and river crossings, duplicate collection IDs, missing/displaced road anchors, stale generated roads and yard geometry, decorative parcel clearances, finite household metadata, curbs near roads, fixed obstacles in driving lanes and the baked StreetGraph. Errors identify what needs correction; normal apply does not silently relocate a conflicting house to another street.

Incremental publishing is transactional. A validation or later map/save error restores the affected scenes and generated files. This includes failures after some files were already written. Unrelated dirty materials/assets are not flushed to disk. Unity may leave a reverted open scene marked dirty; inspect it before saving. A successful edit still needs a quick visual/driving check—validation is a safety net, not proof of every multiplayer or gameplay behavior.

Automated checks cover stable district/save indices, permit migration and replication, six-district schedules, rare-odds preservation, blueprint connectivity and a junction edit propagating to every incident road. These checks do not replace visual and driving QA: inspect houses for overlap, confirm each depot's exterior/interior transition, and try traffic, crossing guards, collection tools and return routes with a larger truck.

## Town Life in Coupon Commons (2026-10-08)

`Assets/Scripts/Editor/TownLife/TownLifeCommons.cs` is a seventh Town Life module, run last in every city publish; its
objects are `NeighborhoodGen/TownLife/TL_Commons` (853 at the first publish; the layer's budget went from 4,000 to 5,600).
It keeps **its own occupancy map** over the district, because the shared one (and `TownLifeScene`'s capture) stop at the
river; it reads roads from `CoastalCityLayout`, the nine shop lots from their `PickupStop`s and the decorative rows from
their `CoastalInfillSite`s. What it builds:

- **Streets** (the six through streets): trees in grates with planters, benches and bins between them just behind the
  pavement; on Coupon Street and High Street parking meters, news boxes, painted parking bays and a bus shelter; hydrants;
  a zebra crossing before each end of every street; bunting across Coupon Street.
- **The nine shop forecourts:** paving, planters, a bench, a bin, a bike rack, a pylon sign and a roof sign with the shop's
  own name (`ShopNames`), cart corral / vending machine / ice box / news box (shops) or cafe tables under string lights
  (restaurants), and the **dumpster pad** with its screen walls and a hatched approach that is kept clear of everything so
  a truck can back up to it (`DumpsterRules.PadLocal`; the dumpster itself is a runtime object, `Docs/Progression.md` §6.11).
- **Two car parks** behind the Coupon Street shops (bays, wheel stops, a dozen parked cars, lamps), a **food court** inside
  the Restaurant Lane loop (chequered plaza, two food trucks, umbrella tables), a **freight yard** inside the Loading Lane
  loop (containers, pallets, a forklift, a low wall), and shade trees on the leftover lawns.
- **Decorative shop rows:** back-door clutter and a shopfront board or display per bay, renderers only (an infill
  footprint rejects any collider inside it).

The four big features sit in hand-measured rectangles and are skipped (and named in the `[commons]` report line) if a
blueprint change puts something in their way. Stackington Heights has no Town Life yet.

## Town Life in Snootington Hills and Billionaire Bluffs (2026-10-08)

Nick: "lets fill out the last 2 neighborhoods with more houses and props." `TownLifeEstates.cs` is the eighth module
(`NeighborhoodGen/TownLife/TL_Estates`, about 720 objects at its first bake; the layer's budget is now 9,000). Like the
Commons module it keeps its own occupancy map, seeded from the roads, each real estate's whole grounds (an oriented box
round everything in its lot), the decorative sites, the nature trees, authored objects and the gates.

- **Scenery estates** (6 in the Hills, 12 in the Bluffs at the first bake): walking every road on both sides, wherever a
  house with a garden round it and a front between it and the pavement is free, it stands a pack house (1.28x in the
  Hills, 1.5x in the Bluffs; the Bluffs try a two-wing pair first), a front walk, a drive down one side with a car, a
  tall hedge (Hills) or stone wall (Bluffs) along the pavement with lamp piers at the openings, topiary down the walk,
  beds under the windows, shade trees and a fountain or birdbath. **They are scenery: no `PickupStop`, no household, not
  on the map, nothing in the economy or the tests that count households.** Real extra households would be a blueprint
  change (`lotStrips`) and a full structural rebuild; not done.
- **Set pieces** on the open lawns (17 placed): tennis courts, pools with loungers, fountains, gazebos, topiary gardens,
  putting greens, and in the Bluffs private helipads and a gold statue of the owner. Each takes the free spot nearest a
  road that fits it.
- **Avenue trees** along every road and **groves** on what is left.
- The report line also says how many estate sites were tried and refused; the usual reason is that the real estates'
  grounds and the road loops leave no free rectangle. To get more, shrink the margins in `Fits` or add smaller houses.


## Terrain relief (2026-10-09)

Nick: "can we get elevation changes in the landscape? eg. mild up and down in the first few neighborhoods, steep bluffs,
sloping down to the river". The town is still **authored flat**: the blueprint, every road, lot, yard, infill and Town
Life rule works in plan, exactly as before. Height is added by the bake, last.

**One grid is the ground.** `TownReliefDesign` (runtime assembly, pure maths) shapes a 3 m height grid: Acres and
Meadows roll gently and fall to the river, Commons and Heights are a shelf tilting up from it, the Hills' loop circles a
dome, the Bluffs are a plateau that climbs to the north-east and ends in a craggy cliff over the beach, and the fringes
rise into the woods. **Depot Row is level at 0**, so nothing about the depots, their roofs or their interiors moved.
On top of that shape:

- **Roads are level from kerb to kerb** and follow a smoothed profile along their own length. Where a street runs into
  another it eases onto that street's level over its last 32 m. Bridges get a slight arch. The bake report lists the
  steepest roads (about 16 % on Estate Spur, 14 % on the Cliff Road climb, 13 % and under elsewhere).
- **Pads are level patches** at the height the land had: one per lot's buildings, per attached shop row / workyard /
  courtyard, per kid pen, per estate feature group (hedge maze, launch pad), per Hoverhog ground pad, and wherever a
  generator asks with `TownLifeKit.LevelPad(...)` (pools, courts, helipads, putting greens, the park pond, the Meadows
  pitch). A `TownReliefPad` component on any object (hand-placed ones included) asks for the same thing.
- The river channel and the shore are cut into the result; under a bridge the deck and the channel part company.

The grid is stored as `Assets/Resources/City/TownRelief.bytes` (part of the published-town signature, so host and guest
must agree) and read at runtime by `TownRelief.Height(x, z)`. The terrain mesh **is** that grid (cells split into two
triangles, diagonals alternating), which is why nothing has to be matched to it by hand.

**How the town gets onto it** (`TownReliefBaker`, Editor): every publish calls `PipelineBegin` first, which puts the
scene back exactly flat from the `TownReliefRecord` on `NeighborhoodGen`; the whole flat pipeline and its validation
run; then `PipelineApply`:

- builds the terrain (`Ground/Relief terrain x_z`, grass / rock on steep faces / sand at the water) and switches the old
  flat terrain off;
- **lifts** small things and shared meshes (props, trees, lamps, cars, people's markers) to the ground under them;
  buildings sit on their pad; a car or a short flat slab is **tilted** to the slope;
- **drapes** anything long or one-of-a-kind, vertex by vertex: road and pavement meshes, driveways, walls, hedges and
  paving (copies in `Assets/Materials/Generated/Relief/Relief_Meshes.asset`), and Town Life's own ground, verge, lawn,
  fence and hedge meshes (in place, in their existing assets). A triangle that crosses terrain facets is first cut along
  them, so a road or a lawn lies exactly on the ground, 10 cm above the terrain as it always was;
- stretches long fence colliders over the slope they stand on, lifts the world points stored on house routes, kid pens
  and yard stamps, and lifts the street graph (edge lengths grow with the climb) before it is saved.

A second bake of an unchanged town writes nothing. `Rubbish → City → Terrain Relief Enabled` (per machine, default on)
is the emergency switch: unticked, the next bake leaves the town flat and writes a flat grid.

**Rules for code**

- Layout code (builders, Town Life modules, validators inside the publish) sees a flat town: `TownRelief` answers 0
  while a bake is laying the town out. To keep something off the cliff or a steep bank, ask the design
  (`TownReliefDesign.Steepness`), as `TownLifeEstates` does.
- Runtime code never assumes y = 0. Use `TownRelief.Height / OnGround / Lift` for a point, `TownRelief.Between(a, b, u)`
  to move between two waypoints (the path samplers in `LifePaths` and `HouseRoutes` do), `TownRelief.Aligned` to rest a
  vehicle on the slope (`CarAgent.Pose` does). Outside the grid (the depot interiors far south of the town, under the
  map) every lookup is 0.
- Clearance checks that skipped "anything no higher than the kerb" skip the ground surfaces by identity
  (`TownRelief.IsGroundSurface`) and measure a collider's top from the ground under it (`TopAboveGround`).
- `CityRoadAnchor.Resolve` stands a lot on the relief, so "placement differs from its road anchor" still means what it
  says.

**The truck on hills** (`Shop/Truck/TruckGradeAssist.cs`, on every truck, acting where its physics is simulated): two
thirds of a hill's pull is cancelled while you drive (a hill slows the climb and quickens the descent, mildly); with
the stick centred it is cancelled outright, so the truck coasts to a stop and stays put as on the flat; extra weight on
the wheels for grip; a pull back to the road over a crest; and engine braking past top speed, which the old torque
limiter never did. Measured in the QA copy with a stock tier-1 engine: 10 to 11 m/s up the Cliff Road climb, 12.6 m/s
(limit 12.5) coming back down at full throttle, 0.0 m/s when let go on the slope.

**Water** is `Rubbish/Water` (`Assets/Scripts/World/Shaders/RubbishWater.shader`): small waves on a fine grid, shaded
one facet per triangle, darker away from the bank, foam lapping at the waterline, lighter streaks drifting downstream.
No textures and no depth texture: the bake builds `Ground/Relief river` and `Ground/Relief sea` and stores each
vertex's distance from the waterline in the mesh. The two materials (`Relief_Water_River.mat`, `Relief_Water_Sea.mat`
next to the relief meshes) are created once and never overwritten: tune colours, wave height and foam there. The old
`Bandit River` and `Eastern Sea` objects are kept, switched off.

**Not done / known limits**

- Town Builder still draws streets on a flat plane at y = 0, so on high ground the cursor is offset from where the
  street lands. Stamped objects are fine (they are dropped onto the ground and re-seated by the next bake).
- Shared tree clumps and woodland props are tilted as a whole (up to 10 degrees) rather than tree by tree.
- Pools and ponds that are part of the original estate lots are levelled by their group; a new kind of flat feature
  needs a `LevelPad`.
- Pond and pool water still use the plain water colour, not the new shader.
- Not tried with a second player or a gamepad. Residents, traffic, the district gates and the Hoverhog were checked in a
  solo session in the QA copy.
