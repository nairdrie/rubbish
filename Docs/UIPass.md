# Rubbish: UI Pass (main menu, Esc menu, settings, minimap, HUD fixes): design + build contract

> **Status: design only (1 Oct 2026). Nothing in this doc is built yet.** It is the source of truth for the menus, the settings
> screen, the minimap and full map, and the HUD layout, the same way `CoreLoop.md`, `LivingWorld.md`, `Character.md`,
> `Progression.md`, `Multiplayer.md` (MP) and `Accounts.md` are for their areas. Where this doc touches co-op or saves, **MP §9 and
> `Accounts.md` win**.
>
> **When it's built:** every package starts **after the M0 gate** (MP §8.7.7). B1 ships a minimal menu and Esc overlay in M0; this
> pass builds on top of them. The main-menu shell is designed so that MP's B14 (company cards, M1 wave 3b) slots into it (§10.4).
>
> **User ask (verbatim, item 3 of 5):** "do some work on the main menu, esc. menu, minimap, etc."
> The lead's brief added: a polished, derpy-cute main menu with an animated Chuck and truck backdrop, Continue / Host with friends /
> Join / Play Solo / Settings / Quit, plus the company save cards and Steam invites already planned in `Accounts.md` §3.2 and MP B14;
> an Esc pause menu; a settings screen; a minimap with a full-screen map; and a pass over the HUD and tooltip issues.
>
> **How it was made:** a read of every UI file (file:line below), the playtest and verification screenshots in the session
> scratchpad (`pt_*.png`, `lw_test/`, `lwfix/`, `prog/`, `chuck_test/`, `final*.png`, `l0_p1_watermark.png`, `mdd/worldlife/wl_*_top.png`),
> the six docs, and the in-flight M0 code (`Net/Session/**`, `Net/Steam/**`, `Net/Player/**`). No code was changed.

---

## 0. The answer in twelve bullets

1. **One UI tech for everything new: runtime-built uGUI on a small `UiKit` that grows out of `FeelUi`.** The HUD, its springs and
   outlined text, the edit-mode screenshot rig and MP's B14 plan are all uGUI already. UI Toolkit stays only where it already lives:
   the laptop apps (ebae, Scrapazon). No new prefabs or asset files for UI; everything is built in code (§2 D1).
2. **The map is vector, baked once.** An idempotent setup script scans the built Neighborhood (every road, lot, park and pad is an
   axis-aligned rectangle) and writes a cartoon map PNG and a small data asset. One texture serves both the minimap and the
   full-screen map. The runtime cost is one quad. No render-texture camera (§2 D2, §7).
3. **Main menu:** a wobbly **RUBBISH** title (letters in the recycling-bin colours, a googly eye for the dot on the i) over a live
   3D diorama: Gertrude (the FEED ME truck) idling at a golden-hour curb, Chuck doing his fidgets and chucking bags into the hopper,
   and a raccoon in the bin. Buttons: **Continue** (a card showing the Town, day, company and cash), **Play solo**, **Host with
   friends**, **Join a friend**, **Change save…**, **Crew Card** (marked SOON until M4), **Settings**, **Quit** (§4).
4. **Company cards and Steam friends drop into that shell as M1 lands:** B14 (cards, Change…, Restore…) and a small Steam
   friends feed for "Friends playing" and invites (UP-5). Until then Continue shows the legacy save, and Join takes a code (§4.2).
5. **The Esc menu is a Rubbish Co. timesheet on a clipboard:** Resume, Invite friends, Settings, Unstuck, Messages, and
   Leave/Quit worded per role. In co-op it adds the Crew list (Kick for the host) and the Town Rules card. **Solo freezes the world.
   Co-op never pauses**: the stamp reads ON BREAK and the world keeps going (§5).
6. **Esc never fights the laptop or the sort station.** One router decides who gets each Esc press, using last frame's state, so
   no legacy file needs editing (§5.1).
7. **Settings has five tabs:** Audio, Controls (look sensitivity, invert Y, FOV, keybinds view), Video (display mode, resolution,
   vsync, frame cap, quality preset, render scale), Interface (UI scale, minimap, hints, toast time, streamer mode) and
   Accessibility (screen-effects intensity covering rabies, shake and hit-stop; colour-blind-safe markers with shapes; reduce
   flashing). They're saved in `settings.json` in the data root (`Accounts.md` §4.1, Steam Auto-Cloud). In the Editor, video
   settings never touch ProjectSettings or the URP asset (§6).
8. **Minimap: a round "bin lid" in the bottom-left corner.** It shows roads, district tints, a curb pip per house coloured by how
   fresh its trash is, a pulsing gold ring on the next stop (the same target as the route arrow), the truck, the crew, you and the
   depot. It's heading-up and zooms out with truck speed. **M** opens the full paper map and **holding Tab** peeks at it. Locked
   districts show what unlocks them, which also helps make progression clearer (§7).
9. **The HUD pass replaces hard-coded positions with one `HudLayout` of zones** and fixes the reported issues:
   - TooltipUI's fixed box becomes an auto-sizing prompt;
   - long hints become key-cap chips that wrap to at most 2 lines;
   - toasts get their own lane: at most 3, at most 2 lines, with a message log;
   - the first-person sort station gets a compact Station mode, so the bin signs aren't covered;
   - the rank chip becomes legible;
   - the top row no longer collides at 5:4 or with large UI scales (§8).
10. **Co-op adds no new network traffic.** Every view reads state that's already mirrored: curbs (B6), transforms, roster (B1)
    and rules (C2). Unstuck uses owner authority, or the existing tow drone for the truck (§9).
11. **Build:** 6 packages (UP-0 to UP-5), about 15–18 builder-days plus B14's 3, which 3 builders can run in parallel by file
    ownership. There are 11 small requests to other owners (one optional), and **one lint allow needs approval** (`timescale`,
    for the solo freeze) (§10).
12. **Open for Nick, each with a default already built in:** a display font, whether solo Esc pauses, where the minimap sits and
    which way it rotates, menu music, and streamer mode on by default (§13).

---

## 1. Ground truth (what's there on 1 Oct 2026)

### 1.1 Files

| Area | What exists (file:line) |
|---|---|
| Main menu | `MainMenuScene.unity`: two legacy uGUI buttons, "Host World" and "Join World", a code field, the default skybox, an EventSystem and a Main Camera (screenshot `l0_p1_watermark.png`). `MainMenuManager.cs` (class `MenuManager`, 48 lines). **B1 (M0, in flight) is rewriting it**: Solo / With Friends split, no client-side `LoadScene`, readable errors. It also adds `UI/Menu/NetErrorPanel.cs` and a **minimal Esc overlay** (Resume / invite code / Leave / Quit). |
| Steam layer | `Net/Steam/SteamHud.cs` (B3/B10, in flight): an IMGUI layer with the "needs Steam" blocker, status lines ("Joining Nick's shift…") and an **Invite Steam friends (F2)** chip. Its header says B14's menu should call `SteamLobby.OpenInviteDialog` itself later and keep this as the fallback. `SteamLobby.OpenInviteDialog(out whyNot)` (:336), `SteamJoinRouter.RequestJoin(lobbyId, …)` (:96). |
| HUD | `Feel/FeelHud.cs`, a runtime uGUI ScreenSpaceOverlay canvas at sort order 100, 1920×1080 reference, match 0.5, positions hard-coded. Cash at (28,−24) 390×122 (:197); clock at (432,−24) (:149); truck gauge right-anchored (−28,−64) 420×96 (:225); left column from y −160 (:64); context hint one line, clamped to 1400 px, never wraps (:541). `Feel/FeelHudToasts.cs`: lane top −172 (:15), max width 1000 (:17), 4 visible (:13). `Feel/FeelHudRoute.cs`: route panel at (−28,−176) (:81). `Progression/ProgressionHud.cs`: sort order 99, rank chip at (748,−26) 432×84 (:20); "RANK" ribbon and Rep text at **14 px** (:146, :161), caption 17 px (:163); Head Office memo bottom-right (−28, 96) 470 wide (:230). |
| Prompts | `TooltipUI.cs` (51 lines): a scene-placed, fixed-size box in `Prefabs/CommonWorldElements.prefab` that only sets text. Called from `PlayerController.cs:108/114` and `Sorting/SortStation.cs:517/611/1183`. |
| Helpers | `Feel/FeelUi.cs`: procedural sprites (rounded, circle, arrow, sun, moon, star, stink), the outlined TMP material, `Panel`, `Text`, `Wave`, `Spring`, and `Fit`. `Fit` exists because TMP's Ellipsis mode fails with our font. The only font is **LiberationSans SDF**. |
| Laptop UIs | UI Toolkit (`Assets/UI/ebae/*.uxml/uss`, `Assets/UI/shop/scrapazon.*`, ~3.5k lines of USS), PanelSettings sort order 200. Esc closes the laptop (`EbaeUI.cs:168`). |
| Other canvases | `SortStation` crosshair 90 · DistrictWelcome card 640 (`World/DistrictWelcome.cs:64`) · ScreenFader 32000 · LoadingSystem 32767 · SteamHud blocker 32767 (IMGUI draws last). |
| Input | No pause menu exists. Esc is read directly by `EbaeUI.cs:168` and `SortStation.cs:267`. Keys are hard-coded `KeyCode`s in about 15 files (E/F interact, G drop, LMB throw, J chug, Ctrl stomp, H horn, Space drift, X arm, hold R tow, R/Q sort station). The Starter Assets Input System drives Move, Look, Jump and Sprint. `activeInputHandler: 2` (both systems). |
| Cameras | Cinemachine 2.10 virtual cameras: the player cam, the truck cam (FOV 40/60 in `Truck V2.prefab`), and the sort-station first-person cam, which rewrites its FOV every frame (`SortStation.cs:876`, base 64). |
| Graphics | **One** quality level ("PC") and one URP asset (`Assets/Settings/PC_RPAsset.asset`: render scale 1, MSAA off, shadows 50 m / 4 cascades / 2048 px, soft shadows high). The Windows playtest build defaults to borderless fullscreen and 1280×720 windowed (`Docs/WindowsBuild.md`). |
| Audio | No AudioMixer and no music. Every sound goes through `Feel/FeelSfxPlayer.cs` pools (`Sfx.MasterVolume` 0.8). Ambience ids are `cricket`, `owl` and `rooster` (`Feel/FeelAmbience.cs`). |
| Map data | All roads, sidewalks, pads and lots are axis-aligned rects named `Road_N`, `Sidewalk_N`, `DepotPad_N` and `Lot_*` (`Editor/NeighborhoodBuilder.cs:450-535`, :767). `Assets/Resources/HouseDirectory.asset` already lists every house with its stopId, address, position and district (`Trash/HouseDirectory.cs`). `Districts.All[i].boundsXZ` holds the district rects. |
| Live data | `FeelWorld.At(stopId)` gives bags, late count, oldest age and district. `FeelHud.Route.Target` is the arrow's house. `CurbTrash.FreshnessColor(d, age)` is the single colour source for markers and HUD rows. `GameSession.CurbBags` is scene-independent, so it works in the garage. |
| Co-op | `NetRoster.Entries` (slot, company, display name), `CoopPlayerHooks.Spawned` (event per player), `RosterView.Slot`. `ChuckPartsData.Variants[slot].playerColor`: Chuck tangerine, Dot pink, Ozzie seafoam, Bert cornflower. |
| Settings storage | `Accounts.md` §4.1 and §5.1: `settings.json` lives in the data root, is per machine, and is synced by Steam Auto-Cloud. Lint rule `save` allows file writes only under `Assets/Scripts/(Net/(Core/)?)?Save/**`. `SaveRoot.Path` and `SafeFile.WriteAtomic/TryReadNewest` have landed (L0). |

### 1.2 Issues found (evidence → fix in §8)

| # | Issue | Evidence | State today |
|---|---|---|---|
| H1 | **TooltipUI's box is a fixed size.** "Grab Saggy Trash Bag (1.2 kg)" wraps onto 3 lines, spills past the box, and the E key cap overlaps the text | `lw_test/26_rabid_chase.png` | open |
| H2 | **Toasts covered the clock and the truck gauge** | `lw_test/26_rabid_chase.png` | moved below the top row in `lwfix` (TopY −172). Still open: the lane is fixed numbers, 4 stacked toasts cover the middle of the play view (`lwfix/12_toasts_v2.png`), and it breaks at narrow aspect ratios and large UI scales |
| H3 | **In the first-person sort station, the HUD covers the bin signs.** City and Finds sit over HAZARD/METAL, and the truck gauge over KEEP! | `chuck_test/20_sort_fp.png`, `final03_fp_bag.png` | open |
| H4 | **Long hint line:** "Suspicious Trash Bag · [E/LMB] Slam it open [RMB] Chuck [Q] Step away (aim at the compactor to crush it unopened)" is one ~1000 px line of 24 px regular-weight, low-contrast text, and it repeats the prompt above it | `final03_fp_bag.png`, `FeelHud.cs:541` | open |
| H5 | **The rank chip is illegible at 720p:** the 14 px Rep numbers and "RANK" ribbon | `prog/fixer/f32_rank7_chip.png` | open |
| H6 | **The top row collides when the reference width is under 1652** (5:4 is 1610). Any UI scale above 1 would collide at 16:9 | arithmetic from §1.1 positions | latent |
| H7 | **The cash panel is 122 px tall**, mostly for a delta line that's usually empty | all HUD shots | cosmetic |
| H8 | Rep floaters ("+25 REP") spawn just under the chip, at the toast lane's top edge (y −156 → −122), so they can touch the first toast | `ProgressionHud.cs:474` | latent |
| H9 | Head Office memo (bottom-right) and a long prompt can overlap at narrow widths | layout arithmetic | latent |
| H10 | The dev-only **TEST DATA ROOT** watermark sits top-centre, over the rank chip in game | `l0_p1_watermark.png`, `SaveRoot.cs` | dev only |

---

## 2. Decisions

**D1. UI tech: uGUI + `UiKit` (built on `FeelUi`), not UI Toolkit.**

| | uGUI + FeelUi (chosen) | UI Toolkit |
|---|---|---|
| Look consistency | Same rounded panels, outlined TMP and spring juice as the HUD | A second look to maintain (TextCore outlines, USS) |
| Juice | `FeelUi.Spring`, `Wave` and procedural sprites already exist | Transitions only; per-letter wave needs custom mesh work |
| Verification | `FeelHud.EnablePreview` + `shot_scene.cs` render in edit mode, **no play mode and no editor lock** | Needs play mode or a panel-to-RT setup |
| Forms (sliders, lists, text fields) | Needs ~700 lines of kit (`UiKit`) | Native |
| Plans already made | MP B14: "runtime uGUI built with the FeelUi helpers" | None |
| Assets | None (built in code, idempotent) | UXML/USS assets to keep in sync |

UI Toolkit keeps the laptop apps. Menus never open over the laptop: Esc closes the laptop first.

**D2. Map: a vector map from the town's rectangles, baked to a PNG by a setup script.** Full comparison in §7.1. A live top-down
camera costs 1–3 ms per frame, is dark at night and is busy with trees. A baked photo is free at runtime but busy at 300 px.
Vector is free at runtime, readable at night, stylable and exact.

**D3. One `HudLayout`.** Every HUD widget asks for a zone rect, computed each frame from the canvas size in reference units, which
already includes UI scale. Hard-coded anchored positions go away. Density steps (Full / Compact / Tight) are chosen by width (§8.2).

**D4. Settings live in `<data root>/settings.json`**, written by a new `Save/SettingsStore.cs` through `SafeFile` (inside the
lint's `save` scope). They're per machine and not per account (`Accounts.md` §4.1). Unknown fields survive a round trip (MP rule
9). Nothing in settings is ever networked.

**D5. Esc routing with a one-frame look-back** (§5.1), so legacy Esc readers (laptop, sort station) keep working untouched.

**D6. Pausing:** solo freezes (`Time.timeScale = 0` plus a held `GameClock`). Hosted and joined sessions never freeze (MP rule 4;
the world is shared). This needs one `timescale` allow, which the lead must approve (§10.7).

**D7. Reuse MP's file names where B14 already named them** (`UI/Menu/MainMenuCoopUI.cs`, `UI/Hud/CompanyWalletLabel.cs`, …),
so the M1 contract doesn't churn. New folders: `UI/Kit`, `UI/Settings`, `UI/Pause`, `UI/Map`.

**D8. No new network messages in this pass.** Map pings and "on break" signs ride M2/M3 channels later (§9).

**D9. Keybinds: a read-only view now; rebinding later.** Keys are hard-coded `KeyCode`s in about 15 files, so true rebinding
needs a migration to Input System actions first, which is a separate job (§6.3).

**D10. Font: LiberationSans Bold with the outline material until Nick approves a display font** (§13 Q1). Shapes and glyphs that
the font lacks (◆ ▲ ✖, mouse icons) are procedural sprites in `FeelUi`, never font characters.

---

## 3. Look and feel

**Two surfaces:**
- **Plum** (`FeelUi.PanelColor` #1C172B at 88%) for anything "game": buttons, HUD, minimap frame.
- **Paper** (#FFF5DB, ink #1F1A2E, stamp red #DB1F29; the same as the Progression stamp card and memos) for anything "document":
  the Esc timesheet, the full map, confirm dialogs and the receipt cards.

**Palette.** Gold #FFD640 marks focus and the next stop. Tangerine #FF7A1F is Chuck. Truck olive is #5B6B2F. Hazard stripes are
yellow #FFD640 on ink. The title letters use the recycling-bin colours: hazard yellow, metal grey #A7AFB8, plastic blue #2F6BD8,
glass green #2FA866, paper brown #B9875A, and KEEP gold.

**Buttons are "stickers":**
- a rounded panel with a chunky offset shadow and a ±1.5° random tilt per button (seeded by label, so it's stable);
- **hover:** a spring to 1.08×, a wobble of 2 Hz × 2°, `Sfx squeak` (pitch 1.4, volume 0.25);
- **press:** squash to 0.92× then rebound, `Sfx pop`;
- **focus:** a hazard-stripe tab on the left edge and a tiny trash-bag cursor;
- **disabled:** greyed, with a strip of tape across that reads "SOON" or the reason.

**Motion.** Panels enter with `FeelEase.OutBack` (0.25 s) and leave in 0.15 s. Screen changes use a **bin-lid iris wipe** (a
circle scaling over 0.3 s, unscaled time). The menu never uses `Time.timeScale`.

**Sound.** Only existing `Sfx` ids: `click` (open), `whoosh` (back), `squeak` (hover), `pop` (press), `ding` (save or confirm),
`buzz` (error), `horn` (Play), `notify` (invite). UI sounds play from the 2D pool, which ignores listener pause, so they're still
audible during the solo freeze.

**Copy voice.** It's Head Office's. Examples: "Clock out for real?" / "Yes, I'm tired" / "One more bag". "Hopping on Nick's
truck…". "Opening the shutters…". "The world keeps going while you're on break." Strings live in one `UiText` static class, so
they're cheap to localise later.

**Derpy checklist (each one is cheap):**
- the googly i-dot on the title follows the mouse;
- one title letter occasionally tips over and springs back;
- a fly buzzes round the title while you hover Quit;
- Chuck's hat pops when you hover Play;
- Dot, Ozzie and Bert slide onto the rear steps when you hover Host with friends;
- the raccoon peeks out of the bin at random;
- the minimap lid gets a fly when the city is under 35% clean;
- the Esc clipboard gets a stamp: PAUSED in solo, ON BREAK in co-op;
- error stickers come with a sad raccoon doodle.

**Legibility floor.** No HUD or menu text is smaller than **18 px at the 1080 reference**. Body text on plum keeps at least 4.5:1
contrast. No information is carried by colour alone (§8.8).

---

## 4. Main menu

### 4.1 Layout (1920×1080 reference)

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│  R U B B I S H     (bin-colour letters, wave, googly i-dot)              (avatar) Nick · Steam │
│    [sticker: "Now with 40% more raccoons!"]                                         ● synced  │
│                                                                                              │
│  ┌ CONTINUE ─────────────────────────────┐                                                   │
│  │ Maple Hollow · Day 14 · 7:42 PM        │        3D DIORAMA (right ~60% of the screen)     │
│  │ ▌RUBBISH CO.  $8,210 · Rank 7   [SOLO] │        Gertrude idling at the curb, Chuck,       │
│  └────────────────────────────────────────┘        wheelie bin + bags, raccoon, house, lamp   │
│    PLAY SOLO          HOST WITH FRIENDS                                                       │
│    JOIN A FRIEND  (2)                                                                         │
│    CHANGE SAVE…                                                                               │
│    CREW CARD  [SOON]                                                                          │
│    SETTINGS                                                                                   │
│    QUIT                                                         ┌ Dot invited you ─────────┐ │
│                                                                 │ Maple Hollow  [JOIN] [x] │ │
│  v0.1.0 playtest · Archway Games                                └──────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Positions.** The left column of buttons spans x 96–640. The logo is at the top-left. The identity chip is at the top-right
  (avatar and name from B10; "editor (dev)" in development builds; the sync dot from M1.5). The version line is at the bottom-left.
  Invite cards stack at the bottom-right.
- **Keyboard.** Arrows and Enter move focus and press. Esc goes back one screen; at the root it does nothing.

### 4.2 What each control does, by milestone

| Control | After M0 (this pass) | After M1 (B14, B10, UP-5) | Later |
|---|---|---|---|
| **Continue** | Hosts solo with the legacy save. The card reads `GameSession`: "Day 14 · $8,210 · Rank 7" | Last Town × last company, **in the last mode** (a chip on the card shows SOLO or WITH FRIENDS). Card: Town · day · time, company name in its tint, cash, rank | — |
| **Play solo** | `NetSession.HostAsync(new HostOptions{ mode = LocalDirect })`, no sign-in | Same, using the selected Town × company | — |
| **Host with friends** | `HostAsync(mode: UnityRelay or Steam per B1/B10, steamLobby: true)`. The code shows in Esc | Same + the Steam overlay invite on arrival (B10 `SteamLobby.OpenInviteDialog`) | — |
| **Join a friend** | Code tab only (§4.4) | Adds the **Friends playing** and **Invites** tabs (UP-5) | Community servers (stage 3) |
| **Change save…** | Hidden (one legacy save) | B14: company cards × Towns, New, Rename, Recolour, Duplicate, Restore…, Delete (`Accounts.md` §3.2–3.3) | — |
| **Crew Card** | Shown, disabled, "SOON" tape | Same | M4: Dumpling, titles, stamps, stats, Pocket |
| **Settings** | §6 | + Privacy tab (M1.5) | Voice (M3) |
| **Quit** | Paper confirm "Clock out for real?" → `Application.Quit` | Same + a final flush (B8 `SaveSlots.FlushNow`) | — |

**Disabled states:**
- While `NetSession.State` is Preparing, Joining or Leaving, every button is disabled and a busy card shows (§4.6).
- Without internet, Host with friends shows "Needs internet (Relay)". Play solo always works (MP §1.3).

### 4.3 The Continue card

- It's a business card, styled after the Head Office paper:
  - a tint stripe in the company colour;
  - the company logo (procedural, B14);
  - the name in caps, cash, the rank badge;
  - "last played in Maple Hollow · 2 h ago";
  - the sync badge (M1.5).
- When the company is ON THE ROAD in another session, the card turns hazard-striped. It reads "ON THE ROAD in Maple Hollow" and
  has a **Take it back** button (`Accounts.md` §3.2, M1.5).
- The `[⋯]` button opens the same actions as B14's card menu.

### 4.4 Join a friend

- **Tabs:** FRIENDS PLAYING (UP-5) | INVITES (UP-5) | CODE.
  - **Friends playing:** rows with avatar, name, Town and crew count (for example "3/4"), plus [JOIN], which calls
    `SteamJoinRouter.RequestJoin`.
  - **Invites:** the invites received this session (`LobbyInvite_t` via the feed).
- **Code:**
  - Entry is six letter tiles styled as a **truck licence plate**.
  - It upper-cases automatically, accepts only A–Z and 0–9, and pastes with Ctrl/Cmd+V.
  - [JOIN] calls `NetSession.JoinAsync(new JoinTarget{ mode = UnityRelay, joinCode = code })`.
- **Copy (M1–M4):** "You'll join as crew in their shop. Your own companies stay safe at home." (`Accounts.md` §3.4)
- **Busy:** "Hopping on Nick's truck…" with a spinning bin lid and [Cancel], which cancels the token and calls `NetSession.Leave`.
- **Errors:** a red sticker with a sad raccoon doodle and the text from `JoinRejectText.For(reason)` ("Crew is full (4/4)",
  "Update your game", …), plus [Try again]. Saves are already reloaded by B1's path.

### 4.5 First launch (M1, with B14)

A paper card headed **START YOUR COMPANY**:
- **Company name:** up to 24 characters, default "Rubbish Co.".
- **Colour:** 8 swatches. **Logo:** 6 procedural logos (bin, raccoon, rubber duck, banana peel, fish bone, star).
- **Town name:** a dice button rolls from a list (Maple Hollow, Lower Puddleton, Binchester, Grimeford, Stinkwater Falls).
- **[Start]** creates the company and Town (`SaveSlots.CreateCompanyAsync` / `CreateTownAsync`), activates them and plays solo.
- Before M1 there is no first-launch flow.

### 4.6 Busy, error and system cards

- **Busy:** "Opening the shutters…" (host), "Hopping on {host}'s truck…" (join), "Saving…" (quit). Each has a spinning bin lid and
  appears after 0.3 s, so quick operations don't flash.
- **B1's `NetErrorPanel`** is restyled with UiKit as a red sticker with an OK button. Its logic stays B1's.
- **Leave cards** after a session use paper with a stamp: "The Boss went home" (MP §2.6), "Rubbish Co. unchanged · Crew Card +3
  bags" (M4).
- **SteamHud** keeps the blocking "Rubbish needs Steam" screen. Its IMGUI invite chip and menu status line are hidden once this UI
  exists (one flag, §10.6).

### 4.7 The diorama

**Build.** `Editor/Setup_MenuDiorama.cs` (idempotent; deletes and rebuilds the root `MenuDiorama` in `MainMenuScene.unity`):
- A 30 × 20 m curb set:
  - asphalt, curb, sidewalk and lawn slabs using the existing `Assets/Materials/Generated/Neighborhood` materials;
  - one `SM_house_suburb_003`, a picket fence panel, `SM_streetlamp_01`, a mailbox, a wheelie bin and 3 bag visuals.
- **`Assets/Prefabs/UI/MenuTruck.prefab`**, copied from `Truck V2.prefab`:
  - It keeps renderers, headlight lights and `DerpyWobble`.
  - It strips NetworkObject, the Network* components, Rigidbody, WheelColliders, `TruckControllerV2`, the Shop truck mods,
    Cinemachine vcams and AudioListeners.
  - It gets one BoxCollider for clicks.
- **`Assets/Prefabs/UI/MenuChuck.prefab`**, copied from `Player.prefab`:
  - It keeps Animator, renderers, `ChuckPuppet`, springs, googly eyes and `ChuckPart` markers.
  - It strips every NetworkBehaviour, `PlayerController`, `ThirdPersonController`, `CharacterController`, `PlayerInput`,
    `StarterAssetsInputs`, `TruckRequester`, `TrashCarrier`, `RosterView` and status components.
  - Stripping uses an allow-list and repeats until nothing more can be removed (because of RequireComponent chains).
  - Plus 3 hidden copies with `variantOverride` 1, 2 and 3 (Dot, Ozzie, Bert).
- `Player.prefab` and `Truck V2.prefab` are **only read, never saved**.
- **Light:** a golden-hour directional light (warm, about 15° elevation), a Day-Night pack skybox and light fog.
- **Camera:** the scene's Main Camera, a 3/4 view at FOV 35 with a 12 s ±1.5° yaw sway and ±1° mouse parallax.

**Beats.** `UI/Menu/MenuDiorama.cs` loops through these in random order, never repeating one back to back:

| Beat | Length | How (existing APIs only) |
|---|---|---|
| Idle fidgets | 6–10 s | `ChuckPuppet` runs its own fidgets after 6 s idle, or `DebugFidget(n)` |
| The Chuck | 4 s | `previewMode`; set `previewHold` to a bag transform (bear hug), ramp `previewCharge` 0→1 over 0.8 s (red face), then "HWAAH": the bag flies on a scripted parabola (no physics) into the hopper. Then `Sfx compactor`, `Juice.Squash(truck)`, and the bag respawns at the curb |
| Raccoon! | 3 s | The bin lid pops and a raccoon visual peeks out with glinting eyes; `previewFlail` on Chuck, `Sfx hiss`. Skipped if no raccoon visual is available |
| Honk | 1.5 s | `Sfx horn`, headlights flash, Chuck's hat pops (`DebugFidget`) |

**Hover reactions:**
- **Play solo:** Chuck looks at the camera.
- **Host with friends:** Dot, Ozzie and Bert slide onto the rear steps and wave.
- **Join a friend:** headlights double-flash.
- **Quit:** Chuck's brows droop and the fly appears.

**Clicks:** clicking Chuck makes him pat his tummy ("hoo"), clicking the truck honks, clicking the raccoon makes it hiss.

**Time of day (M1):** once SaveSlots exists, the diorama matches the Continue Town's clock. At night the lamp and headlamp turn on
and crickets play.

**Guard rails:**
- The diorama holds 0 NetworkObjects and 0 Rigidbodies.
- `previewMode` and its fields are `NonSerialized` public members meant for previews. If any of them doesn't drive the puppet at
  runtime, ask the Character owner for a small `ChuckPuppet.SetMenuPose(...)` instead of editing Character files.

### 4.8 Invites while in the menu

The UP-5 feed raises an event, and an **invite card** slides in at the bottom-right: "Dot invited you to Maple Hollow [JOIN] [x]".
It plays `Sfx notify` and stays for 30 s. During gameplay the same event becomes a toast plus an entry in the Esc menu's Messages.

---

## 5. Esc pause menu

### 5.1 Esc routing (`UI/Kit/UiStack.cs`)

`UiStack` runs at execution order +500 and keeps a **snapshot from the end of the previous frame** (taken in `LateUpdate`):
- `foreignBlock`: `PlayerController.IsUIBlocking` while UiStack holds none of the locks itself (the laptop is open);
- `focusOwner`: `PlayerController.InteractionFocusOverride?.Invoke(Local)` returned true (the sort station);
- `transitioning`: `SceneController.IsTransitioning`.

On an Esc press:

1. If any of the three was true last frame, **ignore it**. The legacy owner (laptop, sort station) handles its own Esc, and closing
   it doesn't also open the pause menu on the same press.
2. Otherwise, if a UiStack screen is open, call `Top.OnBack()`: a dialog cancels, Settings goes back to the pause menu, the pause
   menu resumes, the map closes.
3. Otherwise, if `PlayerController.Local != null`, open the pause menu.
4. Otherwise (main menu), go back one menu screen.

New screens register with `UiStack.Push(screen, lockGameplay)`, which calls `PlayerController.PushUILock` / `PopUILock` exactly once
per screen. `UiStack.EnsureEventSystem()` creates one DDOL EventSystem with `InputSystemUIInputModule` only when none is present in
the loaded scenes.

### 5.2 Variants

| Item | Solo | Host (with friends) | Guest |
|---|---|---|---|
| Stamp | **PAUSED** | **ON BREAK**, banner "The world keeps going while you're on break" | ON BREAK |
| World | frozen (§5.4) | runs | runs |
| Resume | ✓ | ✓ | ✓ |
| Invite friends | "Open shop to friends": "Saves, then reopens this Town as hosted. You'll start in the garage." (a quick re-host; MP §2.3's no-reload switch comes later, §13 Q8) | **Invite Steam friends** (`SteamLobby.OpenInviteDialog`, shows `whyNot` on failure) + **licence-plate join code**, hidden by default in streamer mode, with [Show] and [Copy] | the code they joined with, so they can pass it on |
| Settings | ✓ | ✓ | ✓ |
| Unstuck | ✓ | ✓ | ✓ |
| Messages (N) | ✓ | ✓ | ✓ |
| Crew | — | ✓ + Kick (confirm) | ✓ (no Kick) |
| Town Rules | — | ✓ (display only) | ✓ |
| Leave | **Save & quit to menu** | **Close the shop** (confirm: "Everyone goes home.") | **Leave Nick's shift** |
| Quit to desktop | ✓ (confirm) | ✓ (confirm: closes the shop) | ✓ |

"Solo" means offline, or hosting LocalDirect with remote clients refused and none connected. B1 exposes this as
`NetSession.IsSolo` (§10.6 asks for it if B1 doesn't ship it).

### 5.3 Layout

```
            ┌─────────────── clipboard (paper) ────────────────────────────────┐
            │  RUBBISH CO. TIMESHEET                          ╔══════════╗      │
            │  Maple Hollow · Day 14                          ║  PAUSED  ║ stamp│
            │  ───────────────────────────────────────────────╚══════════╝──── │
            │  ▶ RESUME                     │  TODAY'S SHIFT                     │
            │  ▶ INVITE FRIENDS             │  7:42 PM · City 85% sparkling      │
            │  ▶ SETTINGS                   │  Bags 23 · Net +$412 · Finds 2     │
            │  ▶ UNSTUCK                    │  Work Orders  ✓ ✓ ☐                │
            │  ▶ MESSAGES (12)              │  Rank 7 ████████░░ 1,240 Rep +     │
            │  ▶ SAVE & QUIT TO MENU        │       1 clean day to Rank 8        │
            │  ▶ QUIT TO DESKTOP            │  (co-op: CREW and TOWN RULES here) │
            └───────────────────────────────────────────────────────────────────┘
```

- **Background:** the solo freeze dims the scene to 55%. Co-op dims only 25%, so you can see the world still moving.
- **HUD:** solo hides it except for toasts. Co-op keeps it visible under the dim.
- **Shift card data:**
  - `DailyStats.Today`;
  - `WorkOrders.Today` with ✓ marks;
  - `FeelWorld.Cleanliness`;
  - the rank bar: `Progression.RankProgress01`, `RepToNext`, `CleanDaysToNext`.
  Showing what the next rank needs supports the progression-clarity ask (user item 2).

### 5.4 Pause semantics (`UI/Pause/PauseService.cs`)

- **Solo freeze:**
  - `Time.timeScale = 0` (the only `timescale` allow; needs approval);
  - `GameClock.SetHeld(true)` (a new additive API, §10.6; holds `ElapsedSeconds` by folding the held time into the existing offset);
  - `AudioListener.pause = true` (the 2D UI pool sets `ignoreListenerPause`);
  - `PlayerController.PushUILock()`.
- **Juice.** `Juice.HitStop` is skipped while `PauseService.Frozen`, so its timeScale restore can't unpause the game.
- **Resume** reverses everything in a `finally`. A watchdog also restores on scene change, `OnClientStopped`, quit, or if
  `Frozen` is set while the menu is closed.
- **Co-op:** never freezes. Input is locked and your Dumpling idles where it stands: vulnerable to raccoons and to friends,
  which is the joke. An "on break" sign over your head for others is M2 (§9).

### 5.5 Unstuck (`UI/Pause/Unstuck.cs`)

| Situation | What happens | Authority |
|---|---|---|
| On foot | Drop the held item (`TrashCarrier.Local.Drop()`), then teleport to the best safe point and toast "Unstuck!": (a) the newest **safe-position ring** sample more than 3 m away (sampled every 2 s while grounded on layer Ground, CharacterController not overlapping a Walls collider); else (b) the nearest sidewalk point from the town map data (Neighborhood); else (c) the scene's PlayerSpawner (an on-demand `FindGameObjectsWithTag`) | owner (the player moves itself; Character Controller off → position → on) |
| Driving (local driver) | Calls `TruckRecovery.TryStartTow(fromMenu: true, out whyNot)`: the existing tow drone that lifts, rights and sets the truck down on the nearest road. The fee follows TruckRecovery's own rules (−$25 through the Ledger; free with handling L1+) | the driver simulates the truck (`TruckRecovery` header) |
| Passenger / rear step | Leaves the seat (the existing exit path), then the on-foot rule | owner |
| Carried by a friend, or flopped | Disabled: "Wriggle free first!" | — |

Cooldown 10 s. The button shows the remaining seconds. Each use logs `NetAudit.Event("unstuck", …)`.

### 5.6 Panels

- **Crew (co-op):**
  - One row per `NetRoster.Entries` entry: a swatch in `ChuckPartsData.Variants[slot].playerColor`, the variant name (Chuck, Dot,
    Ozzie, Bert), the display name, and a role (BOSS for the host, otherwise crew).
  - Ping: the host sees everyone's round trip; guests see their own.
  - [Kick] (host only) asks "Kick Dot? They can rejoin." and calls `SessionService.Kick`.
  - [Mute] arrives in M3 and [Block] in M6; both are hidden until then.
- **Town Rules:**
  - Read from `WorldState.Rules` (C2). Before M1 it shows the `TownRules.CrewDefault` text.
  - One row per flag: on/off pill, name, one-liner. Example: "Bonk & shove crewmates: ON. Bonk a friend, they flop for 2 s."
  - Footer for the host: "Changing rules arrives with the spicy toggles update" (MP §11 decision 6).
- **Messages:**
  - The last 30 toasts in this session, with in-game day and time stamps, newest first.
  - Never saved.
  - Invites have a [JOIN] button here.

### 5.7 Leave and quit flows

All of them go through B1:
- Solo: `NetSession.Leave("menu")`, which saves and ends in `SaveGuard.ReloadOwn`, then the menu.
- Host, Close the shop: the same call; guests get "The Boss went home".
- Guest: `NetSession.Leave("left")`.

Quit to desktop runs the same path first, then `Application.Quit`. A paper confirm on each is the only extra step. Copy: "Clock out
for real?" / "Yes, I'm tired" / "One more bag".

---

## 6. Settings

### 6.1 Screen

- **Paper panel, 1280×800 reference**, opened from the main menu or the Esc menu. Tabs: **AUDIO · CONTROLS · VIDEO · INTERFACE ·
  ACCESSIBILITY** (+ PRIVACY at M1.5).
- **Rows:** label, control, and a one-line grey description. A **live preview** applies changes as you move a control.
- **[Reset tab]** and **[Back]**. Back saves.
- **Display changes** (mode or resolution) show a paper dialog, "Keep these settings? Reverting in 10…", and revert unless you
  confirm.

### 6.2 Every setting

| Tab | Setting | Control (range) | Default | Applies to | Edit needed outside this pass |
|---|---|---|---|---|---|
| Audio | Master | slider 0–100 | 80 | `AudioListener.volume` | — |
| | Sound effects | 0–100 | 100 | per-play multiplier in `FeelSfxPlayer` (category SFX) | Feel (UP-1 owns it) |
| | Ambience | 0–100 | 100 | `FeelSfxPlayer` category Ambience (`cricket`, `owl`, `rooster`) | Feel |
| | Music | 0–100 | 70 | **hidden** until a music source exists (§13 Q5) | — |
| | Voice chat | 0–100 | 100 | **hidden** until M3 `IVoiceService` | M3 |
| | Mute when unfocused | toggle | on | `Application.focusChanged` → listener volume 0 | — |
| Controls | Look sensitivity | 0.25–3.00× (step 0.05) | 1.00 | **processor override** on the local `PlayerInput`'s Look action (`scaleVector2(x=s,y=s)`, via `ApplyBindingOverride`, no asset edit); the first-person sort-station look | Sorting: `SortStation.cs:831` × `GameSettings.LookSensitivity` |
| | Invert Y | toggle | off | `invertVector2(invertX=false,invertY=true)` processor; sort-station Y sign | Sorting (same line) |
| | Field of view | 50–90 (shown as the on-foot FOV) | the authored FOV | `FovApplier` caches each live `CinemachineVirtualCamera`'s authored FOV once and sets base + delta; the chess and menu cams are skipped | Sorting: `SortStation.cs:876` adds `GameSettings.FovDelta` |
| | Keybinds | read-only list (§6.3) | — | — | — |
| Video | Display mode | Borderless / Exclusive / Windowed | Borderless (matches WindowsBuild.md) | `Screen.fullScreenMode` | — |
| | Resolution | cycler over `Screen.resolutions` (deduplicated, highest refresh kept) | native | `Screen.SetResolution` + keep/revert dialog | — |
| | VSync | toggle | on | `QualitySettings.vSyncCount` | — |
| | Frame cap | 30/60/120/144/165/240/Unlimited | Unlimited | `Application.targetFrameRate` | — |
| | Graphics quality | Low / Medium / High / Ultra | High | a **runtime clone** of the URP asset (table below) + `QualitySettings.lodBias` | — |
| | Render scale | 50–100% | from the preset | `clone.renderScale` | — |
| Interface | UI scale | 80–150% (step 10) | 100% | `UiScale` → every registered `CanvasScaler` (reference resolution ÷ scale) | World: `DistrictWelcome.cs` registers its scaler (one line) |
| | Minimap | Size S/M/L (200/250/300) · Heading-up / North-up · Show street label | M · Heading-up · on | `Minimap` | — |
| | Map key | M toggles · hold Tab peeks | both | `FullMap` | — |
| | Control hints | toggle | on | hint chips (§8.4) | — |
| | Toast time | 1× / 1.5× / 2× | 1× | `FeelHudToasts` duration multiplier | Feel |
| | Streamer mode (hide join code) | toggle | on (§13 Q7) | Esc invite card | — |
| Accessibility | Screen effects | 0–100% | 100% | rabies post-fx intensity and camera sway ×; `CameraShaker` amplitude ×; `Juice.HitStop` off below 25%; sort-station FOV kick × | Critters/Status: 2 lines; Sorting: 1 line; Feel |
| | Colour-blind-safe markers | toggle | off | `CurbTrash.FreshnessColor` switches to an Okabe–Ito ramp (sky blue #56B4E9 → yellow #F0E442 → orange #E69F00 → vermilion #D55E00) **plus shapes** (● fresh, ◆ going late, ▲ late, ✖ stinky) on world markers, route rows and map pips | Trash: `CurbTrash.cs:28-40` + `StopMarker.cs:120` |
| | Reduce flashing | toggle | off | toast rainbow and wobble off, Bad-toast shake off, FULL! blink steady, cash flash dimmed, PROMOTED rays static | Feel; Progression (`ProgressionHud`) |
| | Auto-pull (tug) | toggle | off | **hidden** until M5 (MP §4.6) | M5 |
| Privacy (M1.5) | Download my data / Delete my data | buttons | — | `Accounts.md` §11 | M1.5 |

**Quality presets** (applied to `Instantiate(GraphicsSettings.defaultRenderPipeline)` assigned to `QualitySettings.renderPipeline`;
any property the builder finds read-only in URP 17.3 is dropped and reported):

| Preset | Render scale | Shadow distance | Cascades | Main shadow res | Extra lights per object | MSAA | LOD bias |
|---|---|---|---|---|---|---|---|
| Low | 0.75 | 25 m | 1 | 1024 | 2 | off | 0.7 |
| Medium | 0.9 | 40 m | 2 | 2048 | 4 | off | 1.0 |
| High (today's asset) | 1.0 | 50 m | 4 | 2048 | 4 | off | 1.0 |
| Ultra | 1.0 | 80 m | 4 | 4096 | 8 | 2× | 1.5 |

### 6.3 Keybinds view

A read-only table grouped by context, generated from `UI/Settings/Keybinds.cs`:

- **On foot:** WASD move, mouse look, Space jump, Shift sprint, **E** interact, **F** other option, **G** drop, **hold LMB**
  throw, **J** chug Garbage Juice (when owned), **Ctrl** stomp (Stompy Boots Lv 2).
- **Truck:** W/S drive, A/D steer, **H** horn, **Space** drift, **X** side-loader arm (when owned), **hold R** tow or unflip,
  **E** get out.
- **Sort station:** **E/LMB** grab or slam, **RMB** chuck, **R** next bag, **Q/Esc** step away.
- **Anywhere:** **Esc** menu/back, **M** map, **hold Tab** peek map, **F2** invite (SteamHud), **Shift+Tab** Steam overlay.
- **Key labels** use the same key-cap sprite as the prompts. Rows gated by an upgrade say so ("with Stompy Boots Lv 2").
- **Source of truth:** the table holds the code's defaults, and an acceptance eval checks it against the serialized values:
  - `hornKey` on `Truck V2.prefab`;
  - `dropKey` and `throwMouseButton` on `Player.prefab`;
  - `armKey` (TruckSideLoader);
  - `nextBagKey` / `exitKey` on the ShopScene sort station;
  - `IInteractable.InteractKey` / `AltInteractKey`.
- A footer says "Rebinding is coming." **Rebinding is out of scope.** It needs every `KeyCode` poll moved to Input System actions
  first; that's listed as a follow-up in §12.

### 6.4 Storage (`Save/SettingsStore.cs` + `UI/Settings/GameSettings.cs`)

- **The file** is `SaveRoot.Path/settings.json`. It's written with `SafeFile.WriteAtomic` at most once per second while
  something changed, and always on Back and on quit. The test data root redirects it automatically.
- **Format:** `{ "schema": 1, "audio": {…}, "controls": {…}, "video": {…}, "ui": {…}, "access": {…} }`. The store keeps the parsed
  `JObject`, so fields it doesn't know survive a save (MP rule 9).
- **A file that won't parse** is quarantined by `SafeFile` and replaced by defaults, with one toast: "Settings were reset (the
  file was damaged)".
- **Steam Auto-Cloud** of `settings.json` is Nick's dashboard step 16 (`Accounts.md` §14). Nothing else is needed in code.
- **Video settings are per machine:** after a cloud sync to a different PC, a resolution that isn't in that PC's list falls
  back to native.

### 6.5 Editor safety (hard rule)

- In the Editor, **Video settings are applied only to runtime objects**:
  - the URP clone, which is assigned on play and **restored to the original on `EditorApplication.playModeStateChanged` →
    ExitingPlayMode**;
  - vsync, frame cap and display mode are logged and not applied.
- `ProjectSettings/QualitySettings.asset` and `Assets/Settings/PC_RPAsset.asset` must be byte-identical after a play session that
  changes every setting (acceptance check UP0-4).

---

## 7. Minimap and full map

### 7.1 Tech choice

| Option | Runtime cost | Look | Night | Upkeep | Verdict |
|---|---|---|---|---|---|
| A. Live top-down render-texture camera | a second camera over ~5.5k GameObjects (MP's district budget): about 1–3 ms per frame including culling, even when throttled; needs URP renderer and layer setup | photo-like; trees hide roads | dark unless lit separately | low code, high tuning | **rejected** |
| B. Editor-baked top-down photo (PNG) | 0 | busy at 300 px (roofs, trees, cars) | baked at noon | re-bake with a lighting state | runner-up |
| C. **Vector map from the town's rectangles, rasterised to a PNG at bake time** | **0** (one quad, 1.8 MB VRAM) | clean cartoon map with outlines and district tints | always readable | an idempotent setup script; the same data gives icons and labels | **chosen** |

The town is entirely axis-aligned rectangles (`NeighborhoodBuilder` `Rect2`; see `mdd/worldlife/wl_town_top.png`). That makes C
both exact and cheap.

### 7.2 The bake (`Editor/Setup_TownMap.cs`, menu *Rubbish/Setup/Town Map*)

- **Inputs.** It opens `Neighborhood.unity` additively, read-only, and closes it without saving. Then it scans:
  - `NeighborhoodGen/Ground/Road_*`, `Sidewalk_*`, `Curb_*`, `DepotPad_*` (BoxCollider bounds → rects);
  - `Park/ParkPath_*`;
  - `Lots/Lot_*` and `Districts/*/Lots/Lot_*`: house footprints are the union of the bounds of renderers named `SM_house*`
    (two-wing mansions give two rects); pools and fountains are optional rects;
  - every `PickupStop` (stopId, address, curb position);
  - every `DistrictGate` (districtId, position);
  - `StreetSign_*` TMP texts;
  - `TruckParking` and the depot `GarageDoorController`;
  - `Districts.All` (bounds, names).
- **Street names per road rect.** First, a vote over addresses of stops within 12 m along the rect's long axis. Then sign texts.
  Then a small fallback table (Depot Rd, Minivan Mile, Old Money Rd, Tax Haven Heights Rd).
- **Outputs:**
  - `Assets/Resources/TownMap.asset` (`TownMapData`, schema 1): bounds x −60..440, z −40..640; `pxPerMeter = 2`; road segments
    {rect, name, axis}; areas {rect, kind}; districts {id, rect, gate position, label position}; labels {text, pos, angle, size,
    kind}; POIs {kind, pos, id}; houses {stopId, curb position, footprint, districtId}; and `sourceHash` (a hash of every input rect).
  - `Assets/UI/map/town_map.png`: 1000×1360, imported as BC7 with mipmaps, bilinear, clamp.
  - A root `[TownMapAnchor]` in `Neighborhood.unity`, **outside `NeighborhoodGen`** so builder re-runs keep it. It holds a
    `TownMapAnchor` component that stores `sourceHash`.
- **Raster (`UI/Map/MapRaster.cs`, pure C#, shared by Editor and runtime).** Layers, back to front:
  1. district lawn tints: Mildly Pleasant #9CCB6B, Minivan Meadows #A8D8A0, Snootington Hills #B9C98F, Billionaire Bluffs #D9CC86;
  2. lots, slightly lighter;
  3. park #7DBE5A with paths;
  4. sidewalks #E9DFC9;
  5. roads #4A4458 with a 2 px #F6F0E2 curb edge and #FFD640 centre dashes (3 m dash, 3 m gap);
  6. depot pad #8E8A99 with a hazard-stripe border;
  7. houses as rounded rects in terracotta #E07A5F with a 2 px #5B3A44 outline (big houses lighter);
  8. water #6EC5E9.
  Edges use 4× supersampling, as in `FeelUi.Supersample`.
- **Staleness.** At runtime, if the anchor's `sourceHash` differs from the asset's (the town was rebuilt without a re-bake), the
  map still works and logs one warning: "Town map is stale: run Rubbish/Setup/Town Map". A dev-only toast shows too.

### 7.3 The minimap (`UI/Map/Minimap.cs`)

- **Where:** bottom-left. The lid is 300 px at the 1080 reference (sizes M 250 and S 200). A street-label pill sits under it
  ("Maple St · Mildly Pleasant Acres"). In HUD zone terms it's `Minimap` (§8.2). It's a nested Canvas under FeelHUD, so its
  per-frame rebatch doesn't dirty the HUD canvas.
- **Frame:** a galvanised **bin lid** (grey ring #9AA3AE, rim #5E6670, 8 rivets). The lid handle at the top is the heading notch.
  An "N" tab orbits the rim. When `FeelWorld.Cleanliness < 35`, a fly lands on the lid.
- **Content:**
  - `RawImage` of the baked map; `uvRect` follows the centre.
  - Masked by a circle (`Mask` + `FeelUi.Circle`).
  - Rotates heading-up (on foot: camera yaw; driving: truck forward) or stays north-up.
  - Visible radius 45 m on foot. Driving, it grows from 45 m to 90 m between 0 and 15 m/s (smoothed).
  - At night a 15% navy tint.

| Layer (back to front) | Source | Look |
|---|---|---|
| Locked districts | `Districts.IsUnlocked` | ink hatch at 35% + padlock at the gate |
| Curb pips | `HouseDirectory` houses × `FeelWorld.At(stopId)` (only `Progression.IsSubscribed` houses in `Districts.IsUnlocked` districts) | disc in `CurbTrash.FreshnessColor`, white outline; size by bags (1–2: 10 px, 3–4: 13 px, 5+: 16 px); neglected = stink lines pulsing at 1.5 Hz; shapes in colour-blind mode |
| Planned route | `FeelWorld.PlannedRoute(route, from, 3, list)` | gold dotted polyline (6 px dots every 4 m) |
| Next stop | `FeelHud.Route.Target`, **the same house as the HUD arrow** | gold ring pulsing at 1.5 Hz + a bouncing chevron |
| Depot | TownMap POI | "R" garage badge |
| Gates | TownMap POI | open gate, or padlock (tooltip on the full map) |
| Truck(s) | `MapBeacon` kind Truck | top-down truck icon in the company tint (Rubbish Co. olive until M1 `CompanyState.Tint`), rotated to heading |
| Crew | `MapBeacon` kind Player + `RosterView.Slot` | arrowheads in the Dumpling colours |
| You | `PlayerController.Local` | bigger arrowhead with a white outline + a 20% view cone |
| Cars (optional) | `MapBeacon` kind Car, if the living-world builder registers them | small grey dots |

- **Rim clamp.** When the next stop, depot or truck is off the map, its icon sticks to the rim with a distance ("Depot 240 m").
  The truck shows on the rim only while you're on foot and more than 40 m away. That doubles as MP §2.5's "arrow to the truck"
  for late joiners.
- **Visibility:**
  - shown when `TownMapAnchor.Active != null` (no scene names, MP rule 6);
  - hidden in the garage (no anchor), in Station mode, while the full map is open, while the pause menu is open in solo, and in
    menus.

### 7.4 The full map (`UI/Map/FullMap.cs`)

- **Open and close:**
  - **M** toggles the interactive map: `UiStack.Push(lockGameplay: true)`, free cursor.
  - **Holding Tab** shows a non-interactive peek at 85% opacity with no input lock (you can keep driving). Releasing Tab closes it,
    and Shift+Tab never triggers it.
  - **Esc** closes either.
  - Works in the garage too, for planning: the depot is marked "You are here (garage)".
- **Frame:** a folded **paper map** (paper colour, two fold lines, a coffee ring, a raccoon doodle in the margin), titled "RUBBISH
  CO. ROUTE MAP · property of Chuck".
- **Legend panel (right side, plum):**
  - City %, bags out, late count;
  - per-district rows: name, Trust % (`Progression.Trust01`), subscribers x/y (`Progression.Subscribers`), bags out;
  - the freshness legend with shapes;
  - control hints.
- **Interactions:**
  - wheel zoom 1×–4×; drag to pan; **Space** recentres on you;
  - **clicking a house** pops a card: "12 Maple St · 3 bags · 2 h left · Mildly Pleasant Acres";
  - **clicking a locked district** pops "Snootington Hills · needs Rank 7 + a $3,500 permit · you're Rank 4", which supports user
    item 2.
- **Labels:** street names along road centrelines (TMP in map space, scaled with zoom, hidden below 1.5×); big tilted district
  names; "RUBBISH CO." at the depot; the park name.
- **"Here be goblins":** a hand-lettered note near any neglected pile.

### 7.5 Beacons (`UI/Map/MapBeacon.cs`)

- A tiny component with a static registry: kind, tint, optional `Transform` override.
- **Trucks:** `Editor/Setup_MapBeacons.cs` adds it to `Truck V2.prefab`. Run it after B2's `Setup_Coop`, once M0 releases the
  prefab.
- **Players:** added at runtime on `CoopPlayerHooks.Spawned`. This avoids another `Player.prefab` edit, which the Character and
  Coop owners share.
- No `Find*ObjectByType` calls anywhere (MP rule 3).

### 7.6 Performance budget

- Bake: under 10 s in the Editor.
- `Minimap.Tick` ≤ 0.15 ms and `FullMap.Tick` ≤ 0.5 ms (Profiler markers, 1080p).
- 0 B GC per frame after warm-up: pooled pips, and `FeelWorld.At` is already a per-frame snapshot.
- Pips rebuild only on `GameSession.CurbTrashChanged` or `DayStarted`. Per frame only the content root moves, rotates and pulses.

---

## 8. HUD pass

### 8.1 Issues and fixes

| # | Fix |
|---|---|
| H1 | **`Feel/FeelHudPrompt.cs` replaces TooltipUI's visuals.** Layout: [key cap][text]. The cap auto-sizes for "E", "LMB" (mouse icon), "E/LMB", "Space" and "Hold R". Text is at most 640 px wide and at most 2 lines; the 2nd line ends in "…" via a 2-line variant of `FeelUi.Fit`. An optional alt row ([F] option) sits below at 22 px. `TooltipUI.cs` keeps its API (`Show`, `AppendAltHint`, `Hide`) but forwards to the prompt and keeps its scene box inactive, so **no call site changes** |
| H2 | Toasts move into the **ToastLane** zone (§8.2): at most **3** visible, at most **2 lines** (the full text goes to Messages), centred in the lane, and the lane's bottom is clamped above any centre card. The rest stays as it is (merging, styles) |
| H3 | **Station mode** (§8.3): the top bar collapses to one slim pill; the left column, truck gauge, route panel, rank chip and minimap hide; toasts move to a **bottom lane** above the dock |
| H4 | **Hint chips** (§8.4) |
| H5 | Rank chip: the ribbon becomes 16 px on a wider ribbon; Rep text is 18 px **under** the bar instead of inside it; caption 18 px; a "badge-only" compact form for narrow widths (§8.2) |
| H6 | `HudLayout` density steps (§8.2) |
| H7 | Cash panel 360×104. The delta becomes a floating pill that rises from the panel. The company name and tint (B14's `CompanyWalletLabel`) take an 18 px line at the top of the panel once M1 provides them |
| H8 | Rep floaters rise **upward out of the chip** (y −26 → +10) instead of into the lane |
| H9 | The Head Office memo moves to the **right column under the route panel** (420 wide), which frees the bottom-right for invite cards |
| H10 | The lead moves the TEST DATA ROOT watermark to the bottom edge, 14 px high (`SaveRoot.cs` is the lead's; §10.6) |

### 8.2 `HudLayout` zones (`Feel/HudLayout.cs`)

Zones are computed every frame in reference units from the HUD root rect, so UI scale and aspect ratio are handled in one place.
`M` = 24 px margin.

```
 refW × refH (1920×1080 at 16:9; 1663×1247 at 4:3; 1610×1288 at 5:4; 2216×935 at 21:9)
 ┌─────────────────────────────────────────────────────────────────────────────────────┐
 │ TopLeft: [CASH 360][CLOCK 300][RANK 432]                        TopRight: [TRUCK 420] │  y −20..−128
 │ LeftCol (x M..M+390):                                           RightCol (420 wide): │
 │  City · Finds · Rabies                     ToastLane            Route panel + rows   │
 │                                   (LeftCol.right+16 .. RightCol.left−16)  Memo       │
 │                                      top = TopBar.bottom − 16                         │
 │                             CenterStage: reveal, PROMOTED, welcome, Work Order        │
 │ Minimap (bottom-left,                                                                 │
 │  lid + label pill)              BottomDock: prompt (y 64+) + hint chips (y 12+)       │
 │                                 width ≤ min(1100, refW − 2·(minimap block + 16))      │
 └─────────────────────────────────────────────────────────────────────────────────────┘
```

**Density** (the top bar has to fit cash + clock + rank + gap + truck, plus margins):

| Step | When | Top bar |
|---|---|---|
| **Full** | refW ≥ 1608 | as drawn |
| **Compact** | 1258 ≤ refW < 1608 | the rank chip becomes **badge-only** (82 px: the number + a circular Rep ring). Its title shows only on promotion |
| **Tight** | refW < 1258 | badge + the clock compacts to 200 (time and day) + the truck gauge compacts to 300 |

The minimum is refW ≈ 1040, so the UI-scale slider is capped at 150%: that gives refW ≥ 1073 even at 5:4.

**Collision rules, applied in order:**
1. The toast lane never overlaps the side columns.
2. The minimap steps L → M → S if the left column would reach it. If it still overlaps, it hides while the rabies widget is up.
3. The bottom dock wraps to 2 rows before it shrinks.
4. A centre card pushes extra toasts into a queue; it never overlaps them.

### 8.3 HUD modes (derived each frame; no new public state)

| Mode | Condition | Visible |
|---|---|---|
| Hidden | `PlayerController.Local == null` | nothing |
| Map | `FullMap.IsOpen` (interactive) | toasts only |
| Paused | `PauseMenu.IsOpen` | solo: toasts; co-op: everything, under the dim |
| Station | `InteractionFocusOverride(Local)` is true (the sort station today; generic for any future focus owner) | a slim pill (cash + time, 56 px tall, y −16..−72), rabies (moved to the bottom-left), toasts in the bottom lane, the dock |
| Laptop | `IsUIBlocking` while no UiStack screen is open | cash, rabies (as today) |
| Driving | `Local.IsDriving` | everything; the minimap zooms out; the hint chips show driving controls |
| Street / Depot | default; `TownMapAnchor.Active` picks between them | everything (Depot: no minimap) |

### 8.4 Prompt and hint chips (`Feel/FeelHudHints.cs`)

- **Parsing.** The text set by `GameHUD.SetContextHint` is split into: an optional **title** (before " · "), **chips**
  (`[key] label` pairs) and an optional **tip** (a trailing parenthetical). For example, "Suspicious Trash Bag · [E/LMB] Slam it
  open [RMB] Chuck [Q] Step away (aim at the compactor to crush it unopened)" becomes:
  - title "Suspicious Trash Bag" (18 px, grey);
  - chips [E/LMB] Slam it open · [RMB] Chuck · [Q] Step away (24 px **bold** on a 72%-opaque backing);
  - tip "aim at the compactor to crush it unopened" (18 px italic).
- **Callers don't change.** Text without brackets renders as plain text.
- **Layout.** Chips wrap to at most 2 rows inside the dock width.
- **De-duplication.** A chip whose key and label equal the prompt's is dropped, so "E Slam it open!" doesn't appear twice.
- **Interface setting.** "Control hints: off" hides chips but keeps the prompt.

### 8.5 Canvas layering (sort orders)

| Order | Layer |
|---|---|
| 0 | legacy TooltipUI scene canvas (kept inactive) |
| 90 | sort-station crosshair |
| 99 / 100 | ProgressionHud / FeelHUD (the minimap is nested in FeelHUD) |
| 200 | laptop UI Toolkit panel |
| 300 | full map |
| 500 | main menu |
| 640 | DistrictWelcome (unchanged) |
| 1000 / 1010 / 1020 | pause / settings / dialogs |
| 32000 / 32767 | ScreenFader / LoadingSystem, SteamHud blocker |

The constants live in `UI/Kit/UiLayers.cs`.

### 8.6 UI scale, colour blindness, flashing

- **UI scale.** `UiScale.Register(CanvasScaler)` is called by FeelHUD, ProgressionHud, the map, menus, pause and DistrictWelcome.
  It sets the reference resolution to 1920×1080 ÷ scale. The edit-mode preview (`FeelHud.EnablePreview`) multiplies its manual
  `scaleFactor` by the same value, so screenshots test it.
- **Colour blindness.** The shapes are procedural sprites (`FeelUi.Glyph(FreshnessStage)`) used by pips, route-row dots and world
  markers. `CurbTrash.FreshnessStage(d, age)` is a tiny new helper next to `FreshnessColor` (Trash owner).
- **Reduce flashing.** The switches are listed in §6.2.

---

## 9. Co-op implications

| Topic | Rule |
|---|---|
| Network traffic | **None new.** Every view reads state that's already replicated: curbs (`GameSession.CurbBags` via B6 `NetMirror.SetCurbs`), positions (NetworkTransform), roster (B1 `NetRoster`), rules (C2 `WorldState.Rules`), wallet (B7 `CompanyState`) |
| Pause | Never freezes a shared world (rule 4). Solo only, decided by `NetSession.IsSolo` |
| Esc input lock | Local only. Your Dumpling idles and can be bonked (Crew rules). M2 option: a roster flag `Away` shows a "BRB" sign over your head (one bit in `RosterEntry.flags`, set by a guarded request) |
| Settings | Local only, never networked. FOV and sensitivity affect only your camera |
| Minimap on guests | Curb pips need B6 (M1). Before M1 a guest sees roads, crew and truck, and no pips ("Curb info arrives when the host's world syncs"). The next stop is computed **per player from their own position**, as the HUD arrow is today |
| Crew colours | Slot → `ChuckPartsData.Variants[slot]`, remembered across rejoin by PlayerKey (B1 `SlotTable`) |
| Trucks | Company tint from `CompanyState.Tint` (M1). Depot Row (M7) trucks show in their own company's tint. Rival trucks are ghosted (MP §4.11) and drawn at 60% opacity on the map |
| Shotgun navigator (M3) | The full map becomes the navigator's tool: clicks drop **pings** sent over `WorldFx` (a named message, through `NetGuard`), and the driver's minimap and arrow follow them (MP §4.3). Not in this pass; `FullMap` exposes `event Action<Vector3> PingRequested` for it |
| Unstuck | On foot: owner authority. Truck: the existing driver-side `TruckRecovery` tow. Never moves anyone else. Logged in `NetAudit` |
| Kick | Host only, through `SessionService.Kick` (B1). Votekick is for public Towns only (stage 4) |
| Invites | Hosts only. Guests can see the code they used. Streamer mode hides it by default |
| Leave wording | Solo "Save & quit", host "Close the shop", guest "Leave Nick's shift". Every path ends in B1's `SaveGuard.ReloadOwn` |
| Join flow M1–M4 | Join as crew, no picker. The "Bring RUBBISH CO." picker is M5 (`Accounts.md` §3.4); the Join screen leaves room for it |
| Toasts | Session toasts ("BEEP: Ozzie clocked in 2:14 PM (LATE)") arrive through `WorldFx.Toast` (B7) and use the same lane and log |
| Lint | `PlayerController.Local` reads in the HUD, map and pause get `// coop:allow(local) HUD/camera only`. `TrashRoute.Current` reads in map presentation get `// coop:allow(current) presentation`. Neither needs approval |

---

## 10. Build plan

### 10.1 Rules for every UI-pass builder

- Follow CoreLoop.md: global namespace; scene and prefab changes only through idempotent `Setup_*` scripts; **never save
  Bootstrap**.
- Follow MP §9: Ledger only (this pass moves no money except the existing tow fee); no new RNG for outcomes (cosmetic `Random`
  needs `coop:allow(random) cosmetic`); no `Find*ObjectByType`; no scene names; no `Time.timeScale` except the approved
  PauseService line; saved data only through `Save/**`.
- Edit only your package's files. Re-read shared files immediately before editing them. Never revert someone else's change.
- **Editor lock** for play mode and setups, run on a **test data root** (`Tools/dev/test_data_root.sh`). Never interrupt Nick's
  play session (`editor_status`).
- Before reporting done:
  - a clean compile (`unity command recompile` → `recompile_status` → `console --level error`);
  - your gate;
  - `Tools/coop_lint.sh --files '<your globs>'`, with every allow listed.
- **Prefer edit-mode screenshots** (`FeelHud.EnablePreview`, `shot_scene.cs`) over play mode.

### 10.2 Contract (UP-0 lands these compile-green first, the way MP's C1 did; bodies follow)

```csharp
// UI/Kit
public static class UiLayers { public const int Map = 300, Menu = 500, Pause = 1000, Settings = 1010, Dialog = 1020; }
public static class UiTheme { /* §3 colours; MinText = 18, Body = 26, Button = 34, Title = 120; sfx ids */ }
public static class UiScale { public static float Factor { get; } public static event Action Changed;
  public static void Register(CanvasScaler s); public static void Unregister(CanvasScaler s); }   // reads GameSettings.ui.scale
public abstract class UiScreen : MonoBehaviour { public virtual void OnShow() {} public virtual void OnHide() {}
  public virtual bool OnBack() => false; }                                                      // true = handled
public static class UiStack { public static void Push(UiScreen s, bool lockGameplay); public static void Pop(UiScreen s);
  public static UiScreen Top { get; } public static bool AnyOpen { get; } public static int LocksHeld { get; }
  public static void EnsureEventSystem(); }                                                   // + the Esc router (§5.1)
public static class UiKit {                                                                   // all built on FeelUi
  public static Canvas Canvas(string name, int layer, Transform parent = null);              // overlay + scaler (UiScale) + raycaster
  public static RectTransform Paper(Transform p, Vector2 size); public static RectTransform Plum(Transform p, Vector2 size);
  public static UiButton Button(Transform p, string label, Action click, UiButtonStyle style = UiButtonStyle.Sticker);
  public static UiSlider Slider(Transform p, string label, float min, float max, float v, Func<float, string> fmt, Action<float> changed);
  public static UiToggle Toggle(Transform p, string label, bool v, Action<bool> changed);
  public static UiCycler Cycler(Transform p, string label, IReadOnlyList<string> options, int index, Action<int> changed);
  public static UiTabs Tabs(Transform p, IReadOnlyList<string> names, Action<int> changed);
  public static ScrollRect ScrollList(Transform p, Vector2 size);
  public static RectTransform KeyCap(Transform p, string key);                                // shared with FeelHudPrompt
  public static TMP_InputField TextField(Transform p, string placeholder, int maxChars, TMP_InputField.CharacterValidation v);
  public static void Confirm(string title, string body, string yes, string no, Action<bool> done, float autoRevertSeconds = 0); }

// UI/Settings
[Serializable] public sealed class GameSettingsData { public int schema = 1; public AudioS audio; public ControlsS controls;
  public VideoS video; public InterfaceS ui; public AccessS access; }                          // field defaults per §6.2
public static class GameSettings { public static GameSettingsData Current { get; } public static event Action<string> Changed;
  public static void Edit(string section, Action<GameSettingsData> change); public static void SaveSoon(); public static void Reset(string section);
  // cheap reads for other systems (no allocation):
  public static float LookSensitivity { get; } public static bool InvertY { get; } public static float FovDelta { get; }
  public static float ScreenFx01 { get; } public static float SfxVolume { get; } public static float AmbienceVolume { get; }
  public static bool ColorblindMarkers { get; } public static bool ReduceFlashing { get; } public static float ToastTime { get; } }

// Save
public static class SettingsStore { public const string FileName = "settings.json";
  public static bool TryLoad(out string json); public static bool Write(string json); }     // SaveRoot.Path + SafeFile

// Feel (UP-1)
public enum HudZone { TopLeft, TopRight, LeftColumn, RightColumn, ToastLane, CenterStage, BottomDock, Minimap, BottomLane }
public enum HudDensity { Full, Compact, Tight }
public static class HudLayout { public static Rect Zone(HudZone z); public static HudDensity Density { get; } public static HudMode Mode { get; } }

// UI/Map (UP-2)
public sealed class TownMapData : ScriptableObject { /* §7.2 */ public static TownMapData Load(); public Vector2 WorldToMap(Vector3 w); public Vector3 MapToWorld(Vector2 m);
  public bool TryNearestRoad(Vector3 w, out Vector3 onRoad, out string streetName); }        // also used by Unstuck
public sealed class MapBeacon : MonoBehaviour { public MapBeaconKind kind; public Color tint; public static IReadOnlyList<MapBeacon> All { get; } }
public sealed class TownMapAnchor : MonoBehaviour { public string sourceHash; public static TownMapAnchor Active { get; } }

// UI/Pause (UP-3)
public static class PauseService { public static bool Frozen { get; } public static bool CanFreeze { get; } }
```

### 10.3 Packages

| ID | Creates | Edits (owned during the package) | What | Gate (§11) | Est. |
|---|---|---|---|---|---|
| **UP-0 Kit + settings** | `UI/Kit/UiKit.cs`, `UiKit.Widgets.cs`, `UiTheme.cs`, `UiScale.cs`, `UiStack.cs`, `UiLayers.cs`, `UiText.cs`; `UI/Settings/GameSettings.cs`, `SettingsApplier.cs`, `FovApplier.cs`, `LookApplier.cs`, `Keybinds.cs`, `SettingsScreen.cs`; **`Save/SettingsStore.cs`** (Save/**: the lead approves the new file) | none | The §10.2 contract first (stubs, day 1), then widgets, settings model, store, applier, screen | UP0-1…5 | 3 d |
| **UP-1 HUD layout** | `Feel/HudLayout.cs`, `Feel/FeelHudPrompt.cs`, `Feel/FeelHudHints.cs` | `Feel/FeelHud.cs`, `FeelHudToasts.cs`, `FeelHudRoute.cs`, `FeelHudCity.cs`, `FeelHudRabies.cs`, `FeelUi.cs` (key cap, glyph, mouse icons, 2-line Fit), `FeelSfxPlayer.cs` (volume categories, `ignoreListenerPause` on the 2D pool), `CameraShaker.cs` + `FeelJuice.cs` (screen-fx scale; HitStop skipped while frozen); `TooltipUI.cs` (forwarder); `Progression/ProgressionHud.cs` (chip, floaters, memo slot, UiScale; **with Progression A's OK**) | §8 | UP1-1…5 | 3 d |
| **UP-2 Map** | `Editor/Setup_TownMap.cs`, `Editor/Setup_MapBeacons.cs`, `UI/Map/TownMapData.cs`, `MapRaster.cs`, `MapView.cs`, `MapIcons.cs`, `Minimap.cs`, `FullMap.cs`, `MapBeacon.cs`, `TownMapAnchor.cs`; written by setups: `Assets/Resources/TownMap.asset`, `Assets/UI/map/town_map.png`, the `[TownMapAnchor]` root in `Neighborhood.unity`, the beacon on `Truck V2.prefab` | `Feel/FeelHud.cs` (mount the minimap in its zone; **same builder as UP-1, or after it**) | §7 | UP2-1…6 | 3–4 d |
| **UP-3 Esc menu** | `UI/Pause/PauseMenu.cs`, `PauseService.cs`, `Unstuck.cs`, `CrewPanel.cs`, `TownRulesCard.cs`, `NotificationLog.cs`, `ShiftCard.cs` | B1's minimal Esc overlay (retired: its key handler steps aside when `PauseMenu` exists, or the file is deleted, lead's call) | §5 | UP3-1…6 | 2–3 d |
| **UP-4 Main menu** | `UI/Menu/MainMenuCoopUI.cs` (shell + screen stack; **B14's file name**), `UI/Menu/MenuTitle.cs`, `MenuDiorama.cs`, `JoinScreen.cs`, `ContinueCard.cs`, `InviteCards.cs`; `Editor/Setup_MenuDiorama.cs` → `Assets/Prefabs/UI/MenuChuck.prefab`, `MenuTruck.prefab`, `MenuDiorama` in `MainMenuScene.unity` | `MainMenuManager.cs` (hosts `MainMenuCoopUI`, hides the legacy buttons, **keeps B1's `NetSession` calls and L0's latch lines**), `JoinCodeUI.cs` (retired → forwards), `UI/Menu/NetErrorPanel.cs` (restyle only) | §4 | UP4-1…6 | 3 d |
| **B14** (MP, M1 wave 3b) | as MP §8.7.4 | as MP | **Re-scoped onto UP-4's shell and UiKit.** `CompanyCardView`, Change…, `RestorePanel`, Towns, first launch (§4.5), `CompanyWalletLabel` drawn in the cash panel's line (§8.1 H7), `SessionToasts` through `GameHUD.Toast` | MP B14 gate + UP4-1 rerun | 3 d |
| **UP-5 Friends and invites** | `Net/Steam/SteamFriendsFeed.cs` (a façade with plain DTOs: friends playing Rubbish with a joinable lobby, invites received via `LobbyInvite_t`, avatars → `Texture2D`; `InjectFake` for tests); `UI/Menu/FriendsPanel.cs` | `Net/Steam/SteamHud.cs` (a `SuppressChrome` flag; **B10 owner's OK**) | §4.4, §4.8 | UP5-1…3 | 1–2 d |

**Builders** (by file ownership, so they can run at once):
- **A** takes UP-0, then UP-3.
- **B** takes UP-1, then UP-2, and owns `Feel/**`.
- **C** takes UP-4, then B14, then UP-5.

UP-1, UP-2 and UP-4 start once UP-0's contract stubs compile (day 1).

### 10.4 Order, and how it fits with M0 and M1

```
M0 gate (MP §8.7.7) ─► UP-0 (contract day 1) ─┬─► UP-1 ─► UP-2            (builder B; Feel/** hand-off needed, §13 Q9)
                                              ├─► UP-3                    (needs B1 landed; GameClock.SetHeld, §10.6)
                                              └─► UP-4 ─► B14 (W3b) ─► UP-5 (needs B10 landed)
C2 ─► W3 (B6 B7 B8 B9 B10) runs alongside; the conflicts are below
```

| Shared file | M1 package | Resolution |
|---|---|---|
| `MainMenuManager.cs`, `UI/Menu/**` | B14 (W3b) | UP-4 lands **before** W3b starts; B14 builds on it (recommended: the same builder) |
| `Core/GameClock.cs` | B6 (owns it after C2) | B6 adds `SetHeld(bool)` together with its additive API, or the lead adds it during C2. UP-3 calls it and never edits the file |
| `PlayerController.cs` | B9 (`GatherOptions` → `CoopGate`) | the UI pass doesn't edit it: TooltipUI forwards, and the Esc router reads public statics |
| `Truck V2.prefab` | B2 (`Setup_Coop`) | `Setup_MapBeacons` runs after `Setup_Coop`. Both are idempotent and add components; neither removes the other's |
| `Neighborhood.unity` | the living-world workflow (NPCs and cars, in flight now) | `Setup_TownMap` runs after it lands. It only adds a root outside `NeighborhoodGen` |
| `Feel/**` | "the Yeet slice" (MP §8.7.1 #17); no Yeet code is in the tree today | the lead confirms the hand-off to builder B before UP-1 (§13 Q9) |

### 10.5 File ownership summary

- **New folders owned by the UI pass:** `Assets/Scripts/UI/Kit/**`, `UI/Settings/**`, `UI/Pause/**`, `UI/Map/**`,
  `Assets/UI/map/**`, `Assets/Prefabs/UI/**`.
- **Shared with B14 by file:** `UI/Menu/**`.
- **One file in `Save/**`:** `SettingsStore.cs`.
- **Editor scripts:** `Setup_TownMap.cs`, `Setup_MapBeacons.cs`, `Setup_MenuDiorama.cs`.
- **Read-only for this pass:** `Player.prefab`, `Truck V2.prefab` (copied, never saved; the beacon setup is the only prefab edit
  and touches only the truck), Bootstrap, ProjectSettings, Packages, `Net/**` except UP-5's new façade file, `Character/**`,
  `Sorting/**`, `Trash/**`, `Status/**`, `World/**`, `Shop/**`, `GameSession.cs`.

### 10.6 One-line requests to other owners

| File | Owner | Change | For |
|---|---|---|---|
| `Sorting/SortStation.cs:831` | Sorting | mouse delta × `GameSettings.LookSensitivity`; Y × (InvertY ? −1 : 1) | sensitivity |
| `Sorting/SortStation.cs:876` | Sorting | `FieldOfView = fieldOfView + GameSettings.FovDelta + fovKick * GameSettings.ScreenFx01` | FOV, screen effects |
| `Status/RabiesStatus.cs` (the `Apply` caller) + `Status/RabiesCameraSway.cs` | Critters | intensity and amplitude × `GameSettings.ScreenFx01` | rabies intensity |
| `Trash/CurbTrash.cs:28-40` + `Trash/StopMarker.cs:120` | Trash | colour-blind palette switch, `FreshnessStage(d, age)`, a glyph on the marker | colour-blind markers |
| `World/DistrictWelcome.cs:62-66` | World | `UiScale.Register(scaler)` | UI scale |
| `Shop/Truck/TruckRecovery.cs` | Shop (Progression B) | `public bool TryStartTow(bool fromMenu, out string whyNot)` wrapping the existing hold-R path | Unstuck |
| `Core/GameClock.cs` | lead / B6 | `public static void SetHeld(bool held)` (solo only; refuses while remote clients are connected) | solo freeze |
| `Net/Session/NetSession.cs` | B1 | `public static bool IsSolo` (if B1 doesn't ship it) | pause variants |
| `Net/Core/Save/SaveRoot.cs` | lead | draw the TEST DATA ROOT watermark at the bottom edge | H10 |
| `Net/Steam/SteamHud.cs` | B10 | `public static bool SuppressChrome` (keeps the blocker) | no duplicate invite UI |
| `Editor/NeighborhoodBuilder.cs` (optional) | Trash / Neighborhood | call `TownMapBaker.Bake()` at the end of `Build` | keep the map in sync |

### 10.7 Lint allows

- **Needs the lead's approval:**
  - `timescale`: one line in `UI/Pause/PauseService.cs` for the solo freeze.
- **No approval needed** (listed in reports):
  - `local`: HUD, map and pause reads;
  - `current`: `TrashRoute.Current` in map presentation;
  - `random`: cosmetic, for diorama beats and splash text.
- **Must stay at 0 new hits:** `cash`, `guid`, `scene`, `save` (outside `Save/**`), `steamapi` (outside `Net/Steam/**`).

### 10.8 Setup run order (lead, under the lock)

1. `Setup_Coop` (B2, M0).
2. `Setup_MapBeacons`.
3. `Setup_TownMap`: after the living-world builder's Neighborhood changes have landed, and after any `NeighborhoodBuilder.Run`.
4. `Setup_MenuDiorama`: after `Setup_Character` / Chuck and `Setup_Coop`, so the copies match.

Each is idempotent. Run each twice and diff the outputs (gates below).

---

## 11. Acceptance checks

Every check reports numbers.

- **EV** = `eval_file` (read-only, no play mode).
- **PREVIEW** = edit-mode render through `FeelHud.EnablePreview` / `shot_scene.cs`.
- **PLAY** = under the lock on a test data root.
- **MPPM2** = MP §8.7.6.

**UP-0 (kit and settings)**
1. EV: settings round trip. Write defaults, add an unknown field `{"future": {"x": 1}}`, load, change one value, save. The unknown
   field is still present, and the changed value is read back exactly.
2. EV: a damaged `settings.json` (truncated at 10 random offsets) loads as defaults. `.corrupt-*` exists, and the original bytes
   are untouched.
3. EV: a dry run of `SettingsApplier.Describe(preset)` for each preset lists the URP clone values in the §6.2 table. Any property
   that's read-only is reported.
4. PLAY: change every setting, exit play mode. **sha256 of `ProjectSettings/QualitySettings.asset` and
   `Assets/Settings/PC_RPAsset.asset` is unchanged.** The test root's `settings.json` holds the new values; Nick's real root is
   unchanged.
5. PREVIEW: SettingsScreen, all tabs, at 1280×720, 1920×1080 and 2560×1080. No text under 18 px reference; no clipped labels
   (each `TMP_Text.isTextOverflowing == false`).

**UP-1 (HUD)**
1. **Overlap matrix.** EV with PREVIEW at 1280×720, 1920×1080, 2560×1440, 1024×768, 1280×1024 and 3440×1440, each at UI scale
   80%, 100% and 150% (18 cases). Worst-case data:
   - 3 two-line toasts;
   - rabies + finds + city with its sub line;
   - a route with 3 rows;
   - the rank chip, a memo and the minimap block;
   - a prompt of 120 characters with an alt row;
   - a 6-chip hint.
   Pass: the screen-space rects of zone-owned widgets overlap **0 times** pairwise, and everything is inside the safe rect.
   Report the density step per case.
2. PREVIEW, Station mode with `chuck_test/20_sort_fp` framing: no HUD rect intersects the bin-sign band (y 90–200 reference).
3. The H1 strings ("Grab Saggy Trash Bag (1.2 kg)", a 120-character prompt): the prompt is ≤ 2 lines, the text stays inside its
   panel, and the key cap doesn't overlap the text (rect test).
4. The H4 hint renders as title + 3 chips + tip, and the chip duplicating the prompt is dropped.
5. PLAY smoke: 2 minutes in ShopScene and 2 minutes in Neighborhood with 0 errors, and HUD build time ≤ 50 ms.

**UP-2 (map)**
1. `Setup_TownMap` run twice: identical sha256 for the PNG and the asset.
2. EV raster probes:
   - pixel at Maple St centreline (80, 60) = road colour ±2;
   - park centre (80, 115) = park colour;
   - depot pad (0, −6) = pad colour;
   - one house footprint centre = roof colour;
   - Meadows lawn (320, 120) = Meadows tint.
3. EV: WorldToMap followed by MapToWorld round trip < 0.25 m over 1,000 random points. `TryNearestRoad` from 100 lawn points
   returns a point inside a road rect.
4. PLAY:
   - the minimap is visible in Neighborhood and hidden in ShopScene;
   - the pip count equals the houses with bags (subscribed and unlocked);
   - the next-stop ring is within 2 px of the projection of `FeelHud.Route.Target`;
   - driving at 15 m/s gives a visible radius of 90 ± 5 m.
5. PLAY: M opens and closes; Esc closes; holding Tab peeks without locking input (the player moves while Tab is held); Shift+Tab
   doesn't peek.
6. Profiler: `Minimap.Tick` ≤ 0.15 ms and `FullMap.Tick` ≤ 0.5 ms (1080p). GC 0 B per frame over 600 frames after warm-up.
   Screenshots at day, night, driving and colour-blind on.

**UP-3 (Esc menu)**
1. **Esc routing matrix** (PLAY, 6 cases):
   - laptop open → Esc closes only the laptop, pause stays closed;
   - sort station → steps away only;
   - full map → closes only the map;
   - Settings from pause → back to pause;
   - pause → resumes;
   - nothing open → pause opens.
2. **Solo freeze:** 30 s paused.
   - ΔTotalHours < 0.001 h;
   - curb bag ages unchanged;
   - truck displacement < 1 cm;
   - after resume `Time.timeScale == 1`;
   - the same after these in turn: resume, a scene change, quit, and a forced exception inside the menu.
3. **Co-op never freezes** (MPPM2): the host opens Esc for 30 s, and the guest's ΔTotalHours = 0.208 ± 0.01 h. The guest opens
   Esc and the host is unaffected. `Time.timeScale` stays 1 on both.
4. Unstuck:
   - on foot inside a wall collider → on Ground within 1 frame, not overlapping Walls;
   - the cooldown shows 10 s;
   - driving → `TruckRecovery.TryStartTow` starts the tow, and the fee posts once through the Ledger, or nothing if handling
     L1+.
5. Leave paths:
   - solo "Save & quit" → the test-root files are written and the menu shows;
   - host "Close the shop" → the MPPM2 guest sees "The Boss went home";
   - guest "Leave" → B1's `ReloadOwn` hook logs once.
6. PREVIEW: solo, host and guest variants at 1280×720 and 1920×1080.

**UP-4 (main menu)**
1. PLAY: Bootstrap → menu interactive in ≤ 2 s; **Play solo** reaches ShopScene; **Host with friends** reaches ShopScene hosting,
   with the code visible in Esc.
2. Join with a bogus code: a readable sticker within 10 s, back on the menu, and the guest's own files unchanged (sha256).
3. Quit confirm: "One more bag" cancels; "Yes, I'm tired" quits (the Editor logs instead).
4. The diorama:
   - 0 NetworkObjects and 0 Rigidbodies in MainMenuScene;
   - 5 minutes idle with 0 errors or warnings and GC < 1 KB per frame;
   - GPU frame ≤ 6 ms at 1080p on this Mac (Profiler);
   - each of the 4 beats observed at least once in 3 minutes (logged);
   - `Setup_MenuDiorama` run twice gives the same object count and the same prefab hashes;
   - `Player.prefab` and `Truck V2.prefab` mtime and sha256 unchanged.
5. PREVIEW, or PLAY capture: 1280×720, 1920×1080, 1024×768 and 3440×1440. Buttons never overlap the logo, and the Continue card
   fits.
6. Keyboard-only: arrows and Enter reach every button; Esc goes back.

**UP-5 (friends and invites)**
1. EV with `SteamFriendsFeed.InjectFake` (3 friends, 1 joinable, 1 invite): the panel lists them; [JOIN] calls
   `SteamJoinRouter.RequestJoin` with the right lobby id.
2. `coop_lint` reports `steamapi` = 0 new hits outside `Net/Steam/**`.
3. Two-device checklist items (a)–(c) of MP §8.7.7 M1-10, done through the new UI.

**Everyone**
- A clean compile.
- `Tools/coop_lint.sh --files` on the package globs: 0 new hits except the listed allows.
- No change under Nick's real data roots (`audit.py snapshot` before and after any PLAY).

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| `Feel/**` ownership is unclear (the Yeet slice, MP §8.7.1 #17) | The lead confirms the hand-off before UP-1. If Yeet resumes, UP-1's edits are small and listed by line, so it can rebase |
| Video settings leak into ProjectSettings or the URP asset in the Editor | Builds-only for vsync, cap and mode; a runtime URP clone restored on exiting play mode; check UP0-4 hashes |
| Esc is handled twice (laptop or sort station + pause) | The one-frame look-back router (§5.1); check UP3-1 |
| The solo freeze sticks (`timeScale` stays 0) | Restore in a `finally`, plus a watchdog on scene change, disconnect, quit and exceptions; HitStop skipped while frozen; check UP3-2 |
| The diorama copies drift from the real prefabs | Idempotent re-run after Character or Coop setups; checks UP4-4 |
| `ChuckPuppet` preview fields don't drive the puppet at runtime | Ask the Character owner for `SetMenuPose` (no Character edits from this pass) |
| The map goes stale after a town rebuild | `sourceHash` warning; an optional builder hook; check UP2-1 |
| Two EventSystems (menu scene + DDOL) | `EnsureEventSystem` creates one only when none exists in the loaded scenes |
| uGUI rebatching from the rotating minimap | A nested Canvas isolates it; budget check UP2-6 |
| TMP Ellipsis fails with our font | Always use `FeelUi.Fit` (one line) or the new 2-line Fit; never `TextOverflowModes.Ellipsis` |
| Rebinding requested later | Follow-up: move every `KeyCode` poll to Input System actions (about 15 files across 7 owners); then the keybinds view becomes editable |
| Scope creep from co-op features (pings, the BRB sign, rules editing) | Hooks only in this pass (`PingRequested`, the roster flag plan); built in M2, M3 and M6 |

---

## 13. Open questions for Nick (the build uses the default unless you say otherwise)

| # | Question | Default |
|---|---|---|
| Q1 | Add a free display font (SIL OFL, e.g. **Fredoka** or **Lilita One**) for the title and buttons? It needs the font file added to the project | Stay on LiberationSans Bold + outline until you say yes |
| Q2 | Should Esc in **solo** freeze the world? | **Yes**: solo freezes, co-op never does |
| Q3 | Minimap position and orientation | **Bottom-left, heading-up** (north-up in Settings) |
| Q4 | Map keys | **M** toggles, **hold Tab** peeks |
| Q5 | Menu music | None at launch; the Music slider stays hidden until there's a track |
| Q6 | Crew Card button before M4 | Shown, disabled, with "SOON" tape |
| Q7 | Streamer mode (join code hidden until you click Show) on by default? | **On** |
| Q8 | "Open shop to friends" from solo: a quick re-host that starts you in the garage now, or wait for MP §2.3's no-reload switch? | Quick re-host now, with clear copy |
| Q9 | Hand `Feel/**` to the UI pass (builder B)? | Yes, once the lead confirms the Yeet slice isn't editing it |
| Q10 | Should B14 be built by the UI-pass menu builder (C) on top of UP-4? | **Yes** |
