# Rubbish: World Life (residents, pedestrians, traffic, knockable street furniture): design + build contract

> **Status: design, 1 Oct 2026. The W0 contract (§12.4) and package A (§12.A) are built; for B see §12.B, for C (residents + puppets) §12.C, for D (schedules + traffic) §12.D, for E (rules + consequences) §12.E, for G (polish, perf, integration) §12.G, the fixer pass after G in §12.H, and the kids in fenced yards and the playground
> in §12.K (2 Oct); F (net) waits for Multiplayer M1.** This doc is the source of truth for neighbourhood life, the same way
> `CoreLoop.md`, `LivingWorld.md`, `Character.md`, `Progression.md` and `Multiplayer.md` are for their areas. Where it touches
> netcode, `Multiplayer.md` wins (§9 rules for new code; §5.2 collision matrix; §8.7 the M0/M1 contract). Where it touches money,
> Rep and districts, `Progression.md` wins.
>
> **How it was made:** I read the code it changes (`NeighborhoodBuilder`, `CurbTrashSpawner`, `PickupStop`, `TrashBag`, `KevinCrew`,
> `Critters/*`, `CritterKnockback`, `TruckControllerV2`, `StreetLamps`, `Character/*`, `Ledger`, `ProgressionEvents`). I also ran
> read-only edit-mode queries and renders of `Neighborhood.unity` (`Docs/WorldLifeRefs/`). Every count below comes from the scene as
> it was built on 1 Oct 2026.
>
> **User ask (verbatim):** "some NPCs walking around, run overable to add to the chaos. the trash bags should be taken out by these
> guys not just appear. and some just walking arouind. maybe some cars driving around, leaving and arriving home to and from the
> neighborhoods. lamp posts and smaller objects should be hittable and have physics, not hard walls."

![town](WorldLifeRefs/town_top.png)

---

## 0. The answer in twelve bullets

1. **Residents carry the trash out.** When `CurbTrashSpawner` puts a bag out, a resident pops out of that house's front door
   holding it, waddles down the garden path, sets it in its curb slot and goes back in. **The curb record is still the only truth.**
   The walk is drawn from the record's own timestamp, so the host and every guest see the same walk with zero network traffic. When
   the Neighborhood isn't loaded, nothing changes: the bag is data only, as it is today.
2. **Pedestrians, dog walkers, joggers and commuters** follow **deterministic daily timetables**. Each one is a pure function of
   (Town seed, day, resident id), so every machine computes the same street life without syncing a single position. About 8–12 people
   are out in the starter district at rush hour and about 0–1 at 3 AM.
3. **Household cars leave home and come back** on the same kind of timetable: they reverse out of the driveway, drive right-hand
   lanes to one of **three new town exits** (tunnels in the tree line), and come back in the evening to park. Cars queue behind a
   parked garbage truck, honk after 3 s, and overtake when the other lane is clear.
4. **Lamp posts, mailboxes, wheelie bins, signs, picket panels, cones, hydrants and lemonade stands are knockable.** At rest they stay
   static-batched (no perf change). When the truck hits one at speed, the original is hidden and a **local physics proxy** flies off.
   The truck loses a little speed instead of stopping dead. Below ~2–3 m/s they're still solid, so you can nudge them. They reset
   after ~2 minutes once nobody is near, and City Works fixes everything at 06:00.
5. **Everyone is run-over-able, cartoon style:** a ballistic flop with a spin, one bounce, a starfish pose with dizzy stars, then
   they get up furious, shake a fist, chase the truck for up to 3 s and go back to their day. No gore and no deaths. **No child
   pedestrians:** kids only cheer the truck from behind hard fences, and are never hittable (§12.K). Dogs always leap clear.
6. **Hits are decided by rule, never by two networked rigidbodies touching** (rule 13). Whichever machine simulates the truck checks
   its own truck pose against its own copy of the NPC or prop and plays the flop at once. The host confirms, charges the
   consequence and broadcasts one small event. Debris physics is local cosmetics on every machine.
7. **Consequences are small, funny and accountable:** a complaint letter, a fine scaled by district ($3 / $12 / $40 / $120 per
   flattened resident), a Rep hit, and a **grudge** that makes that house's bags count as an hour later until midnight (the
   "lateness consequence"). Clear their curb to get "Apology accepted". Props go on a capped nightly **City Works invoice**. Repeat
   offences in a day escalate up to ×3.
8. **The honk is a tool:** pedestrians in range startle and step off your line, cars ahead pull over to let the garbage truck past,
   and dogs bark. Honking near houses between 22:00 and 06:00 earns a noise complaint (Rep only, capped).
9. **No NavMesh.** `NeighborhoodBuilder` already knows every road, sidewalk, lot, door and driveway exactly, so it writes a **street
   graph** (`Resources/StreetGraph.asset`): right-hand road lanes, sidewalk lanes, crosswalks, garden paths, driveway spurs, exits and
   gates. A* on ~2,000 nodes, deterministic on every machine, with no baking.
10. **Performance budget:** ≤1.1 ms main thread on average and ≤2 ms at the 99th percentile on any machine at peak; ≤24 full and 24
    lite puppets; ≤16 moving cars shown; ≤24 awake physics proxies; 0 B/frame of garbage. **Network budget:** 0 B/s steady state;
    ≈260 B/s per driver during a deliberate rampage.
11. **Art:** "Neighbour Puppets" built from primitives in Chuck's grammar (big head, big nose, googly eyes, stubby limbs), ~18
    renderers at close range and 4 further out. There's no Animator: they're procedurally animated like the critters, which is
    what makes the flop cheap and floppy. Each district has its own cast: robe dads and curler grans; soccer moms and the HOA lady;
    monocles and poodles; butlers and billionaires on Segways. Cars are the Suburbs pack `SM_car_town_01..04`, tinted.
12. **Build:** a lead contract step (W0, all cross-system hooks in one go), then five parallel packages (graph, knockables,
    residents, traffic, rules), then the net package after Multiplayer M1. About 14–16 agent-days, roughly a week of wall time.

---

## 1. Ground truth from the code (what exists, what changes)

| # | Finding (file:line) | Consequence for this design |
|---|---|---|
| 1 | `CurbTrashSpawner.PutOut` (`Trash/CurbTrashSpawner.cs:450`) writes a `CurbBag` record and raises `BagPutOut` (`:195`, `:471`). Host only; data only; it runs while the Neighborhood isn't loaded and catches up in 0.02 h steps. Seeded from `Environment.TickCount` (`:269`; M1 B11 moves it to `ContentRng`). | Put-outs stay exactly as they are. The walk-out is **presentation of the record**, not a new decision. |
| 2 | `PickupStop.SyncBags` (`Trash/PickupStop.cs:213`) materialises records; records younger than 0.35 h get a "drop-in" pop (`:238`, `SpawnRecord` `:245`, `OnFreshTrash` `:273`). | The drop-in becomes the walk-out. Today's pop stays as the fallback (far away, pool full, no door anchor). |
| 3 | `PickupStop` knows its curb pose, slots and bin, but **not the house's door, garden path or driveway**. `HouseDirectory` has positions only. The builder computes `doorA`, `dwC`, `dwEnd`, `frontD` per lot and throws them away (`Editor/NeighborhoodBuilder.cs` `BuildLot` :763, `BuildFancyLot` :1665, `BuildEstateLot` :2232). | The builder must emit house anchors (door, path, driveway, car slots) into the street graph. |
| 4 | Every generated object is batching-static except `dynamicRoots` (`ApplyStatic` :2787). Lamps (:1006), mailboxes (:866, :1729), bins (:863, :1726, :2283) and parked cars (:898, :1818) are prefab instances with **non-convex MeshColliders** (cars on layer Ground). | Knockables stay static at rest and swap to a **proxy** when hit (§6.3). Never clone a static-batched scene object. Parked cars become runtime cars. |
| 5 | Scene inventory (1 Oct): 81 houses (37 / 22 / 14 / 8); lamps 139 (80 `SM_streetlamp_01`, 55 `_03`, 4 `_02`); mailboxes 73; wheelie bins 81; parked cars 58 (14 in district 1, 44 Meadows vans, none in Snootington or the Bluffs); picket runs 53 (4 m panels, **one collider per run**, `PicketRun` :1899); garden fences 81 × 10.7 m; street signs 13; yard signs 13; static butlers 9; trees 864 (capsule colliders); 4,791 GameObjects, 4,163 renderers, 3,228 colliders. | Sizes the knockable list (§6.1) and the budget (§4.4). |
| 6 | `Critter.CheckRunOver` (`Critters/Critter.cs:256`) already does rule-based run-overs: truck speed ≥ 2.5 m/s and the critter inside the truck box (`CritterWorld.InsideTruckBox` :291, local x ±1.5, z −2.6..4.2), then a scripted ballistic arc with spin and one bounce. No physics contact. | NPC flops reuse this exact pattern, generalised to any truck and made deterministic. |
| 7 | `CritterWorld.Truck` uses `FindFirstObjectByType<TruckControllerV2>` (`CritterWorld.cs:281`, rule 3 debt). There is no truck registry. The horn has no event: `TruckControllerV2.Honk` is private (`:501`) and `CritterSpawner` polls the H key itself (`:327`). | W0 adds `TruckControllerV2.All` and a `Honked` event (additive). |
| 8 | `StreetLamps` finds lamps by renderer name (`World/StreetLamps.cs:123`), caches their head positions and pools 8 realtime lights. | It needs a "lamp is down" switch, or a knocked lamp keeps glowing in mid-air. |
| 9 | `Ledger.Charge/Rep` is the only money and Rep funnel (`Progression/Ledger.cs`). "Only builder A turns facts into Rep" (`ProgressionIds.cs`). `HeadOffice.Send` is presentation-only letters. | Fines go through `Ledger.Charge`. Rep goes through a new **fact** that Progression A handles. Complaints are `HeadOffice` letters. |
| 10 | The Bluffs have a static `Butler` prop at every curb (`NeighborhoodBuilder.cs:2284`, built at :2328) and Butler Drops at 07:00 and 17:00. | Butlers become the Bluffs' "residents": the cloche gag (§3.5). |
| 11 | Layers 10–31 are free; `ProjectSettings` is owned by the M0 workflow right now. Players **and** the truck body are both on layer Interactable (3). | v1 needs **no new layers**. Knock detection is a predictive overlap on the simulating machine (§6.3), not contact filtering. |
| 12 | No road leaves the map. PlayBounds x −60..440, z −40..640 (invisible walls + tree line). | Three short exit stubs with tunnel portals, in corridors that today hold only scatter trees (§5.4). |

---

## 2. Architecture: data → decide → present

```
 DATA (every machine computes or mirrors the same thing)          DECIDE (host only)                  PRESENT (each machine, local)
 ─────────────────────────────────────────────────────           ───────────────────────────         ──────────────────────────────
 CurbBag records (host writes; guests mirror via WorldState) ──┐  hit claims → validate →             puppets, flops, chases, chatter
 StreetGraph.asset (built by NeighborhoodBuilder)              ├─▶ consequences (Ledger, grudge,  ──▶  cars driving, yielding, honking
 LifeRoster: households, residents, cars (seeded)              │   letters), knock state,             proxies (debris physics), geysers
 LifeSchedule: today's legs per resident and car (pure fn)     │   NPC overrides (interruptions)      Sfx / Fx / floating text
 Overrides + grudges + down props (host-written, tiny)  ◀──────┘                                       LOD, pooling, relevance
```

**Rule of thumb:** where something *is* comes from data plus time. What *happens to it* is decided by the host. *How it looks* is
up to each machine.

### 2.1 Determinism contract

- **Clock:** everything is evaluated against `GameClock.TotalHours` (double, replicated in M1 by B6). The day index is
  `floor(TotalHours / 24)`. Timetables are stored in seconds since local midnight as integer milliseconds.
- **Randomness for data:** a stateless 64-bit hash, `LifeHash.U64(townSeed, kind, id, day, salt)` (SplitMix64), with integer
  weights. It never touches `UnityEngine.Random`, `System.Random` or `ContentRng`. These are not content rolls (no value is decided),
  and every machine must compute them without shared state. **Town seed:** a hash of `TownMeta.guid` from M1; before M1, the
  builder seed 1234.
- **Randomness for looks** (spin axis wobble, chatter pick, debris jitter) may be local `UnityEngine.Random`, marked
  `// coop:allow(random) cosmetic`.
- **Routes:** deterministic A* on the static street graph with ties broken by node index. Routes are cached per (from, to).
- **Put-out walk times** use the record's time rounded down to the in-game minute (`floor(putOutAtHours × 60) / 60`). The host and
  guests then agree exactly, because M1 replicates `CurbBagNet.putOutMin` in minutes.
- **What may diverge between machines:** local avoidance offsets (a pedestrian sidestepping a parked truck, at most 1.2 m), car
  yield delays (bounded and re-converging), debris resting places, and chase paths. None of these pays or hurts. The host validates
  claims with slack that covers them (§9.3).

### 2.2 Activation (no scene-name checks, rule 6)

`NeighborhoodBuilder` places a `WorldLifeAnchor` component under `NeighborhoodGen`. A DontDestroyOnLoad `[WorldLife]` runner
(booted like `[Critters]`) wakes when an anchor is enabled and sleeps when it goes away. All presentation objects live in the anchor's
scene, so they vanish with it. Host-side bookkeeping (grudges, the City Works tally) lives in `GameSession` counters and stamps, so it
keeps working while you're in the garage.

---

## 3. Cast and art

### 3.1 Neighbour Puppets (residents)

Built from Unity primitives at runtime by `ResidentLook`, in **Chuck's grammar** (`Character.md` §4, §13): a big head (about 1:3),
a big nose, googly eyes, a round body and stubby limbs. They are *not* Chuck's rig: there's no Animator, skinned mesh or 60-part kit.
They are procedural transforms like `CritterLook` builds, which keeps 24 of them cheap and makes the flop trivially floppy.

| Part group | Renderers | Notes |
|---|---|---|
| Body (sphere or capsule; bean / ball / pear / stick builds) + outfit layer (robe, vest, apron) | 2 | colour per outfit; property-free shared materials, one cached URP Lit material per colour, the way `CritterLook.Mat` does it |
| Head + nose + mouth | 3 | nose shapes: ball, cone, potato; mouth: flat box that scales open (O, grin, grimace) |
| Googly eyes | 4 | reuse `CritterLook.Eye` / `CritterGooglyEye` (eye sphere + pupil child, spring-rattled). **Both eyes the same size** (mismatched eyes are Chuck's signature) |
| Hair or hat (1–2 pieces) | 2 | bun, comb-over, curlers (3 small spheres count as the hair slot), visor, top hat, beehive, sweatband |
| Arms (upper capsule + mitt sphere) ×2 | 4 | swing opposite the legs; IK-free "hug" pose for carrying a bag |
| Legs (short capsule + shoe) ×2 | 4 | slippers are big squashed spheres; the rhythm is a waddle |
| Prop slot | 1 | mug, newspaper, briefcase, leash handle, clipboard, cane, tumbler |
| **Full LOD (≤35 m)** | **~18–20** | |
| **Lite LOD (35–90 m)** | **4** | body, head, hat, nose; the detail group is `SetActive(false)` |

- **Heights:** 1.45–1.85 m (Chuck is 1.59 m, so the cast reads as "regular people around a pocket-sized binman").
- **Skin tones:** 8-tone palette, uniformly mixed. **Outfit palettes per district** (below). Variants are picked by
  `LifeHash(resident)`, so a resident looks the same every day and on every machine.
- **Faces react:** angry = face tint lerps to red, brows (two thin boxes) slam down, little steam puffs. Startled = eyes kicked
  (`KickEyes`), mouth O, a 0.4 m hop.
- **Voice:** an Animal-Crossing-style mumble (new Sfx id `mumble`, pitch per resident) plus floating-text lines. At most one line per
  NPC every 6 s, and 3 on screen at once.

| District | Cast (archetypes; adults only) | Palette |
|---|---|---|
| Mildly Pleasant Acres | Robe Dad (bathrobe, slippers, coffee mug); Curler Gran (cardigan, curlers); Commuter (tie, briefcase); Jogger (neon, sweatband); Dog Person; Gardener (straw hat) | faded pastels, plaid, terry-cloth |
| Minivan Meadows | Soccer Mom (visor, quilted vest, giant tumbler); Cargo-Shorts Dad (socks + sandals); Power Walker (hand weights); **The HOA Lady** (clipboard; she writes the Meadows complaint letters) | bright pastels, teal, beige |
| Snootington Hills | Monocle Gent (top hat, cane); Pearl Lady (pearls, poodle); Tennis Twins (whites, visors); **Mr. Pemberton** (the `Progression.md` §5.9 HOA inspector, as a resident) | cream, navy, racing green |
| Billionaire Bluffs | **Butlers** (the existing tux, mustache and silver platter); Billionaire (gold robe, sunglasses, **Segway**); Bodyguard (black suit, earpiece) | white, gold, black |

**Dogs** (dog walkers and the Pearl Lady's poodle): built from primitives with `CritterLook`'s helpers (sausage dog, corgi, poodle),
with googly eyes and the leash as a 2-point `LineRenderer`. They sniff lamps and hydrants and bark at raccoons and the horn. **They
are never hit** (§8.2).

**No children on sidewalks or roads.** Kids exist only where the truck can't reach them: fenced front yards and the fenced park
playground. They wave at the truck and jump-cheer when it honks. They aren't bodies, so no rule can hit them, and the hard
fences keep the truck out. This keeps the run-over chaos firmly slapstick (§13 risk 1). As built: §12.K.

### 3.2 Cars

- **Models:** `SM_car_town_01..04` (one renderer each; wheels are baked in, so cars get a body-bounce spring instead of spinning
  wheels). Tints use the builder's existing `TintCar` keys plus a few new ones: Meadows minivans keep their ×1.15 stretch and
  beige or teal paint; Snootington gets a long black "town car" (`_04` ×1.2 z, black); the Bluffs get a white-and-gold stretch limo
  (`_02` ×1.6 z).
- **Driver:** a lite puppet head and hat (3 renderers) shows through the windscreen while driving. When the car is shunted, the
  driver pops out as a full puppet.
- **Lights:** emissive headlight and brake-light quads (no realtime lights; `StreetLamps` keeps its pool of 8). Headlights are on
  from dusk to dawn, and the brake lights flash while yielding.
- **Fleet:** the existing parked cars become household cars (14 in district 1, 44 in the Meadows). A post-pass with its own RNG
  adds cars until 60% of district-1 houses with a long enough driveway have one (about 22), plus one car per Snootington house (14)
  and one limo per Bluffs estate (8). That's **~110 cars**, each one GameObject with one renderer and one box collider.

### 3.3 Exits (where cars go)

A **tunnel portal** in a grassy berm: a dark mouth, a guard rail, a crooked sign ("HIGHWAY →", "Now leaving Maple Hollow. Come back
with your trash!"), and a wall collider 6 m inside the darkness. Cars fade out in the dark. A truck that drives in bonks the wall and
gets a toast ("Nothing out there but highway. The trash is back that way.").

### 3.4 Knockables

These are the existing props (lamps, mailboxes, bins, signs, picket panels, lemonade stands, hoops, drying racks, park benches and
bins), plus new derp, placed by a post-pass with its own RNG:

- **Fire hydrants**, every ~60 m on the verges (≈40). They're primitives: a red cylinder, a cap and two nubs.
- **Road works**: 2–3 sites per district, each with 4–6 cones, 2 sawhorses and a "ROAD WORK (since 1997)" sign.
- **Pink lawn flamingos** in Meadows front yards (≈30).

### 3.5 Butlers (the Bluffs)

The static butler at each estate curb becomes a puppet. At a Butler Drop (07:00 and 17:00 ±10 min, `CurbTrashSpawner.ButlerDropTime`)
every subscribed estate's butler bows, **lifts the silver cloche and the bag(s) appear underneath** with a sparkle. He sets them down
and bows again. There's no walking, so it costs nothing, and the doc's best joke is free. The existing BUTLER DROP toast still fires.

---

## 4. Simulation vs presentation

### 4.1 What's simulated, and where

| Thing | Data (all machines) | Host decides | Presentation (local) |
|---|---|---|---|
| Put-out walk-outs | the `CurbBag` record + house anchors | nothing new (the spawner already decided) | resident walk, set-down, bag spawn |
| Pedestrians and dog walkers | roster + today's legs (pure function) | interruptions (flattened, startled, bumped, bonked) → `NpcOverride` | puppet, avoidance offset ≤1.2 m, chatter |
| Cars | roster + today's trips (pure function) | shunts → `NpcOverride` | driving, yield, overtake, honk-back, driveway moves |
| Knockables | `Knockable.id`, rest pose (scene) | down / up state (bitset), knock claims | proxy physics, lamp off, geysers |
| Consequences | — | fines (`Ledger.Charge`), Rep facts, grudges, letters, City Works tally | toasts, floaters, letters on each machine |

### 4.2 Relevance, pooling and LOD (per machine, from its own camera)

| Tier | Pedestrian / resident | Car | Prop |
|---|---|---|---|
| ≤35 m | full puppet, animated every frame | full: body springs, driver head, lights | static at rest; proxy when knocked |
| 35–90 m | lite puppet, animated at 15 Hz | body only, lights | same |
| 90–160 m | not shown (fog starts at 120 m) | moving cars shown, 15 Hz | same |
| >160 m | — | parked cars shown (static); moving cars hidden | same |
| **Caps** | **24 full + 24 lite**, nearest first; a walk-out beyond the cap falls back to today's drop-in | **16 moving shown** (the town-wide peak is ~12) | **24 awake proxies; 80 down at once** (the 81st knock makes the oldest reset early with a poof) |

Agent positions are evaluated only for agents whose current leg's bounding box comes within 170 m of a camera, and are cached per
frame. Walk-outs are presented only when the house is within 120 m of the camera; otherwise the bag simply appears in its slot.

### 4.3 Population targets (people on the street at once, whole district, unlocked or not)

| In-game hour | Mildly Pleasant | Minivan Meadows | Snootington | Bluffs | Moving cars, town-wide |
|---|---|---|---|---|---|
| 05–06 | 1 (dawn jogger) | 1 | 0 | 0 | 1 |
| 06–09 (rush) | 8–12 | 8 | 4 | 2 | 8–12 |
| 09–16 | 4–6 | 6 | 3 | 2 | 2–4 |
| 16–20 (rush) | 8–12 | 8 | 5 | 3 | 8–12 |
| 20–23 | 2 | 2 | 1 | 1 | 2 |
| 23–05 | 0–1 (night-owl dog walker) | 0 | 0 | 0 | 0–1 |

These are tuned through routine templates (§7.1). An EditMode test asserts each cell to ±25% averaged over 20 seeded days.

### 4.4 Performance budget (any machine; 60 fps target; measured with `ProfilerRecorder` markers)

| Item | Budget | How |
|---|---|---|
| `WorldLife.Schedule` (legs, routes, positions for relevant agents) | 0.15 ms | legs built once per day per agent on first relevance; positions are closed-form along cached polylines |
| `WorldLife.Puppets` | 0.60 ms | 24 full × ≤0.02 ms + 24 lite × ≤0.005 ms; no allocations; transform writes only |
| `WorldLife.Cars` | 0.20 ms | ≤16 × 0.012 ms (lane sampling, springs, yield check against a 16 m spatial hash) |
| `WorldLife.Sensor` | 0.05 ms | per *simulated* truck per FixedUpdate: one `OverlapBoxNonAlloc` plus a spatial-hash query |
| `WorldLife.Knock` | 0.05 ms | bookkeeping, reset timers |
| **Total main thread** | **≤1.1 ms average, ≤2.0 ms p99** | |
| Physics | ≤0.4 ms (inside `Multiplayer.md` §5.3's 3 ms) | ≤24 awake proxies with primitive colliders; settled proxies go kinematic after 3 s |
| GC | 0 B/frame steady state | pools for puppets, proxies, chatter; no LINQ; `NonAlloc` queries |
| Renderers (worst case visible) | ≈750 extra | 24 × 19 + 24 × 4 + 16 × 4 + ~100 parked cars + 24 proxies; SRP-Batcher-compatible materials |
| Scene GameObjects | +≈600 net (4,791 → ≈5.4k) | cars (+~110, replacing 58), hydrants, cones and flamingos (+~250), exits (+~60), anchors live in the asset (0 GameObjects). Stays under `Progression.md` §5.3's ~5.5k guideline |
| Memory | <1 MB | `StreetGraph.asset` ≈150 KB; roster ≈200 residents × 64 B; legs ≈700 × 32 B per day |

---

## 5. Street graph and pathing

### 5.1 Decision: generated lane graphs, no NavMesh

| Option | Verdict |
|---|---|
| **Lane graph generated by `NeighborhoodBuilder` from its own street specs** | **Chosen.** Streets are axis-aligned `Rect2`s with known widths, and every lot already knows its door, path and driveway. The graph gives directed right-hand lanes, sidewalks and crosswalks, which a NavMesh doesn't. It's deterministic everywhere (guests compute identical routes), needs no bake step and works headless (Community servers, M8). A* over ~2,000 nodes is ~0.1 ms, and routes are cached. |
| Unity AI Navigation (NavMesh + agents) | Rejected: it needs a bake per rebuild, has no lane direction, and its local avoidance isn't deterministic across machines. It would also add a package and a scene component for something the builder already knows exactly. |
| Steering only (critter style) | Rejected for traffic and commutes (no routes); kept for local avoidance, chases and rejoins. |

### 5.2 What the builder emits (`Assets/Resources/StreetGraph.asset`, written next to `HouseDirectory.asset`)

```
StreetGraph : ScriptableObject   (schema in W0; filled by NeighborhoodBuilder; read-only at runtime)
  int version, builderSeed
  Node[]  { Vector3 pos; NodeKind kind; byte district; ushort gate }      kind: Lane, Walk, Cross, Door, PathEnd, Drive, Slot, Exit, Park
  Edge[]  { ushort a, b; EdgeKind kind; float len; byte flags }           kind: Road(directed), Walk, Crosswalk, Jaywalk, GardenPath, Driveway, ParkPath
                                                                          flags: Yield, Pad, GateLocked(by district), Turn
  House[] { string stopId; ushort door, pathEnd, walkJoin, slot; ushort driveIn, driveOut; Pose[2] carSlot; byte cars;
            byte size, district; Pose butlerPost; Vector3 mailbox }
  Exit[]  { string id; ushort node; Pose portal; byte district }
  Gate[]  { string districtId; ushort nodeIn, nodeOut }
  Loop[]  { ushort[] nodes; byte district }                               precomputed sidewalk cycles (block, park figure-eight) for strolls
```

- **Road lanes:** each road rect yields two directed lanes 2.5 m either side of its centre line (drive on the right). Intersection
  nodes are linked by turn edges sampled as 4-point arcs (radius 5 m). Cul-de-sac bulbs (Meadows) and the Yacht Turnaround (Bluffs)
  get a loop lane at 70% of the bulb radius.
- **Sidewalk lanes:** both sides of every road at 7.4 m from the centre line (the curb lip is 5.0–5.3 m and the sidewalk
  5.3–8.0 m), clear of bins (6.15 m) and both rows of curb bags (≈6.1 m and ≈6.8 m). Corners are joined around each intersection.
  - **Crosswalk** edges at every intersection (the 12 `SM_pedestrian_crossing` sites plus implicit ones in the new districts).
  - **Jaywalk** edges every ~60 m mid-block, used by about 1 stroll in 6 ("headphones guy").
- **Houses:** door point (front-door position of the placed prefab: `doorA`, `centerD − doorZ`) → garden-path end at the sidewalk
  → the walk-lane join. The **slot** node is the curb stop. **Driveway** spur: the lane node at the curb ramp → the `carSlot` poses
  (`dwEnd − 2.9` and `dwEnd − 7.9`, where the builder already parks cars). The estate drive and gravel turnaround in the Bluffs.
- **Park paths** (`ParkPath_NS/W/E`) and the Meadows commons join the walk network.
- **The depot pad** is a **yield zone** (flag `Pad`). The only pad traffic is Depot Rd residents. The south loop of lanes must keep
  clear of the truck bay (x 9.6..13.4, z −6.1..1.7), the door apron (x −5..5, z −14..−8.9) and the truck spawn (0, −4) ±3 m. Cars
  cross it at ≤2.5 m/s.
- **Gates:** edges through a `DistrictGate` carry the district id. A route may use them only when that district is unlocked
  (`Districts.IsUnlocked`). NPC cars never pass a locked gate.

### 5.3 Validation (added to `NeighborhoodBuilder.Validate`)

- Every house door reaches its own slot (walk) and, if it has cars, an exit (road) without a locked gate *or* is flagged "stays home
  while locked".
- The walk network is one connected component per district (plus crosswalks and gates).
- Every lane sample (every 2 m) raycasts down onto `Road_*`, `DepotPad_*`, `CurbRamp`, `Driveway`/`Drive` or exit slabs.
- No walk sample lies inside a house, hedge, wall or knockable collider (0.35 m radius probe).
- **District-1 byte stability:** stopIds, positions (±0.001 m), wealth and capacity in `HouseDirectory` are identical before and after.
- The report lists node, edge and loop counts per district, plus a gizmo screenshot (`StreetGraphGizmos`, edit-mode render).

### 5.4 The three exits (corridors checked on 1 Oct: only scatter trees and 4 verge props stand in them)

| Exit | Where | Serves | Note |
|---|---|---|---|
| **West: "Old Highway"** | a 10 m road at z ≈ 200 from Old Money Rd (x −5) west to the boundary (x −60); tunnel at x ≈ −56 | Mildly Pleasant (via Elm Ave north) and Snootington (through its gate when unlocked) | on the free stretch of Old Money Rd between Birch St and the Snootington gate (z 175..230). Reserve the junction on that verge frontage so its lamp and trees move (Snootington's own RNG; Snootington lots are unaffected) |
| **East: "Interstate On-Ramp"** | Carpool Ln extended from x 405 to the boundary (440) at z −6 | Minivan Meadows | entirely inside the Meadows, so Meadows cars commute even while it's locked (life behind the fence) |
| **North: "Private Airport Rd"** | Tax Haven Heights Rd extended from Offshore Account Ave (0, 555) to the boundary (z 640) | Billionaire Bluffs | the limos leave for the airport. A helicopter on a helipad lifting off at 07:30 is a Phase 2 gag |

Exit roads join `c.streets` (ground slabs; nature scatter avoids them) through a new `ExitSpec` list built with
`seed ^ StableHash("worldlife")`. District 1's main RNG sequence is untouched.

---

## 6. Knockable street furniture

### 6.1 What's knockable (everything else stays a hard wall)

| Kind | Instances | Proxy mass | Knocks at truck speed ≥ | Truck speed lost | Extra |
|---|---|---|---|---|---|
| Lamp post (`SM_streetlamp_01/02/03`) | 139 | 60 kg | 3.0 m/s | 10% | light dies, bulb pops, sparks fizz at the stump ("CLANG!"); `StreetLamps` drops it from its light pool |
| Mailbox (`SM_mailbox_01/02`) | 73 | 10 kg | 2.0 | 2% | letter confetti |
| Wheelie bin (`SM_garbagecan_01..03`) | 81 | 15 kg | 2.0 | 3% | lid flaps; the GoldStar sticker is hidden while the bin is down |
| Street sign, yard sign, park sign | 28 | 8 kg | 2.0 | 1% | spins like a propeller |
| Picket panel (4 m, Meadows fronts) | ≈210 panels in 53 runs | 12 kg | 3.0 | 4% | planks; the builder gives each panel its own box collider instead of one per run |
| Lemonade stand (`SM_lemonade_shop`) | ≈6 | 25 kg | 2.0 | 4% | lemons (`SM_lemon`) bounce everywhere |
| Basketball hoop (Meadows driveways) | ≈10 | 30 kg | 3.0 | 6% | the ball rolls away |
| Drying rack, park bench, park bin | 12 + 4 + 4 | 6 / 40 / 12 kg | 2.0 / 3.0 / 2.0 | 1–6% | laundry flutters |
| **New:** hydrant | ≈40 | 40 kg | 3.0 | 8% | **20 s water geyser**; launches loose bags, items and critters standing in it, and gives the local player a 3 m hop (applied on their own machine) |
| **New:** cone / sawhorse | ≈60 / 20 | 2 / 6 kg | 1.5 | 0% / 1% | sometimes a cone sticks to the bumper for 10 s |
| **New:** lawn flamingo | ≈30 | 1 kg | 1.0 | 0% | flies, spins, lands beak-first |
| **Hard (never knockable)** | houses, trees (they shake and drop leaves on a hit, but stay solid), hedges, stone and estate walls, gate pillars and barriers, the depot, depot fence and junker, pools, statues, fountains, yachts, playground frames, district-1 garden fences | — | — | — | garden fences are mostly backyard lines; splitting them is Phase 2 |

That's about **720 knockables**, each given a stable `ushort id` in build order.

### 6.2 Truck behaviour

- **Above the threshold:** the prop goes down **before** the physics step can make contact (predictive, below), and the truck keeps
  going with the "speed lost" factor applied once, plus a small camera shake and a floater ("CLANG!", "BONK!", "*mailbox noises*",
  rate-limited to one per 0.4 s).
- **Below the threshold:** the rest collider is solid. The truck bumps and stops, so you can nose up to things carefully.
- **Feel cap:** speed loss from props is capped at 35% per second, so a cone slalom never feels like hitting a wall.

### 6.3 Lifecycle (rest → proxy → settle → reset)

1. **Rest:** the builder's object, still **batching-static**, with its rest collider. The builder adds a `Knockable { id, kind }`
   component, and for prefab props replaces non-convex MeshColliders with a capsule or box (cheaper overlaps).
2. **Detect (on the machine that simulates the truck's Rigidbody):** `TruckLifeSensor`, added to each truck at runtime (no prefab
   edit). Every FixedUpdate it overlaps a box of the truck's body bounds, stretched forward by `|v| × 2 × fixedDeltaTime + 0.3 m`,
   against knockables (collider → id map built at scene load). A hit above the kind's threshold disables the rest collider
   **that same FixedUpdate**, so PhysX never resolves the contact. No contact modification or new layers are needed.
3. **Swap:** the original's renderers (and TMP text) are disabled. Disabling one renderer of a static batch is supported, and the
   original never moves. A **proxy** comes from a pool, keyed by kind:
   - **Prefab props** use an instance of the *prefab asset* (registered as `knock/<prefabName>` by `Setup_WorldLife`), with
     primitive colliders and a Rigidbody.
   - **Builder-made props** (signs, picket panels, hoops) use a recipe that rebuilds the same primitives.
   - **Never `Instantiate` a static-batched scene object:** its mesh is the combined batch.
4. **Launch:** the proxy starts at the rest pose with velocity `v_truck × 1.1 + up × (1.5 + 0.1 |v|)` plus a seeded spin. It ignores
   collisions with that truck for 0.3 s (`Physics.IgnoreCollision`) so it doesn't explode out of the bumper.
5. **Settle:** after 3 s at rest it goes kinematic and asleep (it's still an obstacle, at zero cost). Debris stays local on each
   machine; nothing pays or hurts based on where it lies.
6. **Reset:** the host marks the prop *up* once it has been down ≥120 s **and** no player is within 25 m (from replicated
   positions), and every prop resets at **06:00** ("City Works was here"). On each machine the proxy shrinks out with a poof, the
   original re-enables and pops in, and a lamp flickers back on.

### 6.4 Lamps and the light pool

New API on `StreetLamps` (Sky owner, §11): `SetLampDown(Renderer r, bool down)`. A down lamp is skipped by the nearest-N sort,
its pooled light fades out and the bulb shows unlit. On reset it plays the existing flicker. Moths flee.

---

## 7. Residents and street life

### 7.1 Households and routines (`LifeRoster`, `LifeSchedule`)

- **Households:** 1–2 residents (small houses), 2–3 (medium), 2–4 (big); Meadows 2–4; Snootington 2 plus one staff member; Bluffs one
  owner plus the butler. That's about **200 residents**, ordered by stopId then member index (`ushort` ids). Names come from a word
  list ("Gary", "Linda", "Mrs. Okafor", "Mr. Pemberton").
- **Routine templates** (one or two per resident per day, hashed by day): `dog_walk` (06:30–08:00 or 18:00–20:00, a 300–600 m
  sidewalk loop), `jog` (05:30–07:30, 800–1,200 m loop at 2.8 m/s), `commute` (door → car, 07:00–09:00; back 16:30–19:00), `errand`
  (car, 1–2 trips 10:00–16:00, away 30–90 min), `stroll` (to the park or around the block, any time 09:00–20:00), `mail` (door →
  mailbox → door around 11:00), `gossip` (two neighbours meet at a property line for 2–4 min, glance at you and whisper),
  `night_owl` (rare, 22:00–00:30).
- **Walking speeds:** 1.3 m/s waddle, 1.1 m/s carrying a bag, 2.8 m/s jog, 0.9 m/s for Curler Gran and the Monocle Gent (cane).
- **Position at time t** = find the active leg (binary search), arc length = (t − leg start − delay) × speed, then a point on the
  leg's cached polyline. `delay` is the sum of host-written `NpcOverride` delays for today (§9.2).

### 7.2 Put-out walk-out (the trash ask)

```
 record time T (minute-quantised) ──► door: resident pops out with the bag (door creak + squash pop-in)
   │  walk carrying (1.1 m/s, both hands hugging; 2 bags = one per hand, extra waddle)
   ▼
 slot: bend + set down (1.0 s) ──► the real TrashBag spawns in its slot (thud, dust; no drop-in pop) ──► marker bump
   │  beat (1.5 s): stretch / wave at the truck if it's within 30 m / glare at a neglected pile
   ▼
 walk back ──► door: pop out of existence (squash)
```

| Rule | Detail |
|---|---|
| **Timing** | Progress `p = (TotalHours − T) / walkHours(house)` on every machine. Walk distances are 12–25 m, so the set-down lands 11–23 real seconds (0.2–0.45 in-game h) after T. The bag's age still counts from T. That costs at most ~10% of the 4 h on-time window and only matters if you collect at the very end of it. Accepted, because back-dating would change the meaning of a record. |
| **Who carries it** | the household member who is home at T (not on a commute or errand; ties broken by hash). If a commute starts within ±10 in-game minutes, the commuter carries the bag out on the way to the car. |
| **Mid-walk on scene load** | a record younger than its walk time is shown mid-walk at its progress `p`. Older records materialise directly in their slots, as today (catch-up, garage, debug skips). |
| **Far, pooled out, no anchor** | today's drop-in (`SpawnRecord(rec, dropIn: true)`). |
| **Hand-over** | while the resident carries it, the player can press **E "Take bag"**. The resident hands it over ("Oh! Thanks, dear!"), and the real bag spawns in the player's hands from the same record. +1 Rep (`neighborly`, cap 10 a day). In M2 this goes through the normal `RequestGrab(bagId)`, because it's still a curb record. |
| **Record disappears mid-walk** | Kevin's sweep, a debug clear, or in co-op another player grabbing it: the resident shrugs ("Oh. Okay then.") and goes back in. |
| **Resident flattened while carrying** | the bag flies out of their arms and lands where physics puts it, as a resting bag (never bursts). It keeps the same bagId and record. |
| **Night** | pajamas and a flashlight (an emissive cone, not a light). If a raccoon is within 6 m: "RACCOON!", the bag drops on the spot and they sprint back inside. |
| **Bluffs** | the butler cloche reveal (§3.5) instead of a walk. |

**Neglected piles get a witness.** While a house `IsNeglected` and the camera is within 60 m, one household member stands by the
pile with arms crossed and a hand-lettered sign ("IT'S BEEN 2 DAYS", "SMELL THAT?", "HOA NOTIFIED"), tapping a foot. When you clear
it: "FINALLY!" and they go in. If a Pile Goblin moves in, they yelp and flee. This is a free, readable neglect cue.

### 7.3 Traffic (`CarAgent`, `TrafficDirector`)

- **Trips:** a commute or errand trip is home slot → driveway reverse-out spline (4 s) → lanes → exit, away for the gap, then
  exit → lanes → nose-in to the slot. Speed profile: 9 m/s straights, 4.5 m/s turns, 2.5 m/s driveways and the pad, 1.5 m/s
  reversing. Cars are deliberately slower than the truck (12.5–17 m/s), so you can overtake them (or hit them).
- **Gate rule:** a trip whose route crosses a locked gate doesn't happen; that car stays home today.
- **Following and intersections** (local presentation): keep ≥6 m behind the car ahead on the same lane. Slow to 3 m/s entering an
  intersection, and wait if another car occupies its box. Yield to pedestrians on crosswalks.
- **Stuck behind the truck:** a car blocked by any truck brakes 8 m short and stops 3 m short. After 3 s it honks ("HONK HONK!
  GARBAGE DAY, HUH?!"). If the opposite lane is clear for 30 m, it overtakes on a lane-offset spline and merges back.
- **Re-convergence:** a car's drawn position is `s(t − localDelay)`. Blocking adds local delay, and once it's free the car catches
  up at up to 1.4× speed. Divergence between machines is bounded and short-lived, and cosmetic.
- **Players on foot in the road:** cars brake and honk. A player who steps in front of a car at the last moment gets a gentle
  **boop**: a local `CritterKnockback`-style 0.4 s stumble, no bag drop, 5 s immunity (`Multiplayer.md` §4.1 rule 4).

---

## 8. Interaction rules

### 8.1 Truck vs pedestrian or resident (the flop)

| Truck speed (inside the swept truck box, 0.35 m pad) | Result |
|---|---|
| < 0.8 m/s | nothing (they step aside) |
| 0.8–2.5 m/s | **nudge:** pushed 0.6 m aside, "HEY! WATCH IT!", glare. No fine. |
| ≥ 2.5 m/s | **flattened:** cartoon flop (below), plus consequences (§9) |

**Launch** (deterministic; the host's event carries the canonical values):
`v0 = flat(v_truck) × 0.55 + side × (2 + 0.12 s) + up × clamp(3.5 + 0.3 s, 4, 8)`, where `s = |flat(v_truck)|` and `side` is the
truck's right or left vector toward the NPC. Horizontal speed is capped at 10 m/s, the spin axis comes from the event's seed byte,
and the spin rate is 540–900°/s. The arc is closed-form, `p(t) = p0 + v0 t + ½ g t²`, landing on the launch point's ground height and
then snapped to the raycast ground. There is one bounce (restitution 0.35). Walls clamp the horizontal motion locally, as
`Critter.TickAirborne` does.

| Phase | Duration | What you see |
|---|---|---|
| Airborne | 0.8–1.6 s (3–12 m) | limbs flail on springs (`ChuckSpring`), "WHOAAA", mug or newspaper flies off as debris |
| Bounce + land | 0.3 s | squash, dust, "OOF!" |
| Lie | 1.6 s | starfish pose, `DizzyStars.Attach(head, 2 s)`, a "MY HIP!" / "I'M OKAY!" line |
| Get up | 0.6 s | wobbly |
| Angry | 1.4 s | red face, brows, steam, fist shake, a line by district ("I PAY MY TAXES!", "THIS IS GOING IN THE NEWSLETTER", "Do you know who I AM?!") |
| Chase | ≤3.0 s at 4.5 m/s | runs after **the hitter's truck** (from the event; never `PlayerController.Local`); gives up past 15 m with a stomp: "AND STAY OUT!" |
| Rejoin | to the path | jogs back to the hit point; the timetable resumes with the host's fixed delay (10 s), and the drawn position catches up |

**Immunity:** an NPC can't be flattened again until 2 s after rejoining (~12 s in total), and the same driver can't trigger the
same NPC for 20 s. Nobody is ever hurt: no blood, no injury states, nobody stays down.

### 8.2 Dogs

Dogs never get hit. Inside the truck box they do a scripted **leap**: a 1.2 m-high arc sideways, a yelp, and a landing that wags.
The owner shakes a fist ("YOU NEARLY HIT BISCUIT!"), Rep −1, no fine. If the owner is flattened, the dog runs barking circles and then
sits beside them until they get up.

### 8.3 Truck vs car (moving or parked)

- Car colliders are kinematic boxes. Below 2.5 m/s the truck bumps them like a wall (cars are solid; a car that's yielding never
  drives into the truck). At ≥2.5 m/s (sensor, predictive, like props) the car's collider turns off for 1 s and the car gets a
  **cartoon shunt**: a yaw spring kick of ±20–70°, a 0.3 m hop, a closed-form slide of ≤2 m along the truck's velocity, a crooked
  tilt until the next reset, a 6 s alarm (`car_alarm`) and flashing lights.
- The truck loses 35% of its speed plus a 6°/s yaw kick and a shake ("CRUNCH!").
- The driver pops out 0.5 s later, yells ("MY MINIVAN!", "That's a LEASE!"), shakes a fist for 3 s, gets back in, and the trip
  resumes (host delay 10 s). For a **parked** car, the owner storms out of the front door instead.
- Car immunity: 10 s.

### 8.4 Other contacts

| Contact | Rule | Who detects |
|---|---|---|
| Player on foot bumps an NPC | walking: they sidestep ("pardon me"); sprinting (≥4.5 m/s): they stumble 0.5 m back, spill their mug ("excuse YOU"). No fine. | the player's own machine (L), relayed as a ≤16 B `WorldFx` stumble |
| Thrown bag or item hits an NPC | <6 m/s boop; 6–11 m/s stumble and "HEY!"; ≥11 m/s a short flop (1.0 s lie) and Rep −1. **Bags never burst on NPCs** (like the `IsPlayer` guard). Per thrower per NPC, once every 4 s. | the thrower's machine claims; the host confirms (same as `BonkClaim`, `Multiplayer.md` §4.5) |
| Truck vs prop | §6 | the simulating machine claims; the host confirms |
| NPC car vs player on foot | the boop (§7.3) | the player's own machine (L) |
| NPCs vs critters | residents shriek and flee raccoons and goblins; dogs bark and pull toward raccoons (the raccoon flees); rabid raccoons never bite NPCs | local presentation |

### 8.5 The horn

Radius = `UpgradeEffects.HornScareRadius` (12 / 25 / 40 m).

- **Pedestrians** in range and ahead of the truck (or within 6 m in any direction) **startle**: a 0.4 m hop, "AAH!", then 2–3 s
  stepping off your line onto the grass, then a glare.
- **Cars ahead in the same lane** pull to the kerb and stop for 3 s ("yield to the garbage truck"). Cars elsewhere honk back.
- **Dogs** bark. **Raccoons** scatter (existing `CritterSpawner` behaviour).
- **Noise complaint:** a honk within 30 m of any house between 22:00 and 06:00 earns −1 Rep, at most 3 per night per district, and one
  batched letter in the morning.
- **Net:** the horn becomes a host-relayed truck event in M1/M2 (`Multiplayer.md` §5.1, "Horn ... H relays"). Reactions are
  deterministic per NPC from (npcId, honk tick), so every machine shows the same scatter.

### 8.6 Rule 12 declarations (`Multiplayer.md` §9)

| Action | Cooldown | Counter | Max helpless time (players) | Preset toggle | Detected by |
|---|---|---|---|---|---|
| Flatten an NPC | NPC immune ~12 s; same driver + NPC 20 s | brake, honk first | n/a (NPC) | `NeighborFines` (cash only; flops always happen) | the machine simulating the truck; the host confirms |
| Shunt a car | car immune 10 s | brake | n/a | `NeighborFines` | same |
| Knock a prop | prop stays down until reset | — | n/a | none (City Works invoice is capped) | same |
| NPC car boops a player | player immune 5 s | step aside; cars brake | 0.4 s stumble | none | the player's own machine |
| Thrown item flops an NPC | per thrower + NPC 4 s | — | n/a | none | the thrower; the host confirms |
| Honk scatter or complaint | horn 0.3 s, 4/s; complaints capped at 3 per night per district | — | n/a | none | the driver → host relays |
| Bump an NPC on foot | 1 s per NPC | — | n/a | none | the player's machine (L) |

---

## 9. Consequences and economy

### 9.1 Table (charged in the district where it happened; Rep × that district's multiplier ×1 / ×1.5 / ×2 / ×3)

| Incident | Mildly Pleasant | Minivan Meadows | Snootington | Bluffs | Rep (base) | Also |
|---|---|---|---|---|---|---|
| Resident or pedestrian flattened | $3 | $12 | $40 | $120 | −4 | grudge on their house; complaint letter |
| Car shunted (moving or parked) | $5 | $20 | $60 | $200 | −3 | grudge on the car's house; "insurance" letter |
| Dog near miss | — | — | — | — | −1 | — |
| Thrown-item flop | — | — | — | — | −1 | — |
| Noise complaint (night honk) | — | — | — | — | −1 (≤3 a night) | morning letter |
| Prop knocked (City Works, per prop) | $0.50 | $2 | $5 | $15 | — | hydrant ×4 |
| **City Works daily cap** | **$5** | **$20** | **$60** | **$200** | — | one invoice per district at midnight |
| Hand-over ("Take bag") | — | — | — | — | **+1** (cap 10 a day) | "Service with a smile!" |

- **Escalation:** the n-th flatten or shunt in a district on the same in-game day costs × min(3, 1 + 0.25 (n − 1)). It resets at
  midnight and is stored per company (`GameSession` counter `wl_incidents:<district>:<day>`).
- **Grudge (the lateness consequence):** until the next midnight, that house's bags pay as if they were **1 in-game hour older**
  (`Districts.PayFraction(d, age + 1)`), and the on-time tip is void. The household glares and doesn't wave. **Redemption:** collect
  every bag at that curb and you get "Apology accepted", which clears the grudge (no Rep, so it can't be farmed). Stored per
  company: `GameSession` stamp `wl_grudge:<stopId>` (until-hours) until M1's stores convert timers to remaining hours (rule 9).
- **Letters** (`HeadOffice.Send`, presentation only): one pop-up per in-game hour at most, the rest go to the inbox. The voice
  changes by district:
  - Mildly Pleasant: "Dear Garbage Person, at 7:42 AM your truck flattened me. I was holding a latte. — Gary, 14 Maple St".
  - Meadows: "HOA VIOLATION NOTICE #4471" from the HOA Lady.
  - Snootington: "We are most displeased." from Mr. Pemberton.
  - Bluffs: "From the desk of Dewey, Cheatham & Howe LLP".
- **Trust:** no effect in v1. The knob `WorldLifeTuning.IncidentTrustLoss = 0` is there for later. Progression's sim found Trust
  already self-correcting, so adding a second source isn't needed yet.

### 9.2 What it costs (against `Progression.md` §8.1 net per day)

| Play style per in-game day | Mildly Pleasant (net $320–932/day) | Bluffs (net $15k–30k/day) |
|---|---|---|
| Careful (≤1 flatten, 5 props) | ≈$6 (≤1.8%), Rep −4 | ≈$195 (≤1.3%), Rep −12 |
| Typical (2 flattens, 1 shunt, 15 props) | ≈$19 (2–6%), Rep −11 (≈4% of a day's Rep) | ≈$770 (2.5–5%), Rep −33 |
| Rampage (15 flattens, 8 shunts, 100 props) | ≈$227 (24–70%), Rep −84, ≈15 grudges | ≈$9k (30–60%), Rep −252 |

Accidents are a rounding error. A deliberate rampage costs a third of a day or more, and it's still possible, because that's the
clip.

### 9.3 Who pays in co-op

- The **company of the truck** pays (`LedgerActor.As(driver)`), and the receipt names the driver: "Ozzie flattened Gary at 14 Maple
  St (−$3)". This follows `Multiplayer.md` §4.1 rule 9 ("consequences are accountable").
- **Town rules:** propose a new `TownRules.NeighborFines` bit (default **on** in Crew, Rival and Open). With it off, fines become
  receipt shame only; Rep, grudges and flops always apply.
- **Rival Towns:** each company pays for its own trucks, and grudges are per company ("they hate *GoldStar*, not you").

---

## 10. Co-op and netcode (`Multiplayer.md` §9 compliance)

### 10.1 Authority

| State | Lives in | Decides | Synced how | Size |
|---|---|---|---|---|
| Street graph, roster, schedules | asset + pure functions | nobody (data) | not at all (Town seed + day) | 0 |
| Walk-outs | curb records | spawner (H, existing) | `WorldState.Curbs` (B6, existing plan) | 0 extra |
| `NpcOverride { id, day, delayCs, kind, untilTick, actorSlot }` | `WorldLifeNet` (host) | H | event + late-join snapshot | 12 B each, a few alive |
| Knock state (down bitset, ≈720 bits) | `WorldLifeNet` (host) | H | events + 90 B bitset on join | — |
| Grudges, incident counters, City Works tally | company (`GameSession` facade; M1 `CompanyState`) | H | company snapshot (D4) | bytes |
| Flop arcs, debris, chases, yield, puppets | local | L | event params only | — |

### 10.2 Messages (`[NetSessionComponent] WorldLifeNet : NetworkBehaviour`, ChessController pattern, all through `NetGuard`)

| Message | Direction | Bytes | Rate limit | Host checks |
|---|---|---|---|---|
| `NpcHitClaim(ushort id, int tick, half3 truckVel, byte truckRef)` | simulating machine → H | 16 | 4/s per client | the claimant simulates that truck; NPC hittable and not immune; the host's position for the NPC at `tick` (timetable + overrides, or its 1 s presented history while interrupted) within **2.5 m** of the host's rewound truck box (1 s pose history, `Multiplayer.md` §4.12); speed ≤ tier max + 15% |
| `KnockClaim(ushort knockId, int tick, half3 impulse)` | same → H | 12 | 15/s per client | prop up; within 3 m of the rewound truck box; speed ≥ the kind's threshold |
| `NpcEvent(ushort id, byte kind, int tick, half3 launch, byte seed, byte actorSlot, ushort delayCs)` | H → all | 20 | — | — |
| `KnockEvent(ushort knockId, half3 impulse, byte seed)` / `KnockReset(ushort)` | H → all | 12 / 4 | — | — |
| `LifeSnapshot { day, seed, overrides[], downBits }` | H → joiner | ≤1.5 KB | once | — |
| Horn | existing truck horn event (M1/M2) | 0 extra | 4/s (§4.11) | seat |

**Prediction:** the machine simulating the truck plays the flop or knock immediately, with the same formula. When the host's event
arrives, it keeps its animation if the launch matches within 15%; otherwise it blends. A **rejected** NPC claim (rare) plays a
"dodged!" cartoon leap, so the rejection itself is the joke (`Multiplayer.md` §4.1 rule 5). A rejected knock stays down locally and
resets with the next `KnockReset`.

**Bandwidth:** 0 B/s steady state. A rampage at the rate limits is ≈4 × 20 + 15 × 12 = 260 B/s per offending driver, ≈1.8 KB/s of
host upload at 8 players. That's well inside §5.3.

### 10.3 Phasing

- **Now (single player / M0):** everything runs on the host or offline. "Claims" are direct calls into `WorldLifeAuthority`, so
  the code is already split into decide and present (rule 5). A guest in an M0/M1 test session sees deterministic NPCs and the
  walk-outs (from mirrored curbs), but **hits by a guest driver are local-only (no fine) until package F**.
- **Package F (after M1 waves B6, B7, B11 land):** `WorldLifeNet` with the messages above, the Town seed from `WorldState`, ledger
  actors from claims, and grudges and counters in the company DTO.

### 10.4 The §9 rules, one by one

| Rule | How WorldLife complies |
|---|---|
| 1 Ledger | fines through `Ledger.Charge(…, LedgerIds.NeighborDamages / CityWorks)`; Rep through a fact that Progression A applies |
| 2 RNG | data uses the stateless `LifeHash`; no content is rolled; cosmetic `Random` is marked `coop:allow(random) cosmetic` |
| 3 no Find / Current | trucks from `TruckControllerV2.All` (W0); street graph from the anchor; until W0 lands, a 1 Hz `FindObjectsByType` scan |
| 4 no timeScale | never calls `Juice.HitStop` (global timeScale today); camera shake only |
| 5 decide / present | `WorldLifeAuthority` (decide) vs `*Presenter` / `ResidentPuppet` (present) |
| 6 no scene names | `WorldLifeAnchor` activation; exits, gates and districts by data |
| 7 per-player state | none beyond the player's own boop immunity (on their machine) |
| 8 Local | camera relevance only; chases target the hitter's truck from the event |
| 9 saves | `GameSession.SetCounter/SetStamp` now, company DTO in M1; no new files |
| 10 ChessController pattern | `WorldLifeNet` |
| 11 company-scoped | fines, grudges and counters belong to the company |
| 12 declarations | §8.6 |
| 13 no networked rigidbody pairs | NPCs and cars are scripted; debris is local; hits come by rule from the simulating machine's truck |
| 14 lint | `Tools/coop_lint.sh --files 'Assets/Scripts/WorldLife/**'`; expected allows: `random` (cosmetic) only |
| 15 host-only | consequences, grudges, invoice, overrides and knock state only when `!GameSession.IsGuest` |
| 16 guests don't mutate | guests send claims; they never touch `GameSession` |
| 17 secrets | n/a |

---

## 11. Changes to existing systems

| File (owner) | Change | Package |
|---|---|---|
| `Editor/NeighborhoodBuilder.cs` (Neighborhood / Trash) | `BuildStreetGraph(c)` after `BuildDistricts`. `LotInfo` keeps door, path, driveway and car-slot poses (no RNG change). Parked cars become `CarSlot` data: **the `c.Chance` and `c.RI` draws stay**, only `Place` is replaced. `Knockable` components and primitive rest colliders. A per-panel collider for picket runs. `ExitSpec` stubs and portals. Post-pass props (hydrants, road works, flamingos) on their own RNG. Butler posts. `WorldLifeAnchor`. Validation (§5.3). Writes `Resources/StreetGraph.asset` next to `HouseDirectory`. | A |
| `Trash/PickupStop.cs` (Trash) | `public static Func<PickupStop, CurbBag, bool> PutOutPresenter`, consulted in `SyncBags` for fresh records instead of the drop-in. A `presenting` id set so `SyncBags` doesn't double-spawn. `public TrashBag MaterializeRecord(CurbBag rec, Vector3? at, Vector3 vel, bool intoHands)` (wraps `SpawnRecord`). | W0 |
| `Trash/TrashBag.cs` (Trash) | `public static Transform BuildSackVisual(BagContents c, Transform parent)` (the cosmetic sack in a resident's arms; same model and colour as the real one) | W0 |
| `Trash/CurbTrash.cs` (Trash) | pay and tip read `public static Func<string, float> ExtraAgeHours` (grudge), default 0 | W0 |
| `Trash/CurbTrashSpawner.cs` | **no change** | — |
| `World/StreetLamps.cs` (Sky) | `SetLampDown(Renderer, bool)`; skip down lamps in the pool and the bulb swap | W0 stub, B body |
| `TruckControllerV2.cs` (Flow; additive, lead-approved) | `public static IReadOnlyList<TruckControllerV2> All` (OnEnable / OnDisable); `public static event Action<TruckControllerV2> Honked` raised in `Honk()`; `public Rigidbody Body` | W0 |
| `Progression/ProgressionIds.cs`, `ProgressionEvents.cs`, `ProgressionRules.cs` (Progression A, additive) | `LedgerIds.NeighborDamages = "neighbor_damages"`, `CityWorks = "city_works"`; `RepIds.NeighborIncident`, `NoiseComplaint`, `Neighborly` + `RepValues` (−4 / −3 / −1 / −1 / +1); `NeighborIncidentFact { kind, stopId, districtId, pos, escalation }` + `RaiseNeighborIncident`; A's handler applies Rep | W0 (A signs off) |
| `Critters/CritterSpawner.cs` (Critters) | optional: switch horn polling to `TruckControllerV2.Honked` | later |
| `Feel/FeelSfxSynth.cs` (Feel) | recipes for `clang`, `car_alarm`, `car_horn`, `geyser`, `sparks`, `door_creak`, `oof`, `yelp`, `bark`, `mumble` (+ `mumble2`, `mumble3`), `tire_screech` | G (built, §12.G) |
| `World/DistrictGate.cs` | none (routes read the lock state through `Districts.IsUnlocked`) | — |

**Not touched:** `Net/**`, `Save/**`, `SaveModel/**`, `UI/Menu/**`, `MainMenuManager`, `GameStartup`, `PlayerController`,
`PlayerSpawner`, `SceneController`, Player and Truck prefabs, `ProjectSettings`, `Packages`. These belong to the M0 workflow (and
Bootstrap is never saved). **Synergy:** the minimap package can draw roads straight from `StreetGraph.asset`.

---

## 12. Build plan

### 12.1 Folders and ownership

New system **WorldLife** owns `Assets/Scripts/WorldLife/**`, `Assets/Scripts/Editor/Setup_WorldLife*.cs`,
`Assets/Materials/Generated/WorldLife/**`, `Assets/Prefabs/WorldLife/**`, `Assets/Resources/StreetGraph.asset` (written by A's
builder code) and `Assets/Tests/EditMode/WorldLife/**`.

| Package | Owns (edit or create only these) | Est. |
|---|---|---|
| **W0 Contract (lead)** | `WorldLife/WorldLifeTypes.cs` (enums, ids, `ILifeBody`, `LifeBodies` spatial hash, `NpcHit`, facts, `WorldLifeTuning` tables from §6.1 and §9.1), `WorldLife/Graph/StreetGraph.cs` (schema + `StreetGraph.Test()` hand-made 2-block graph), `WorldLife/WorldLifeRunner.cs` (boot, anchor activation, profiler markers, sub-director slots), `Editor/Setup_WorldLife.cs` (umbrella that calls each package's `Setup_WorldLife<X>.Run`), plus every hook row in §11 marked W0 | 0.5–1 d |
| **A Street graph + world build** | `Editor/NeighborhoodBuilder.cs` (exclusive for this build), `WorldLife/Graph/**` (except the schema), `Editor/Setup_WorldLifeGraph.cs` | 2.5–3 d |
| **B Knockables** | `WorldLife/Knock/**`, `World/StreetLamps.cs` (the `SetLampDown` body), `Editor/Setup_WorldLifeKnock.cs` (proxy prefabs `Prefabs/WorldLife/Knock_*`, `knock/*` keys), `Prefabs/WorldLife/Knock*` | 2 d |
| **C Residents + puppets** | `WorldLife/Residents/**`, `WorldLife/Puppet/**` (`ResidentLook`, `ResidentPuppet` + `.Flop`, `DogLook`, pool, LOD, `WalkOutPresenter`, `ButlerPost`, neglect witness), `Editor/Setup_WorldLifeResidents.cs` (materials, `mat/worldlife_*`) | 3 d |
| **D Schedules + traffic** | `WorldLife/Schedule/**` (`LifeHash`, `LifeRoster`, `LifeSchedule`, routine templates, A*), `WorldLife/Traffic/**` (`CarAgent`, `TrafficDirector`, `PedestrianDirector`, exit portals), `Tests/EditMode/WorldLife/**` | 3 d |
| **E Rules + consequences** | `WorldLife/Rules/**` (`TruckLifeSensor`, `WorldLifeAuthority`, `NeighborConsequences`, `NeighborGrudge`, `HonkReactions`, City Works midnight invoice) | 1.5–2 d |
| **F Net (after M1 B6/B7/B11)** | `WorldLife/Net/**` (`WorldLifeNet`, snapshot, claims) | 1.5 d |
| **G Polish + perf** | tuning in `WorldLifeTuning`, Sfx asks, letters, chatter lines | 1–2 d |

### 12.2 Order and dependencies

```
 W0 contract ──┬──► A graph + builder ──(lead runs Setup/Neighborhood)──┐
               ├──► B knockables (sandbox: Knockable on a test cube row) ┼──► integrate in Neighborhood ──► E rules ──► G polish
               ├──► C residents (StreetGraph.Test())                    ┤                                              │
               └──► D schedules + traffic (StreetGraph.Test())          ┘                         (after M1) F net ◄───┘
```

- B, C, D and E code against W0's types and `StreetGraph.Test()`, so none of them waits for A's scene rebuild. Integration needs A's
  real asset.
- **Setup order** (extends `CoreLoop.md` §5): Items → Pickup → Feel → Flow → Sorting → Ebae → **Neighborhood (A)** →
  **WorldLife** (`Setup_WorldLife`: Graph check → Knock → Residents → Traffic → Rules → Kids → Polish) → (M1+) `Setup_Coop` picks
  up `WorldLifeNet` through `[NetSessionComponent]`.
- **Editor etiquette:** check `editor_status` first. Rebuilds and play tests run only under
  `scratchpad/editor.lock` (+ owner file), never while Nick is playing, and with the test data root
  (`Tools/dev/test_data_root.sh`). Never save Bootstrap. Re-read shared files right before editing them. Before reporting: a clean
  compile and `Tools/coop_lint.sh --files 'Assets/Scripts/WorldLife/**' <your other files>`.

### 12.3 Acceptance checks (each package, via `eval_file`, reported with numbers)

| Pkg | Check |
|---|---|
| W0 | Compiles. Each hook is a no-op with no presenter registered: a full play-mode loop (garage → Neighborhood → collect 3 bags → garage) behaves exactly as before. `coop_lint` clean. |
| A | §5.3 validation passes. Node, edge and loop counts per district are reported. District-1 `HouseDirectory` is byte-identical (stopIds, positions ±0.001, wealth, capacity). ≥700 knockables with unique ids and non-mesh rest colliders. The 3 exits exist and are reachable. GameObjects ≤ +700 vs 4,791. A gizmo top-down render is saved to `Docs/WorldLifeRefs/graph_top.png`. |
| B | Drive the truck at 8 m/s through 5 lamps in a row: all 5 down within one FixedUpdate of overlap, speed after ≥ 4.3 m/s, no NaN, the lights go out (`StreetLamps.ActiveLights` drops). At 1 m/s into a lamp: it stays up and the truck stops. Awake proxies ≤24. Every down prop resets within 125 s once no player is within 25 m, and all reset at 06:00. 0 B GC per frame after warm-up. A hydrant geyser lifts a resting `TrashBag` ≥2 m. |
| C | `CurbTrashSpawner.DebugPutOut(1)` at a house 20 m from the camera: a resident appears at the door within 0.5 s, the bag spawns in its slot at set-down, and the record count is unchanged throughout. The same at 200 m: the bag appears directly. "Take bag" puts the same bagId in the player's hands, and it pays in the hopper. Flatten a carrying resident: the bag lands intact with the same bagId. Butler Drop at 07:00: one cloche reveal per subscribed estate. ≤24 full puppets; ≤0.02 ms per full puppet (profiler marker). |
| D | Same seed and day → identical leg lists and positions (EditMode test, hash compare). The §4.3 population table holds to ±25% over 20 seeded days. In play at 07:30: 6–12 moving cars and 8–14 pedestrians in district 1; at 02:00, ≤2. Over a 5-minute run, no lane deviation > 0.6 m except overtakes, and same-lane gaps ≥4 m. A car stuck behind a parked truck honks within 3 s and overtakes within 8 s with the other lane clear. No car route crosses a locked gate. |
| E | Flatten a pedestrian at 6 m/s: they land 3–8 m away, lie ≥1.5 s with stars, get up, chase ≤3 s and rejoin. The ledger shows exactly one `neighbor_damages` −$3 (Mildly Pleasant) and Rep −4. Their house has a grudge, and a bag there pays as 1 h older. A second hit within 12 s does nothing. Honk at 12 m: everyone within 12 m startles and cars ahead pull over. Night honks give ≤3 complaints. The midnight City Works invoice ≤ cap. `WorldLife` CPU ≤1.1 ms average during the 07:30 rush with 24 puppets. |
| F | BOTS (host + 2 guests, Crew Bad Wi-Fi): a guest driver flattens 10 NPCs and knocks 30 props. The host ledger has 10 charges attributed to the guest's slot. All machines agree on down bits and overrides at matched `StateVersion`. A late joiner sees fallen props and grudges. WorldLife traffic ≤1 KB/s per client during the rampage. 0 `GuestWritesDropped` from WorldLife. |


### 12.4 W0 contract as built (1 Oct 2026)

What packages A–G compile against. Global namespace, Assembly-CSharp. `Setup_WorldLife.SelfTest()` (menu **Rubbish/WorldLife/Contract
Self Test**) checks all of it without a scene.

| File | Contents |
|---|---|
| `WorldLife/WorldLifeTypes.cs` | `LifeKind`, `LifeId` (kind + ushort), `LifeHash` (SplitMix64: `U64`, `Str`, `Next`, `Range`, `Chance`, `Pick` with integer weights, `Float01` for looks only), `WorldLifeIds` (district byte = tier − 1, `UnlockedDistrictMask`, GameSession keys, asset key prefixes, new Sfx ids), `DailyCount`, `LifeBodyKind`, `ILifeBody`, `NpcHitKind`, `NpcHit`, `NpcHitClaim`, `KnockKind`, `KnockFact`, `KnockClaim`, `HandOverFact`, `NpcOverride` + `LifeOverrides` (host store; D reads `DelaySeconds(id, day)`), `TruckSweep` + `ITruckSensorClient`, `IWorldLifeAuthority`, `WorldLifeEvents` |
| `WorldLife/LifeBodies.cs` | the 16 m spatial hash: `Register/Unregister/Sync/Find/Query/QueryBox/Nearest`, 0 B per frame |
| `WorldLife/WorldLifeTuning.cs` | every number in this doc: `Knock[]` (§6.1), `Props`, `Hits` (+ `FlopLaunch`, `PersonResult`), `Horn`, `Fines` (+ `Escalation`), `Lod`, `Walk`, `Traffic`, `Budget`, `Density`, `NeighborFines` |
| `WorldLife/Knockable.cs`, `WorldLife/WorldLifeAnchor.cs` | the two components A places (W0 because A, B and the runner all need them before A lands) |
| `WorldLife/WorldLifeRunner.cs` | `WorldLifeSlot`, `WorldLifeDirector` (Wake/Sleep/Tick/FixedTick/LateTick), `WorldLifeContext`, the `[WorldLife]` runner, the no-op authority, `WorldLifeProfiler` (avg / p99 / max per `WorldLife.*` marker) |
| `WorldLife/Graph/StreetGraph.cs`, `StreetGraph.Test.cs` | the §5.2 schema, CSR adjacency (`OutBegin/OutEnd/OutEdge/OutTo`, edge order = tie-break), gates (`GateOf`, `EdgeOpen(edge, mask)`), `Flood`, structural `Validate` (§5.3 minus the scene probes), `Describe`; `Test()` = the hand-made 2-block graph (273 nodes, 311 edges, 8 houses, 1 exit, 1 gate into Meadows, 2 loops) |
| `Editor/Setup_WorldLife.cs` | **Rubbish/Setup/WorldLife** (runs `Setup_WorldLife{Graph,Knock,Residents,Traffic,Rules}.Run` if they exist, then validates the graphs), **Rubbish/WorldLife/Validate Street Graphs**, **Profiler Report**, **Contract Self Test**, and `Setup_WorldLife.Probe` / `ProbeBody` for play tests |

Hooks in other systems (all no-ops until something registers): `PickupStop.PutOutPresenter` / `IsPresenting` /
`MaterializeRecord` (records younger than 0.5 h are offered; a presenter that never materialises loses the record after 90 s);
`TrashBag.BuildSackVisual`; `CurbTrash.ExtraAgeHours`; `StreetLamps.SetLampDown` / `IsLampDown` (static; W0 records the state, B
adds the behaviour); `TruckControllerV2.All` / `Honked` / `Body` / `BodyBoxMetres`; `RepIds`, `RepValues.Neighbor*`, `LedgerIds`
and `ProgressionEvents.RaiseNeighborIncident(NeighborIncidentFact)`, applied by `Progression.OnNeighborIncident`.

Decisions taken in W0 (read these before building B–E):

- **One sweep, many clients.** The runner builds each simulated truck's predictive box once per FixedUpdate (body box stretched by
  |v| × 2 × dt + 0.3 m toward the direction of travel; only trucks whose Rigidbody isn't kinematic on this machine) and hands it to
  every `ITruckSensorClient` before the physics step. B registers the knockable overlap, E the people / dogs / cars query. E's
  `TruckLifeSensor` is a sensor client, not a per-truck component, so `WorldLife.Sensor` is billed in one place.
- **Decide / present.** Hits go `ClaimNpcHit` → the authority decides → `ILifeBody.PlayHit` + `WorldLifeEvents.HitDecided`. Knocks
  and hand-overs are presented first by B / C (prediction), then claimed (`ClaimKnock`, `ClaimHandOver`) for bookkeeping. Until E sets
  `WorldLifeRunner.Authority`, a no-op authority decides hits with the §8 formulas and charges nothing. F forwards guest claims into
  the same interface.
- **Directors** register from `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` with `WorldLifeRunner.Register` (the runner's statics
  reset at SubsystemRegistration). Presentation objects go under `ctx.Root`, which lives in the anchor's scene.
- **LifeHash is W0's**, so C can pick looks before D lands; D uses it rather than adding its own.
- **Rep caps live in Progression's handler** (noise 3 per night per district, the night running 12:00–12:00; hand-overs 10 per day).
  E only raises the facts. Rep is never escalated; `NeighborIncidentFact.escalation` is informational.
- **Day counters are packed** (`(day << 10) | count` in one GameSession int: `DailyCount`, `WorldLifeIds.IncidentCounter(district)`,
  `CityWorksCounter(district)`) instead of one `:<day>` key per day, so saves don't grow.
- **The grudge** changes pay only: fee × PayFraction(age + 1) and no tip. Lateness, the BagCollected fact (and its Rep) and the label
  keep the real age; the floater says "(grudge)".
- **Schema additions:** `EdgeFlags.OneWay` (driveway in / out ramps; Road edges are always one-way), `NodeKind.Cross` for
  intersection corners, `House.carSlot0/carSlot1` (Unity can't serialise a fixed `Pose[2]`), `StreetGraph.None = 0xFFFF`.
- **Fines toggle:** `WorldLifeTuning.NeighborFines` reads `NeighborFinesProvider` (F: `TownRules.NeighborFines`), else the company
  counter `wl_fines_off` until M1.
- **EditMode tests:** `Assets/Tests/EditMode/**` is the `Rubbish.Net.Tests` asmdef, which can't reference Assembly-CSharp. Put
  WorldLife tests in an editor class (like `Setup_WorldLife.SelfTest`) and run them by menu or `eval_file`.

### 12.A Package A (street graph + world build) as built (1 Oct 2026)

![graph](WorldLifeRefs/graph_top.png)

`Rubbish/Setup/Neighborhood` builds all of it. Every WorldLife object comes from one RNG of its own (seed ^ `StableHash("worldlife")`)
and is built **after** the town and nature. The district RNG sequences don't change, and `HouseDirectory.asset` is byte-identical
(same MD5, 81/81 entries) after the rebuild. Two consecutive builds give the same graph, knockables and cars.

| File | Contents |
|---|---|
| `Editor/NeighborhoodBuilder.cs` | `DefineExits`, `BuildWorldLife` (exits, household cars, hydrants, road works, flamingos, `Knockable` marking, the anchor), `BuildStreetGraph`, and the §5.3 checks in `Validate`. It writes `Resources/StreetGraph.asset`, then calls B's `Setup_WorldLifeKnock.BakeScene` (by name) before saving. `Build(scene, seed, graphPath)` builds a scratch copy |
| `WorldLife/Graph/Editor/StreetGraphBuilder.cs` | generic: road description → graph (lanes, turn arcs, bulb loops, exits, corners and crosswalks, bulb rings, gates, jaywalks, houses, paths, pad lanes, Dijkstra loops) |
| `WorldLife/Graph/Editor/StreetGraphProbes.cs`, `StreetGraphRender.cs` | the scene probes, the knockable checks, and the top-down and perspective renders with the graph drawn on top |
| `WorldLife/Graph/HouseholdCar.cs`, `TownExitPortal.cs`, `StreetGraphGizmos.cs` | runtime: the parked-car marker (`All`), the tunnel trigger (`All`, toast), and Scene-view gizmos on the anchor |
| `Editor/Setup_WorldLifeGraph.cs` | the umbrella's **Graph check** (read-only: asset, anchor, cars, portals, knockables, probes) and **Rubbish/WorldLife/Render Street Graph** (`graph_top.png`, `exit_west/east/north.png`) |

**As built:**

- **Graph:** 1,777 nodes, 1,957 edges, 81 houses, 3 exits, 3 gates and 10 loops.
  - Per district (nodes / edges / loops): 629 / 695 / 4, 509 / 564 / 3, 382 / 423 / 2, and 257 / 275 / 1.
  - Edges: Road 744, Walk 788, Crosswalk 45, Jaywalk 33, GardenPath 97, Driveway 231, ParkPath 19. The asset is 324 KB.
- **§5.3 probes:** 5,683 lane samples (0 off-road, 0 blocked) and 9,732 walk samples (0 inside a collider).
  - Every door reaches its slot. Every car household reaches an exit with its own district unlocked.
  - The 14 Snootington households stay home while Snootington is locked. Meadows and Bluffs reach their own exits behind their gates.
- **Knockables:** 759, ids 0..758 in build order.
  - Lamp 146, Mailbox 73, WheelieBin 81, Sign 51, PicketPanel 241, LemonadeStand 3, BasketballHoop 11, DryingRack 12, ParkBench 4, ParkBin 4,
    Hydrant 39, Cone 47, Sawhorse 20, Flamingo 27.
  - 808 primitive rest colliders and no mesh colliders. All still static-batched.
  - B's bake: 323 fly from proxy prefabs, 436 from the mesh bake.
- **Cars and props:** 89 household cars, 10 road-works sites.
- **Scene:** 5,280 GameObjects, +489 against the 4,791 baseline (+397 against the scene before this rebuild).

Decisions (read before C, D or E):

- **Exits** are `exit-west` "Old Highway", `exit-east` "Interstate On-Ramp" and `exit-north` "Private Airport Rd".
  - Each portal is 6 m inside the boundary. The `Exit` node sits at the portal, and the lanes stop 2 m before it.
  - The tunnel's end wall (layer Walls) is 6 m in, and `TownExitPortal` toasts whoever is driving.
  - The corridors were **cleared** after the town was built (39 verge props and trees) instead of being reserved, so no RNG sequence moved.
- **Household cars stay static-batched at rest**, like knockables. One GameObject each, with a BoxCollider on layer Ground and a `HouseholdCar`.
  - D hides `body` and `restCollider` and drives a pooled copy (`prefab`, `tint`, `scale`, `noseYaw` = −90: the pack's noses point +X).
  - The car shows again when it parks.
  - Slot poses face the house (`carSlot0` is the deepest). `House.cars` counts only cars whose driveway joins a lane.
  - 2 corner lots (elm-3, mm-juicebox-254, 3 cars) are `parkedOnly`.
  - The old parked-car RNG draws are kept, and every car now parks nose-in.
  - Top-ups: district 1 to 60 % of houses with a long driveway; one black town car (`_04` × 1.2 long) per Snootington house; one
    white limo (`_02` × 1.6) per estate.
- **Doors:** the node is where a 0.35 m person fits at the front door: the first hit on the house along the garden path, 0.45 m out.
  - It stands on a low porch step (≤ 0.45 m). Otherwise it steps out in front of a raised porch.
  - **Estates** walk door (hall front) → drive link → the drive's inner edge → sidewalk. Their garden path has `Walk` bends, so route it; don't draw a straight line.
- **Butlers** moved from 7.4 m to 6.5 m from the centre line, off the sidewalk lane. `House.butlerPost` holds the pose. The static
  butler is still there for C to swap.
- **Gates:** lanes get samples 3 m either side (the GateLocked edge spans the line). The sidewalk crosses only on the turnstile side, at
  6.855 m (the gap between pillar and post), and the other sidewalk is cut at the block.
  - `Gate.nodeIn` / `nodeOut` are on the inbound lane.
  - The barrier collider stands on the locked lane edges by design (the probes ignore it).
- **Picket panels are 2 m** (5 pickets, `NB_PicketPanel2`), not 4 m: that gives 241 panels, which matches §6.1's count better and
  breaks off a section instead of a fence. District perimeter fences stay one hard 4 m-panel run.
- **Proxy keys:** pack props use `knock/<prefab>`. The 3 stop signs use `recipe/stopsign` (B has no proxy prefab for
  `SM_roadsign_011`). Builder-made props use `recipe/streetsign`, `yardsign`, `parksign`, `signpost`, `cautionsign`, `exitsign`,
  `picket`, `hoop`, `roadworksign`, `hydrant`, `cone`, `sawhorse` or `flamingo`.
  - Hydrants, cones, sawhorses and flamingos are **one GameObject each**: B's `KnockRecipes` parts combined into a mesh asset
    (`Generated/Neighborhood/NB_Knock_<name>`, a submesh per material). That keeps the GameObject budget.
- **Depot pad:** two hand-laid one-way `Pad` lanes link Depot Rd and Minivan Mile, clear of the truck bay, spawn and apron, plus a walk across its corner.
- **Jaywalks** (33) slide up to ±9 m to stay clear of curb stops with their bins and road-works sites, or are dropped.
- **Node districts** come from `Districts.AtPosition`. Loops are Dijkstra over Walk, Crosswalk and ParkPath edges within one district:
  the Pickle Park block and its halves, the Mildly Pleasant outer ring, the Meadows inner and outer rings and cul-de-sac figure,
  the Snootington inner and outer rings, and the Offshore promenade.

### 12.B Package B (knockables) as built (1 Oct 2026)

| File | Contents |
|---|---|
| `WorldLife/Knock/KnockableDirector.cs` | the `Knock` director + the knockable `ITruckSensorClient`: predictive knock, the truck's speed loss, the awake and down caps, settle, re-kick, the cone on the bumper, resets (120 s / 25 m on the host, City Works at 06:00), and F's seams (`PresentRemoteKnock`, `PresentRemoteReset`, `CopyDownBits`, `ResetsFromHost`) |
| `WorldLife/Knock/KnockProxies.cs` | `KnockShape` (how a prop flies, resolved once per knockable), `KnockProxy`, `KnockProxyPool` |
| `WorldLife/Knock/KnockableMeshes.cs` | the asset mesh of each renderer, baked at edit time (static batching hides it at runtime) |
| `WorldLife/Knock/KnockRecipes.cs` | hydrant, cone, sawhorse and flamingo recipes: `Build(name, parent, pos, rot, knockId)` makes the at-rest prop with its `Knockable` |
| `WorldLife/Knock/KnockFx.cs`, `HydrantGeysers.cs` | the juice of §6.1 (`KnockTuning` holds the presentation knobs) and the 20 s geyser |
| `World/StreetLamps.cs` | the `SetLampDown` behaviour: a down lamp is dark, out of the nearest-N sort, has no moths, and flickers back on reset |
| `Editor/Setup_WorldLifeKnock.cs` | materials `mat/worldlife_knock_*`, the proxy prefabs `Prefabs/WorldLife/Knock_<prefab>` (`knock/<prefab>`), the `KnockableMeshes` bake of Neighborhood.unity (saved only on change) with a validation report, **Knock Self Test**, and the play-test kit (`Probe`, `SpawnLampRow`, `SpawnSign`) |

How a prop flies: if `proxyKey` is `knock/<prefab>` and the prefab exists, its proxy prefab is used (pooled per key). Otherwise
B assembles a proxy from the knockable's own renderers. Poses and materials are read live, and meshes come from the bake
(`baked`), or from the renderer itself when it isn't batched (`live`). If neither is there, B uses the code recipe for
`recipe/<name>` (`recipe`), or failing that a plain box (`box`, with a one-time warning). So **package A only has to place
`Knockable`**, with `restColliders` set to primitives. Any `recipe/<name>` works, because the bake copies whatever the builder made.
TMP text such as street names isn't copied: the original text objects ride on the proxy and go home on reset. A can call
`Setup_WorldLifeKnock.BakeScene(scene, out _)` before it saves, and the umbrella runs it anyway.

Decisions taken in B:

- **The 35 %/s cap is a sliding 1 s window** of the speed fractions lost to props, not a refilling bucket. A bucket would let
  70 % through in one second.
- **Debris in the truck's way is batted aside by rule**, not plowed: if the sensor box overlaps a settled proxy, or a slower
  awake one, it gets a fresh launch with no speed loss. A plowed lamp post that settled under the bumper turned into a wall in testing.
  A proxy touching a truck never settles, and below 1.5 m/s a settled proxy wakes so it can be nudged.
- The launch adds a seeded push out of the truck's path, (2 + 0.2 |v|) × 0.6–1.4 m/s. It's cosmetic, and so is the spin
  (signs spin like a propeller).
- **Over the awake cap**, the slowest proxy freezes if it's moving under 1 m/s; otherwise it poofs and the prop stays down until its reset.
- Bags and items in a geyser are launched on the host only, critters on the host, players on their own machine
  (`CritterKnockback`), and debris on every machine.
- The players for the 25 m rule come from `CoopPlayerHooks.Spawned` (every copy, never `PlayerController.Local`). A reset waits
  while a truck is within 6 m of the spot or a player within 1.5 m, so a returning rest collider never shoves anyone.
- Down state isn't saved. A reloaded Neighborhood starts with everything up, and leaving the scene counts as "nobody near".


### 12.C Package C (residents + puppets) as built (1 Oct 2026)

![lineup](WorldLifeRefs/residents_lineup.png)
![poses](WorldLifeRefs/residents_poses.png)

*The cast (18 archetypes, two night walk-outs in pajamas with a flashlight, and the three dog breeds), then the main poses: the
bear hug, a bag in each hand, the set-down, the flop, the starfish, the angry HOA Lady, the butler's cloche, and the witness.
In play: a walk-out ([walkout](WorldLifeRefs/residents_walkout.png)), "Take bag" ([handover](WorldLifeRefs/residents_handover.png)),
the starfish with dizzy stars ([starfish](WorldLifeRefs/residents_starfish.png)), the cloche ([cloche](WorldLifeRefs/residents_cloche.png)),
the neglect witness ([witness](WorldLifeRefs/residents_witness.png)) and a night walk-out ([night](WorldLifeRefs/residents_night.png)).*

| File | Contents |
|---|---|
| `WorldLife/Puppet/PuppetKit.cs` | the primitive meshes (Unity's four, plus a procedural cone and dome), one cached URP material per colour cloned from `mat/worldlife_res_{lit,emissive,unlit,beam}`, the 8 skin tones, and `PuppetDice`, cosmetic dice built on `LifeHash` (C uses no `UnityEngine.Random`) |
| `WorldLife/Puppet/ResidentLook.cs` | `ResidentArchetype` (18, per district), `ResidentStyle`, `StyleFor` (a look from LifeHash), `Compose` (a style → the rig's 28 part slots and its layout; night = pajamas, slippers and a flashlight beam), `BuildDriverHead` (D's windscreen head) |
| `WorldLife/Puppet/PuppetRig.cs` | the universal rig: every slot is built once per pooled puppet and **restyled** on assignment (meshes, shared materials, local poses), the lite LOD, the face (angry tint in 4 shared steps, brows, mouth), googly pupils on springs, blinks, and the pose setters |
| `WorldLife/Puppet/DogLook.cs`, `PuppetFx.cs` | `DogRig` (sausage dog, corgi, poodle; googly eyes; a 2-point LineRenderer leash) and the pooled extras: dizzy stars, a mug or newspaper flying off, Segways, the witness's sign |
| `WorldLife/Residents/ResidentCast.cs` | households and members (the resident ids), archetypes, dogs and looks: a pure function of (graph, Town seed). **D's `LifeRoster` reads it with the same ids** |
| `WorldLife/Residents/ResidentDirector.cs` | the **Puppets** director: the request API (`Show`, `ShowInternal`, `ReleaseInternal`), relevance / pooling / LOD and the caps, the interim person sensor, chatter, the perf stats and section timers, and the dev stand-ins (`StandIns`) |
| `WorldLife/Residents/ResidentAgent.cs`, `.Acts.cs` | the `ILifeBody` person. Drawn position = the driver's request + avoidance (≤ 1.2 m) + a catch-up blend after an interruption; the procedural poses; the acts (flop, nudge, startle, stumble, boop, dodge) |
| `WorldLife/Residents/DogAgent.cs` | the `ILifeBody` dog: heels on the leash, sniffs when the owner stops, barks at raccoons, always leaps clear, runs barking circles round a flattened owner and then sits by them |
| `WorldLife/Residents/WalkOutPresenter.cs`, `HouseRoutes.cs`, `TakeBagInteractable.cs` | the walk-outs (`PickupStop.PutOutPresenter`), door → curb routes (a bounded Dijkstra per house, cached) with `WalkPath`, and the "[E] Take bag" prompt |
| `WorldLife/Residents/ButlerPosts.cs`, `NeglectWitness.cs`, `ResidentLines.cs` | the Bluffs butlers and the cloche, the neglected-pile witness, and what everyone says |
| `WorldLife/Residents/PedestrianFeed.cs` | **every reference C makes to D**: the pop and gossip looks for D's pedestrians, `IsOut` / `Suppress` for the walk-outs, and `CarScenes` (D's `DriverPoppedOut` / `ParkedCarShunted`) |
| `Editor/Setup_WorldLifeResidents.cs` | the umbrella's Residents step (the 4 base materials), **Rubbish/WorldLife/Residents Self Test**, **Render Resident Lineup**, and the play-test `Kit` (`PutOutAt`, `NearestStop`, the `Driver` cruise / sampler) |

**Decisions taken in C (read before E, F or G):**

- **Puppets.** No Animator and no colliders. A full puppet has 18–24 renderers (the 2 brows only while angry or startled), and
  a lite one has 4 (5 with a visor or sweatband). The lite body is a bean that reaches the feet, so the legless lite puppet
  doesn't float.
  - Rigs are universal: at most 24 + 24 are built, then they're restyled, so the pool never allocates after warm-up.
  - At most 3 rigs are built and 8 restyled per frame. A rig keeps its last look, so the same person usually gets it back
    with no restyle.
- **Googly eyes and stars are the puppet's own.** The pupils are springs ticked by the puppet (full LOD only), not 48
  `CritterGooglyEye` MonoBehaviours, and the dizzy stars come from a pool rather than `DizzyStars.Attach`. Same look, no
  per-hit GameObjects.
- **C owns the cast** (`ResidentCast`): 212 residents (82 / 67 / 42 / 21) and 30 dogs with seed 1234.
  - One HOA Lady (Meadows) and one Mr. Pemberton (Snootington).
  - The Snootington staff member is a Housekeeper. Each Bluffs estate has the Billionaire, the Butler and sometimes a Bodyguard.
  - No children anywhere. The yard and playground kids of §3.1 are **not built** (left for G; built later, outside the cast,
    in §12.K).
- **Requests.** Every driver calls `Show` / `ShowInternal` every frame it wants a person drawn. An agent nobody asked for
  retires once its act is over. C's own scenes (walk-out, butler, witness, car scene) own their resident, and D's requests for
  that id are ignored meanwhile.
  - D draws its pedestrians with `ResidentDirector.Show` (slot Schedule, before C). C's feed then adds the door / car pop
    and the gossip look. `Show` takes an optional `pop`, so D can pass it itself.
- **Walk-outs** run on a timeline of real seconds from T = floor(putOutAtHours × 60) / 60:
  - 0.45 s pop out of the door, then the route at 1.1 m/s (two bags = one per hand), then a 1 s set-down. The real bag
    spawns at 0.6 of the set-down.
  - Then a 1.5 s beat (wave at a truck within 30 m, glare at a neglected pile, or stretch), the walk back at 1.3 m/s and a
    0.4 s pop in.
  - A mild act (startle, nudge) **pauses** the timeline, so set-down waits for it. A clock jump past set-down materialises
    the bag at once.
  - Declined (today's drop-in): the curb is more than 120 m from the camera, 16 walk-outs are already running, the route is
    over 45 m, nobody is free, or the record is already past its set-down time.
  - The carrier comes from D's `CarrierPicker` when it's set, else a hashed household member who isn't out (`IsOut`). D is
    told not to draw them meanwhile (`Suppress`).
  - On sleep, every bag still in someone's arms is materialised in its slot.
- **Hand-over** is offered while the resident is still walking to the curb, and only on the host or in solo (a guest's goes
  through M2's `RequestGrab`). It raises `BagHandedOver` and calls `Authority.ClaimHandOver`. **Interim:** while the no-op
  authority decides, C raises the Neighborly fact itself (Progression caps it at 10 a day). E's `ClaimHandOver` replaces this
  automatically.
- **Interim person sensor.** While `WorldLifeRunner.Authority` is the no-op one, `ResidentDirector` (an `ITruckSensorClient`)
  claims every hittable person or dog in the truck sweep (pad 0.35 m, 8-tick re-claim guard per id). That way residents can
  already be run over: cartoon flops, no consequences. It switches itself off the moment E sets the authority, and D's
  `TrafficDirector` does the same for cars.
- **Flop.** It is integrated per frame from the decided launch. Walls clamp the motion (Default, Ground and Walls; never the
  truck). The ground is probed at 16 Hz, and there's one bounce at 0.35.
  - The chase only happens while the hitter's truck is within 15 m when the angry beat ends. A truck that drives on means a
    stomp and "AND STAY OUT!" instead.
  - Rejoin: D's people jog back to the hit point and then catch up with the timetable. C's scenes (a walk-out) walk home from
    where they landed.
- **Butlers.** At runtime each estate's static `Butler` prop has its renderers and its body collider switched off, and they're
  restored on sleep; the butler puppet stands at `House.butlerPost` in its place.
  - The reveal lasts 3.7 s from T: bow 0.8 s, lift the cloche (up and to his right, the bag pops onto the platter), set it
    down (the real bags spawn at 2.5 s), bow.
  - The platter stays level, whatever the butler does.
  - A drop more than 120 m from the camera falls back to the bag appearing. The Bluffs are 263 m wide, so one camera covers
    about half the estates.
- **Witness.** At most 3 at a time, within 60 m of the camera. They leave once it's beyond 75 m.
  - The sign goes on the side of the pile away from the wheelie bin.
  - No witness walks out to a pile a Pile Goblin is already sitting on. One who sees a goblin arrive flees in a panic.
  - A house rests for 45 s before sending out another witness.
- **Chatter**: one line per NPC per 6 s, 3 on screen at once, and only within 45 m. The beats that matter ("OOF!", "Oh!
  Thanks, dear!", the shrug) skip the per-NPC limit. The voice is `Sfx` `mumble` (it falls back to `blip` until Feel adds it),
  pitched per resident.
- **Flashlight beam**: an open cone in a Cull Off transparent unlit material. Its alpha is 0.1 because blending happens in
  linear space, where 0.2 already read as nearly solid.

**As built** (seed 1234, real graph; play tests in a test data root with D's traffic running):

- **Self test:** 15/15 pass.
  - Every non-Bluffs door walks to its curb (73 routes; 10.6–29.9 m, average 18.8), so set-down lands 10.7–28.2 real s after T.
  - 8 butler posts.
  - A 6 m/s flop lands 5.3 m away after 1.08 s in the air.
- **Walk-out at 22 m (maple-18):** the resident was drawn as a full puppet at the door 0.026 s after the put-out (pop 0.97,
  0.00 m from the door).
  - The TrashBag spawned 16.32 s after the put-out, exactly the scripted set-down, 0.08 m from its slot.
  - Over 7,424 frames the town's record count stayed 9..9, this house's 1..1, and the record was never missing.
- **At 201 m (sh-hedgerow-32):** declined (declined-far 0 → 1, no job), and the bag was in its slot on the next sync.
- **Take bag:** the "[E] Take bag" candidate came from the resident's InteractionZone while they walked.
  - `OnInteract` put the same bagId (maple-18/5ac6f744a59c) in Chuck's hands. The record stayed alive, Rep went 0 → 1.00
    (interim Neighborly) and the job switched to Returning.
  - Tossed into the hopper, it paid $4.00 = `PickupPay`, the record was removed and the bag was in the truck.
- **Flattened carrier:** decided `Flatten` (launch (−1.26, 5.30, 5.77)). The bag flew out and came to rest 2.7 m away,
  Resting, not burst, with the same bagId and its record alive. The resident was airborne 1.27 s, landed 6.5 m away, lay
  1.60 s with stars for 1.60 s, then got up.
- **Real run-over** (the interim sensor, truck at 6 m/s into a stand-in in the lane): 1 claim gave `Flatten`.
  - Airborne 1.45–1.47 s, landing 5.4–5.5 m away. Lay 1.60 s with dizzy stars 1.60–1.74 s, got up, angry 1.4 s.
  - With the truck stopped: **chased 3.00 s**, stomped, rejoined, and was back on the spot 6–7 s after getting up.
  - With the truck driving on: gave up at once.
  - A second claim in the same step was rejected (`None`, not hittable mid-act).
- **Two bags:** one job for both records, `CarryTwo`, and 2/2 real bags in their slots at set-down.
- **Night (21:36):** pajamas and a flashlight beam. A raccoon spawned 3 m ahead made the carrier panic: the bag dropped
  0.21 s later, 9.1 m short of the curb and not burst, and they sprinted home.
- **Butler Drop (17:00):** the 4 subscribed estates within 120 m got **exactly 1 reveal each** (+4) and every bag was
  materialised by the butler.
  - The subscribed estate 188 m away and the 3 unsubscribed ones got 0.
  - At 07:00 with the camera at the east end, the one subscribed estate in range got 1.
- **Witness:** started within 1 s of a curb going neglected 16 m away, walked out, and planted "STILL WAITING" beside the
  pile, arms crossed, tapping a foot. Clearing the curb gave "FINALLY!" and they walked home.
  - With a goblin on the pile, they fled. That's how the goblin rule was found: before it, a house kept sending witnesses out.
- **Car scenes:** a parked maple-8 car shunted at 6 m/s by the truck (D's interim sensor) brought its owner out of the front
  door (1 owner scene). A shown timetable car's driver (a RobeDad) got out and shook a fist for 3 s, then got back in.
- **D's pedestrians:** 7 fed and drawn at arrival (D had 20–22 out town-wide). Dogs were on leashes (up to 5 dog rigs).
- **LOD and caps** (70 walking stand-ins 4–86 m around the camera plus D's people, 79–80 agents): **full 24, lite 24**,
  31–32 unshown, and the maximum ever seen was 24 / 24. 48 rigs built (the cap), 614–615 renderers on.
- **Cost:**
  - **Per full puppet 0.0035–0.0036 ms (p99 0.0042), per lite puppet 0.0002 ms** (budgets 0.02 / 0.005).
  - WorldLife.Puppets averages 0.129–0.139 ms (p99 0.19, budget 0.60), and WorldLife.Total 0.17 ms (p99 0.21–0.24).
  - Tick sections: scenes 0.004, D feed 0.017, step 0.019, LOD 0.002, draw 0.082 ms.
  - With a normal street (7 agents): the tick is 0.007 ms.
- **GC:** **0 B over 300 director ticks** with 24 full + 24 lite puppets ("GC Allocated In Frame" deltas; `new object[4]`
  calibrates at 64 B). Allocations happen only at events: a sack visual, a floater, the first restyle of a colour.

### 12.D Package D (schedules + traffic) as built (1 Oct 2026)

| File | Contents |
|---|---|
| `WorldLife/Schedule/LifeRoster.cs` | the timetables' view of the town: households (index ranges), residents (**package C's `ResidentCast`, same ids**), each house's cars (`LifeId.Car`, stopId order then slot) and who drives them, walking speeds |
| `WorldLife/Schedule/LifeRoutines.cs` | the routine templates (§7.1) and trip rules (§7.3) as data: rows per archetype (kind, start window, ‰ per day), walk lengths, speeds, gossip, commute / errand / outing / dawn-shift / late-return chances. Tuned to §4.3 |
| `WorldLife/Schedule/LifePaths.cs` | `LifeAStar` (integer A* + a bounded Dijkstra, ties by node index), `CarPath` (a trip leg's polyline with a closed-form motion profile: speed and time per vertex, constant acceleration between), `WalkPathBuffer` (a day's walking polylines in one reused buffer) |
| `WorldLife/Schedule/LifeSchedule.cs` | `LifeDay` (one day's car trips, resident excursions and their segments, plus the no-alloc queries) and `LifeSim` (roster, route caches, car paths, the day builder, and the queries C and E use: `PickCarrier`, `IsHome`, `CountOnFoot`, `CountMovingCars`) |
| `WorldLife/Traffic/PedestrianDirector.cs` | the **Schedule** director: owns `LifeSim.Active`, rebuilds the day at the day roll or when a district unlocks, tracks who is out on foot, poses the ones near the camera and hands them to C (`ResidentDirector.Show`), and sets C's `WalkOutPresenter.CarrierPicker` |
| `WorldLife/Traffic/TrafficDirector.cs` | the **Traffic** director: car phases, relevance and the 16-shell pool, local driving (following, intersection boxes, backing out, people, players, trucks, honk, overtake, catch-up), static twins, parked-car bodies, shunt / pull-over / honk-back presentation, the boop, an interim truck-vs-car sensor, test cars (`DebugTrip`) |
| `WorldLife/Traffic/CarAgent.cs` | the pooled car shell (pack mesh + the house's tint, kinematic box, a 3-part driver's head, lights by material swap, body springs) and `ParkedCarBody`; both are `ILifeBody` cars |
| `Editor/Setup_WorldLifeTraffic.cs` | the umbrella's step (routes for every car), **Rubbish/WorldLife/Traffic Self Test** (the EditMode suite), and the play-mode `Probe` (lane deviation, same-lane gaps, honks) |

**Time.** Timetables are in *day-ms*: real milliseconds of game clock since in-game midnight (one in-game hour = 50,000 ms), so
walking and driving speeds stay in real m/s. An agent is drawn at its timetable position at (now − host delay); a resident who
drives a car and the car share their delays (each one's `LifeOverrides` delay is added to both), so a flattened commuter's car
waits for them.

**Decisions taken in D:**

- **C owns the cast.** C's `ResidentCast` (households, members, archetypes, dogs, looks, names) is the roster; D reads it with the
  same ids and adds households as index ranges, cars and drivers. Drivers by archetype preference (commuters, soccer moms and
  bodyguards first; the butler never drives).
- **Walks are budgeted door to door** (`LifeRoutines.WalkLength`: dog walks 80–150 m, jogs 240–360 m, strolls 60–140 m, Segway
  220–360 m). The doc's 300–600 m dog walks and 800–1,200 m jogs would last 5–9 in-game hours at one in-game hour per 50 real
  seconds. A walk goes round the nearest block loop when the way there leaves room for it, and otherwise out along the sidewalk
  and back. The way home is budgeted too. Gossip stands 25–50 real seconds; "2–4 minutes" in-game would be 2–3 seconds.
- **Nothing crosses midnight**: every excursion and trip ends by 23:57, so a day roll never cuts anyone mid-walk or mid-drive.
- **Trips**: commute (06:12–09:18 out, 16:18–19:36 back; *the fixer pass changed these to 05:51–08:45 and 16:00–19:12 on a
  trapezoid, with fewer commuters, §12.H*), errand (10:00–15:12, away 0.5–1.5 h), evening outing, dawn shift and late
  return. The street-side car of a two-car driveway leaves first and comes back last. The deeper car only drives inside those
  away windows, so cars never drive through each other.
- **Exits**: each car uses the exit with the shortest out-and-back route the gate mask allows. If no exit is reachable, the car
  stays home that day (Snootington's 14 while it's locked). Routes use Road edges only; driveways are entered and left
  explicitly.
- **Car speeds** (canonical profile): 9 m/s on straights, 4.5 in turns, 5 through a junction box (3 at its entry), 2.5 on
  driveways and the pad, 2.2 / 1.5 reversing, 7 in the tunnel. Acceleration is 2 m/s² and braking 3 m/s². A leg reverses out of
  the slot to a cusp on the lane, 2.5 m upstream of the driveway mouth, then drives forward; the way back ends nose-in at the
  slot. Cars appear and vanish 5 m inside the tunnel.
- **Presentation can only lag the timetable.** A shown car's arc never passes its timetable arc. A blocked car falls behind, catches
  up at up to 1.4×, and rides the timetable exactly whenever it's within 0.3 m and unobstructed. More than 25 s behind (a clock
  jump), it snaps.
- **Following**: a car stops 5 m behind the car ahead, and keeps 5 m + 0.2 s × v while moving. It stops 3 m short of a truck,
  2.5 m short of people and players, and at the line of an intersection box while another shown car is inside a box whose path
  comes within 3 m of its own. A car backing out waits on the driveway while a shown car is coming along the lane. A car
  doesn't pop in on top of another shown car.
- **Stuck behind a truck**: it honks 2.6 s after stopping (§7.3 says "after 3 s"; the acceptance asks for "within 3 s"). On a
  straight lane, with the other lane clear for 30 m past the truck, it then overtakes: it swings out over about its gap plus
  2.5 m, passes, and merges back over 11 m. Oncoming cars wait for an overtaker. A truck that blocks both lanes gets a honk every
  6 s. *(Fixer pass, §12.H: the swing is now planned against the truck's box and only goes as far as it must, and after 5 s a
  stuck car asks the oncoming lane to hold back.)*
- **The boop**: on the player's own machine (`IsOwner`), a moving car's box or stopping distance reaching a player on foot gives a
  0.4 s `CritterKnockback` stumble, a "BOOP!", a honk and 5 s immunity. Nothing is dropped.
- **Hits are presented here; E decides them.** Shown cars (`CarAgent`) and parked cars (`ParkedCarBody`, registered while home)
  are `ILifeBody` cars. The presented hits are `Shunt` (yaw kick, 0.3 m hop, slide, crooked tilt, a 6 s alarm with flashing
  lights, the driver out after 0.5 s), `PullOver` (to the kerb for 3 s) and `Startle` (honks back). A shunted car's collider is
  off for 1 s, and stays off until no truck overlaps it. A shunted parked car swaps its static twin for a shell. The shell stays
  crooked until the car next leaves, or the camera has been more than 120 m away for 10 s.
- **Interim sensor**: until E sets the real authority, `TrafficDirector` (an `ITruckSensorClient`) claims truck-vs-car overlaps,
  like C's interim sensor does for people. The truck gets the §8.3 bump: −35 % speed, a 6°/s yaw kick, a shake and "CRUNCH!". It
  switches off once E's authority is in, so E owns that bump from then on.
- **Lights** by material swap: by day the pack's emissive headlight material swaps to an unlit copy, and the backlights swap to a
  bright copy for braking (flashing while yielding or for the alarm). There are no realtime lights. *(Fixer pass: parked cars, the
  static twins and a shunted parked car's shell, now have every light off; §12.H.)*
- **LOD and density**: full detail within 35 m (springs, driver), lite within 90 m, and beyond that updates at 15 Hz. Cars show up
  to 160 m away, and hide at 170 m (hysteresis). `WorldLifeTuning.Density` cuts shown cars by a hash, never their timetables.

**Seams.**

- **C**: D calls `ResidentDirector.Show(id, pos, yaw, pose, 0, withDog)` every frame for each resident out on foot within 170 m.
  Poses are Walk / Jog / Segway / Stand / Talk. D also sets `WalkOutPresenter.CarrierPicker` to someone home over T plus 45 real
  seconds (−1 = nobody, so the bag drops in). `PedestrianDirector.Out`, `Appeared` / `Left`, `Find` and `Suppress` are there
  for anything else. `TrafficDirector.DriverPoppedOut` and `ParkedCarShunted` are for full puppets; without a listener, the lines
  float.
- **E**: car bodies are in `LifeBodies`. Write `LifeOverrides` with car or resident ids. The truck side of a shunt is E's; turn
  `TrafficDirector.InterimSensor` off, or just set the authority.
- **F**: everything is data from the Town seed (`TownSeedProvider`) and the unlocked-district mask.

**As built** (seed 1234, real graph):

- 212 residents, 86 cars and 30 dogs.
- A day builds in 1.5 ms. The first build at wake is 17 ms, which includes every car's routes and paths.
- The §4.3 table holds over 20 days, and the self-test reports the table.
- At 07:30 in play (mask 1): 6–13 moving cars town-wide (average 10.1 over 20 days), and 7–14 people on foot in Mildly
  Pleasant (average 11.5). The live sample at 07:32 had 11 and 14. At 02:00: 0 and 0.
- A 5-minute rush-hour run (06:49 → 12:42 in-game, a 3-car test convoy plus the real traffic): 3,191 lane samples, maximum
  deviation 0.000 m; 362 same-lane pairs, minimum bumper gap 6.38 m.
- A car stuck behind a parked truck stops 2.7 m short and honks 2.6 s after stopping. It swings out at 3.1 s, is past the
  truck's nose at 6.1 s and is back in its lane at 8.1 s.
- Cost: WorldLife.Cars averages 0.011 ms in normal traffic and 0.097 ms (p99 0.108) with 15 cars shown in a queue.
  WorldLife.Schedule averages 0.008 ms. The two directors allocate 0 B over 500 ticks (`ProfilerRecorder` "GC Allocated In
  Frame").

### 12.E Package E (rules + consequences) as built (1 Oct 2026)

![chase](WorldLifeRefs/rules_chase.png)
![letter](WorldLifeRefs/rules_letter.png)

*Toni (a Robe Dad) chasing the truck that flattened him, and the Head Office memo for the next flatten (Sal, 56 Maple St,
lying with dizzy stars behind the truck). In play: the starfish ([starfish](WorldLifeRefs/rules_starfish.png)), the horn (a car
pulled over to the kerb and the pedestrians stepping off the truck's line; [horn](WorldLifeRefs/rules_horn.png)), and a bag
bonking off a jogger's head ([bonk](WorldLifeRefs/rules_bonk.png)).*

| File | Contents |
|---|---|
| `WorldLife/Rules/WorldLifeAuthority.cs` | the real `IWorldLifeAuthority`: `Decide` (the §8 rules + the host's immunity memory) → `Present` (`PlayHit` + `HitDecided`) → `Commit` (host: the `NpcOverride` and the consequences). Also `ClaimHorn`, `ClaimBump`, `ClaimThrownHit`, `ClaimKnock` (City Works tally) and `ClaimHandOver` (neighborly Rep). `ClaimNpcHit(claim, seen)` lets the local sensor decide on the body it saw; `ActorSlotOf` is F's seam |
| `WorldLife/Rules/TruckLifeSensor.cs` | the people / dogs / cars `ITruckSensorClient` on the runner's per-truck sweep, plus the §8.3 truck side of a shunt |
| `WorldLife/Rules/NeighborConsequences.cs` | `Price` (fine × escalation, pure), `Apply` (host: `Ledger.Charge`, the Rep fact, the grudge, the letter), names and addresses for receipts, and the Town toggle (`SetFines`) |
| `WorldLife/Rules/NeighborGrudge.cs` | the grudge stamps, the `CurbTrash.ExtraAgeHours` hook and "Apology accepted" (`ProgressionEvents.CurbCleared`) |
| `WorldLife/Rules/CityWorksInvoice.cs` | the per-district daily tally (capped) and the one invoice per district at midnight |
| `WorldLife/Rules/NeighborLetters.cs` | the letters by district voice, the "insurance" letter, the morning noise letter, the City Works invoice letter, the one-pop-up-an-hour limit |
| `WorldLife/Rules/HonkReactions.cs` | the horn scatter (`Classify` is the pure §8.5 geometry) and `NightNoise` (complaints, the shout from the door, the morning letter) |
| `WorldLife/Rules/PlayerContacts.cs` | §8.4 on this machine: a player bumping people, and things this machine's player throws at them |
| `WorldLife/Rules/RulesDirector.cs`, `RulesBooks.cs`, `RulesTuning.cs` | the Rules director (boot, wake, horn, contacts, grudge glares, the hourly books), the books interface (`GameBooks` = GameSession / Ledger / ProgressionEvents / HeadOffice; `MemoryBooks` for tests) and E's own knobs |
| `Editor/Setup_WorldLifeRules.cs` | the umbrella's Rules step (no assets: it runs the self test), **Rubbish/WorldLife/Rules Self Test**, **Rubbish/WorldLife/Neighbor Fines: Toggle (play mode)**, and the play-test `Kit` (`People`, `ResidentOf`, `StopOf`, `Honk`, `StartLedgerLog`) |

**Decisions taken in E (read before F or G):**

- **The authority boots with the game** (BeforeSceneLoad), so C's and D's interim sensors are off from the first frame (C's
  `InterimClaims` stayed 0 in play). The sensor is a sweep client, as W0 decided, not a per-truck component: it runs for every
  truck this machine simulates, with no prefab or `TruckControllerV2` edit.
- **A re-hit inside the immunity is a cartoon dodge, not nothing.** A flattened NPC is immune for 12 s, and the same driver
  can't flatten them again for 20 s. A claim inside either is decided `Dodge` ("Ha! Missed me!"): no fine, no Rep, no flop and
  no override. C's own body immunity (not hittable mid-act and for 2 s after rejoining) usually means no claim is made at all.
- **Dogs always leap.** Rep −1 for a dog near miss only at ≥ 2.5 m/s (a real near miss), at most once per dog per 20 s.
  A dog walker in the lane at 6 m/s: the owner is flattened, the dog leaps, and both are booked (−4 and −1).
- **Fines are booked in the district where it happened** (`Districts.AtPosition` of the hit), as §9.1 says. The escalation
  counter is shared by flattens and shunts there. Fines are rounded to cents, and the receipt is short enough for the cash
  ticker ("Flattened Gary", "Shunted Deb's car").
- **The Town toggle** is `NeighborConsequences.SetFines` (the company counter `wl_fines_off` until F points
  `NeighborFinesProvider` at `TownRules.NeighborFines`). Off = receipt shame: "Fine waived (Town rules)" floats over the spot,
  the letter says "We will remember, though", and Rep, the grudge, the letter and the flop still happen. City Works ignores it
  (§8.6).
- **Grudges** last until the next midnight and change pay only (W0). A stamp more than 24 h ahead is ignored, which covers a clock
  that restarted with a new session. Clearing the curb forgives (the last bag still pays the grudge price). A grudged household
  glares at a truck within 18 m (arms crossed, one "Hmph." per encounter) instead of waving.
- **City Works** is tallied in half-dollar units on W0's packed day counters (the Bluffs cap is 400 units, inside the 10-bit
  count). It is billed on the first in-game hour of a new day, in any scene of a running session (not the menus), as one
  `city_works` charge and one letter per district. A tally from an earlier session is billed on its next hour.
- **Noise complaints**: the night runs noon to noon (Progression's formula). E keeps the same 3-a-night cap so its count and the
  morning letter agree with the Rep, and the shout over the nearest door happens even when the night is already capped.
- **Letters** go through `HeadOffice.Send`. Only the first of E's letters in an in-game hour pops up and the rest go to the inbox
  (Progression's own letters pop up separately). Letters, receipts and the Rep facts are written on the host only.
- **Player contacts**: a walking bump is a "pardon me" line (C's avoidance already sidesteps). A sprinting one (≥ 4.5 m/s) is a
  stumble, plus a brown spill if they hold a mug or tumbler. A thrown thing that reaches a person is decided by rule: the
  puppets have no colliders, so the bag bounces back off their head at 22 % of its speed and never bursts on them.
- **Guests** (until F): their hits are local cartoons with nothing booked, as §10.3 says. Overrides are written on the host
  only.
- **The horn's radius is measured from the truck's body box** (fixer pass, §12.H), not its centre: 12 m reaches 12 m past the
  nose and 12 m to the side, and "within 6 m in any direction" is 6 m from the bodywork. The play test below predates it.

**As built** (seed 1234, real graph, play tests in a test data root with C's and D's systems running):

- **Self test** (edit mode, `MemoryBooks`, fake bodies): **36/36 pass**.
  - Fines $3 / $12 / $40 / $120 and $5 / $20 / $60 / $200. Escalation in Mildly Pleasant over n = 1..11:
    $3, 3.75, 4.5, 5.25, 6, 6.75, 7.5, 8.25, 9, 9, 9.
  - Speeds: 0.5 m/s does nothing, 1.5 m/s is a 0.6 m nudge, and 6 m/s flattens with launch (2.72, 5.30, 3.30), the §8.1 formula.
  - Immunity: same driver at +5 s gives a dodge. Another driver at +13 s gives a flatten (×1.25), and the first driver at +26 s
    another flatten (×1.5).
  - Cars: 2 m/s nothing, 7 m/s a $20 Meadows shunt with a 1.05 m slide. Again at once: nothing. After 10.5 s: ×1.25.
  - City Works: 30 lamps make $5 (the cap), a Bluffs hydrant $60. Midnight posts 3 invoices ($70 total), each once.
  - Night honks: 5 → 3 complaints. One morning letter.
  - The horn geometry, thrown boop / stumble / short flop, bumps, a hand-over, and a guest's hit booking nothing.
  - When two bodies share an id (D's car while it leaves or reaches its driveway), the sensor's claim acts on the body it saw.
  - 0 B for 2,000 empty sweeps and 2,000 decisions on an immune target.
- **Flatten at 6 m/s** (Steve, a Curler Gran of 18 Birch St, standing in the Birch St lane, the first incident of the day):
  airborne 1.47 s, landed 5.44 m from the hit point, lay 1.60 s with dizzy stars for 1.60 s, got up, angry 1.4 s.
  - The ledger shows exactly **one `neighbor_damages` −$3.00 "Flattened Steve" (mildly_pleasant)** and **Rep −4.00**.
  - The override is +10.0 s. birch-18 has a grudge until 24.00. One letter, "Dear Garbage Person … — Steve, 18 Birch St", popped up.
- **With the truck stopped next to them** (Toni, the second flatten of the day): **chased 3.00 s**, gave up with a stomp
  and was back on their spot 1.1 s later. −$3.75 (×1.25), Rep −4.
- **Second hit within 12 s** (Linda, 28 Birch St): the truck drove on, she rejoined, and the truck came back as a different
  driver slot, so only the 12 s NPC immunity applied. The re-hit **9.7 s after the flatten was decided `Dodge`**, and the ledger
  kept her one charge. With the same driver at 18.4 s: two dodges, nothing booked.
- **Grudge pay**: a 4.44 h bag at grudged birch-19 paid **$3.46 (as 5.44 h old) instead of $3.84**. It was the curb's last
  bag, so "Apology accepted" followed and the grudge was cleared (forgiven 0 → 1).
- **Parked car shunt at 6 m/s** (Deb's car, 49 Maple St): decided `Shunt`. The truck went **5.90 → 3.83 m/s (−35 %)**, D played
  the shunt, maple-49 got a grudge, there was an "Insurance claim" letter, and Rep −3.
- **Horn at radius 12 m** (air horn level 0):
  - **8/8 people as §8.5 says.** Five ahead at 5.0–11.4 m and one 5.5 m behind startled and stepped 1.4–1.7 m off the line; one
    9.5 m behind and one 14.5 m ahead didn't.
  - A **car 10.8 m ahead in the truck's lane pulled over** (lateral 0 → 1.40 m) for 3.0 s, then drove on. An oncoming car honked
    back.
- **Night honks at 23:00**, 18 m from a door: 6 honks gave **3 complaints** (Rep −1 each) and 3 over the cap. At 06:05 one
  letter came: "Last night your horn woke Mildly Pleasant Acres (3 complaints)."
- **City Works**: 16 Mildly Pleasant props and a Meadows hydrant were knocked through B's sensor path at 23:06. At midnight:
  **`city_works` −$5.00 (mildly_pleasant, the cap; 16 things) and −$8.00 (minivan_meadows)**, one letter each.
- **Thrown bag** (a full-charge throw, 14 m/s, 2.7 m away): a short flop with a 1.0 s lie and Rep −1. The bag came back intact,
  same record, at rest by the thrower.
- **Sprint bump** (5.33 m/s into a resident): one stumble.
- **Take bag** now goes through E: +1 Rep `neighborly`.
- **Fines off**: a flatten left the cash unchanged and booked Rep −4, a grudge and a letter saying "Fine waived (Town rules)".
- **Cost at the 07:30 rush** (24 full + 24 lite puppets, 70 stand-ins plus D's traffic, the truck cruising and honking):
  **WorldLife.Total avg 0.25 ms, p99 0.30** (budget 1.1 / 2.0).
  - WorldLife.Rules avg 0.002 ms. WorldLife.Sensor (B's and E's clients) avg 0.005 ms, p99 0.026 (budget 0.05).
  - 300 × (Rules tick + glare pass + fixed tick + a sweep with 8 bodies in it, 2,400 claims) allocated **0 B**, measured as
    "GC Allocated In Frame" deltas (`new object[4]` = 64 B).

### 12.G Package G (polish, perf, integration) as built (1 Oct 2026)

![flop](WorldLifeRefs/g_flop_air.png)
![lamp](WorldLifeRefs/g_knock_lamp.png)

*A commuter flattened at 6 m/s on Elm Ave, and a lamp post going over on Birch St with its sparks. More from the integration
play test: the [starfish](WorldLifeRefs/g_flop_starfish.png), the [angry beat](WorldLifeRefs/g_flop_angry.png) and the
[chase](WorldLifeRefs/g_flop_chase.png); the [hydrant geyser](WorldLifeRefs/g_knock_hydrant.png) and a
[wheelie bin](WorldLifeRefs/g_knock_bin.png); a [two-bag walk-out](WorldLifeRefs/g_walkout.png); a grudged household
[glaring at the truck](WorldLifeRefs/g_grudge_glare.png); a car [queued behind the parked truck](WorldLifeRefs/g_queue.png) while
another overtakes; an [evening arrival](WorldLifeRefs/g_evening_arrival.png); a parked car
[shoved aside](WorldLifeRefs/g_shunt_aside.png) and a moving one [rear-ended](WorldLifeRefs/g_rearend.png). Game-view captures
with the HUD (TEST DATA ROOT watermark): the [flop and "MY COUPONS!"](WorldLifeRefs/g_flop_hud.png), the
[letter](WorldLifeRefs/g_letter_hud.png), the [horn](WorldLifeRefs/g_honk.png), the [rush-hour drive](WorldLifeRefs/g_rush_drive.png).*

| File | Contents |
|---|---|
| `Feel/FeelSfxSynth.cs`, `Feel/FeelSfxPlayer.cs`, `Core/FeelApi.cs` (Feel, additive) | recipes for the 11 §11 ids plus two more mumble takes (`mumble2`, `mumble3`); `car_horn` / `car_alarm` stay in tune; 3D distances for the new ids; the API comment lists them |
| `WorldLife/WorldLifeTuning.cs` | the tuning pass (below) |
| `WorldLife/Residents/ResidentLines.cs`, `WorldLife/Rules/NeighborLetters.cs` | the chatter and the letters (below) |
| `Editor/Setup_WorldLifePolish.cs` | the umbrella's last step (**Rubbish/Setup/WorldLife Polish**, no assets: it runs the G self test) and G's play-test kit: `Autopilot` (drives the truck along lane routes or any polyline with a throttle that keeps the speed knocks and shunts take off), `LaneRoute`, `Snap` (photographs a flop phase by phase and the first knocks), `Shot`, and `MeterStart` / `MeterReport` (main thread, physics, GC and every `WorldLife.*` marker) |

**Sound.** Every WorldLife Sfx id now has a Feel synth recipe, so `KnockSfx` never falls back: `clang` (strike, thump, an
inharmonic hollow-pole ring), `sparks` (zap, crackle, hum), `geyser` (1.8 s of rushing water, re-fired every 1.6 s), `car_horn`
(a thin "meep meep", pitched per car), `car_alarm` (one 0.44 s whoop, fired on a 0.45 s beat at two pitches = wee-woo),
`tire_screech`, `door_creak`, `oof` (in the resident's own voice pitch), `yelp` and `bark` (pitched per dog) and the
Animal-Crossing `mumble` in three takes (picked by the cosmetic dice; louder and a touch higher when yelling). New uses: tyres
squeal when a car panic-stops for a truck, a person or a player, and every shunt skids before the alarm starts. Feel prewarms
every clip at boot, one per frame (the longest, `geyser`, renders in 19 ms).

**Lines.** More takes per beat (airborne "MY COUPONS!", lying "Is it Tuesday?", startled "MY HEART!"...), the driver and
car-owner lines by district (a Bluffs limo is "MY LIMO!", not "MY MINIVAN!"), the honk lines, and the grudge glare ("Oh. It's
YOU.", "We remember.") all live in `ResidentLines`; every line is checked to be ≤ 48 characters. Letters have three takes per
district voice; every take names the neighbour, says what happened and carries the fine.

**Tuning.** Picket panels are A's 2 m panels, so they weigh 6 kg and take 2 % of the truck's speed (the doc's 4 m row was
12 kg / 4 %). Everything else stayed: the fines are the lead's "small fines on" defaults, and the measured costs below sit far
inside every §4.4 budget, so no density or LOD knob had to move.

**Integration fixes (found in the play test):**

- **Car bodies shared an id.** D registered a car's moving shell before its parked body unregistered ("Two bodies share id
  Car#N" in every session). The parked body now hands over first, and `LifeBodies.Unregister` makes an older body with the same
  id findable again. No such warning in play since.
- **A shunted car ended up under the truck.** The §8.3 slide (≤ 2 m along the truck's velocity) left a car that was hit
  square-on (rear-ended in its lane, nosed into in its driveway) in the truck's footprint, and with its collider off the truck
  drove straight through it. `TrafficDirector.ShoveAside` adds a sideways push, toward the side of the truck's line the car is
  already on, just far enough to clear the truck (≤ 3 m).
- **Teardown.** On play-mode exit the scene's objects can be destroyed before the runner sleeps: B's geysers and C's puppet
  effects threw `MissingReferenceException`, and a resident still carrying a bag materialised it into the next scene (a curb
  bag at Neighborhood coordinates in the garage) or leaked it past play mode ("Some objects were not cleaned up"). The runner
  now knows when the anchor's scene is leaving (`WorldLifeRunner.Leaving`: another scene became active, quitting, or the
  presentation root already gone); walk-outs and butler reveals don't materialise then (the record is data, so the bag shows
  next time), the effects are null-checked, and a destroyed object met while leaving is logged, not an error.
- **Grudged walk-outs wave.** E's glare only reached people standing near the truck. `WalkOutPresenter.Grudged` (set by E's
  Rules boot to `NeighborGrudge.Has`) makes a grudged house's walk-out beat a glare at the truck instead of a wave.
- **The W0 GC check never measured.** `GC.GetAllocatedBytesForCurrentThread` reads 0 in this editor, so W0's "0 B" could never
  fail. `Setup_WorldLife.AllocBytes` reads calibrated "GC Allocated In Frame" deltas. The real numbers: LifeBodies allocates
  576 B while a 200-body crowd's cell lists grow, then 0 B in every later 1,000-frame window.

**Integration run** (1 Oct, under the editor lock): Items → Pickup → Feel → GarageFlow → SortingStation → Ebae → Neighborhood →
WorldLife, plus GarageUpgrades after SortingStation (its log asks for it).

- `StreetGraph.asset` and `HouseDirectory.asset` were byte-identical after the Neighborhood rebuild.
- The Graph check gave `RESULT: OK`: 1,777 nodes, 759 knockables, 89 household cars, 0 probe failures.
- Self tests: W0, Knock, Residents 15/15, Traffic, Rules 36/36 and G 11/11 all pass.
- `Setup_SortingStation` dirtied Bootstrap. It was reloaded from disk, not saved.

**Play test** (test data root, Play Solo, the truck on G's autopilot, real D traffic and C residents):

- **Morning rush (07:30).**
  - A 465 m drive round the Mildly Pleasant block at 7 m/s: 10–15 people on foot in the district, and 15–26 cars moving
    town-wide (≤ 12 shown).
  - A two-bag walk-out at 8 Birch St: the resident set both bags in the slot and waved at the truck.
- **Rampage** along Birch St's north verge at 8 m/s, about 17 knocks a run (lamps, mailboxes, bins, a hydrant geyser).
  - The sidewalk residents were flattened and their dogs leapt clear. Fines escalated $3 + 3.75 + 4.5 + 5.25.
  - 16 of 16 props were back within 130 s once no player was within 25 m.
  - Another day's City Works at 06:00 put 16 back, and the invoice letter said $5.00 (capped).
- **Run-over at 6 m/s** (Mr. Kowalski, 28 Birch St, first incident of the day).
  - Airborne 1.47 s, landing 5.4 m away. Lay 1.60 s with stars for 1.60 s, got up, angry 1.4 s, chased 3.00 s, then rejoined.
  - The ledger shows exactly one `neighbor_damages` −$3.00 "Flattened Mr. Kowalski" and Rep −4.
  - A grudge on birch-28 until midnight, a +10 s timetable override, and the "Dear Garbage Person … a very important briefcase"
    letter popped up.
- **Horn:** the people inside the 12 m radius startled ("AAH!", "MY HEART!") and an oncoming car honked back.
- **Queue:** with the truck parked in the Elm Ave lane during the rush, cars queued behind it and honked every 6 s.
  Overtakes went 1 → 4 while the oncoming lane allowed; one car waited 22 s behind a stream of oncoming traffic.
- **Evening (18:00):** 14–18 cars inbound, parking nose-in with brake lights at dusk.
- **Cost:**
  - **WorldLife.Total avg 0.10–0.12 ms, p99 0.14–0.17 ms** in every window (budget 1.1 / 2.0). That covers the rush drive, the
    queue at the rush, and a stress run of 70 stand-ins plus the truck honking through them (24 full + 24 lite puppets, 48 rigs).
  - Puppets ≤ 0.066 ms, Cars ≤ 0.062 ms, Sensor p99 0.019 ms.
  - The runner's FixedUpdate + Update + LateUpdate with every director in the live scene allocated **0 B** over 300 frames,
    twice.
- **Console:** no errors from WorldLife in four play sessions, apart from the harness's eval timeouts.

**Not built in G:** the yard and playground kids of §3.1. They need a non-hittable puppet path in C (residents register as
bodies) and safe places behind hard fences from A. F (net) waits for M1, as planned. *(The kids were built after the fixer
pass: §12.K.)*

### 12.H Fixer pass (after G) as built (1 Oct 2026)

![courtesy](WorldLifeRefs/fix_courtesy_hold.png)
![swing](WorldLifeRefs/fix_overtake_swing.png)

*A car stuck behind the parked truck while oncoming cars hold back beyond its corridor, then its (gentler) swing round the
truck. Also: a car [batting a fallen lamp post](WorldLifeRefs/fix_debris_bat.png) off the road ([after](WorldLifeRefs/fix_debris_after.png));
parked cars at night [before](WorldLifeRefs/fix_parked_night_before.png) and [after](WorldLifeRefs/fix_parked_night_after.png)
(tail lights off); a parked car's owner [waiting indoors](WorldLifeRefs/fix_owner_waits.png) while the truck rolls up the driveway,
then [shaking a fist by the shoved car](WorldLifeRefs/fix_owner_fist.png); a rear-ended car that
[stays shoved aside](WorldLifeRefs/fix_shoved_stays.png) beside the truck. All camera renders (no watermark).*

| File | Change |
|---|---|
| `WorldLife/Schedule/LifeRoutines.cs`, `LifeSchedule.cs` | commutes on a trapezoid (`Trap`, flat with 15 % ramps) over 05:51–08:45 out and 16:00–19:12 back, instead of a triangle; commute chance 360 / 400 / 320 / 300 ‰ (+140 for two-car houses; was 500 / 560 / 450 / 400 + 200); outings 170 ‰; a few walk rows trimmed so the people cells still hold |
| `WorldLife/Traffic/TrafficDirector.cs`, `CarAgent.cs` | `OvertakePlan` (the swing goes only as far as the truck needs, over gap + 3.5 m, and the line is checked against the truck's box); courtesy (`CourtesyGate`); cars bat fallen props aside; parked cars' lights off; the driver waits while the truck rolls past (`TruckRollingNear`); a shoved car doesn't slide back into the truck (`TruckOverlapsAt`); unique test-car ids |
| `WorldLife/Knock/KnockableDirector.cs`, `KnockFx.cs` | `BatForCar` (+ `CarBats`, `KnockTuning.CarBatSlack`): a car's box against the down list, re-kick by the truck's rule |
| `WorldLife/Residents/PedestrianFeed.cs` | a parked car's owner waits indoors while a truck is still rolling up (≤ 6 s), then storms out to where the car is *now*, never inside a truck's box |
| `WorldLife/Rules/HonkReactions.cs` | the horn radius is measured from the truck's body box (`Classify(…, boxHalf)`) |
| `Editor/Setup_WorldLifeTraffic.cs`, `Setup_WorldLifeRules.cs`, `Setup_WorldLifePolish.cs` | a rush-plateau check in D's self test; E's horn geometry check on the box; the meter's physics marker is `Physics.Simulate` (`Physics.Processing` doesn't exist here, so G's meter read n/a) |

**What changed, and the numbers** (seed 1234; play in a test data root, Play Solo, the truck on G's autopilot; test cars from D's
`DebugTrip`):

- **Rush traffic is a plateau.** The commute shape was a triangle peaking at 07:45, and a trip lasts ~64 real s (≈ 1.3 in-game h),
  so 35 commuters stacked into 22–26 moving cars at 08:00 (G) against §4.3's 8–12.
  - 20-day timetable, all districts unlocked: the worst 10-minute average is **13.3 moving cars (06–09) and 12.9 (16–20)**, from
    22.3 and 21.2. Day peaks: median 16 (was 25), max 23 (was 32).
  - Starter district only (mask 1): worst 10-minute average 10.5 (was 18.3); day peaks median 13, max 18 (were 21 / 27).
  - Live, mask 1: **6–12 cars moving town-wide from 07:39 to 08:47** (day 1), and 13–14 from 07:22 to 08:13 on day 0, the
    busiest of the 20 days.
  - §4.3 still holds on every cell (0 out). D's self test now also checks the plateau (≤ 15 = 12 + 25 %).
- **Stuck behind the truck.** With the other lane clear: the car stopped behind it, honked **2.56 s** later, swung out at 3.0 s
  and was past the nose **5.9 s** after stopping. With an oncoming test car every 2.5 s for 30 s (D's rule alone waits for the stream to
  end): after 5 s the stuck car asked, up to 5 oncoming cars held back beyond its corridor, it swung out **8.5 s** after
  stopping and was past the truck at **11.5 s**. A car that has held for 12 s ignores new asks, so the lanes take turns.
- **The swing.** Max yaw **33°** (D measured ~50°): 2.95 m into the other lane over 6.4 m for a truck in the middle of the lane. The car's
  box never touched the truck's (0 frames) and the truck didn't move (0.00 m).
- **Debris.** A lamp post, a picket panel and a flamingo knocked into the road: the first test car **batted the lamp off the
  lane** (3 bats over 4 cars) and slowed to 5.9 m/s for the 60 kg pole. `BatForCar`: 0 B, 0.26–0.42 µs a call
  (16 cars ≤ 0.007 ms a frame).
- **Parked cars' lights**: 89 static twins switched to the unlit head- and tail-light copies (0 still lit); the shunted shells too.
- **Fairness.** A parked car at maple-46 shunted at ~6 m/s (4.4 m/s after the hit) while the truck kept rolling up the driveway:
  the owner **waited 1.2 s**
  indoors, came out as the truck stopped (0.8 m/s), went to the shoved car **2.9 m clear of the truck's box**, flattens **0**.
  At maple-56 (a longer roll) the owner waited 3.3 s. A rear-ended moving car: the driver **got out 1.9 s after the shunt**
  (was 0.5 s), once the truck was down to 1.5 m/s; flattens 0. That shoved car then **stayed beside the truck** (2.6 m aside, 0
  overlap) and honked every 6 s instead of sliding back into its bodywork.
- **Horn** (radius 12 m, now from the box): 11 m past the nose startled (14.8 m from the pivot, out of range before), 13.5 m past
  didn't; 5.5 m behind the rear and 4 m beside startled, 8 m behind didn't.
- **Cost** (07:24–08:47 drive, 3,259 frames): **WorldLife.Total avg 0.091 ms, p99 0.140** (budget 1.1 / 2.0); Cars 0.040 / 0.062;
  Puppets 0.016 / 0.033; **Physics.Simulate avg 0.019 ms, p99 0.129** (budget 0.4; G couldn't measure it). One 4.8 ms frame
  (WorldLife.Sensor 4.3 ms) when the truck rear-ended a car on the route, most likely the shunt's one-off work (ledger, letter,
  first shell); p99 is untouched. The runner with every director allocated
  **0 B** over 300 frames, twice.
- **Self tests**: W0, Knock, Residents 15/15, Traffic (with the plateau check), Rules 36/36, G 11/11 all pass; the Graph check is
  `RESULT: OK`. `coop_lint`: OK, 0 new hits, 0 allows. No scene or asset was rebuilt: nothing here changes the builder.
- **Console** over two play sessions: no WorldLife errors or warnings (the test-car "Two bodies share id" warnings are gone);
  only the harness's eval timeouts.

**Still open** (not code bugs; decisions or limits):

- **Kids** (§3.1) weren't built here. Nick then said "Yes, add them": built since in §12.K (a fenced playground, fenced front-yard
  pens and a non-hittable kid director).
- Day-to-day spread remains: some days peak at 18–23 moving cars town-wide for a few minutes with every district unlocked
  (the averages hold). `LifeRoutines.CommuteChance` is the knob.
- Lemonade stands (3 vs ≈ 6), cones (47 vs ≈ 60), the 2 parked-only corner lots, the 2 m picket panels, the static household cars
  and the cleared exit corridors stay as A built them (byte stability; the counts are estimates).
- The SFX are synthesized and still unheard by any agent (WAVs in G's scratchpad). F (net) waits for M1.
- The whole game allocates ~16 KB a frame in editor play; WorldLife's share is 0 B.

### 12.K Kids in fenced yards and the playground, as built (2 Oct 2026)

**Nick (verbatim):** "Yes, add them" (the optional cheering kids of §3.1), and "doesnt have to be perfect".

![fence](WorldLifeRefs/kids_playground_fence.png)
![cheer](WorldLifeRefs/kids_depot_cheer.png)

*The truck rammed into the playground's east fence and stopped there, with three kids cheering right behind the pickets. Below
that, the Depot Rd yard pen on a honk: Theo holds up "HI TRUCK!" and Mia (cape, balloon) jumps. More: the game view of both
honks with the HUD ([playground](WorldLifeRefs/kids_playground_honk_game.png), "I'M GONNA BE A TRASH MAN!";
[Depot Rd](WorldLifeRefs/kids_depot_honk_game.png), "HE HONKED AT US!!"), the [side view](WorldLifeRefs/kids_playground_side.png),
and the edit-mode renders: the [kid lineup](WorldLifeRefs/kids_lineup.png), the [playground](WorldLifeRefs/kids_playground_preview.png)
and a [yard pen](WorldLifeRefs/kids_yard_preview.png).*

| File | Contents |
|---|---|
| `WorldLife/Kids/KidPen.cs` | the scene placement data of one pen (id, household, district, kids, fence rectangle, run-loop inset, home spots, sign) with its frame maths (run loop by arc length, nearest loop point, distance to the fence) and the `All` registry |
| `WorldLife/Kids/KidLook.cs` | `KidStyle`, `KidLook.StyleFor` (looks from LifeHash) and `Compose` (kid proportions on the residents' universal `PuppetRig`), the cardboard sign texts, and `KidLines` |
| `WorldLife/Kids/KidSchedule.cs` | the pure timetable: when each kid is outside |
| `WorldLife/Kids/KidDirector.cs` | the **KidDirector** (slot Puppets): roster, timetable, relevance, waving and following, the horn's jump-cheer, the daily fan moment (`ClaimFan`), the safety net, its own rig pool and LOD, signs, chatter, perf stats; and `KidAgent` |
| `WorldLife/WorldLifeTypes.cs`, `WorldLifeTuning.cs` | `LifeKind.Kid` (a hash stream only, never a body id) and `WorldLifeTuning.Kids` (distances, speeds, cheer times, caps) |
| `Editor/NeighborhoodBuilder.cs` | `BuildKidPens` (after the street graph, no RNG), `TryYardPen`, `KidPenClear`, `BuildKidPen`, `KidFenceRun`, `KidSpots`, `PenSign`, `ValidateKidPens`, the `NB_KidFence` material, and the `SkipTownMapBake` switch |
| `Editor/Setup_WorldLifeKids.cs` | the umbrella's Kids step (self test and a read-only scene check), **Rubbish/WorldLife/Kids Self Test**, **Kids Scene Check**, **Render Kids**, and the play-test `Kit` |
| `Editor/Setup_WorldLife.cs` | Kids in the umbrella's order, after Rules and before Polish |

**Decisions:**

- **Never hittable, by construction.** A kid is a `KidAgent`, not an `ILifeBody`, and nothing about it is registered in
  `LifeBodies`. Its puppet has no colliders. The truck sensor, the horn scatter, bumps and thrown things all look people up in
  `LifeBodies`, so they can't reach a kid, and kids never flop and never block anything.
  - The safety net behind the fences: if a truck's body box (+0.3 m) ever contains a kid, the kid hides indoors ("EEK!") until
    the truck is 10 m from the pen. It never fired in the tests.
- **The fences are hard.** Each pen is one static GameObject whose four BoxColliders are the fence: 1.6 m high, 0.9 m thick,
  layer Default, overlapping at the corners.
  - The truck's body starts 0.41 m up and its Rigidbody uses discrete collisions. At 20 m/s it moves 0.4 m per step, less than
    half the fence's thickness, so it can't tunnel through.
  - The pickets are the builder's 4 m panel mesh squashed to 0.86 m, so the kids (0.95–1.15 m) show over them. They use a new
    sunny-yellow `NB_KidFence`, so a kid pen reads as solid next to the Meadows' knockable white pickets.
- **Where the pens are.**
  - **The playground:** the fence runs x 45–72, z 99.5–113 round the sand and its kit. Its front faces east, toward the
    north-south park path. A sign on the front fence says "PLAYGROUND, no trucks allowed (sorry, truck)".
  - **Five front-yard pens**, one per row: Depot Rd (the first house out of the depot), Maple St south, Birch St north, Oak Ave
    east, and the Meadows' Carpool Ln north.
  - **How a yard pen is placed.** Each row takes the lot nearest a preferred point whose front yard has a clear rectangle. It must
    stay 1.35 m off the garden path and 0.9 m off the driveway, with nothing solid in it (an `OverlapBox` above the lawn) and
    no bush or tree inside.
  - **Meadows yards are shallow** (setback 5.5–7 m), so their pens sit 1.2 m behind the knockable picket front and right up to
    the bushes.
  - **Kids per pen:** 2 in each yard (3 at a big house) and 5 at the playground. One kid per pen holds a cardboard sign.
  - **Home spots** are chosen by farthest-point sampling over clear cells. The straight way from a spot out to the run loop
    has to be clear, so kids don't run through the playground kit.
- **Byte stability.** `BuildKidPens` runs after `BuildStreetGraph` and uses no RNG, so `HouseDirectory.asset` and
  `StreetGraph.asset` can't change. The pens are in place before `Validate`, so the §5.3 walk probes check them too.
- **Presentation.**
  - **Rig pool:** the kids get their own pool (8 full + 8 lite) of the residents' universal rig, with the same LOD tiers (full
    ≤ 35 m every frame, lite ≤ 90 m at 15 Hz) and restyling on assignment.
  - **Cost:** billed to WorldLife.Puppets, with its own marker WorldLife.Puppets.Kids. Pens more than 130 m from the camera
    aren't simulated: their kids snap home when the camera comes back.
  - **Looks:** chibi proportions (the head is ~43% of the height), bigger googly eyes, giant sneakers. Caps (some backwards),
    propeller beanies (they spin faster when the kid is excited), pigtails, afros. Capes, tutus, overalls. Balloons that float
    above the hand whatever the arm does, toy garbage trucks, lollipops.
  - **Signs (ASCII only):** "HI TRUCK!", "FEED ME!", "HONK!!", "HONK PLZ", "TRUCK #1", "BEEP BEEP!", "I LOVE TRASH".
- **The timetable** is pure LifeHash data (`KidSchedule`, rule 2):
  - Afternoons from 15:00 (up to 45 min later) to 19:00 (up to 30 min earlier), on 22 days in 25 per kid.
  - No-school mornings on 2 days in 7 town-wide, when 3 kids in 4 are out from about 08:30 to 11:30.
  - Never between 19:00 and 08:30. Kids pop out and back in at their home spot.
- **Reactions.**
  - **A truck within 25 m** (they let go at 28 m): the kids hop onto the run loop (the fence inset 0.7 m) and run along it, at
    2.4 m/s and 0.8 m apart, to the point nearest the truck, waving with their free hand ("HI TRUCK!").
  - **When it's gone** they stroll home at 1.1 m/s ("BYE TRUCK!").
  - **Chatter** is limited to one line per kid per 5 s and two on screen at once, within 45 m.
  - **A honk within 30 m** (or the air horn's bigger radius) is a 2.2 s jump-cheer: arms up, bouncing hops, mouth wide open,
    the sign held overhead. Each pen gets one "YAAAY!"-style floater and a couple of squeaky mumbles.
- **The fan moment.**
  - The first honk of the in-game day at each pen is decided on the host (rule 15) by `KidDirector.ClaimFan`. It sets a bit for
    that pen in the GameSession counter `wl_kids_fans` (`day << 10 | a bit per pen`) and raises the Neighborly fact, which
    Progression turns into +1 Rep (capped together with the hand-overs at 10 a day).
  - **What you see:** "BEST. DAY. EVER!" (or "HE HONKED AT US!!", "I'M GONNA BE A TRASH MAN!"), confetti, a sparkle and a 3.4 s cheer.
  - **A guest's honk** only gets the cheer until F relays horns to the host.
- **Sound** uses no new Sfx ids (Feel belongs to another package). The kids' voices are the three mumble takes pitched 1.75–2.3,
  "pop" plays as a kid comes out, and "sparkle" plays for the fan moment.
- **The town map:** the builder's end-of-build town-map bake can be switched off (`NeighborhoodBuilder.SkipTownMapBake`). This
  rebuild switched it off because the map's owner was mid-change, so **Setup_TownMap needs a re-run** to draw the pens.

**As built** (seed 1234; play tests in a test data root, Play Solo, the truck on G's autopilot):

- **Rebuild:** `RESULT: OK`. 6 pens and 16 kids: playground 5, depot-9 2, maple-19 2, birch-36 3, oak-12 2, mm-carpool-22 2.
  - **`HouseDirectory.asset` and `StreetGraph.asset` are byte-identical** to before (MD5 `1c096904…` and `dc9b7314…`;
    HouseDirectory 81/81).
  - Graph probes: 5,683 lane samples and 9,732 walk samples, 0 blocked. Knockables unchanged at 759.
  - Run-loop samples blocked 0, bad home spots 0.
  - GameObjects 5,281 → 5,331 (+50; +540 against the 1 Oct baseline, inside the +700 budget).
- **Setups:**
  - Graph check `RESULT: OK`; Knock 759 knockables, 0 re-baked; Residents 15/15; Traffic no FAIL; Rules 36/36; Polish 11/11.
  - Kids 12/12, plus the scene check: 6 pens, 0 bad. Every pen has 4 boxes, 1.6 m high and 0.9 m thick.
- **Self test:**
  - 0 of 162,000 night samples were out.
  - Out at 16:00 on 88% of kid-days, and at 10:00 on 26%.
  - The fan gate: once per pen per day, a new day again, and a guest books nothing.
  - Kid heights 0.95–1.15 m; 18–23 renderers full and 4 lite; no colliders; every sign and line is ASCII.
- **At 16:00, 15 of 16 kids were out.**
  - **Depot Rd:** the two kids ran to the front fence and along it as the truck rolled past at 3 m/s (z 20.6 → 24.4).
  - **Honk there:** 2 cheers and the fan moment, **Rep 0 → 1.00**. A second honk the same day gave 2 cheers and no Rep.
- **The playground:**
  - The truck drove west at 6.2 m/s into the east fence and **stopped with its nose on the fence's collider face** (x 72.45),
    still pushing at 0.03 m/s.
  - The three kids within 25 m ran to the fence in front of it. The two 26–29 m away kept playing.
  - **Honk:** all 5 cheered (inside 30 m). The fan moment gave +1 Rep (the counter read day 1, bit 5).
  - **A 14.0 m/s ram:** the nose got 6 cm into the 0.9 m collider and stopped. A 10.2 m/s ram into the Depot Rd yard pen: 2 cm.
  - **The kids weren't touched:** 0 shies, and their run line is 1.1 m inside the collider face.
  - Driving north up the park path, the kids chased the truck along the inside of the east fence and round its corner.
- **Night:** at 20:31 and 22:00 every kid was indoors (0 out, 0 rigs, 0 renderers).
- **Cost:**
  - With kids out and following, 5 full and 5 lite: WorldLife.Puppets.Kids averaged **0.028 ms (p99 0.044)** over 1,072 frames.
  - WorldLife.Total averaged 0.131 ms (p99 0.19; budget 1.1 / 2.0), with 125 kid renderers.
  - **0 B** over 600 steady-state kid ticks. Only events allocate: the first restyle of a look, a sign's text, a floater.
- **Console:** no errors or warnings from the kids.

**Known limits:**

- A flattened adult can probably be flopped over a fence. The test drives flattened two people on the park path, and right
  after the 14 m/s ram one body stood inside the playground for a moment. A 14 m/s flop peaks well above the 1.6 m fence. The
  kids aren't touched, but the adult walks out through the fence when they rejoin, because C's flop doesn't know about fences.
- The town's only trampoline is behind a Meadows house (mm-juicebox-210), out of sight of the street, so there's no trampoline kid.
- A guest's honk gets the cheer only; the fan moment's Rep needs F.
- `CoreLoop.md` §5 still lists WorldLife's sub-steps without Kids (that file isn't WorldLife's).

---

## 13. Risks

| Risk | Mitigation |
|---|---|
| **Taste:** running people over | Strictly cartoon: no blood, no injury states, everyone gets up angry and funny; **no children on streets**; dogs always leap clear; fines and letters make it a joke with consequences. Expect an E10+ / PEGI 7 "cartoon violence" descriptor. |
| A builder change breaks district-1 byte stability | All new RNG uses separate streams; the parked-car draws are kept; A's acceptance compares `HouseDirectory` exactly. |
| Lane-graph edge cases (cul-de-sacs, the depot pad, gates, the turnaround) | Validator plus gizmo renders; the pad is a slow yield zone; every route is tested in the EditMode suite. |
| Static-batch proxy swap surprises (GPU Resident Drawer later, or batching off) | Disabling a renderer works in every mode; proxies come only from prefab assets or recipes. |
| Too much noise or too many floaters | Chatter caps (1 per NPC per 6 s, 3 on screen); floaters rate-limited; letters batched hourly. |
| Perf on low-end machines | Hard caps and LOD (§4.2); profiler markers gate the acceptance checks; a `WorldLifeTuning.Density` knob (0.5×). |
| Divergence between machines | Only cosmetic state may diverge; host checks carry slack; re-convergence is built into cars and pedestrians. |
| Fines feel punishing | Accidents ≤6% of a day; escalation only on repeats; the `NeighborFines` Town toggle; redemption by clearing the curb. |
| Debris vs bags in M2 (`InFlight` and bag layers) | When M2 adds its layers, set debris × networked bags to off in the matrix (one line in `Setup_Coop`'s physics step). |
| Kevin's sweeps, Butler Drops and walk-outs overlapping | A record vanishing mid-walk = shrug and go back in (§7.2). |

---

## 14. Open questions for Nick (each default is already built into this spec)

1. **Fines for flattening residents:** on by default, but small (district 1: $3 a resident, $5 a car), with a Town toggle to make
   them receipt shame only. *Default: on.*
2. **Kids:** none on sidewalks or roads; they only cheer from yards and the playground. *Default: none.* **Answered (2 Oct):**
   "Yes, add them", "doesnt have to be perfect": built in §12.K.
3. **Dogs:** always leap clear (owner furious, Rep −1). *Default: yes.*
4. **Exits:** three new short roads with tunnel portals (west Old Highway, east Interstate, north Private Airport Rd). *Default: build
   them.*
5. **Hand-over:** walk up to a resident carrying a bag and they hand it to you (+1 Rep). *Default: yes.*
6. **Car boops:** NPC cars give a player on foot a gentle 0.4 s stumble, without making you drop your bag. *Default: yes.*
7. **Phase 2 vehicles**, using the same lane graph: Kevin's Crew truck shown at its 08:00, 14:00 and 20:00 sweeps; the rival GoldStar
   truck collecting unsubscribed houses; a mail van that delivers your complaint letters to the depot; an ice-cream truck.
   *Default: after v1 playtests.*
