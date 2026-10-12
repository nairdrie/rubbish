# The Lucky Raccoon (casino + bar)

Status: built and integration-checked in a private copy (2026-10-09); not set up in the live project yet (steps in §0.4). This doc is the contract between the parts; keep it current.

## 0. Current state (read this first; where an older section below disagrees, THIS section wins)

The doc grew in layers: design (§1-§6), world (§7), QA fixes (§7b), shared tables (§8), seat mode (§9), host and wire (§10), presenters (§11), controllers (§12). The
uGUI screens of §3 / §7b / §8.1 were retired by the owner's seat-mode decision (§9) and no longer exist; passages that describe them are marked SUPERSEDED.

### 0.1 Final architecture

| Layer | What it is now | Where |
|---|---|---|
| Rules | Pure, deterministic given a dice function; no Unity scene access. Blackjack (6 decks, H17), American roulette, five slot machines (exact strips), drink menu, shared-table state machines and seats. | `Casino/Rules/**` |
| Host | ONE blackjack table `bj0` (5 seats), ONE roulette table `rl0` (4 spots), five single-occupancy machines `slot0..4`, the bar. All money in cents through `Ledger` (leisure ids: a night out is not business income). A guest never rolls or credits itself; solo is the same code with the local machine as host. | `Casino/Net/CasinoTables.cs`, `CasinoHost.cs`, `CasinoBank.cs` |
| Wire | `CasinoNet` (`[NetSessionComponent]`, RPCs `rb.casino.*`, guarded, byte snapshots per table to every client, hole card sent as 255 until revealed); `NetVersion.Protocol` 21 (shared bump with `DrunkNet`). `CasinoClient` is the one facade controllers and presenters use. | `Casino/Net/CasinoNet*.cs`, `CasinoClient.cs` |
| World | `Casino.unity` (pocket interior at world (325, -20, -640), built by `Rubbish/Setup/Casino`) plus the street front built at runtime on every machine (`CasinoFront`, from `Resources/Casino/CasinoFront.prefab`). Entered by a local fade-and-teleport through `CasinoDoor`; "inside" is positional. | `Casino/World/CasinoWorld.cs`, `CasinoScene.cs`, `CasinoFront.cs`, `CasinoDoor.cs`, `Editor/Setup_Casino*.cs` |
| What everyone sees | Presenters draw ALL table state (cards, chips, wheel and ball, reels, name tags, hand totals, results, the bar's glasses) from the replicated snapshots, on every machine. World-space lettering is pooled TextMeshPro (`CasinoLabels`); `CasinoFacing` turns every label to whichever camera is rendering it (name tags about the vertical axis only, totals and results fully), so nothing is ever seen mirrored. | `Casino/World/*Presenter.cs`, `CasinoLabels.cs`, `CasinoPaint*.cs`, `CasinoProps.cs` |
| Local play | Seat mode only: `CasinoStation.OnInteract` -> `CasinoControl` -> `CasinoClient.Sit` -> one controller per game + `CasinoSeatCamera` (first-person pose, look limits, lean, watch). Screen furniture: the cash readout (held until reveals finish), ONE hint strip (above the HUD's own bottom dock) and the reticle. Everything else is a 3D thing you point at: chip rack, lit action plates, ghost chips, the bar's menu card. | `Casino/Control/*.cs`, `CasinoSeatCamera.cs`, `CasinoCursor.cs` |
| Drunk | Level 0..1.2, post-FX volume, camera sway (through `RabiesCameraSway`, so it rides on the seat camera too), walking and driving wobble, replicated sway on puppets, pass-out and cab fare. | `Status/Drunk/**` |

### 0.2 Controls (keyboard + mouse / pad; the hint strip always shows the live subset)

Everywhere: mouse or right stick looks within the spot's limits; **hold Shift / LT** lean; **hold V / D-pad up** watch (dealer, wheel, pay table, bartender); **Esc / B / Start** leave.
Chips: Q / E, wheel, 1-5 (1-4), LB / RB, or click the rack.

| Game | Do |
|---|---|
| Blackjack | Click the bet circle or Space: stack the selected chip (RMB / Backspace: take one off, C clear, R last hand's bet, Enter deal now, O sit out). Then the lit 3D plates (Hit / Stand / Double / Split / Insurance; at the EYE pose they are the tilted row on the felt edge, labelled with the extra stake, e.g. `DOUBLE +$50`) or Space hit, S stand, D double, P split, I / N insurance, H basic-strategy line. |
| Roulette | Free cursor: LMB drops the selected chip on the spot under it (a big see-through ghost chip with a gold outline shows where, with "pays N to 1"), RMB takes the last chip off, Z undo, C clear, R rebet, Space / Enter or the READY plate ready. |
| Slots | Space, a click, the lever or the SPIN plate pulls; hold 0.5 s for auto-spin (never faster than 2.6 s). One player per machine; an "X is playing" tag sits on top of the cabinet and a red IN USE lamp (lens plus halo) shows from across the room. |
| Bar | Click a drink on the card (or 1-4); hover shows price and effect. |

### 0.3 Odds (real, nothing rigged, no streaks)

| Game | Rules | House edge / return |
|---|---|---|
| Blackjack | 6 decks, dealer hits soft 17, blackjack 3:2, insurance 2:1, double any two cards (and after split), split to 3 hands, split aces one card, no surrender, $5-$200 | about 0.6% with basic strategy (measured by a seeded test) |
| Roulette | 38 pockets (0, 00), straight 35:1, split 17:1, street 11:1, corner 8:1, six line 5:1, top line 6:1, dozen / column 2:1, even money 1:1; $1 min, $100 inside / $500 outside per spot, $1,000 per spin | 5.26% on everything except the top line (7.89%) |
| Slots | 3 reels, one line, fixed virtual strips; Lucky Sevens $1 95%, Trash Panda Bandit $1 92%, Golden Gnome $5 90%, Dumpster Fire $5 88%, Mystery Bag $25 93% | exact (a test enumerates every stop combination, +/- 0.5 points) |
| Bar | Flat Beer $6 (+0.12), House Whisky $10 (+0.20), Dumpster Fire $14 (+0.30), Water $1 (-0.10) | n/a |

### 0.4 Setup steps for the live project (Editor stopped, not in play mode)

1. `Rubbish/Setup/Casino`: builds `Assets/Scenes/Casino.unity`, `Assets/Materials/Generated/Casino/**`, `Assets/Resources/Casino/CasinoFront.prefab`, adds the scene to Build Settings (about a minute; a "main thread timed out" message from a CLI is harmless, the build carries on).
2. `Rubbish/Setup/Coop`: adds `CasinoNet` and `DrunkNet` to the session prefab (`NetSession.prefab`), "1 change(s) saved".
3. Save (File > Save Project) and press Play. Expect the project's "Town edits need baking" prompt (several town-dependent scripts and assets changed): choose **Bake and play**. The casino itself needs no bake; the street front is runtime.
4. Everyone in a co-op session needs the same build (Protocol 21).

### 0.5 Known limits and risks

- Co-op was checked on one machine over loopback only (bot harness and, where noted in the final report, an Editor guest); never on two real machines or over Relay.
- Seat poses are tuned for 16:9 (checked at 1080p and, supersampled, 4K; the hint strip scales with the UI scale setting). A much taller or ultrawide window shows more or less table above the strip.
- Station-mode HUD toasts (drunk stage changes, big wins) stack in the same bottom lane as the hint strip and cover it for their 2-3 seconds.
- World lettering (tags, totals, results) is TextMeshPro at fixed world sizes: legible across the room (about 10 m) but not distance-scaled.
- Remote pass-outs are not replicated: a remote controller sends `Leave` itself (§10.2).
- The keyboard Esc path is verified by the scripted-input driver (`CasinoInput.Inject`), not by a physical keyboard.
- `TruckProgression.cs:55` carries a `coop:allow(scene)` marked NEEDS LEAD APPROVAL; it predates the casino. `Tools/coop_lint.sh` (whole tree) also flags one new `[random]` hit in `Shop/Truck/TruckModelVisuals.cs:325` (a fixed-seed cosmetic generator, another workstream's file).

### 0.6 Verification, final pass (2026-10-09, private copy `~/Dev/Rubbish-build`, never the live Editor)

- EditMode: the whole suite, 1,584 / 1,584 green (about 2 minutes); `Casino*` 189, `Drunk*` 12, `CollectibleLoot*` 7, `CoastalDistrictRules*` 15. New: labels never face away from the viewer (any camera, high or low), the hint strip's stack height.
- Solo, through the real controllers' entry (`CasinoStation.OnInteract`, scripted keys and cursor): blackjack (6 rounds with double / split / insurance as the basic-strategy line chose), roulette, slots (6 spins, auto-spin pacing), the bar; money start + sum of results = end every time; Esc leaves and clears the seat, the hold on the HUD cash and the busy flag. Street door (F, the real interaction path) in and out, night street front (music louder, door light on, searchlights), a pass-out inside the casino (cab fare $40, wakes in the garage), drunk look and sway on the seat camera.
- Co-op over loopback with the bot harness (a real host and two guests, `--brain casino`, the build carries Casino.unity so every request is range-checked against the real stations): host and bot 2 at blackjack (two seats, the hole card never in a snapshot early), bot 2 leaving mid-hand is settled, all three at the wheel, three machines with the third refused on an occupied one; each bot's cash equals the nets its public pictures show. All three PASS. The audit's only failures are the project's known "Unable skin the associated mesh ... Cloth" errors.
- Performance in the Editor at 1736 x 918 (a build is faster): 2.1 to 5.5 ms a frame, 59 to 143 SetPass calls, 29 real-time lights in the room (2 with shadows), the roulette seat is the heaviest view (5.2k batches, 1.2 M triangles).

## 1. What it is (design; still current)

An old-school, slightly dingy bar-casino in Coupon Commons that the player can walk into. Dark wood panelling, patterned
carpet, brass rails, warm pendant lamps, framed pictures, neon SLOTS and COCKTAILS signs. Not crowded: a bartender, a
blackjack dealer, a roulette dealer and a handful of seated regulars.

Inside:
- a bar with a bartender who sells drinks (drinking makes you drunk, §5);
- one blackjack table with a dealer;
- one roulette table with a dealer;
- five slot machines, each with its own flavour, maths and return.

Odds are real casino odds. Nothing is rigged per player, there is no pity and no streak logic.

## 2. World and scene

- New scene `Assets/Scenes/Casino.unity`, built only by `Rubbish/Setup/Casino` (`Assets/Scripts/Editor/Setup_Casino.cs`),
  idempotent, one root rebuilt each run, added to Build Settings. No in-scene NetworkObjects.
- It is a pocket interior like the Depot Row interiors: loaded additively beside the town, far from it, entered by a
  local fade-and-teleport. Co-op cannot load a scene for one player (Multiplayer.md §scenes), so there is never a
  per-player scene swap. Players inside see each other because the pocket shares the world's coordinates.
- Street side: a casino front on Coupon Street (Coupon Commons) with a neon sign and a door, built at runtime on every
  machine (no town bake), following `DumpsterRuntime` / `ScavFence`. Uses `TownRelief.Height` for ground height.
- The door is an `IInteractable` + `IAltKeyPinned` (F, like every door). Inside, an exit door returns the player to the
  street. Indoor lighting is switched locally while inside.
- Always open (the game has no opening hours).
- Modelling: no raw Unity primitives for hero props. Use `TownLifeKit` recipes (editor-built, merged meshes) for the room,
  bar, tables, stools, machines, lamps, frames; runtime kits (`HogMesh`, `ItemVisuals`) for chips, cards, the wheel ball.
- Staff and regulars are `PuppetRig` + `ResidentLook` puppets with custom outfits (waistcoat and bow tie for staff),
  procedural idle poses, and short barks through the resident line system.

## 3. Code layout

| Area | Files |
|---|---|
| Pure rules (no Unity scene access, deterministic given a dice function) | `Assets/Scripts/Casino/Rules/BlackjackRules.cs`, `RouletteRules.cs`, `SlotRules.cs`, `CasinoRules.cs` (limits, ids, drink menu) |
| Host authority | `Assets/Scripts/Casino/Net/CasinoNet.cs` (`[NetSessionComponent]`, RPCs `rb.casino.*`) |
| Local controllers (seat mode, §12; the uGUI screens are gone) | `Assets/Scripts/Casino/Control/*.cs` |
| World | `Assets/Scripts/Casino/World/CasinoWorld.cs` (load, enter, exit), `CasinoFront.cs` (street front + door), `CasinoStation.cs` (table, machine and bar interactables), `CasinoStaff.cs` |
| Scene builder | `Assets/Scripts/Editor/Setup_Casino.cs` |
| Drunk | `Assets/Scripts/Status/Drunk/DrunkRules.cs`, `DrunkStatus.cs`, `DrunkPostFx.cs`, `DrunkDriving.cs`, `DrunkLook.cs`, `DrunkNet.cs` |
| Tests | `Assets/Tests/EditMode/Business/Casino*Tests.cs`, `DrunkRulesTests.cs` (reflection into Assembly-CSharp, like `HogRulesTests`) |

Contracts between parts:
- `CasinoStation` raises `public static event Action<CasinoStation> Used` when the local player interacts. It exposes
  `Kind` (`Blackjack`, `Roulette`, `Slot`, `Bar`), `Index` (slot machine 0..4, otherwise 0) and a `Seat` transform. The
  controllers subscribe (`CasinoControl`, §12); the world code knows nothing about them. (SUPERSEDED wording: "screens", retired in §9.)
- `DrunkStatus.Drink(float units)` adds drink units for the local player; `DrunkStatus.Level` (0..1.2) is the current
  level. `BarController` (§12) calls `Drink` only after the host confirms the purchase.
- Rules classes take a `Func<float>` (0..1) or `Func<int,int>` dice so the host can pass host dice and tests can pass a
  seeded generator.

## 4. Games and odds

Money: stakes come from the player's business cash through `Ledger` (new `LedgerIds`: casino wager, casino payout, bar).
The host validates funds and limits, charges the stake, rolls with host dice, credits the payout and replies. A guest
never rolls and never credits itself. Solo runs the same path with the local machine as host. All RPCs are guarded
(`rb.casino.*`), and changing them bumps `NetVersion.Protocol`.

### 4.1 Blackjack
- Six-deck shoe, real cards dealt without replacement, reshuffle at the cut card (about 75% dealt).
- Dealer hits soft 17. Blackjack pays 3:2. Insurance 2:1 is offered against an ace.
- Double on any first two cards, double after split allowed, split up to three hands, split aces get one card each.
- No surrender. Dealer peeks for blackjack on an ace or ten.
- House edge with basic strategy is about 0.6%. A test plays basic strategy over a large seeded sample and asserts the
  measured edge is between 0.2% and 1.2%.
- Limits: $5 to $200 per hand.

### 4.2 Roulette
- American wheel: 38 pockets (0, 00, 1 to 36), standard red/black layout.
- Bets and payouts: straight 35:1, split 17:1, street 11:1, corner 8:1, six line 5:1, top line (0, 00, 1, 2, 3) 6:1,
  dozen and column 2:1, red/black, odd/even, low/high 1:1. 0 and 00 lose all outside bets.
- House edge 5.26% on every bet except the top line (7.89%). A test enumerates all 38 outcomes for every bet type.
- Limits: $1 minimum per spot, $100 maximum on an inside spot, $500 on an outside spot, $1,000 per spin in total.

### 4.3 Slots
Three reels, one pay line, fixed virtual reel strips and a pay table per machine. Return is exact: a test enumerates
every reel combination and asserts each machine's return against its target (within 0.5 points).

| # | Machine | Flavour | Stake | Target return | Feel |
|---|---|---|---|---|---|
| 0 | Lucky Sevens | classic bars and sevens | $1 | 95% | frequent small hits |
| 1 | Trash Panda Bandit | raccoons, masks, loot bags | $1 | 92% | medium |
| 2 | Golden Gnome | gnomes, toadstools, gold | $5 | 90% | rare big jackpot |
| 3 | Dumpster Fire | flames, bins, hazard cans | $5 | 88% | streaky, lots of near misses that are honest (strip driven) |
| 4 | Mystery Bag | sacks, question marks, the odd treasure | $25 | 93% | high roller, wide spread |

Near misses must come from the strips only; the display always shows the real stop positions.

## 5. Drinks and being drunk

Menu (bartender): Flat Beer $6 (0.12), House Whisky $10 (0.20), Dumpster Fire cocktail $14 (0.30), Glass of Water $1
(sobers 0.10). Units add to the drunk level.

- Level 0..1.2, falling at 0.25 per game hour. Stages: Tipsy from 0.2, Drunk from 0.5, Wasted from 0.8.
- Vision: camera sway and slow spin (reuses `RabiesCameraSway`), blur and a little double vision. None of rabies' hue
  shift or green vignette. All scaled by `GameSettings.ScreenFx01`. If the player is also rabid, both run.
- Walking: drift, occasional stumble, slightly slower.
- Driving: steering wobble and lag on the truck and the Hoverhog, through their input overrides, only while the local
  player is in the seat.
- Others see it: a replicated drunk level drives a procedural sway on the remote puppet plus "*hic*" text. The stumbles
  and hiccup hops already replicate as movement.
- Passing out: from Wasted upward there is a rising chance per check; at 1.2 it is certain. It reuses the rabies
  pass-out flow: the camera tips, the screen fades, and solo you wake at the garage with a $40 cab fare; in co-op you
  flop where you stand for up to 30 s (friends can carry you). You wake at 0.25 (hung over, not sober).
- Not saved to disk, like rabies today.
- No edits to `PlayerController.cs` or `Shop/TruckHopper.cs` (they force a town bake prompt).

## 6. Setup steps for the live project

Run once in the Editor when not in a play session: `Rubbish/Setup/Casino` (builds the scene and registers it), then
`Rubbish/Setup/Coop` (adds `CasinoNet` and `DrunkNet` to the session prefab). No town bake is needed for the street
front. The protocol number goes up, so everyone in a session needs the same build.

## 7. World side as built (2026-10-09)

- **Files:** `Casino/World/CasinoLayout.cs` (ids, origin, bounds), `CasinoWorld.cs` (+ `CasinoRunner`), `CasinoScene.cs` (scene root:
  interior on/off, indoor lighting, runtime grade Volume), `CasinoFront.cs`, `CasinoDoor.cs`, `CasinoStation.cs`, `CasinoStaff.cs`,
  `CasinoNeon.cs`, `CasinoMusic.cs`; `Editor/Setup_Casino*.cs` (own modelling kit: TownLifeKit writes into the town's asset
  folder, so it is not used), `Editor/CasinoDebugMenu.cs`. Generated assets: `Assets/Materials/Generated/Casino/**`,
  `Assets/Scenes/Casino.unity`, `Assets/Resources/Casino/CasinoFront.prefab`.
- **Scene loading:** the host loads `Casino` additively through NGO whenever the town is up (after Depot Row, never during a
  door transition); guests and late joiners get it from NGO; the next Single load unloads it. Same mechanism as `DepotRow`.
- **Pocket:** room floor centre at world (325, -20, -640), 22 x 12 x 3.6 m. "Inside" is positional (`CasinoLayout.Contains`),
  so leaving by a pass-out, Unstuck or a scene change needs no clean-up. `DepotRowWorld.LocalIndoors` includes it.
- **Street front:** centre bay of the decorative storefront parcel `coupon-opposite-east`, north side of Coupon Street, door
  at about (271.8, 2.29, 482.3) facing south. Runtime prefab, no colliders, no bake.
- **Station contract:** see the tooltips in `CasinoStation.cs` (seat, view; slot: `reels[3]` named Reel0..2, `lever`,
  `coinTray`, `glow`; roulette: `wheelCentre`, `wheelRotor`, `ball`, radii, `layoutOrigin`, `layoutCell`, `LayoutPoint(n)`,
  `WheelOrder`, `PocketAngle(n)`; blackjack: `dealerCards`, `playerHands[3]`, `chipSpot`, `shoe`, `discard`, `chipTray`;
  bar: `counterSpot`, `pourSpot`). The seat controller sets `CasinoStation.LocalBusy` while seated (SUPERSEDED: it used to be a screen). Reel drums: 12 faces, see
  `CasinoLayout.ReelFaces`; (SUPERSEDED: the drum art is now painted from the real strips by `CasinoPaint.SkinReel`, §11.)
- **Staff hooks:** `CasinoStaff.Dealer(kind).Play(DealerGesture)`, `CasinoStaff.Bartender.Play(BartenderGesture)`, `.Say(line)`,
  `.Hand`. Never null.
- **Lettering is TextMeshPro, ASCII only** (anything else would dirty the dynamic fallback font atlas).

## 7b. Changes from the integration QA pass (2026-10-09; the HUD-cash and screens items are SUPERSEDED, the ledger and world items still hold)

- **A night out is not business income.** `LedgerIds.IsLeisure` (casino wager, casino payout, bar tab, cab fare): these
  postings are moved out of the day's credits / charges by `DailyStats.MoveLeisure`, so "Net income" (and with it the next
  day's Work Order pay) ignores them, casino winnings are taken back out of `GameSession.TotalCashEarned`, and they earn no
  payroll share (`PayrollShareRules.EligibleCredit` is a whitelist). The Career page's Shift report lists them on rows of
  their own ("Casino paid out", "Casino stakes", "Bar": drinks and the cab home), today and yesterday. Counters
  `st0_/st1_casino_in`, `casino_out`, `bar`. Test: `CasinoLedgerTests`.
- **The HUD keeps the secret.** (SUPERSEDED in who calls it: `CasinoClient.HoldHudCash(bool)` now, from the controllers, §12.4.) `FeelHud.HoldCash(holder)` / `ReleaseCash(holder)` freeze the HUD's cash readout (and its
  counting clicks) from the moment a screen sends a request until its reveal has played; every cash change in between is
  shown as one change on release. `CasinoScreen.HoldCash` / `ReleaseCash` / `HoldHudCash` drive it; a hold nobody releases
  ends after 15 s or when the screen is destroyed.
- **World:** you arrive 2.9 m inside the south wall (`CasinoLayout.EntranceLocal`; any closer and the 5 m camera boom
  arrives pressed into the player's back). Interaction zones reach 0.3 m below the floor, and the street door's zone 1 m
  below its sill (the apron slopes with the relief: the player's feet are a few cm under the door's base). The slot
  cabinets' colliders are on the walls layer so the camera stops at them. Reel 0 is on the player's left and the lever on
  their right, like the screen. The table lamps' lights sit 0.3 m toward the players (the dealer bloomed out to white).
- **Screens (SUPERSEDED: the uGUI screens are gone; the same fixes live on as `CasinoRouletteLayout.SpotAt` and the presenters):** the "2 to 1" column boxes take the whole box (the first 16 px used to bet 34-36); the roulette win labels
  are staggered; the blackjack felt's print goes once cards are dealt; the bar's price sits on the name's line.
- **Drunk + rabid:** when the drunk look forced the camera's post-processing flag and ends while rabies still shows, the
  flag is remembered and put back when rabies ends too (`DrunkPostFx.SettleCamera`). The street door, the exit door and the
  stations refuse to be used while `DrunkStatus.PassingOut`.

## 8. Shared tables, occupied machines, spectators (design, 2026-10-09)

Goal: co-op players can sit at the same blackjack table, share one roulette wheel, and use slot machines one player at a
time, and everyone in the casino can SEE what is happening (cards, chips, ball, reels, wins). Pure rules first
(`Casino/Rules/BlackjackTable.cs`, `RouletteTable.cs`, `CasinoSeats.cs`), then host/net/presenters/screens.

### 8.1 Principles
- The host owns every table: seats, shoe, dealer hand, timers, dice, money. Guests send intents; the host replies.
- Everything a spectator sees is derived from a small PUBLIC snapshot per table that the host broadcasts to every client
  whenever it changes (plus on late join). The only hidden values are the shoe order and the dealer hole card (sent as
  255 until it is revealed, to everyone, seated or not).
- 3D presentation (cards on the felt, chips, wheel and ball, reel drums, win lights, name tags) is done by PRESENTER
  components that run on every machine from the snapshots. The local controllers (§12; SUPERSEDED: "uGUI screens") only send intents and read the same
  snapshots; they never draw the 3D table themselves.
- Solo is the same code with one seat and no network: the local machine is the host.
- Money is charged by the host in cents through `Ledger` inside `Ledger.ToBusiness(own)`; payouts are credited the same
  way, including to a player who walked away mid-hand (disconnected clients' winnings count as `UnpaidCents`).

### 8.2 Blackjack (one table, 5 seats, shared shoe and dealer)
- Seat assignment: using the table takes the nearest free seat (the seat's stool position and facing); full table =
  "Table full" toast; a seated player leaves with Esc/B or by walking away.
- One six-deck shoe for the table (reshuffled between rounds at the cut card, ~75%). One dealer hand per round.
- Phases: Idle → Betting (15 s from the first bet; ends early when every seated player has bet or sat out) → Deal
  (all seats with a bet, two cards each, dealer up card) → Insurance (8 s, simultaneous, only if the dealer shows an ace;
  then the dealer peeks) → Turns (seat order 0..4, each seat plays all its hands; 25 s per decision with 2+ players in the
  round, 120 s idle with one; timeout = Stand, insurance timeout = decline) → Dealer (one card per 0.8 s) → Settle (net
  per hand per seat, 5 s on screen) → Idle/Betting again.
- Rules unchanged from §4.1 (H17, 3:2, DAS, split up to 3 hands, split aces one card, no surrender, whole-dollar stakes
  $5–$200). House edge with basic strategy stays about 0.6% per seat.
- Joining mid-round: you may sit but wait for the next Betting phase. Leaving: before the deal the stake is refunded;
  after the deal the seat auto-stands (and declines insurance) and settles normally in absentia.

### 8.3 Roulette (one shared wheel per table)
- Participants are players seated at the roulette table (up to the co-op cap of 4; was: "with the roulette screen open"); everyone else is a
  spectator. Chips on the felt are coloured per player and visible to all.
- Phases: Idle → Betting (25 s from the first bet, countdown shown on the hint strip and on a 3D lamp; ends early when every
  participant has pressed Ready) → Locked (host charges each participant's confirmed bet set — unaffordable sets are
  cancelled with a toast — then spins with host dice) → Spinning (7 s; wheel and ball animation is derived from the pocket
  and a start time so every machine animates identically) → Result (6 s: marker on the winning number, wins paid, shared
  history of the last 12) → Idle.
- Bets are re-sent as a whole set whenever they change (guarded), replace the previous set, and may be undone/cleared
  until Locked. Leaving during Betting drops your set; after Locked it stands and pays in absentia.
- Limits unchanged (§4.2).

### 8.4 Slots (one player per machine)
- A machine has at most one occupant, assigned by the host on request; the prompt shows "In use · <name>" to others. The
  occupant is released when they leave the seat, walk away, pass out, disconnect, or after 3 minutes without a spin.
- Every spin is broadcast as an event {machine, stop0..2, win, who, start time}; presenters on every machine animate the
  three 3D drums with the same stagger and stop on the true strip positions, light the glow, play the coin sound and show a
  floating "+$X" over the machine; wins of ≥ $1,000 or ≥ 50× add the celebration lights. A name tag shows who is playing.
- NPC regulars: two of the five machines have a cosmetic regular pulling the lever now and then (cosmetic randomness,
  annotated) so the floor looks used. A regular steps away when a real player takes the machine and returns after 60 s idle.

### 8.5 Wire
- One `[NetSessionComponent]` (`CasinoNet`) keeps all RPCs under `rb.casino.*` guards. Public state goes out as a compact
  byte snapshot per table via a host→all RPC, and a client asks for the current snapshot on entering the casino. Intents
  (sit, bet, act, ready, confirm bets, spin, leave) go guest→host and are validated (proximity, seat ownership, legal action,
  limits, affordability, rate). `NetVersion.Protocol` stays 21 (the feature is unreleased).

## 9. Seat mode: no pop-up screens (owner decision 2026-10-09; supersedes the uGUI screens in §3 and §8.1)

The tables are played IN THE WORLD. Using a table, machine or the bar swoops the camera to a first-person view at that
station and you play with the physical things in front of you. The 3D presenters from §8 are the game; there is no modal
panel, no dimmed background and no screen-sized window.

- Enter: interact at the station → the player is placed at the station's standing spot (blackjack/roulette: standing at the
  rail; the stool at an occupied spot is slid out of the way), movement is locked, the camera glides (about 0.6 s) from the
  normal third-person camera to the station's EyePose (first person at eye height, head hidden), then mouse / right stick
  looks around within limits (yaw ±55°, pitch −70° to +30°) so you can watch the dealer, the wheel, the other players.
  Leave: Esc / B, or the host releases you (pass out, teleport, session end); the camera glides back.
- Allowed screen furniture: the normal HUD cash readout (held until reveals finish), ONE thin hint strip along the bottom
  edge (key/button hints for what you can do right now, no background panel beyond a soft shadow), and small WORLD-SPACE
  labels that every player sees: hand totals, bet amounts, a betting countdown lamp, names. Nothing else.
- A world cursor: the mouse (or a gamepad stick-driven reticle) raycasts onto the table; the thing under it highlights
  and shows a floating label (e.g. the roulette spot with its payout, a chip denomination). Click/A acts on it.
- Blackjack: your chips sit in a rack in front of you; Q/E, scroll or D-pad changes the selected denomination; click the
  betting circle (or Space) to stack that chip, right click removes; the round starts by itself when the betting timer ends
  or everyone is ready; Hit / Stand / Double / Split / Insurance are keys/buttons shown on the hint strip (and can be
  clicked on 3D buttons at your spot); cards, chips and the dealer's hands are the 3D props; a hold-to-lean key zooms the FOV in
  on your hand. Optional strategy hint is a small floating glyph over your hand, off by default.
- Roulette: lean over the felt layout (EyePose looks down at it), hover a zone to see the bet and its payout, click to drop the
  selected chip; chips are 3D stacks in the colour of their owner, visible to all; wheel, ball, marker and the result
  history lamp board are 3D; look up to watch the spin. Undo/Clear/Rebet/Ready are keys on the hint strip.
- Slots: the camera sits at the machine; Space/click/A pulls the 3D lever (animated), the three drums spin and stop on the
  true stops; hold to auto-spin (min 2.6 s); the pay table is a lit plate on the machine (generated from the real
  SlotRules data, including "Returns xx.x% over the long run"); a payout shows as coins, lights, glow and a floating total.
- Bar: stand at the counter, look at the drinks board (the chalkboard on the wall behind the bar, or a menu card on the counter),
  hover a drink to see its price and effect, click to order; the bartender pours and slides it over, you drink with the
  existing animation/effect, the drunk meter is the existing HUD indicator.
- Everything (host rules, money, snapshots, seat/occupancy) stays as §8; only the local controller changes: a `StationSeat`
  camera/input mode plus per-game controllers that read the replicated snapshots and send the same intents. The old uGUI
  screens are retired, and their reusable parts (hit-zone mapping, card/chip art, formatting) move into the controllers.
- Drunk effects apply to this camera like any other (sway, blur); lean and wobble must not break picking (cursor picking
  uses the final camera).

### 9.1 World prep for seat mode: the contract (built 2026-10-09)

Everything a controller or presenter needs is a named field on `CasinoStation` (`Casino/World/CasinoStation.cs`) or on its
`CasinoSpot`s (`CasinoSpot.cs`); `Setup_Casino` builds them, nothing needs finding by name. Rebuild with `Rubbish/Setup/Casino`.

**Eye poses as tuned in the final polish (the fields and ids are unchanged):** blackjack eye at 1.76 m from the dealer's arc centre, aimed at 1.20 m (about 34 degrees down), `eyeFov` 66, so the chip rack and the action plates sit in the picture above the hint strip; the five plates are 0.112 x 0.052 m, tilted 26 degrees toward the eye (front edge on the felt), 0.122 m apart, at radius 1.372 (any nearer the rim and the leather rail hides them). Roulette eye at the body's own spot (z -1.21), aimed at z -0.70 (about 34 degrees down), `eyeFov` 64; the rack on a 14-degree slant on the player side of the ledge, the four plates (tilted 28 degrees) on the felt side. Bar eye aimed at the menu card's centre. The hint strip is 14 reference px off the bottom (above the HUD dock), rows 38 px apart, shade 210 px at 42%.

**Spots** (`station.spots[]`, one `CasinoSpot` each; `stand`, `eye`, optional `lean` / `watch`, `eyeFov` / `leanFov` / `watchFov`,
`lookYaw` (+/- degrees around the pose), `lookPitchMin/Max` (ABSOLUTE degrees above the horizon), `floatText`, `stool` + `stoolAway`):

| Station | Spots | Order | Notes |
|---|---|---|---|
| Blackjack | 5 | 0 = dealer's left = players' right (+X, first base) ... 4 | stand on a 1.84 m circle round the table, facing it; `stool` slides `stoolAway` (0.66 m outward, `SetStoolAway(true)`) |
| Roulette | 4 | left to right as the players see the table | all four on the near long side (the felt is printed for that side; the dealer is opposite); `watch` = the wheel |
| Slot (index 0..4) | 1 | | `watch` = the pay table plate; `stand` is the old `Seat` |
| Bar | 3 | south to north | `watch` = the bartender |

Felt-side transforms are in the PLAYER'S frame (+X right, +Z away from the player toward the dealer, +Y up):
`betCircle`, `insuranceSpot`, `handSpots[3]` (`CasinoHandSpot`: `root`, `cards[5]` fanned along the row and tilted 14 degrees toward
the player, `floatText`), `chipRack[]` (`CasinoLayout.BlackjackChipCents` = 5/10/25/50/100 dollars, `RouletteChipCents` = 1/5/25/100),
`buttons[]` (`CasinoLayout.BlackjackButtonIds` = Hit, Stand, Double, Split, Insurance; `RouletteButtonIds` = Ready, Undo, Clear,
Rebet; `SlotButtonIds` = Spin, BetUp, BetDown; `spot.Button("Hit")`), `plate` (the numbered brass plate). Bar: `counterSpot`,
`pourSpot`, `drinkSlots[4]` (`CasinoDrinkSlot`: `button`, `nameText`, `priceText`, `effectText`, `Set(name, price, effect)`), a menu card
on the counter in front of each bar spot (so each spot reads its own, 0.5 m away).
Station-wide: blackjack `dealerSpots[6]` (tilted 26 degrees toward the players), `dealerFloatText`, `shoe`, `discard`, `chipTray`, `printRoot`
(the felt lettering; hide it while cards cover it); `timerLamp` (blackjack, roulette: `CasinoTimerLamp.SetValue(0..1)`, 12 bulbs, green to
red), `limitsText` (blackjack MIN / MAX sign, roulette plaque); roulette `resultBoard` (`CasinoResultBoard.SetHistory(newest first, 0..36,
37 = 00)`), `winMarker` (`PlaceWinMarker(n)` / `HideWinMarker()`), `layoutPlane`; slot `payPlate` (`CasinoPayPlate`: `SetTitle`, `SetLine(i 0..13,
combo, payout)`, `SetReturns`, `SetStake`), `spinButton`, `betUpButton`, `betDownButton`, `leverPick` (a `CasinoPickable`, kind Lever, rides the
lever; the lever pulls about +70 degrees on local X), `coinTray`, `coinTrayLight`. The old single-seat members (`seat`, `view`,
`dealerCards`, `playerHands`, `chipSpot`, `counterSpot`, `pourSpot`) still exist and point at the middle seat / spot. Prop sizes (1.6x
real): `CasinoLayout.CardWidth/Height` 0.10 x 0.14 m, `ChipDiameter` 0.064, `ChipThickness` 0.0065; a card lies face up with its width
along local X; a chip stack grows along local Y.

**Picking.** Every pick collider is a trigger box on `CasinoLayout.PickLayer` (2, the stock "Ignore Raycast" layer: ordinary raycasts,
the interaction scan and the camera ignore it) carrying a `CasinoPickable` (`kind`, `station`, `spot`, `index`, `id`, `hint`; kinds:
Button, BetCircle, ChipBin (`index` = denomination), Layout, Lever, DrinkSlot) or its subclass `CasinoButton` (`id`, `label`, `SetLit`,
`SetEnabled`, `Press()`, `Pressed` / static `AnyPressed`). Raycast with `CasinoLayout.PickMask` and `QueryTriggerInteraction.Collide`.
`CasinoCursor` (`Create()`; `cam`, `Position`, `Ray()`, `Hovered`, `HoverChanged`, `Clicked` with primary = mouse 0 / pad A or RT and
secondary = mouse 1 / pad X or RB, `autoPressButtons`, `ghost`, `SuppressFor`) does it through `Camera.ScreenPointToRay` of the real
camera; with the OS cursor locked the mouse position is the screen centre, with the pad the left stick moves a virtual cursor.
**Roulette layout coordinates:** `station.layoutOrigin` is the corner of number 1's cell; `LayoutHitLocal(world, out u, out v)` /
static `CasinoStation.LayoutHit` return CELL units (u along the layout: 1's cell is u 0..1, 34-36 is 11..12, the "2 to 1" boxes 12..13,
0 / 00 u -1..0; v away from the players: 1, 4, 7 are v 0..1, 3, 6, 9 are v 2..3, dozens v -0.72..0, even-money boxes -1.44..-0.72);
`LayoutPoint(u, v)` / `LayoutPoint(number)` invert it. `CasinoRouletteLayout.SpotAt(u, v)` (§11.1) is the QA'd
hit-zone mapping on the same numbers (it came from the retired `RouletteScreen`: CW = 80, CH = 86, OutH = 62).

**Seat camera** (`CasinoSeatCamera.Ensure().Enter(station, spot)` / `Exit()`, `Lean(bool)` / `ToggleLean()`, `Watch(bool)`, `Mode`
Locked | Free, `Entered` / `Exited(reason)`): a runtime Cinemachine virtual camera (priority 60) blended in over 0.6 s through a pass-through
`GetBlendOverride` hook (the way `SortStation` does it); the brain stays in charge, so drunk / rabies sway, shake, post-processing and
the fade apply on top. It disables the third-person controller and CharacterController, slides the player to `stand` facing the table
(replicates through the owner's NetworkTransform), hides the local renderers, slides the stool away, claims
`PlayerController.InteractionFocusOverride` (which is also what makes `UiStack` leave Esc to it: Esc and pad B / Start leave the seat first),
and sets `CasinoStation.LocalBusy`. Forced exits (cut, no glide): pass-out, door / scene transitions, teleport or being moved, shove /
flop / carry, driving, session end, scene unload, leaving the room. Look: mouse delta / right stick through the project's look setting
and invert; Locked mode also reads `StarterAssetsInputs.look`; Free mode pans at the screen edges or with the middle mouse button.
`CasinoControl` (§12) hooks it into `CasinoStation.Used`; the debug menu `Rubbish/Debug/Casino/Seat camera at ...` still enters and leaves it directly.

## 10. Host and wire (built 2026-10-09)

The host and network layer for §8's shared tables and §9's seat mode. The old per-client private rounds (own shoe, one hand,
one roulette spin, one pull per request) are gone; the bar still works as before.

### 10.1 Files

| File | What |
|---|---|
| `Casino/Net/CasinoTables.cs` | `CasinoTables`: ONE `BlackjackTable` (`bj0`), ONE `RouletteTable` (`rl0`), five `CasinoSingleSeat` (`slot0`..`slot4`), the money, proximity, presence sweep and output events. Pure (delegates for the till, dice, positions). Also `CasinoTableIds`, `CasinoResult`, `SlotEventInfo`, `CasinoSync`, `CasinoNotice`, `CasinoWire`. |
| `Casino/Net/CasinoHost.cs` | Wires `CasinoTables` and `CasinoBank` to `Ledger`, `NightDice.Host`, `PlayerBody`/`CasinoStation`; `CasinoHost.Tables`, `TableNow`; the 6 line `CasinoHostRunner` ticks it every frame. |
| `Casino/Net/CasinoNet.cs`, `CasinoNet.Tables.cs` | The `[NetSessionComponent]` (`rb.casino.*`): bar RPCs (unchanged) and the table RPCs. |
| `Casino/Net/CasinoClient.cs` | The facade the in-world controllers and presenters use (below). |
| `Casino/Net/CasinoBank.cs` | Now only the bar cashier + shared money helpers (`IsBig`, `Describe`, `BarRefuses`) + `CasinoError`, `BarView`. |
| `Casino/Net/CasinoSmoke.cs` | Dev-only bot brain `casino` (`#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD`): plays the tables over a real session in the headless bot harness. |
| `Tests/EditMode/Business/CasinoTablesTests.cs`, `CasinoBankTests.cs` | Host logic and bar tests. |

### 10.2 Host rules (`CasinoTables`)

- **Keys.** A player is the int client id, taken from the transport (`rp.Receive.SenderClientId`), never from a message: nobody can sit,
  bet or act for another seat. Solo and the host use their own `Permissions.LocalClientId`.
- **Time.** One host clock, `Time.realtimeSinceStartupAsDouble` (`CasinoHost.TableNow`). The host ticks every frame; the tables stamp events with
  the time a step was due. Snapshots carry `now`, `phaseDeadline`, `spinStart`; a slot event carries `start`.
- **Money, always in this order:** validate, affordable, charge, roll, credit. Charges and credits are `Ledger.Charge/Credit` inside
  `Ledger.ToBusiness(own)` with `LedgerIds.CasinoWager` / `CasinoPayout` / `BarTab` (so the day's Net income, payroll shares and
  `GameSession.TotalCashEarned` ignore gambling exactly as before, §7b). A blackjack bet, double, split and insurance are charged by
  the rules' `charge` delegate only after the rest validated; a refund (leaving before the deal, `BetRefunded`) goes back on the wager id.
  Roulette sets are only a promise until the lock; at the lock the rules draw the pocket from the host dice and then charge each set (an
  unaffordable one is cancelled: nothing charged, `CasinoNotice.SetCancelled` to that player).
  A slot pull charges, then draws three stops with `SlotRules.Spin(machine, Dice)`, then credits.
- **In absentia.** Every `BetRefunded` / `SeatSettled` / `ParticipantSettled` is credited to the event's key even if that player left or
  disconnected. If their business is gone the amount is counted in `UnpaidCents` (and `UnpaidFor(key)`), never lost silently.
- **One station at a time.** `Sit` stands the caller up from every other station first. `Leave` is always allowed (no range check).
- **Proximity.** `Sit`, `BlackjackBet`, `RouletteJoin`/`SetBets` and `SlotSpin` need the caller within 5 m of the station's `Seat` (flat) and
  inside the casino room (`CasinoHost.Near`, the QA'd tolerance; this machine's own player is trusted as before). `BlackjackAct`,
  `SitOut` and `Ready` have no range check (a hand in play must stay finishable). A presence sweep (twice a second, 1.5 s grace) stands a
  seated player up when their body is outside the room or more than 9 m from the table (teleport, carried off), or when the local player
  is passing out; they get `CasinoNotice.Released`. Remote pass-outs are not replicated, so a remote controller must send `Leave` itself.
- **Slots.** Single occupancy; the occupant keeps it until `Leave`, the sweep, a disconnect, or 180 s without a spin (`CasinoNotice.IdleRelease`).
  Host floor of 1 s between pulls. A spin that wins `>= $1,000` or `>= 50x` the stake carries `big = true`.
- **Big wins** (same rule everywhere, `CasinoBank.IsBig`): blackjack and roulette by the net win, slots by the pay; announced through the existing
  `CasinoHost.BigWinHeard` (host) / `BigWinRpc` (guests) toasts.
- **Session.** A client leaving stands up everywhere (hands play on, locked bets pay). Session start/stop closes the tables: everyone stands
  up, a round in play runs to its settlement at once and is paid, machines are freed.

### 10.3 `CasinoClient` (the same calls on host, solo and guest)

Intents take an optional `Action<CasinoResult>` that runs exactly once on the main thread (before the call returns on the host/solo; when
the host answers, or `Unavailable` after 8 s, on a guest). `CasinoResult { CasinoError gate; CasinoTableError error; RouletteBetError betError; sbyte seat; bool Ok; bool Seated }`;
`gate` is the request-level verdict (BadMachine, TooFar, NoBusiness, Throttled, Unavailable), `error` what the table said;
`CasinoTables.Describe(result)` words it.

```
Sit(string table, int preferredSeat = -1, cb)      Leave(string table, cb)
BlackjackBet(long cents, cb)  BlackjackSitOut(cb)  BlackjackAct(BlackjackAction, cb)
RouletteJoin(cb)  RouletteSetBets(RouletteBet[] bets, int count, cb)  RouletteReady(bool, cb)
SlotSpin(int machine, cb)    Drink(int drink, Action<BarView>)   RequestSync()   HoldHudCash(bool)
events: BlackjackSnapshotChanged(BlackjackTableSnapshot, double offset)  RouletteSnapshotChanged(RouletteTableSnapshot, double offset)
        SlotSpun(SlotEventInfo)  SlotOccupancyChanged(int machine, int occupantKey, string name)
        BigWin(int key, string who, string what, long cents)  Notice(string tableId, CasinoNotice)
queries: LatestBlackjack/HasBlackjack  LatestRoulette/HasRoulette  OccupantOfSlot(m)  LocalKey  LocalSeatAt(tableId)  NameOf(key)
        TableTime()  ClockOffset  ToLocalTime(hostTime)  ToTableTime(localRealtime)  Reachable  CashCents
```

`SlotEventInfo { machine, stop0..2, rule, winCents, stakeCents, key, start, big }`. Table ids: `CasinoTableIds.Blackjack` ("bj0"), `Roulette` ("rl0"), `Slot(i)`.
Call `RequestSync()` when the local player enters the room: it raises both snapshot events and the occupancy events once. A snapshot is the only
thing that carries table state; the host's own machine receives them with offset 0, and an older snapshot that arrives late is ignored.
`HoldHudCash(true)` BEFORE the request that can move money (a bet, a spin), `HoldHudCash(false)` once the reveal has played (it wraps
`FeelHud.HoldCash/ReleaseCash`; a hold nobody releases ends after 15 s).

**Mapping table time.** All times in snapshots and slot events are HOST seconds. `ClockOffset` is `local - host` for
`Time.realtimeSinceStartupAsDouble` (0 on the host and solo; on a guest it keeps the quickest delivery seen and drifts slowly up, so it is within one
network trip). A presenter animates from the host stamps: `elapsed = CasinoClient.TableTime() - snapshot.spinStart` (wheel and ball over `RouletteTable.SpinSeconds` = 7 s,
`SpinFraction(TableTime())`), a countdown lamp is `snapshot.phaseDeadline - TableTime()` (+infinity in Idle), reels start at `SlotEventInfo.start`
(stagger the three drums from it, then stop on `stop0..2`). Deal and dealer steps are already stamped by the table (`DealStepSeconds`, `DealerStepSeconds`);
cards appear in the snapshot as they are dealt and the hole card is 255 until the dealer turns it.

### 10.4 Wire (`CasinoNet`, `[NetSessionComponent(Order = 47)]`, protocol stays 21)

Guest to host (all `RpcInvokePermission.Everyone`, all answered with `ResultRpc(ticket, CasinoResult)` to that client; at most 24 requests in flight, 8 s timeout):

| RPC | Guard (`SessionService.SetGuard`): per-second, burst, max bytes | Host validation |
|---|---|---|
| `SitRpc(ticket, table, preferredSeat)` / `LeaveRpc` / `RouletteJoinRpc` | `rb.casino.seat` 2/s, 6, 16 | table id; range; business; seat free |
| `BlackjackBetRpc(cents)` / `BlackjackSitOutRpc` / `BlackjackActRpc(action)` / `RouletteReadyRpc` | `rb.casino.play` 6/s, 12, 16 | seated; phase and deadline; limits; whole dollars; affordable; turn; legal action |
| `RouletteSetBetsRpc(byte[] 6 bytes per bet)` | `rb.casino.bets` 4/s, 8, 64 x 6 + 16 | whole bets, at most 64; every spot and amount via `RouletteRules.Validate`; participant; phase |
| `SlotSpinRpc(machine)` | `rb.casino.slot` 1/s, 2, 16 | machine id; range; occupant; 1 s floor; affordable |
| `SyncRequestRpc()` | `rb.casino.sync` 0.5/s, 2, 8 | answered with `SyncReplyRpc(bj bytes, rl bytes, int[5] occupants)` |
| `DrinkRpc` | `rb.casino.bar` 2/s, 4, 8 | unchanged |

A refused-by-guard request is answered `Throttled` (itself limited to 4/s per client) so a callback does not wait for its timeout.
Host to clients: `BlackjackSnapshotRpc(byte[])` and `RouletteSnapshotRpc(byte[])` (`SendTo.NotServer`, whole table, at most once per frame per table, around 0.3 to 1.5 KB),
`SlotEventRpc(machine, stops, rule, win, stake, key, start, big)`, `SlotOccupancyRpc(machine, key)`, `BigWinRpc`, and to one client `NoticeRpc(table, notice)`.
The shoe and the hole card never leave the host (the rules' snapshots only hold what is face up).

### 10.5 Parked behind `CASINO_LEGACY_UI` (RETIRED: deleted when the §12 controllers landed; kept for the history of what moved where)

`UI/BlackjackScreen.cs`, `UI/RouletteScreen.cs` and `UI/SlotScreen.cs` (whole files), and in `UI/CasinoUi.cs` the table-screen switch in `Open`,
the `CasinoHost.AutoSettled` / `CasinoNet.BlackjackPushed` hooks and `OnHostAutoSettled` / `OnAutoSettled`. `CasinoStation.Used` for the bar still opens
`BarScreen` through the old `CasinoClient.Drink` flow; for blackjack, roulette and slots `CasinoUi.Open` only toasts "Pull up a stool soon — the tables are being rebuilt."
and returns null. Removed from the host/net: `SlotView`, `RouletteView`, `BlackjackView`, `CasinoHost.Slot/Roulette/BlackjackDeal/Act/Leave`, `AutoSettled`,
the per-client shoes, `CasinoNet.AskSlot/AskRoulette/AskBlackjack*`, `PackBets/UnpackBets` (now `CasinoWire`). `CasinoChips.cs` now uses `BlackjackTable.Hidden`.
`CasinoError.RoundOpen` and `NoRound` are unused leftovers of the enum.

**Gotchas for presenters.** (1) A material tinted with `MaterialPropertyBlock` must not have GPU instancing on when many renderers share
one mesh (all the casino's block-tinted materials have it off): instanced copies mixed their blocks into diagonal stripes. (2) Flat
parts under the eye (plates, boards, buttons) don't receive shadows; do the same for card and chip props that sit in the lamp's cone.
(3) TextMeshPro text set at run time is fine; text set in edit mode needs `ForceMeshUpdate()` before a manual `Camera.Render`.
(4) Scripts here are one MonoBehaviour per file named like the class: a second behaviour in a file loads as a missing script.

### 10.6 Verification (2026-10-09)

- **EditMode** (`Casino*` fixtures, private copy): 140 green. `CasinoTablesTests` covers seats and nearest-first, one station at a time, money conservation with a fake
  till (three players: stand, leave mid-hand, disconnect; business gone = `UnpaidCents`; refund before the deal; turn timeouts; double and insurance charged first),
  refusals (not seated, wrong phase, out of range, can't afford, limits, not your turn, bad action byte), no hole card or shoe in any snapshot, slot single occupancy and
  release on leave / disconnect / 180 s idle / body gone, slot stops equal `SlotRules.Spin` on the same dice with the stake charged before the first die, big-win threshold
  (slots by machine, roulette), the shared wheel (lock charge, cancelled set, pays a player who left), late-join `Sync`, and a 40,000-step chaos run with four clients
  where every player's cash must equal the nets their PUBLIC snapshots and slot events report.
- **Real co-op session** (bot harness, loopback, one host and two guests, with and without `crew_bad_wifi`): the `casino` bot brain (`Casino/Net/CasinoSmoke.cs`)
  sits at blackjack (host seat 0, bot 2 seat 1, bot 3 watching), bot 2 walks away mid-hand and is settled in absentia, all three join the wheel and bet, all three use
  machines (bot 3 is refused the host's machine), and each bot checks its final cash against the public pictures and that the hole card was never visible. All PASS. To repeat:
  in a private copy run `TownWorldSignatureBuilder.Publish` once, `Tools/bots/sync_build.sh --build --no-sync --copy <copy>`, then
  `RUBBISH_BUILD_COPY=<copy> Tools/bots/run_bots.sh -n 3 -m 4 -p none --brain casino` and read `[CasinoSmoke]` in each `player.log`. A bot build has no Casino scene, so each
  bot is parked at the room's world position (the host's range check finds no station and only checks the room).

## 12. Controllers (seat mode, local player; built 2026-10-09)

The retired pop-up screens are replaced by one controller per game in `Assets/Scripts/Casino/Control/`. They own the local player's side of
a seat (camera session, cursor, plates, the hint strip, HUD cash hold, sounds) and send the §10 intents; they draw none of the table.

### 12.1 Files

| File | What |
|---|---|
| `CasinoControl.cs` | Static front desk: `CasinoStation.Used` -> `Sit` through `CasinoClient` -> controller + `CasinoSeatCamera.Enter`. Routes `CasinoClient.Notice`, toasts other players' big wins, `Celebrate` (sound, toast, shake for own big wins). `TryEnter(station)` is also what the debug menus call. |
| `CasinoController.cs` | Base class: begin / `End(reason)` for EVERY way out, cursor + reticle, lean / watch, `Say`, `HoldCash`. `ScriptedCursor` lets tools drive the cursor. |
| `BlackjackController.cs`, `RouletteController.cs`, `SlotController.cs`, `BarController.cs` | The four games. |
| `CasinoHintStrip.cs` | The only screen furniture: one bottom strip of up to 3 lines (key-chip hint line, message line, info line), plus the reticle dot. `[key] label` markup. Hidden while any UI holds the player (`PlayerController.IsUIBlocking`), honours the "control hints" setting (key line only), scales with the UI scale setting, never touches `UiStack`. |
| `CasinoInput.cs` | The key map constants and the one place that polls keys, the wheel and the pad (legacy `Input` AND the Input System, so test devices work). |
| `CasinoSeatLogic.cs`, `RouletteBetBook.cs` | Pure rules (tested by `CasinoControllerTests`): `ChipSelector`, `PendingBet`, `BetTiming`, `BlackjackSnapshotRules` (legal actions, costs and basic-strategy advice from the PUBLIC snapshot; checked against the host table over 3,000 seeded rounds), `BlackjackPlates`, `AutoSpin`, `SlotRevealTiming`, `BarMenu`, `CasinoSeatText`, `RouletteBetBook`, `RouletteSync`, `SendGate`. |
| `CasinoGhosts.cs`, `ChipRackGlow.cs` | Local see-through chips (pending stake, roulette ghost chip), the selected-chip ring and the rack glow, a small world label. Nothing networked. |

### 12.2 Entering and leaving

- **Enter:** blackjack `Sit("bj0", nearest spot)` (the host's seat is the spot), roulette `Sit("rl0")` (the chip colour is the spot, so nobody shares one),
  slot `Sit("slotN")` (occupied = toast "In use - name", no camera), bar: nearest bar spot with nobody standing at it (no host seat). Errors toast
  ("Table full", ...) and the camera never moves. If the seat camera then refuses (hands full, driving...) the host seat is given back.
- **Leave:** Esc / pad B / Start (the seat camera's own exit, which also keeps Esc away from the pause menu). Every exit, user or forced
  (pass-out, teleport, carry, driving, leaving the room, session end, the host's `Released` / `IdleRelease` notices), ends in `CasinoController.End`:
  `CasinoClient.Leave(table)`, `HoldHudCash(false)`, cursor and strip dropped, plates restored, `LocalBusy` cleared, controller destroyed. A drink already
  paid for at the bar is drunk on the way out. Leaving a blackjack hand in play says "The dealer plays your hand out"; locked roulette bets say "Your bets ride".

### 12.3 Input map (keyboard / pad)

Everywhere: mouse or right stick looks (within the spot's limits); **hold Shift / LT = lean**; **hold V / D-pad up = watch** (dealer, wheel, pay table, bartender);
**Esc / B / Start = leave**. Blackjack and slots use a LOCKED cursor (a dot at the screen centre: look at a thing and click); roulette and the bar use a FREE cursor
(the mouse points at the felt / menu card; the view pans at the screen edges or with the middle mouse button; pad: left stick moves the dot).

| Game | Keyboard + mouse | Pad |
|---|---|---|
| Chips (blackjack 5/10/25/50/100, roulette 1/5/25/100) | Q / E, wheel, 1-5 (1-4), or click the rack; the selected bin glows with a ring | LB / RB, D-pad left / right |
| Blackjack, betting | click the bet circle or Space: stack a chip (see-through stack + "BET $35" label); RMB or Backspace: last chip off; C: clear; R: last hand's bet; Enter: deal now; O: sit out. A legal stack is sent by itself 1 s before the clock ends | A / RT: click; X / RB: take off; Y: deal; D-pad down: same bet; L3: sit out |
| Blackjack, deciding | the lit 3D plates (click) or Space hit, S stand, D double (+$X), P split (+$X); insurance window: I insurance (+$X), N or S decline; H: basic-strategy line on / off | X hit, Y stand, RB double, LB split; insurance X, no thanks Y |
| Roulette | LMB drop the selected chip on the spot under the cursor (a see-through chip shows where, with "pays N to 1", and the strip names the spot); RMB take the last chip off that spot; Z / Backspace undo; C clear; R rebet; Space / Enter or the READY plate: ready (toggle); the plates Undo / Clear / Rebet work too | A / RT drop, X / RB take off, D-pad down undo, L3 rebet, Y ready |
| Slots | Space, a click anywhere, the lever or the SPIN plate: pull. Hold 0.5 s: auto-spin (never sooner than 2.6 s after the last pull) | A / RT, hold A |
| Bar | click a drink on the card, or 1-4; hover shows price + effect | D-pad / LB RB pick, A order |

### 12.4 Intent mapping

Blackjack: `BlackjackBet(total)` once per round (Enter, or auto 1 s before `phaseDeadline`), `BlackjackAct(action)`, `BlackjackSitOut()`. The local stack only
exists until the host accepts the bet (then the presenters show the chips from the snapshot). Roulette: `RouletteSetBets(whole set)` after any change,
coalesced to at most 4 a second (one more right before the lock); `RouletteReady(bool)`. The snapshot's participant bets are the truth: whenever nothing is unsent
or in flight (and always from `Locked` on) the local book is replaced by them (refusals, cancelled sets, the host clearing). Slots: `SlotSpin(machine)`.
Bar: `Drink(id)`, then `DrunkStatus.Drink(units)` about 3 s later (the pour and slide), the 1.15 cut-off and "water is always sold" unchanged. The bartender and the glass come from the host's `CasinoClient.BarServed` broadcast (presenters).

HUD cash (`HoldHudCash`): held from a blackjack bet / action until 0.7 s after the `Results` snapshot; from the roulette lock until 1 s after the `Result`
phase; at slots from the pull until the reveal ends (`SlotRevealTiming`: reel 2 stops 2.05 s after `SlotEventInfo.start`, 3.1 s on a tease, plus 0.35 s). Released in
`End` on every exit path. Wins react locally (`CasinoControl.Celebrate`: cash sound; big wins: legendary sound, toast, camera shake).

**Hint strip details.** The strip rides above the HUD's own bottom dock (`HudLayout.Layout.bottomDock`: the prompt and hint chips) so the two never overlap (the pocket / gear bar left the dock for the bottom-right corner, and is hidden at the seats); the pulse of the selected
chip and its ring holds still with the "reduce flashing" setting. The cursor dot (a reticle) shows while the OS cursor is locked (blackjack, slots) or the pad drives the cursor.

**Tools and tests.** `CasinoInput.Inject(KeyCode, bool)` presses / releases a key without a device (call it before the controllers' `Update`, e.g. from a pump with a negative execution order);
`CasinoController.ScriptedCursor = true` leaves the cursor to the caller (`Cursor.Position`, `PressPrimary()`). The Input System's own test devices only deliver while the Editor window has focus.

### 12.5 Verification

EditMode: `CasinoControllerTests` (pending-bet arithmetic and clamping, chip cycling, auto-send timing, snapshot-derived legal actions / costs / advice equal to the host
table's over 3,000 seeded rounds, plate states, roulette book limits / undo / rebet / reconciliation, send pacing, auto-spin pacing, strip markup, the bar's rules).

## 11. Presenters: the 3D table state (built 2026-10-09)

Everything the room SHOWS (cards, chips, wheel and ball, reels, wins, name tags, drinks) is drawn by the presenters from what
`CasinoClient` replicates, on every machine, so every player in the casino sees the same thing. The controllers (seat camera, cursor,
hint strip, 3D buttons, intents) draw none of it and the presenters send nothing.

### 11.1 Files (all `Assets/Scripts/Casino/World/`)

| File | What |
|---|---|
| `CasinoPresenters.cs` | The lifecycle (`CasinoPresenters`, a DontDestroyOnLoad object made by `CasinoRunner` once `Casino.unity` is up and gone when it unloads) and the station-presenter base. |
| `BlackjackPresenter.cs`, `RoulettePresenter.cs`, `SlotPresenter.cs`, `SlotRegulars.cs`, `BarPresenter.cs` | One per station (blackjack, wheel, each machine, the two NPC regulars, the bar). |
| `CasinoPresentMath.cs` | The pure parts, tested: `BlackjackDiff` (new cards between two snapshots, hole reveal, "7/17" / BUST / BLACKJACK text), `CasinoChipMath` (stack decomposition), `CasinoWheelMath` (rotor and ball path), `CasinoReelMath` (drum travel), `SlotPlateText`. |
| `CasinoRouletteLayout.cs` | The roulette hit-zone mapping in layout cell units: `SpotAt(u, v) -> RouletteBet?` and its inverse `BetCentre(bet, out u, out v)` (chip placement). Test: every one of the 161 spots round-trips. |
| `CasinoPaint.cs`, `CasinoPaint.Art.cs` | Run-time textures: the card atlas (52 faces + back, single-stroke cartoon font), the chip atlas (denomination colours for blackjack, four owner colours for roulette, the amount printed on top), the slot symbols and drum strips (the QA'd art and the QA'd face mapping). |
| `CasinoPropKit.cs`, `CasinoProps.cs`, `CasinoLabels.cs` | Meshes and materials (rounded 1.6x cards, bevelled chips, lathe glasses), the pooled `CardProp` / `ChipProp` / `ChipStackView` with their slide, drop and flip, and the pooled world-space TextMeshPro labels (one shared outlined material, billboarded). |

Edits to existing files: `CasinoStaff.cs` (new pose `CasinoRegularPose.SlotStander`, `CasinoStaffer.Pull / Cheer / Shrug / Walking / SetVisible / Anchor`), `CasinoWorld.cs` (`CasinoRunner` calls `CasinoPresenters.Ensure()`), and
in `Casino/Net/`: `CasinoHost.BarServedHeard / BarServedToBroadcast / HearBarServed`, `CasinoNet.BarServedRpc` (host to guests, `SendTo.NotServer`, nobody else can invoke it), `CasinoClient.BarServed(int key, int drink, int spot)`. Protocol stays 21.

### 11.2 Contract

- **Lifecycle.** `CasinoPresenters.Instance` exists while the casino scene is loaded (`Ensure()` is idempotent). It builds a presenter per station it finds (a missing station or optional transform is logged ONCE with `WarnOnce` and skipped), subscribes to `CasinoClient`
  (`BlackjackSnapshotChanged`, `RouletteSnapshotChanged`, `SlotSpun`, `SlotOccupancyChanged`, `BarServed`), and does its work only while `CasinoWorld.LocalInside`. On entering the room it re-applies `LatestBlackjack` / `LatestRoulette` / `OccupantOfSlot`
  WITHOUT animation (a late joiner sees the table as it is) and calls `CasinoClient.RequestSync()` (at most once a second). Snapshots are coalesced to the newest per table per frame; one older than what was drawn is ignored. All animation uses this machine's
  `Time.realtimeSinceStartupAsDouble`; host stamps go through `CasinoClient.ToLocalTime` / `TableTime`.
- **Animate or snap.** A snapshot that is older than 1.2 s when it arrives (`TableTime() - snapshot.now`), the first one, a late join, or a diff of more than 8 new cards is shown at once in its final layout.
- **What it draws, per game.** *Blackjack:* name tags (`spot.floatText`), the stool slid out while a seat is occupied (edge-triggered `SetStoolAway`, so the seat camera's own call never fights it), bet chips on `betCircle` (an extra stack beside it for doubles and splits, a "$" label), insurance chips on
  `insuranceSpot`, cards on `handSpots[h].cards[i]` (hands beyond the fifth card and split hands side by side; the doubled hand's last card is laid across), the dealer's on `dealerSpots` (hole card face down, flipped when the snapshot reveals it), totals above each hand
  (`7/17` soft, `BUST 24`, `BLACKJACK`, `21`, `x2`), a glowing ring on the active seat's bet circle, `timerLamp` on the phase countdown (betting 15 s, insurance 8 s, the active turn 25 s or 120 s alone), results floating over each hand for 4 s, chips swept to `chipTray` / paid from it,
  the dealer's `Deal / Sweep / Pay` gestures, a cut-card gauge on the shoe (red when the cut card is out) and a SHUFFLING beat. *Roulette:* each participant's bets as stacks at `CasinoRouletteLayout.BetCentre` in their colour (`colour` 0..3 = blue, orange, purple, yellow; a spot shared by
  owners is spread a little; the amount shows only on tall stacks), name tags at `spots[colour]` (READY while betting), `timerLamp` on the betting countdown and three red bulbs plus "NO MORE BETS" and the dealer's Spin gesture at the lock, the rotor and ball (below), the dolly
  (`PlaceWinMarker`) and the history board (`SetHistory`) from the result on, each participant's win or loss over their rack, losing chips swept, winnings counted out from the tray, everything gathered at the end. *Slots:* below. *Bar:* below.
- **Roulette convention for controllers:** a participant's chip colour index (`CasinoClient.LocalSeatAt("rl0")`, the `seat` of the Sit result) is also the spot it is drawn at (`spots[colour]`), so a controller should stand the player at that spot.
- **The wheel is a pure function of the clock** (`CasinoWheelMath`): the rotor turns at 26 degrees per second all the time (`RotorAngle(TableTime())`); the ball is thrown against it, slows (angle `7 turns x (1 - t/7)^2.2` relative to the rotor), leaves the track at 4.9 s, drops in by 5.7 s, hops, and sits EXACTLY in `WheelOrder`'s pocket
  at `spinStart + 7 s`; afterwards it rides the rotor. Late joiners and spectators anywhere in the room compute the same picture.
- **Slots.** `SlotEventInfo` starts the lever (pulled 0.2 s before `start`) and three drums that stop 0.4 s apart from 1.25 s (the last reel waits until 3.1 s when the first two hold the top symbol: the stop is still the host's). A drum rests at stop `s` with local rotation `Euler(-s * 30, 0, 0)` (face `(-s) mod 12`, QA'd) and its texture
  is repainted while it is a blur so the faces around the stop are the true strip symbols (`SlotRules.SymbolAt`). A win adds a coin burst at `coinTray`, the tray light, glow and a floating total; a big win (>= $1,000 or >= 50x) adds a banner and a 2 Hz glow wave (steady under `ReduceFlashing`, scaled by `ScreenFx01`).
  `SetOccupant` shows "<name> is playing" over the machine and a red beacon on top; an unoccupied machine's glow breathes. `payPlate` is filled from `SlotRules` at build time (14 lines, "RETURNS xx.x% over the long run", "STAKE $n A PULL").
- **Regulars (`SlotRegulars`).** Machines 1 and 3 each get one cosmetic person (`CasinoStaffer`, pose `SlotStander`) standing beside the player's stand spot. Every 9-20 s they haul the lever and the drums spin to random stops (`SlotPresenter.SpinCosmetic`), then a small reaction (a cheer only for a real prize). When a real player takes the machine (`OccupantKey >= 0`) they walk to an idle spot 1.6 m back; 60 s after
  the machine was freed they walk back. All randomness is a seeded `PuppetDice` (cosmetic), nothing touches the host.
- **Bar.** `CasinoClient.BarServed(key, drink, spot)` (raised on the host machine by `CasinoHost.Drink`, on guests by `BarServedRpc`; the buyer's own `Drink` reply is unchanged) makes the bartender pour (`BartenderGesture.Pour`), a glass fill at the spot's `pourSpot`, slide to its `counterSpot` (`Serve`), rest 6 s, drain and go. Beer is a pint with foam, whisky a short tumbler, the Dumpster Fire cocktail has a flame, water is clear.
- **Not touched by the presenters:** `CasinoButton` state (enabled / lit / pressed), the cursor and its hover ghost, the selected-chip highlight and the chip racks' contents, `CasinoSeatCamera`, the HUD cash hold. The presenters write: `timerLamp`, `resultBoard`, `winMarker`, `payPlate`, the slot reels / lever / glow / coin tray light, `wheelRotor` / `ball`, the stools, and the props they parent under `[CasinoProps]` in the interior.

### 11.3 Performance and look

Cards and chips are pooled, share one atlas material each (no GPU instancing, no property blocks on them), cast no shadows and receive none; the materials are matte (no specular, no reflections) and tinted down because the table lamps are strong. World text is TextMeshPro, ASCII only, pooled, one shared outlined material.
Nothing allocates per frame while idle; snapshots allocate small arrays at snapshot rate. Per-frame work while the player is in the room: the wheel and ball (two transforms), the lamps (only when a bulb changes), moving props, live labels and the regulars' poses.

### 11.4 Tests and verification (2026-10-09)

EditMode `CasinoPresenterTests` (chip decomposition, deal diff, hand text, ball lands on the exact pocket at 7 s for all 38 pockets and several clock bases, drum travel and face mapping, pay plate text, `BarServed`) and `CasinoRouletteLayoutTests` (161 spots round-trip, every spot reachable, nothing illegal).
Play mode in a private copy, solo game with fake players seated through `CasinoTables` (extra keys with a patched till): a full blackjack round with split, double, natural, insurance and dealer bust; late join (presenters destroyed and rebuilt mid-round: state restored at once); a four-colour roulette round with shared spots and a forced pocket (the pocket under the ball equals the winning number); slot spins with forced dice
(win, big win, tease); regulars stepping aside and returning after 60 s; the bar with three glasses.
