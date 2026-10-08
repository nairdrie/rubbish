# Rubbish — Living World Update (design + build contract)

Extends `Docs/CoreLoop.md` (read it first: conventions, ownership rules, setup-script rules, world facts).

## What the user asked for (verbatim, in order)

- "i think we want the day night cycle to just go on a timer while the server runs. 20 mins total maybe? you can collect garbage any time of day."
- "racoons hide in and around trash bags at night. maybe attack you if you disturb them if their rabid. maybe you get [bit] and you need to buy a rabies shot from the shop."
- "the shop will eventually have upgrades in your shop [I don't know] what to call it (eg. conveyor belts, magnus upgrades, truck upgrades, etc."
- "people just put out trash randomly. not like a daily reset."
- "maybe you get less pay for being late on peoples trash. on time is like a better pay" / "we are trying to keep the city clean type shi."
- "if trash piles up [something] just sleep on it they'll get aggressive with you too, like raccoons" → lead decision: the pile squatter
  is a **Pile Goblin** (grumpy gremlin that naps in neglected piles; swappable later — keep its look/name data-driven).
- "i still love the arrows pointing you to next stop" → the route system stays, as a **live route planner** over random trash.

## 1. Day / night (`GameClock`, frozen contract — `Assets/Scripts/Core/GameClock.cs`)

- 20 real minutes per in-game day, running on NGO `ServerTime` while the server runs (clients agree for free). Fresh server = 7:00 AM.
- `GameClock.Hours` (0–24), `TotalHours` (monotonic), `Day`, `IsNight` (20:30–5:00), `Daylight01` / `Night01` (smooth dawn 5:00–6:30,
  dusk 19:00–20:30), `AgeHours(stamp)`, `ClockText` ("7:42 AM"), events `HourChanged(int)`, `NightFell`, `MorningCame`,
  debug `DebugSkipHours(h)` / `DebugSetHour(h)`. 1 in-game hour = 50 real seconds.
- No world reset on a new day: `GameSession.Day` just counts up (`GameSession.SetDayFromClock`, `DayStarted` event).
- `GameSession.StartNextDay / RouteCompleteToday / CollectedBagIds / ClearedStops / MarkStopCleared / MarkRouteComplete` are
  **legacy** — stop using them (they remain only so old code compiles until reworked).

## 2. Random curb trash + lateness pay + clean city (Trash system)

- **World data**: `GameSession.CurbBags` (`CurbBag {bagId, stopId, slot, putOutAtHours, contents}`), `AddCurbBag`,
  `RemoveCurbBag(bagId)`, `FindCurbBag`, `CurbBagCountAt(stopId)`, event `CurbTrashChanged`. Scene-independent: the spawner keeps
  running (and catches up) while you're in the garage; the Neighborhood materializes records as `TrashBag`s at their house's curb.
- **Every house** has a `PickupStop` (NeighborhoodBuilder puts one on all ~37 lots; `onRoute` is obsolete → always true;
  `bagCount` becomes the curb capacity 3–6 by house size; `wealth` as before).
- **CurbTrashSpawner** (host only, DDOL, starts when the server starts): houses put out 1–2 bags at random times (a Poisson-ish
  process per house; busier in the morning 6–9 and evening 17–21; a little at night). Target ≈ 30–40 bags per in-game day for the
  whole neighborhood early game (tunable consts). Caps: per house = its capacity (a full curb stops putting out more, but its bags keep
  ageing), global ≈ 60. Bags roll contents with `ItemCatalog.RollBag(bagId, stableHash(bagId), wealth, address)`. When the scene isn't
  loaded it only edits `CurbBags`.
- **Pay** (`Economy`): each bag that goes into the truck pays `PickupFeePerBag × PickupPayFraction(ageHours)` — on time (≤ 4 h) = full
  $4, decaying to 25 % by 12 h. `LatenessLabel` for toasts/labels ("On time! +$4.00", "Late… +$1.60"). Last bag at a house →
  `CurbClearedBonus` ($2) "Curb clean!". The hopper removes the `CurbBag` record. Bags that burst are removed from the records (their
  loose items are litter you can still collect).
- **City Cleanliness** (0–100 %, `CityCleanliness` static in Trash/): derived from curb bags weighted by age (fresh bags barely count,
  late/neglected ones a lot) + loose litter in the Neighborhood. Drives: HUD meter ("City: 87 % clean ✨" … "City: 31 % — STINKY"),
  raccoon/goblin spawn pressure, and a small end-of-day "Clean Streets bonus" at midnight = cleanliness × $30 (toast).
- **Neglected pile**: a house whose oldest bag is older than `Economy.NeglectedHours` (16 h): stink lines + flies (`Fx.Stink`), bags
  visibly heaped, marker red, and it becomes a **Pile Goblin** nest candidate. API for critters: `PickupStop.IsNeglected`,
  `PickupStop.OldestBagAgeHours`, `PickupStop.SpawnedBags`, `PickupStop.All` (static list), static event
  `TrashBag.AnyPickedUp(TrashBag, TrashCarrier)` fired when the player grabs any bag (critters react).
- **Live route planner** (`TrashRoute` keeps its frozen members; `StopsTotal/StopsCleared/IsComplete` become "houses with trash" style
  numbers): `NextStop(from)` = best house by urgency (bags close to going late or already late score highest) and distance; new
  `PlannedRoute(from, n)` returns the next n stops as a greedy chain; `TotalCurbBags`, `LateBags`. HUD arrow + stop beam point at
  `NextStop`. Stop markers colored by freshness (green fresh → yellow → orange late → red neglected), label "3 bags · 2 h late".

## 3. Critters (Critters system): raccoons, Pile Goblins, rabies

- **Raccoons** (night only): at nightfall and through the night, spawn near curb bags (more when the city is dirtier; ~25–40 % of houses
  with trash get 1–2). Built from primitives (derpy low-poly: gray round body, black bandit mask, ringed tail, googly eyes with eye-shine
  at night). States: Hiding (inside/behind a bag — bag wiggles, eyes glint, chitter), Rummaging (tearing a bag: occasionally spills a
  couple of loose items = litter), Alert (player within ~4 m: stands up, hisses), Flee (most run off when you approach / grab their bag /
  honk), Attack (only **rabid** ones — ~20 %, more when dirty: red eyes + foam; they lunge and bite if you disturb them (grab their bag
  or get within ~2 m)). Retreat and despawn at dawn. Simple steering on flat ground (no NavMesh), ground-snapped, avoid houses.
- **Pile Goblins**: at any time of day, a neglected pile may get a sleeping goblin (snoring Z's). Disturbing it (grabbing a bag from its
  pile, bumping it) wakes it: grumpy chase + bonk (knockback, drop your bag), gives up after a bit or when you've driven off; clearing the
  whole pile evicts it (it stomps off muttering). Derpy primitive gremlin, swappable look.
- **Bites / bonks**: knockback on the player's CharacterController (short impulse), `TrashCarrier.Local.Drop()`, camera shake, "OW!"
  floating text, Sfx. Rabid raccoon bite → 50 % chance `GameSession.Infect(GameClock.TotalHours)`.
- **Scaring**: truck horn (H while driving — poll it; Flow may later add an event) scatters raccoons within ~12 m (more with the
  "air_horn" upgrade); a bag thrown into a critter stuns/scares it.
- **Rabies** (`Assets/Scripts/Status/`): after `Economy.RabiesIncubationHours` symptoms ramp up: trippy URP volume overrides (lens
  distortion wobble, chromatic aberration, hue shift, green vignette), camera sway, occasional stumble/drift in movement, random hiss,
  foam bubbles; HUD status. Untreated for `Economy.RabiesPassOutHours` → pass out (fade), wake up in the garage, pay
  `Economy.HospitalBill`, cured. Cure = Rabies Shot from the shop (`RabiesStatus.CureWithShot()` static API: giant comedic syringe
  moment + "OWWW. Cured!"). Must fully restore volume/camera/inputs on cure, scene change and exit.

## 4. The shop: **Scrapazon** (Shop system)

The buy-side app on the shop laptop, next to ebag (sell-side). Laptop opens a tiny desktop / tab strip: **ebag** | **Scrapazon**.
Name alternatives offered to the user (easy rename, keep the name in one const): Scrapazon, Bin There Depot, Hoarder's Hardware.
Purchases are instant ("Scrapazon Prime: delivered in 0.3 seconds"), persisted via `GameSession.SetUpgradeLevel/GetUpgradeLevel` and
`AddConsumable/GetConsumable`. **Upgrade/consumable ids (contract, other systems read these):**

| id | kind | effect | price |
|---|---|---|---|
| `rabies_shot` | consumable | cure rabies now (if infected: used instantly; else stored, auto-used on infection) | $35 |
| `repellent` | consumable (per night) | "Eau de Raccoon-Be-Gone": critters won't bite you until morning | $20 |
| `truck_capacity` | upgrade 0–3 | +40 kg per level (`GameSession.SetTruckCapacity(Economy.StartingTruckCapacityKg + 40·lvl)`) | $150 / $400 / $900 |
| `truck_turbo` | upgrade 0–1 | +35 % motorTorque, +25 % maxSpeed on TruckControllerV2 (applied by a Shop component, not by editing Flow code) | $300 |
| `air_horn` | upgrade 0–1 | horn scare radius 12 → 25 m, louder derpier horn | $120 |
| `headlamp` | upgrade 0–1 | a light on the player's head that turns on at night | $80 |
| `conveyor_belt` | teaser | "BACK-ORDERED since 1997" — listed, not buyable yet | — |
| `magnus_brain` | teaser | Magnus auto-sorting upgrade — "coming soon™" | — |

Effects re-apply on every scene load / player spawn / truck spawn.

## 5. Feel / HUD additions (Feel system)

Clock widget (time + sun/moon icon + Day N), City Cleanliness meter, live route panel (arrow → NextStop, next 3 stops with freshness
colors, curb bag count, late count), pickup pay popups with lateness label, rabies status widget (incubating/symptomatic/timer to pass
out, "Buy a Rabies Shot on Scrapazon!"), night ambience (crickets/owl hoots at night, rooster at morning — low volume), new Sfx ids:
`hiss`, `chitter`, `snore`, `grumble`, `bite`, `ow`, `syringe`, `rooster`, `owl`, `cricket`, `goblin_angry`, `raccoon_scream`.

## 6. Ownership for this update

| System | Owns / may create |
|---|---|
| **Trash** | `Assets/Scripts/Trash/**`, `Assets/Scripts/Editor/NeighborhoodBuilder.cs`, `Editor/Setup_Pickup.cs` |
| **Sky** | `Assets/Scripts/World/**`, `Editor/Setup_World.cs` (day/night lighting in Neighborhood + garage) |
| **Critters** | `Assets/Scripts/Critters/**`, `Assets/Scripts/Status/**`, `Editor/Setup_Critters.cs` |
| **Shop** | `EbaeUI.cs`, `LaptopController.cs`, `Assets/UI/ebae/**`, `Assets/Scripts/Ebae/**`, `Assets/Scripts/Shop/**`, `Assets/UI/shop/**`, `Editor/Setup_Ebae.cs`, `Editor/Setup_Shop.cs` |
| **Feel** | `Core/FeelApi.cs`, `Assets/Scripts/Feel/**`, `Editor/Setup_Feel.cs` |
| **Lead (frozen)** | `Core/GameSession.cs`, `Core/GameClock.cs`, `Core/RubbishTypes.cs`, `Core/InteractionZone.cs`, `Core/RubbishAssets.cs` |
| **Off-limits right now** | `Assets/Scripts/Sorting/**`, `PlayerController.cs`, `Core/TrashCarrier.cs`, `Editor/Setup_SortingStation.cs` (the first-person sort-station work is in progress there), `Assets/Scripts/Character/**` (character build) |
| **Flow files** (`TruckControllerV2`, `SceneController`, …) | read-only for everyone in this update — use public APIs/fields |
