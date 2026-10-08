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

This is an Edit Mode authoring tool, not an in-game editor, live multiplayer map synchronization, terrain sculptor, arbitrary gameplay-script importer, or traffic-spawn editor. Placement caps and collision checks are safety limits, not performance guarantees for a town filled with thousands of objects.

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

Mildly Pleasant Acres and Minivan Meadows carry a generated dressing layer, built by the `TownLife*.cs` editor classes in `Assets/Scripts/Editor/TownLife/`. It adds no households, roads, graph nodes, knockables or runtime scripts.

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
