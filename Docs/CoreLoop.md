# Rubbish — Core Loop Design & Build Contract

Derpy friend-slop garbage game. You drive a garbage truck around a suburb, hop out at each house,
grab the trash bags and chuck them in the truck. Back at the shop you tip the load out, rip bags
open on the sorting table and dig through the gunk: recyclables go in the recycling bins for cash,
junk goes in the compactor, batteries go in the hazard barrel (or they explode in the compactor),
and every now and then you find something *amazing* — somebody's wedding ring — which you keep and
sell on **ebae**, the online pawn shop on the shop laptop.

**Feel target:** every action has a punchy reaction. Squash/stretch, pops, dings, floating
"+$0.42", combo counters, camera shake on slams, sparkles + slow-mo on rare finds, dumb sound
effects. Physics is a little floppy and silly. Nothing is precise or serious.

**Multiplayer status (M0 landed 1 Oct 2026, `Multiplayer.md` §8.7):** sessions exist (Play Solo / Host with Friends /
Join, through `Net/Session/NetSession`), but gameplay state is still **per machine**: `GameSession`'s statics and the
trash, bags and sorting objects are local, and a guest's own save is latched off while they're in someone else's game
(`SaveGuard`). Shared world state arrives in M1. Gameplay systems stay plain MonoBehaviours with local state in
`GameSession`; don't add NetworkObjects/NetworkVariables to them, and don't break existing netcode (player and truck are
NetworkObjects). **New code follows `Multiplayer.md` §9:** money and Rep through `Ledger`; host-only systems decide only
when `!GameSession.IsGuest`; no new scene-name checks or static `Current` singletons; saved data only through `Save/**`
(never `File.*`, `PlayerPrefs` or `persistentDataPath` elsewhere). `Tools/coop_lint.sh --files <your files>` checks these.

---

## 1. The loop

1. **Garage (ShopScene)** — start of day. Truck parked in the bay facing the garage door.
   Walk to the truck → **E Enter Truck**. Drive at the door (or look at it) → **E Drive Out**.
2. **Neighborhood** — arrive in the truck on the depot driveway. HUD shows route progress and the
   nearest uncollected stop. Houses on the route have 1–4 black sacks at the curb, beside a bin,
   with a floating marker. Stop the truck next to the curb → **E Exit Truck**.
3. **Pickup** — walk up to a bag → **E Grab (6.2 kg)**. Carrying slows you a bit (heavier = slower).
   Walk to the truck's rear hopper → **E Toss into truck**: bag arcs in with a satisfying
   whoomp; HUD truck gauge fills. Or **Left-mouse** to chuck it (hold to charge); if it lands in
   the hopper trigger it counts; if it hits the ground too hard it **bursts** and spills loose
   items you have to pick up individually (they can go into the hopper too). **G** drops.
   Last bag at a stop → "+$8 Stop cleared!". All stops → "+$25 Route complete! Head back to the
   depot."
4. **Truck mass** — the truck holds `GameSession.TruckCapacityKg` (160 kg to start). A bag that
   would overflow bounces off the hopper: "Truck's full! Go dump it." Plan: some routes take two
   trips.
5. **Back to garage** — drive into the depot's garage door → **E Drive into Garage**. You arrive in
   the bay. Walk to the **TIP IT** lever by the truck's rear → bags cascade out of the hopper onto
   the tipping floor behind the truck.
6. **Sorting** — grab a bag from the pile, toss it onto the **sorting table** → it rips open,
   items spill out on the table and *reveal* (name, category-colored outline; rares sparkle with a
   big "RARE FIND!" card + slow-mo). Grab items and toss them into:
   - **Recycling bins** (Metal / Plastic / Glass / Paper) — instant `$` by weight × rate, with a
     combo multiplier for quick consecutive correct sorts. Wrong bin → buzzer, "Contaminated!",
     small penalty.
   - **Compactor** — junk or unopened bags. **COMPACT** processes only what's in the chamber;
     bags can go straight in without ripping them first. The press slams (shake, crunch), pays by
     weight, and the ordinary bale floats away and disappears. Hazard effects do not charge a fine.
   - **Hazard barrel** — batteries, spray cans, paint.
   - **Keep crate** — valuables go to your inventory (`GameSession.Inventory`).
   Quick-clear option: **BULK COMPACT SHOP** gathers eligible loose trash and unopened bags from this
   shop's tipping floor, table and compactor drop zone. It never reaches into a truck, another
   depot, a player's hands, saved inventory or displays. It uses the same per-kg payout as the chamber,
   with no hazard or landfill fees. Hazards are consumed once; L2 dampens their comic bangs. Recycling
   still pays more; the main cost is destroying undiscovered treasures (unless the L3 grate rescues
   them at 30% value). Compacted players retain their temporary cartoon-cube gag, not ordinary bale cleanup.
7. **ebae** — shop laptop. Inventory list → list an item at a price (Lowball / Fair / Greedy
   presets or custom). Over real time, derpy buyers send offers with dumb messages; accept/decline.
   Or **Pawn now** for 45%. Sold → cash (minus 8% ebae fee). Offers ping a toast even when the
   laptop is closed.
8. **Next day** — when the route was completed and the truck is empty, driving/walking out of
   the garage starts the next day: fresh trash at the curbs (new rolls).

## 2. Numbers (all in `Economy` in `Assets/Scripts/Core/RubbishTypes.cs`)

| Thing | Value |
|---|---|
| Truck capacity (start) | 160 kg |
| Bag | 5–10 items, ~3–15 kg total |
| Pay per stop cleared | $8; route completion bonus $25 |
| Recycling $/kg | Metal 2.00, Plastic 1.10, Glass 0.55, Paper 0.45 (min $0.10/item) |
| Hazard binned correctly | $0.75 |
| Wrong bin | −$0.30 |
| Compaction, chamber or bulk | $0.20/kg stock; $0.25 / $0.30 / $0.35 at compactor L1 / L2 / L3 |
| Compaction downside | Lower pay than recycling; valuables destroyed (L3 grate saves them at 30% value). No hazard or landfill fees. |
| Combo | +10% per consecutive correct sort within 2.5 s, cap ×2 |
| Pawn | 45% of value; ebae fee 8% |

Valuable drop chance per item ≈ 2% + 4% × house wealth (tuned in ItemCatalog loot tables),
rarity weighted Uncommon ≫ Rare > Epic > Legendary.

## 3. Frozen contracts (DO NOT change signatures; you may add members)

All in global namespace, Assembly-CSharp. Legacy `Input` API is fine (input handling = Both).

- `Assets/Scripts/Core/RubbishTypes.cs` — `ItemCategory`, `Rarity`, `ItemDef`, `ItemInstance`,
  `BagContents`, `ItemCategoryInfo`, `RarityInfo`, `Economy`. **Owner: lead (frozen).**
- `Assets/Scripts/Core/GameSession.cs` — static session state: `Day`, `Cash`, `AddCash(amount,
  reason, worldPos?)`, `TrySpend`, truck cargo (`TruckBags`, `TruckLoadKg`, `TruckCapacityKg`,
  `CanFitInTruck`, `AddBagToTruck`, `TakeTruckBag`, `TakeAllTruckBags`), truck location
  (`TruckSceneName/TruckPosition/TruckRotation`), `Inventory` + `AddToInventory/RemoveFromInventory`,
  daily progress (`CollectedBagIds`, `ClearedStops`, `MarkStopCleared`, `RouteCompleteToday`,
  `MarkRouteComplete`, `StartNextDay`), stats, events `Changed`, `CashChanged`, `DayStarted`,
  `NewUid()`, `Save/Load`. **Owner: lead (frozen).**
- `Assets/Scripts/Core/InteractionZone.cs` — box volume exposing an `IInteractable` when the local
  player (or their truck, when driving) is inside. Fields: `center`, `size` (local space), `mode`
  (`OnFoot/Driving/Either`), `score` (default 1), `targetOverride`. **Owner: lead (frozen).**
- `Assets/Scripts/Core/RubbishAssets.cs` — ScriptableObject at `Assets/Resources/RubbishAssets.asset`
  mapping namespaced keys → prefabs/materials/clips for runtime. Runtime: `RubbishAssets.Prefab(key)`,
  `.Material(key)`, `.Clip(key)`, `.PrefabsWithPrefix("sack/")`. Editor:
  `RubbishAssets.EditorRegisterPrefab(key, assetPathOrPrefab)`, `EditorRegisterMaterial`,
  `EditorRegisterClip`, `EditorGetOrCreate`. Key namespaces: `sack/…` (pickup), `item/…` (items),
  `mat/…` (anyone, prefix with your system e.g. `mat/sorting_bin_blue`). **Owner: lead (frozen).**
- `Assets/Scripts/PlayerController.cs` — now has `PlayerController.Local`, proximity scan (on foot,
  finds bags at your feet), zone candidates, carrier candidates, and a UI lock:
  `PlayerController.PushUILock()` / `PopUILock()` / `IsUIBlocking` (frees cursor, stops player
  input; pair every push with a pop). Candidate scores: LOS 0–2, proximity 0–1.5, zones default 1,
  carrier toss 3, truck "Exit Truck" 0.25. **Owner: lead.**
- `Assets/Scripts/Core/Carry.cs` — `Carryable` (abstract; `Body`, `State`, `Holder`, `LastHolder`,
  `MassKg`, `DisplayName`, `PickupVerb`, IInteractable impl, `BeginCarry`, `EndCarry(velocity,
  thrown)`, `Consume()`, `SetInFlight()`, hooks `OnPickedUp`, `OnReleased`, `OnImpact`),
  `CarryState`, `ICarryReceiver` (`TossTargetPosition`, `TossRange`, `CanAccept(item, out reason)`,
  `GetTossPrompt`, `Accept`), `CarryReceivers` registry (`Register/Unregister/FindBest`),
  `CarryReceiverTrigger` (trigger that forwards physically thrown carryables to its parent
  receiver). **Owner: Pickup.**
- `Assets/Scripts/Core/TrashCarrier.cs` — on the Player prefab. `Local`, `Held`, `IsCarrying`,
  `PickUp`, `Drop`, `Throw`, `TossInto(receiver)`, `ForgetHeld`, `HoldPoint`, events
  `PickedUp/Released`, implements `IInteractionProvider` (adds the "Toss into …" candidate).
  **Owner: Pickup.**
- `Assets/Scripts/Core/FeelApi.cs` — `GameHUD.Toast/FloatingText/SetContextHint/BigReveal`,
  `ToastStyle`, `Sfx.Play(id, pos?, volume, pitch)`, `Fx.Burst/Sparkle/Dust/Stink`,
  `Juice.Squash/Punch/Shake/PopIn/ShrinkAndDestroy/HitStop`. Stubs today. **Owner: Feel.**
  (Feel may move implementations into other files under `Assets/Scripts/Feel/` but these static
  classes and signatures must remain.)
- `Assets/Scripts/Items/ItemCatalog.cs` — `All`, `Get(id)`, `RollBag(bagId, seed, houseWealth,
  foundAt)`, `RollItem(def, rng, foundAt)`. `Assets/Scripts/Items/ItemVisuals.cs` —
  `Build(def, parent)` (renderers only, no colliders). **Owner: Items.**
- `Assets/Scripts/Trash/TrashBag.cs` — `Contents`, `SourceStop`, `static Spawn(contents, pos, rot,
  velocity)`, `Burst(revealItems)`. `Assets/Scripts/Trash/PickupStop.cs` — fields `stopId`,
  `address`, `bagCount`, `wealth`, `onRoute`; `IsCleared`, `BagId(i)`, `NotifyBagCollected(bag)`.
  `Assets/Scripts/Trash/TrashRoute.cs` — `Current`, `routeName`, `Stops`, `StopsTotal`,
  `StopsCleared`, `IsComplete`, `NextStop(from)`. **Owner: Pickup.**
- `Assets/Scripts/Sorting/LooseItem.cs` — `Item`, `Def`, `Revealed`, `static Spawn(item, pos, rot,
  velocity, revealed)`, `Reveal()`. **Owner: Sorting.**
- `Assets/Scripts/GarageDoorController.cs` — public fields `targetSceneName`,
  `targetSpawnPointName`, `targetSpawnPointNameDriving`, `promptOnFoot`, `promptDriving`.
  **Owner: Flow.**

### Integration seams (agreed conventions)
- **Truck hopper**: Pickup's setup adds a child GameObject named exactly `Hopper` to
  `Truck V2.prefab` at local ≈ (0, 1.9, -3.0) (the rear mouth) with the `TruckHopper` receiver.
  Other systems locate it by name (`truck.transform.Find("Hopper")`), falling back to
  `truck.transform.TransformPoint(0, 1.9, -3.0)`.
- **Loose cargo**: a `BagContents` with `sackVariant == -1` is a pile of loose items that went into
  the truck individually (no sack). Unloading spawns its items as `LooseItem`s instead of a sack.
- **Finding the truck**: use `FindFirstObjectByType<TruckControllerV2>()` (don't depend on new
  static members from another system).
- **Sfx ids** also include `horn`, `engine_start`, `beep` (reverse beeper), `slam`, `splat`.
- Everything new is local-only (no RPCs). The host's own player is the only one that drives the
  single truck / trash state in early game.

## 4. File ownership (only edit files you own; create new files only in your folders)

| System | Owns / may create |
|---|---|
| **Pickup** | `Core/Carry.cs`, `Core/TrashCarrier.cs`, `Assets/Scripts/Trash/**`, `Editor/Setup_Pickup.cs` |
| **Flow** | `TruckControllerV2.cs`, `TruckRequester.cs`, `GarageDoorController.cs`, `SceneController.cs`, `SceneTransitionData.cs`, `PlayerSpawner.cs`, `Assets/Scripts/Flow/**`, `Editor/Setup_GarageFlow.cs` |
| **Sorting** | `Assets/Scripts/Sorting/**`, `Editor/Setup_SortingStation.cs` |
| **Items** | `Assets/Scripts/Items/**`, `Editor/Setup_Items.cs` |
| **Ebae** | `EbaeUI.cs`, `LaptopController.cs`, `Assets/UI/ebae/**`, `Assets/Scripts/Ebae/**`, `Editor/Setup_Ebae.cs` |
| **Feel** | `Core/FeelApi.cs`, `Assets/Scripts/Feel/**`, `Editor/Setup_Feel.cs` |
| **Neighborhood** | `Assets/Scripts/Editor/NeighborhoodBuilder.cs`, `Assets/Scripts/World/**` |
| **Depot Row** (`Multiplayer.md` §13, N3) | `Assets/Scripts/World/DepotRow/**`, `Editor/Setup_DepotRow.cs`, `Assets/Scenes/DepotRow.unity`, `Assets/Materials/Generated/DepotRow/**` |
| **WorldLife** (`WorldLife.md` §12.1) | `Assets/Scripts/WorldLife/**`, `Editor/Setup_WorldLife*.cs`, `Assets/Materials/Generated/WorldLife/**`, `Assets/Prefabs/WorldLife/**`, `Assets/Resources/StreetGraph.asset`, `Assets/Tests/EditMode/WorldLife/**` (packages per `WorldLife.md` §12.1) |
| **Multiplayer** (`Multiplayer.md` §8.7.4) | `Assets/Scripts/Net/**`, `Assets/Scripts/Save/**`, `Assets/Scripts/UI/Menu/**`, `Assets/Tests/**`, `Assets/Prefabs/Net/**`, `Editor/Setup_Coop.cs`, `Editor/RubbishBuild*.cs`, `Editor/Setup_WindowsPlaytest.cs`, `Editor/SteamBuildPostprocess.cs`, `Tools/bots/**`, `Tools/dev/**`, `Tools/playtest/**` |

**Assemblies:** everything is Assembly-CSharp except `Assets/Scripts/Net/Core/**` (`Rubbish.Net.Core`: pure code that
references only Netcode and Collections, never Assembly-CSharp) and `Assets/Tests/EditMode/**` (`Rubbish.Net.Tests`,
Editor-only). Steamworks.NET is used only under `Net/Steam/**`, behind `#if !DISABLESTEAMWORKS`.

`Editor/` = `Assets/Scripts/Editor/` (Editor-only assembly; wrap nothing else).
Need something from another system that doesn't exist? Code against the contract; if a contract
is genuinely missing something, work around it in your own files and mention it in your report.

## 5. Setup scripts (how scene/prefab changes get made)

Code-writing agents **do not modify scenes, prefabs or assets directly** during the build phase
(the one live Editor is shared). Instead each system ships an idempotent editor entry point:

```csharp
public static class Setup_<System>
{
    [MenuItem("Rubbish/Setup/<System>")]
    public static void Run() { ... }   // idempotent: safe to run twice; logs a summary
}
```

The lead runs them in this order: Items → Pickup → Feel → Flow → Sorting → Ebae → Neighborhood → WorldLife
(`Setup_WorldLife`: Graph check → Knock → Residents → Traffic → Rules → Kids → Polish, each package's setup if it exists) → Coop
(`Setup_Coop`: the NetSession prefab from every `[NetSessionComponent]`, player/truck network flags; re-run it whenever a new
`[NetSessionComponent]` lands) → DepotRow (`Setup_DepotRow`: builds `DepotRow.unity`, the co-op Depot Row shops and their
pocket interiors copied from ShopScene; re-run it after any ShopScene setup: SortingStation, GarageUpgrades, GarageDisplays,
Shop, Ebae) → Windows Playtest (`Setup_WindowsPlaytest`).
Rules for setup code:
- **Never save `Bootstrap.unity`** (it may hold Nick's unsaved changes). Networking is configured at runtime by `NetBoot`
  instead. If a setup script dirties Bootstrap, reload it from disk rather than saving. Don't use
  `ObjectFactory.CreateGameObject` (its Undo marks the open scene dirty); use `new GameObject` in a preview scene.
- A NetworkObject prefab made by script must have its `GlobalObjectIdHash` written to disk (NGO only sets it in memory);
  see `Setup_Coop.EnsurePrefabHash`.
- Open scenes **additively** if not already loaded (`EditorSceneManager.OpenScene(path,
  OpenSceneMode.Additive)`), modify, `EditorSceneManager.SaveScene`, then close it again if you
  opened it. Never replace the user's open scene.
- Prefab edits via `PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset` / `UnloadPrefabContents`.
- Find existing objects by name before creating (idempotency). Parent your generated objects under
  one named root per system so re-runs can delete and rebuild them.
- Walkable surfaces must be on layer **Ground (6)** (the ThirdPersonController's GroundLayers=64).
  Interactable things on layer **Interactable (3)** (PlayerController.interactLayer = 1<<3).
- Materials: URP Lit (`Universal Render Pipeline/Lit`, `_BaseColor`), save new ones under
  `Assets/Materials/Generated/<System>/`. Register runtime-needed assets in `RubbishAssets`.

Read-only inspection of scenes is fine **inside a single eval** (open additively → read → close in
the same eval call; eval runs atomically on the main thread).

## 6. World facts

**ShopScene (garage interior)** floor at y≈-0.1, walkable x∈[-7,7], z∈[-5,5].
- Garage door: -X wall, center (-6.76, 0, -2.41), 4 m wide (z -4.4..-0.4). `GarageDoor` root has
  `GarageDoorController` + child `Interactable` BoxCollider (layer Interactable).
- `TruckSpawner` (tag TruckSpawner) at (-0.11, 0, -2.47), rot Y 270 (truck faces the door, -X).
  Truck is ~2.6 w × 3.3 h × 6 m; pivot ≈ center; rear bumper ≈ x 2.1.
- Magnus robot-arm rail at x 2.8–3.7, z -3..1.1 (don't touch Magnus / chess / office).
- Office/chess/laptop: x 3..7, z 0..5. Laptop (`LaptopController`) at (5.2, 1.3, 3.0).
- **Tipping floor (dump pile)**: x 4.2–6.8, z -4.6..-1.6 (free floor behind the truck & rail).
- **Sorting station**: x -6.5..-1.5, z 1.5..4.8 (free, north-west quadrant). Keep z -1.2..1.5 as a
  walkway.
- Default `PlayerSpawner` (tag PlayerSpawner) at (-0.51, 0, 1.86) — Flow may move it to
  (0.5, 0, 0.2) facing +X so it doesn't land in the sorting station.
- New spawn points (Flow setup creates): `SpawnPoint_FromNeighborhood` (on foot) at (-5.8, 0, 0.6)
  rot Y 90; `SpawnPoint_FromNeighborhood_Truck` = TruckSpawner pose.

**Neighborhood** — rebuilt by NeighborhoodBuilder (old content disabled, not deleted). Must provide:
`SpawnPoint_FromGarage` (on foot, beside the depot door), `SpawnPoint_FromGarage_Truck` (in front
of the depot door, facing out along the driveway), a `PlayerSpawner`-tagged object (= on-foot
spawn) and a `TruckSpawner`-tagged object (depot driveway), a depot garage door with
`GarageDoorController` (target `ShopScene`, spawns `SpawnPoint_FromNeighborhood` /
`SpawnPoint_FromNeighborhood_Truck`, prompts "Enter Garage" / "Drive into Garage") plus an
`InteractionZone` (Either) in front of it, `PickupStop`s on route houses, one `TrashRoute`.

**Player prefab** `Assets/Prefabs/Player.prefab`: CharacterController (h 1.8, r 0.28), pivot at
feet, layer Interactable, MoveSpeed 3, Sprint 5.3, has PlayerController, ThirdPersonController,
TruckRequester, NetworkObject. **Truck prefab** `Assets/Prefabs/Truck V2.prefab`: Rigidbody 1500 kg,
NetworkObject + NetworkTransform + NetworkRigidbody, TruckControllerV2, 6 WheelColliders, body
MeshCollider on layer Interactable, `DriverEjectPosition` child at local (-2.41, 1.27, 4.53), rear
lights at local z -3.08. Local +Z = forward. Truck body local z range ≈ -2.2..+3.8.

**Scene flow**: Bootstrap (NetworkManager, GameStartup+SceneController+PlayerSpawner, all DDOL; `NetBoot` turns on
connection approval at runtime) → MainMenuScene → **Play Solo** (LocalDirect on 127.0.0.1, no internet) / **Host with
Friends** (Unity Relay invite code + a Steam lobby when Steam is on) / **Join** → `ShopScene` (guests follow the host's
scene through NGO; only the server transitions). `SceneController.TransitionToScene(scene, spawnPoint,
isDriving)` → NGO `SceneManager.LoadScene(Single)` → `PlayerSpawner.OnSceneLoaded` despawns and
respawns every player at the named spawn point → `TruckRequester` (on the player) asks the server
to spawn a truck.

## 7. Verifying your code

Use the Unity CLI against the live Editor (`unity command ...`, run from `/Users/nick/Dev/Rubbish`):
- `unity command recompile` then poll `unity command recompile_status` until `completed`.
- `unity command console --level error --tail 50 --json` for compile errors; filter to your files.
  Other agents are writing code at the same time — ignore errors in files you don't own, retry
  later if the whole assembly is blocked by someone else's error.
- `unity command eval_file --file <abs path>` runs C# in the Editor (return a string). Don't enter
  play mode, don't save/modify scenes/prefabs/assets (read-only inspection only).
- Saves live in `SaveRoot.Path` (`persistentDataPath` = `~/Library/Application Support/Archway Games/Rubbish` on the Mac).
  Play tests never touch it: take the editor lock and switch on `Tools/dev/test_data_root.sh on` first (the "TEST DATA
  ROOT" watermark shows), and `off` afterwards. Unit tests and bot builds run only in the build copy through
  `Tools/bots/sync_build.sh` (`Multiplayer.md` §8.7.6), never in the live project.
