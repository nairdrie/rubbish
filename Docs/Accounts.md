# Rubbish Accounts: Steam identity, save slots and persistence

> **Status: approved direction (30 Sep 2026). Nothing in this doc is built yet.**
> This is the source of truth for identity, accounts, save slots, saved data, backups and save-side anti-cheat.
> `Multiplayer.md` (MP) defers to it for §2.1 (terms), §6.1 (save files), §6.2 (a brought company) and §6.4 (trust).
> The build packages that implement the local half (M0, M1) are in **MP §8.7**. The cloud half (M1.5) gets its own package
> list at the M1 gate, written against §5 and §7 here.
>
> **How it was made:** Steam and Unity Gaming Services (UGS) research, an accounts design, a build contract, then a red-team
> review (29 attacks). **§16 lists every attack and its verdict.** Where the first drafts disagreed, **§0.1 is the decision.**
> Prices are vendor list prices on 30 Sep 2026. Anything marked *unverified* wasn't re-checked.
>
> **User ask (verbatim):** "yes lets have your shop travel with you. each players account (can we connect it to their steam
> profile and store their save data in our own database?) so like they join a world with friends (host a world or join a world)
> and pick a save they can load into any world. yeah bonk shove and carry a flopped friend are all great lets make it chaotic.
> steam only launch. 8 per town is perfect. whatever you think would be more fun on tug of war. go for it."

---

## 0. The answer in twelve bullets

1. **Yes to both.** Your account *is* your Steam account. Your saves live in a database we control (UGS Cloud Save, written only
   by our own server code in Cloud Code), with a copy on your PC so the game works offline. No servers for Nick to run.
2. **An account holds three things:** **1 Crew Card** (you: Pocket, cosmetics, titles, lifetime stats), **3 company slots** (your
   shops; these are the "saves" you pick) and **up to 5 Towns** (the worlds you host). Every Town uses the same map, so any
   company really can go into any Town.
3. **Hosting:** the main button is **Continue** (your last Town with your last company). **Change…** lets you pick any Town and
   any company. **Invite friends** opens the same Town to friends.
4. **Joining, honestly staged:**
   - **M1–M4:** you join **as crew** in the Boss's shop. Your company stays safe at home.
   - **M5 ("Moonlighting"):** pick **Bring Rubbish Co.** and your share of the wages and half the Rep for bags you lifted go into
     *your* company, capped so it can't outpace playing your own shop. **Just me** sends wages to your Pocket.
   - **M7 (Depot Row):** your actual shop appears on your friend's street, with its truck, cash and Magnus.
5. **One writer per record.** The host writes its Town. A company and a Crew Card are written only by their **owner's** game,
   through our Cloud Code module. **A host never writes anyone else's save.**
6. **The cloud checks every save.** Cloud Code **rejects** structural cheats (unpaid upgrades, downgrades, stale sessions) and
   **clamps** implausible earnings to a rate cap measured on server time. No leaderboards, so clamping beats false accusations.
7. **One company can't be in two places.** Taking a company into a session takes a **lease with an epoch** (a counter).
   "Take it back" on another PC bumps the epoch, and the old session's saves are refused.
8. **Local first, cloud second.** Saves hit your disk (atomic write, tiered backups) and reach the cloud every 5 minutes while
   something changed, plus on leave, quit, day end and big purchases. **Joining a game never waits on the cloud.**
9. **Backups are time-tiered**, not "the last three writes": latest, 10 minutes ago, start of this session, 7 daily copies, and a
   copy from before every migration and every visit. Deleted companies wait 30 days.
10. **The live data-loss bugs get fixed first (hotfix L0)**, before any co-op feature: today a corrupt save is overwritten with a
    blank one on quit, and a guest can still write a friend's world into their own save.
11. **Cost:** about **$0 / $11 / $190 a month** at 1k / 10k / 100k monthly players (§2.3).
12. **What you (Nick) do:** nothing for M0 and M1 beyond a few approvals. The cloud (M1.5) needs your Steamworks account, App ID,
    a publisher key and some Unity dashboard switches, which only you can do (§14).

### 0.1 Decision table (where the drafts disagreed, this wins)

| Topic | Accounts draft | Build-contract draft | **Decision** |
|---|---|---|---|
| Save format | Newtonsoft envelope v3 | `JsonUtility` `SaveData` v2 | **Newtonsoft DTOs** in a pure `Rubbish.SaveModel` assembly with opt-in serialization (§5.2). The network snapshot uses the same DTO JSON. v1/v2 files are read by migrations. |
| Money | integer cents in saves | float cash, `CashCents` network variable | **`GameSession` stores cash as `long` cents from C2**; the float `Cash` property stays (frozen API). Ledger posts the applied cents. Tests compare cents. |
| Company timers (gnomes, Garbage Juice, allowances) | remaining in-game hours | absolute `GameClock.TotalHours` stamps | **Remaining in-game hours at the Town boundary**, converted in C2's capture/restore. Needed in M1, because M1 already loads any company into any Town. |
| Towns per account | 5 | 8 | **5** |
| Editor identity | `dev:editor` | Steam whenever Steam is running | **`dev:editor`**; Steam only by an explicit opt-in define. Identity never depends on whether Steam happens to be running (§1.2). |
| "Bring my company" before Depot Row | Moonlighting in M1, Bring by default | a disabled tile | **Deferred to M5**, when wages exist. M1–M4 join as crew; the picker is hidden until M5. |
| The company in `ConnectionData` | no; named message after approval | no | **No.** `JoinRequest` ≤1,200 B; a company blob is streamed after approval (M7). |
| Local backups | rotate 3 on every write | rotate 3 on every write | **Time-tiered generations** (§5.1) |
| Host → owner data | deltas + the Guard | one-way mirror snapshot to guests | **Both, for different jobs.** M1: the host *mirrors* its own company to guests for display. M5+: the host sends *deltas* to the owner of a brought company, who applies them through the Guard. |
| Signed session summaries, checkout seals | hash chain now, seal at M8 | none | **No hash chain.** A seal or a `VerifyCheckout` call only at M8, described as "cloud-consistent", not "verified". |
| Guests' IDs in Town saves | `HMAC(townSalt, SteamID)` | none | **Plain SteamIDs in the host's local Town file**; the cloud copy of a Town **drops `players{}`** and keeps only the ban list (§11). |
| Same-PC lock after a crash | 15-minute expiry | 15-minute expiry | **Liveness check** (owning process ID + its start time). The 15-minute expiry applies only to the cloud lease. |
| Lint rule for file writes | `persist` | `save` | **`save`**, allowed only under `Assets/Scripts/(Net/(Core/)?)?Save/**` (+ the two legacy files during M0) |
| Crew Town size | not covered | 4 until Depot Row | **4 until M7** (one company, four truck spots, four Dumplings). Raising it to 8 is one constant if Nick wants it (§15). |
| Cloud cadence | every 5 min | every 5 min | **Every 5 min while dirty**, plus events, never more than 1 per 30 s |
| Cloud history | a 5-entry ring rewritten on every commit | none | **1 read + 1 write per normal commit.** History is written only at session start and end and on the first commit of each UTC day (§5.4). |

---

## 1. Identity

### 1.1 Keys

| Key | Form | Used for | Comes from |
|---|---|---|---|
| **PlayerKey** | `steam:7656119…` or `dev:<name>` | connection approval, player slots (replacing `PlayerSpawner.cs:183`'s `clientId % 4`), bans, blocks, company members, Town player rows | Steam. The host verifies it with `BeginAuthSession` on a ticket bound to the host's identity (§7.6). |
| **AccountId** | UGS player ID | cloud keys only. The backend never needs SteamIDs. | `AuthenticationService.SignInWithSteamAsync` (M1.5) |
| UGS profile name | `s<SteamID64>` | `AuthenticationService.SwitchProfile` before sign-in, so two Steam accounts on one PC keep separate cached sessions | derived |
| Display name | Steam persona, fetched live | name tags, receipts | Steam (`GetFriendPersonaName`/`RequestUserInformation`). **Never saved, never taken from the join request.** |

`IPlayerIdentity { PlayerKey Key; string DisplayName; bool IsSteam; byte[] GetJoinTicket(ulong hostSteamId); void CancelJoinTicket(); }`
has two implementations, `SteamIdentity` and `DevIdentity` (MP §8.7, package B3).

### 1.2 Which identity a build uses (fixed per build, never by whether Steam is running)

| Build | Identity | If Steam is missing |
|---|---|---|
| **Release** (shipped, Steam-only) | always `steam:<id64>`. `SteamAPI.RestartAppIfNecessary(appId)` then `SteamAPI.Init()`. Steam offline mode still returns the SteamID. | "Rubbish needs Steam running" screen. **No fallback.** |
| **Development build** | `dev:<name>` by default. **`-steam`** on the command line opts into the Steam identity and Steam features (AppID 480). | With `-steam`: an error screen, **never a silent fallback to `dev:`**. |
| **Unity Editor** | **`dev:editor`**, always. The scripting define `RUBBISH_EDITOR_STEAM` opts into Steam. | Same as a development build. |
| Batchmode bots, Multiplayer Play Mode (MPPM) clones, `-nosteam` | `dev:bot-<n>` / `dev:mppm-<tag>`; Steam never initialises | n/a |

`dev:<name>` comes from, in order: `-playerid <name>`, the MPPM player tag, `editor` in the Editor, else a per-install GUID file in
the data root. **Why fixed:** if identity followed "is Steam open right now", Nick's Editor would flip between
`accounts/steam_…` and `accounts/dev_…` from one day to the next and his progress would appear to vanish (red-team R5).

**Release builds reject `dev:` keys at connection approval.** Development builds accept them, so bots and MPPM work.

### 1.3 First launch (release build)

1. Steam starts the game (or `RestartAppIfNecessary` relaunches it through Steam).
2. Create or open `<data root>/accounts/steam_<id64>/` and run the **legacy import check** (§5.6).
3. **The menu appears at once from the local copy.** In the background (from M1.5): `UnityServices.InitializeAsync`
   (environment `production`), `SwitchProfile`, `GetAuthTicketForWebApi("unityauthenticationservice")`, `SignInWithSteamAsync`.
4. **First sign-in with no cloud data:** upload the local slots. An imported company goes through `ImportLegacy` (allowlisted
   playtesters only, §7.4). Anything else goes through `CreateCompany`, where **the server builds the starter state itself** from
   the same `StarterCompany()` function the client uses, so nobody can create a rich company.
5. **A brand-new player makes one decision:** the company name (default "Rubbish Co.") and a colour, then drops into solo play in
   Town 1 "Maple Hollow". The company and Town pickers appear from the second launch on, behind **Change…**.

### 1.4 Degraded modes

| Situation | Solo | Host friends | Join friends | Bring a company (M5+) | Cloud |
|---|---|---|---|---|---|
| Everything online | yes | yes | yes | yes | syncs |
| Steam offline mode | yes (local) | no | no | no | changes queued |
| Steam online, UGS down or cold-starting | yes | yes (host's company shows "not synced") | yes, as crew / **Just me** | **no** (needs a checkout) | changes queued |
| No App ID yet (today, development) | yes | yes (Unity Relay, dev only) | yes | yes, trusted locally | none |
| Picked company is "on the road" elsewhere | blocked for that slot; **Take it back** offered | same | same | same | n/a |

### 1.5 Edge cases

- **Several Steam accounts on one PC:** each has its own account folder and UGS profile. The automatic legacy import runs only for
  the first account; others get an "Import old save from this PC" button on an empty slot.
- **Family Sharing:** the borrower has their own SteamID and so their own account. Bans use the owner SteamID from
  `ValidateAuthTicketResponse_t` (*unverified field*).
- **The Editor (Nick):** `dev:editor` with its own folder. The first M1 run imports Nick's current save into it, so his progress
  continues.
- **Agents and bots never touch Nick's data.** Bots run with `-dataroot <run>/<bot>`; agent play tests in the live Editor use a
  seeded scratch data root (MP §8.7.6). Batchmode never signs in to UGS.

---

## 2. Backend choice

### 2.1 Decision: UGS Authentication (Steam) + Cloud Save (Protected data) + one C# Cloud Code module

| | **UGS (chosen)** | Supabase / Neon Postgres + our own auth | PlayFab | Steam Cloud only |
|---|---|---|---|---|
| Effort | **7–10 days.** Steam sign-in is built in; Cloud Code is C#, so the pure save model compiles into it; Authentication 3.7.4 and `services.deployment` 1.7.2 are already installed | 12–16 days, **plus ongoing ops**: our own function that verifies Steam tickets and issues tokens, schema, access rules, backups | 9–12 days; a second SDK and identity system | 1–2 days, but no server can read or check it |
| $/month at 1k / 10k / 100k MAU | **$0 / ~$11 / ~$190** (§2.3) | $25 / $25 / ~$80–115 | ~$1 / ~$15 / ~$155–215; free tier stops at 1,000 *lifetime* players (cut Mar 2026) | $0 |
| Server-side validation | **Protected player data: the player can read it, only server code can write it.** Write locks catch conflicting saves. | strongest (SQL constraints) | read-only data + CloudScript | none |
| Lock-in | medium-high (export via CLI/admin API) | low | high | high |
| Other | **Voice (Vivox) needs UGS sign-in anyway**, so one identity system | still needs UGS sign-in for Vivox | a third identity system | n/a |

- **Exit plan:** everything sits behind `IAccountBackend`/`ISaveStore` (§5.1). A Supabase backend is one new class plus an edge
  function, and must pass the same contract tests (MP §8.7, B4). A **monthly export** (UGS CLI or admin API) is insurance against
  Unity closing the service, as it did with Multiplay on 31 Mar 2026.
- **Steam Cloud:** Auto-Cloud for `settings.json` and keybinds **only**. Never the `accounts/` folder: the database is the truth.
- **Steamworks.NET** (2025.164.1, SDK 1.64) is the Steam library: the path Unity documents for Steam sign-in and the base of the
  Steam transport we'll fix up (MP §1.3).

### 2.2 Two Steam tickets for two jobs

| Job | Ticket | Verified by | Works on test AppID 480? |
|---|---|---|---|
| Sign in to our backend (M1.5) | `GetAuthTicketForWebApi("unityauthenticationservice")` | **UGS**, calling Steam's `ISteamUserAuth/AuthenticateUserTicket` with **our publisher key**, which lives only in the Unity dashboard | **No.** It needs our own App ID's publisher key. |
| Join a friend's Town (M1, W3) | `GetAuthSessionTicket` **bound to the host's `SteamNetworkingIdentity`** (so a ticket can't be replayed to another host), sent in the join request | **The host**: `BeginAuthSession` inside connection approval (`Pending`, 5 s timeout); `EndAuthSession` when the guest leaves | Yes |

### 2.3 Cost model (per hour played)

An engaged hour produces about **17 commits** (12 cadence commits while something changed, 3 in-game day ends, ~2 events), each
**1 Cloud Code call, 1 read and 1 write** (§5.4), plus ~5 profile or history writes, at ~0.1 s of compute each.
Paid unit prices beyond the free tier: call $1.50/M, write $0.025 per 10k, read $0.009 per 10k, compute $0.00002/s, storage
$0.50/GiB. Free tier: 1M calls, 20 compute-hours, 1M writes, 1M reads, 5 GiB.

**≈ $0.00013 per played hour** once past the free tier, plus storage (~300 KB per registered player: an active company, its
pre-visit copy, 5 session-end versions, 7 dailies, 2 Towns).

| Monthly active players (12 h each; registered ≈ 3× MAU) | Calls/writes/reads/compute | Storage | **Total** |
|---|---|---|---|
| 1,000 | inside the free tier | 0.9 GB, free | **$0** |
| 10,000 | ~$9 | 9 GB → ~$2 | **~$11** |
| 100,000 | ~$150 | 90 GB → ~$42 | **~$190** |

Cloud saves every 60 s instead would cost about 5× as much, which is why the cloud cadence is 5 minutes and the 60-second
snapshots stay on local disk. **Relay fallback cost** is separate and bigger: if friends hosting ever ran entirely on Unity Relay,
MP §7 estimates ~$2,000 a month at 1,000 concurrent players. So the Steam relay (SDR) being on by default is a **launch gate**, and
the Relay fallback sits behind a remote flag with usage alerts (MP §1.3).

---

## 3. Save slots and the menu

### 3.1 Account shape

- **3 company slots** (the model supports 10), **5 Towns**, **1 Crew Card** shared across the whole account.
- Being a Member or Helper of someone else's company is a role *inside that company* and takes no slot.

### 3.2 Main menu (M1; red-team R26: two clicks to play)

```
MAIN MENU                                                   Nick (Steam)   [synced]
  ► CONTINUE           Maple Hollow · Day 14 · RUBBISH CO. $8,210          [Change…]
  ► INVITE FRIENDS     same Town, opened to friends (Steam invite / Join Game / code)
  ► JOIN A FRIEND      invites · friends playing · code
  ► CREW CARD          ► SETTINGS (Privacy: Download my data / Delete my data)

[Change…]
+- YOUR COMPANIES ---------------------------------------------------------------------+
| [RUBBISH CO.]               [DOT'S DISPOSAL]                    [ + New company ]    |
|  Rank 7 Regional Rubbish     Rank 2 Bag Wrangler                                      |
|  Manager · $8,210            ON THE ROAD: "Maple Hollow"                              |
|  last played in Maple Hollow, since 8:14 pm  [Take it back]                           |
|  2 h ago   [synced]          [not synced]                                             |
+--------------------------------------------------------------------------------------+
  Selected: RUBBISH CO.   ⋯ Rename · Recolour · Duplicate · Restore… · Delete
  Town: [Maple Hollow · Day 14 ▾]   [+ New Town]      ► Play solo   ► Host with friends
```

### 3.3 Card actions

| Action | Rule |
|---|---|
| **New company** | Name ≤24 characters (others see it through Steam's `ISteamUtils::FilterText`), 8 preset colours, preset logos. Starter state from the shared `StarterCompany()`. |
| **Rename / recolour** | Any time it isn't on the road. Mid-session, founder only (M7). |
| **Duplicate** | Needs a free slot and a source that isn't on the road. Copies the *cloud* version under a new guid, named "Copy of …", with `lineage.copiedFrom`. Harmless without trading or leaderboards; a Community server may refuse copies (M8). |
| **Delete** | Type the name to confirm. 30 days in "Recently deleted", then purged. Not while on the road. |
| **Restore…** | Lists every backup tier (§9). Restoring is itself undoable: the current version is backed up first. |
| **Towns** | New (a name), Rename, Delete (with a 7-day local backup). Deleting a Town never touches a company. |

### 3.4 Joining, by milestone

| Milestone | What the join screen offers | Where your earnings go |
|---|---|---|
| **M1–M4** | nothing to pick: you join as crew | the Boss's company gets the money (it's their shop); stats onto your Crew Card from M4 |
| **M5** | **Bring RUBBISH CO.** (default, remembered per friend) or **Just me** | Bring: your wage share and half the Rep of bags you lifted go to *your* company, **capped at 1.5× your own tier's typical income per real minute**, and **no Clean Days**, so rank and districts can't be skipped. Just me: wages to Pocket. The host's company still gets 100% (wages are created on top, MP §4.7). |
| **M7** (Rival Towns) | **Bring RUBBISH CO. onto Depot Row** | your company is physically there: its plot, truck, cash, Magnus |

**What players see (red-team R25):**
- The HUD wallet carries the company's name and colour: **"NICK'S CO. $41,200"**, so nobody mistakes the Boss's money for theirs.
- On join: "You're on Nick's shift. Rubbish Co. is safe at home."
- On leave: a card, e.g. "Rubbish Co. unchanged · Crew Card +3 bags".
- Company cards show the Town they were last played in.

---

## 4. What lives where

### 4.1 The records

| Record | What it is | Writer | Stored as |
|---|---|---|---|
| **Company** (×3) | the shop that travels | its owner's game, through Cloud Code | `co.<guid>` (Protected) + local copy |
| **Town** (≤5) | a host's world | the host's game | `town.<guid>` (Default) + local copy |
| **Crew Card** | the person | its owner's game, through Cloud Code (Pocket, unlocks); directly for the block list | `profile` (Protected) + `blocks` (Default) + local copy |
| Settings | the machine | local | `settings.json` + Steam Auto-Cloud |

### 4.2 Every field saved today, and its new home

| Today (`file`: field) | New home | Notes |
|---|---|---|
| `rubbish_save.json`: `cash` (float) | Company `cashCents` (long) | `GameSession.Cash` stays a float property |
| `day` | Town `clock.day` **and** Company `daysOperated` | the calendar is world data; the company counts its own days |
| `truckCapacityKg` | derived from upgrades, not saved | read only by the v1 migration |
| `inventory` | Company `inventory` | kept finds, KEEP crate, trophy window |
| `upgradeIds/Levels`, `consumableIds/Counts` | Company `upgrades`, `consumables` | permits, Magnus, truck tiers and paints are all upgrades |
| `totalBags/Items/Treasures/KgRecycled` | Company `stats`, **and** Crew Card `lifetime` | the import copies them into both |
| `ebae_save.json`: listings (offers store `remaining` seconds), history (capped), totals | Company `ebae` | already portable |
| Progression v2: `rep`, `cleanDays`, `keyToCity`, `totalCashEarned`, district `trust`, `counters`, `depotDeliveries` | Company | `dex.*` counters move to the Crew Card (personal Trash-dex). Progression.md gets a note. |
| Progression v2: `stamps` (absolute `GameClock.TotalHours`) | Company `timers` as **remaining in-game hours** | rule 2 below |
| *not saved today:* truck cargo (`TruckBags`) | Company `truck.cargo` | closes Progression §9.8's gap |
| *not saved today:* tipped pile, table leftovers | Company `depotFloor` | it's inside the company's depot |
| *not saved today:* clock, curb records (+ host-private contents), cleanliness, route progress | Town `world` | "fresh server = 7 AM" now applies to **new Towns** only (LivingWorld §1 changes) |
| *not saved today:* rabies timer | Town `players[key].rabiesAtHours` | per player, per Town (MP §2.6) |
| truck parked pose | Town `companyPoses[companyGuid]` | where *this* company parked in *this* Town |
| Town-only | rules preset, ban list, grant recipients, `plots[4].lastSeen{companyGuid, name, colour, logo}`, `players{key → lastCompany, slot, lastSeen}` | |
| Crew Card-only | Pocket (cents), cosmetics owned + loadout (≤64 B), titles, Career XP (+ the one-level-per-hour counter), personal Trash-dex, trophies, MVB count, brand presets, slot index, Town index, the block list | |

### 4.3 Three rules at the Town boundary

1. **Money is integer cents** (`long`) everywhere it's stored or sent. Float drift can't break a ledger, and sums are exact.
2. **Company timers are remaining in-game hours**, never one Town's `TotalHours`. On load, `SetStamp(k, town.TotalHours + remaining)`;
   on save, `remaining = stamp − town.TotalHours`. Otherwise a gnome bought on day 30 of one Town would last ~670 extra hours in a
   Town on day 2, or expire instantly the other way round (red-team R16).
3. **Company day numbers are company-relative** (`daysOperated`). `ItemInstance.dayFound` and ebae `dayListed` stay as flavour.

---

## 5. Storage

### 5.1 Local layout and the write protocol

```
<data root>/                              default = Application.persistentDataPath
  settings.json                           (Steam Auto-Cloud)
  rubbish_save.json, ebae_save.json       legacy files: written by today's code until M1, then never written again
  rubbish_save.gen/, ebae_save.gen/       their tiered generations (from hotfix L0)
  legacy_import.json                      marker: sha256 of the legacy pair, which account imported it, when
  accounts/<steam_7656… | dev_editor | dev_bot-1>/
    profile.json            + profile.gen/
    companies/<guid>.json   + <guid>.gen/   <guid>.journal (append-only accepted deltas, M5+)   <guid>.lock
    towns/<guid>.json       + <guid>.gen/
    sync.json               per key: cloudVersion, baseVersion, dirty, lease{epoch, sessionId}, lastSyncAt (M1.5)
```

- **The data root** is `SaveRoot.Path`: `persistentDataPath`, or `-dataroot <dir>` on the command line, or (Editor only) a
  per-project override in `UserSettings/RubbishDev.json` that **expires** (≤2 h) and puts a **"TEST DATA ROOT"** watermark on
  screen. Not EditorPrefs, which are shared by every Unity project and editor on the machine (red-team R24).
- **Every write:** write `x.tmp`, flush, rename over `x.json`, then update the generations. Each file step is tried up to
  5 times (≤ 0.2 s) on a sharing violation or "access denied", because on Windows an antivirus scan or the search indexer
  often holds a file we just closed (`SafeFile.Io`).
- **Time-tiered generations** (red-team R3), each a full copy in `x.gen/`:

  | Tier | Updated | Kept |
  |---|---|---|
  | `latest.1`, `latest.2` | every write | 2 |
  | `10min` | when the existing copy is older than 10 minutes | 1 |
  | `session` | first write of each play session | 1 (plus the previous session's) |
  | `daily-<yyyymmdd>` | first write of each local day | 7 |
  | `premigration-v<n>` | before any migration rewrites the file | forever |

  A logic bug that zeroes cash therefore can't reach the session-start or daily copies within seconds.
- **Write rate:** at most one write per 2 s (dirty flag, flushed at the end of a frame), always flushed on quit, pause and scene
  change. Today's code writes on the first change of every frame while sorting.
- **Loading a file that won't parse** (red-team R1): copy it to `x.corrupt-<utc>.json`, fall back to the newest generation that
  parses, and toast "Recovered from a backup". If nothing parses, **block every save for the session** and toast "Your save
  couldn't be read. A copy is kept and nothing will be overwritten." Never write defaults over an unreadable file.
- **The two legacy files move together** (red-team R4): until M1 merges them into one company record, both are written with the
  same `gen` number on every save, and recovery picks the newest generation present in **both**. A sold item can't reappear, and a
  listed item can't vanish.
- **Same-PC lock** (`<guid>.lock`): holds the process ID and its start time. A lock whose process is gone is free immediately.

### 5.2 Format, versions and migrations

```json
{ "kind": "company", "schema": 3, "content": "0.4.2", "guid": "…", "version": 17,
  "savedAt": "2026-10-02T20:14:00Z", "writer": "steam:7656…", "data": { … }, "ext": { … } }
```

- **DTOs, not runtime types** (red-team R18). The save model is a set of plain classes in `Assets/Scripts/SaveModel/`
  (`Rubbish.SaveModel.asmdef`, `noEngineReferences: true`, references only Newtonsoft) marked
  `[JsonObject(MemberSerialization.OptIn)]`. Runtime types such as `ItemInstance` (whose `Def` property points at a
  ScriptableObject, `RubbishTypes.cs:51`) are mapped to and from DTOs in Assembly-CSharp. **The same source compiles into the
  Cloud Code module.**
- **Also in that pure assembly:** migrations, the legacy parsers (JSON → DTO), the Guard (§7.3) and the Cloud Code plausibility
  checks, so they're all unit-testable (red-team R22).
- **Migrations** are pure `JObject → JObject` steps: v1 (today's pre-progression file), v2 (Progression's `saveVersion = 2`),
  **v3** (the envelope; cents; timers as durations; the company/Town split; ebae folded into the company).
- **Fixtures** (`Assets/Tests/EditMode/Fixtures/`): copies (never the originals) of Nick's real 388 B and 2.2 KB files, plus a
  **generated "maximal" fixture** that fills every field (inventory, stamps, depot deliveries, offers mid-countdown). Every
  migration must reproduce an exact expected output.
- **Unknown fields round-trip** via `[JsonExtensionData]`; unknown item `defId`s are kept verbatim and shown as "Mystery Item".
- **A newer schema than the game knows** makes the card read-only ("Saved by a newer Rubbish. Update to play."). No downgrades.
- Cloud blobs are gzip + base64. Standalone builds use Mono today; if they move to IL2CPP, add a `link.xml` and an AOT check.

### 5.3 Interfaces

```csharp
interface ISaveStore      { ReadAsync, WriteAsync(expectedVersion), ListAsync, DeleteAsync, TryWriteNow /* sync, for quit */ }
interface IAccountBackend { State, AccountId, IsCloud, Store, SignInAsync,
                            CheckoutCompanyAsync, CommitCompanyAsync, CheckinCompanyAsync, TakeBackAsync, RestorePreviousAsync }
LocalFileStore + LocalAccountBackend (M0, package B4)   // lease = local lock file with liveness check
UgsAccountBackend                     (M1.5)           // write-through: local first, then Cloud Code; must pass B4's contract tests
TownStore / CompanyStore / ProfileStore (M1, B8)       // typed DTO stores; GameSession + EbaeMarket become facades over the
                                                       // ACTIVE company and Town
```

Exact signatures are in MP §8.7.3 (contract C1).

### 5.4 Cloud layout (UGS player data, M1.5)

| Key | Access | Contents | Written by |
|---|---|---|---|
| `co.<guid>` | Protected | `{meta: {version, epoch, sessionId, townName, since, expiresAt, contentVersion, dirtyAt}, blob}`. **The lease lives inside the company key**, so a normal commit is 1 read + 1 write (write-locked). | Cloud Code only |
| `co.<guid>.prev` | Protected | copy taken at checkout into someone else's Town (kept 7 days) | Cloud Code |
| `co.<guid>.end0..4` | Protected | the last 5 session-end versions | Cloud Code, at check-in |
| `co.<guid>.day0..6` | Protected | first commit of each UTC day | Cloud Code |
| `co.<guid>.conflict` | Protected | the losing side of a two-PC conflict (7 days) | Cloud Code |
| `trash.<guid>` | Protected | a soft-deleted company + `deletedAt` (30 days) | Cloud Code |
| `slots` | Protected | slot index: guid, name, colour, rank, cash, lastPlayed, lastTown, version, on-the-road summary | Cloud Code, on create/rename/delete/check-in |
| `profile` | Protected | Crew Card | Cloud Code, only when dirty |
| `towns`, `town.<guid>` | Default | the host's worlds (without `players{}`, §11) | the host's game directly |
| `blocks` | Default | block list | owner directly |

About 300 KB per active account, far under the 5 MiB per-player cap. No player ever reads another player's cloud keys.

### 5.5 Cadence

| Record | Local | Cloud (M1.5) |
|---|---|---|
| Town (host only) | every 60 s + day rollover, truck in/out of a bay, a company arriving or leaving, quit | every 5 min while dirty + quit |
| Company (owner) | ≤ every 2 s while dirty; journal on every accepted delta batch (M5+) | `CommitCompany` every 5 min **while dirty**, plus leave, quit, in-game day end, purchases or sales ≥ $500; **≥30 s apart** except leave/quit |
| Crew Card | ≤ every 2 s while dirty | inside the company commit; standalone every 5 min while dirty in "Just me" sessions |

### 5.6 Sync and conflicts (two PCs)

| Cloud vs local | Action |
|---|---|
| cloud = base, local clean | nothing |
| cloud = base, local dirty | commit (write lock on `baseVersion`) |
| cloud > base, local clean | pull |
| cloud > base, local dirty | **conflict card:** both versions side by side (day, cash, rank, last played, machine name): **Keep this PC / Keep cloud** |

- **Companies are never merged.** The loser is kept as `co.<guid>.conflict` (7 days) and in local generations.
- **Crew Card conflicts** use the same card, but unlock sets (cosmetics, titles, dex stamps) are unioned. Nobody loses a cosmetic.
- **A company that only exists locally on a linked machine** (red-team R27) offers **Upload to account**, **Keep local
  (read-only)** or **Export**.
- **Offline play:** solo and hosting work from the local copy with a "not synced" badge. An unsynced company **can't be brought**
  into someone else's Town until it syncs.

### 5.7 Importing today's saves

- **Sources:** `rubbish_save.json` (v1 or v2) and `ebae_save.json` in the data root, plus their L0 generations (picking the newest
  matching `gen` pair). If Player Settings' company name has changed by then, the importer also checks
  `~/Library/Application Support/DefaultCompany/Rubbish/` (today's path, because `companyName: DefaultCompany`).
- **Produces:** company slot 1 **"Rubbish Co."** (cash, upgrades, consumables, inventory, ebae, Rep, rank, Clean Days, trust,
  counters, timers, Kevin's deliveries, stats), Town 1 **"Maple Hollow"** (day from the save, 7:00 AM, fresh curbs), and a Crew
  Card (lifetime stats copied, Pocket 0).
- **The imported company's guid is derived from the sha256 of the legacy pair**, so the same old save imported on two PCs becomes
  one company and meets the conflict card instead of silently forking (red-team R27).
- **Runs once per account**, on first launch with no slots, and writes the `legacy_import.json` marker. **It never edits or
  deletes the old files**; they stay as the last-resort backup.

---

## 6. Session flow

### 6.1 The "these statics aren't mine" latch (hotfix L0; red-team R2)

`GameSession` and `EbaeMarket` are static and hold whatever the game is showing. On a guest that's the host's world, and today
nothing marks it as foreign once the connection drops. So:

- **`SaveGuard.MarkForeign(reason)`** is called when the player clicks Join (before any scene load) and, as a backstop, on NGO's
  `OnClientStarted`. From then on **every save path is refused**: both `Save()` methods, `SaveSoon`, the pause and quit saves, and
  both `ResetAll(true)` deletes.
- **`SaveGuard.ReloadOwn()`** is the *only* way to clear it: reset both statics while still latched, reload the player's own files
  (from M1: re-activate their own Town and company), then clear. It runs on leave, on join failure and from `OnClientStopped`.
- So: a host crash, a quit while connected, or "leave, then Play solo" can never write the friend's world into the guest's files,
  and solo play after co-op saves normally again.
- `EbaeMarketRunner` doesn't tick offers on a guest (offers are host-simulated); the guest's own market resumes after `ReloadOwn`.

### 6.2 Host (solo is the same over the loopback transport)

1. Pick a company **C** and a Town **T** (or Continue), and Solo or Friends.
2. `Checkout(C, sessionId, T.name)`: a local lock (M1) or a cloud lease (M1.5), **in parallel with** the fade and loading T.
3. `StartHost`. For friends, the Steam lobby gets `rb_game`, `rb_proto`, `rb_content`, `rb_session`, `rb_town`, `rb_rules`, `rb_net`.
4. Load T into the world state and C into the company state.
5. Play. Autosave per §5.5.
6. Quit: final world state to guests ("The Boss went home"), save T, commit C, check in C.

### 6.3 Join

1. Entry: an invite, "Join Game" on the friends list, or a code. Join the lobby; check `rb_proto` ("Update your game" on a mismatch).
2. **M5+:** the picker (Bring / Just me). For Bring, `Checkout(C, rb_session, rb_town)` **in parallel with** the fade; on failure,
   "Couldn't reach your company vault. Join as Just me?"
3. **`JoinRequest`** (≤1,200 B): protocol and content versions, PlayerKey, a host-bound Steam session ticket, cosmetic loadout
   (≤64 B), and the choice `{kind, companyGuid, version, blobHash}`. **Never the company itself.**
4. **Host approval** (`Pending`): `BeginAuthSession` → ban check → lobby membership → ≤8 per Town, ≤4 per company (Crew Town ≤4)
   → slot by PlayerKey → approved.
5. **M7:** the guest streams its company blob in 4 KB chunks; SHA-256 must equal `blobHash`; the host validates schema and size
   (bounded decompression, §7.5) and loads it onto a plot.
6. **In session (M5+):** host → owner `CompanyDelta`/`ProfileDelta` batches every 10 s (at once for purchases and sales ≥ $500);
   the owner acks by sequence number, applies through the Guard, journals, saves locally, commits to the cloud every 5 minutes.
7. **Leave:** final deltas → **Visit Receipt** (rejected or clamped lines marked) → commit → check in → `ReloadOwn`.

### 6.4 Leave, quit, disconnect

| Event | Owner's company and Crew Card | Host's Town | Lease |
|---|---|---|---|
| Guest leaves normally | final deltas → receipt → commit → check-in | plot shows "CLOSED, back tomorrow" (M7) | released |
| Guest crashes | journal holds everything to the last batch (≤10 s). Next launch: "Finishing your visit to Maple Hollow…" → commit + check-in | CLOSED; host keeps unacked deltas 10 min | expires after 15 min; **a late commit with the same epoch is still accepted** |
| Guest reconnects ≤10 min | same epoch; host resends from the last acked sequence | same plot, slot, colour | renewed |
| Host quits | final deltas, then shutdown | final save | guests check in |
| Host crashes | guests lose at most the unsent batch (≤10 s) | ≤60 s lost | guests commit and check in |
| Taken back on another PC | old session's commits get "stale epoch" → toast "Dot's Disposal was taken back on another PC" | plot goes CLOSED | new epoch |
| Game killed in solo | ≤2 s local loss (≤60 s for the Town) | ≤60 s | local lock freed at once (dead process) |

---

## 7. Who writes what, and what's checked

### 7.1 One writer per record

| Record | Writer | Checked by | Read by |
|---|---|---|---|
| Town | the host's game | nobody (the host's own world) | the host |
| Company | owner's game → Cloud Code `CommitCompany` | the owner's **Guard** per delta (M5+) + **Cloud Code** diff | the owner; a host it's brought to (via the owner) |
| Crew Card | owner's game → Cloud Code | Guard + Cloud Code | the owner; hosts see loadout and titles |
| In-session company state | the host | host rules (MP §4) | everyone in the Town |

### 7.2 Deltas, not snapshots, to the owner (M5+)

```
CompanyDelta { companyGuid, sessionId, seq, entries[] }
 entry { kind, cents | level | count, reasonId (LedgerIds/RepIds), actorKey, refId (bagId / item uid / shopId / listingId),
         requestId?, townHours }
 kinds: Credit · Spend(shopId, newLevel | +count) · Fine · ItemAdd · ItemRemove(uid, cause, pairedSeq) · ListingOpen/Close
        · Rep · Trust(district, delta) · Counter(key, delta) · Timer(key, remainingHours) · CargoSet · FloorSet · NeighborRating
```

- **Idempotent:** an applied set keyed by `(sessionId, seq)`, unique item uids, a listing's sale pays once.
- **Depends on MP §9 rule 1:** every company change goes through `Ledger`. A direct mutation that skips it silently diverges; the
  host's 60 s company hash (compared, never used to overwrite) catches it in development builds.
- No hash chain over batches (red-team R28): the Visit Receipt lists the ledger lines; trust comes from the Guard and Cloud Code.

### 7.3 The Guard (on the owner's machine; pure code in `Rubbish.SaveModel`)

M5 enables Crew Card and Moonlighting kinds (wage credits, Rep, stats, stamps, XP). M7 enables the rest.

1. **Order:** sequence numbers only go up; gaps trigger a resend; duplicates are ignored.
2. **Credits:** a cap per `reasonId` (pickup ≤ the district's max fee + tip + curb bonus; recycling ≤ mass × top rate; an ebae sale
   ≤ 1.5 × the appraisal of a listing *this company holds*; Work Orders and Clean Streets ≤ their formulas), plus a session cap.
   - **Moonlighting (M5):** ≤ **1.5 × the guest company's own tier's typical net per real minute** × minutes worked: about $70
     (starter), $225 (Meadows), $940 (Snootington), $2,250 (Bluffs) a minute, from Progression §8.1's typical end-of-stage $/day
     ÷ 20 minutes. **No Clean Days.** So helping a rich friend pays about what playing your own shop pays, never far more
     (red-team R13).
   - **Brought companies (M7):** `burst[tier] + rate[tier] × real minutes`, rate = 6× typical: $300 / $900 / $3,750 / $9,000 a
     minute, burst = 2× the tier's most valuable item. *Provisional*: re-derived from the Progression sim before M7.
   - Over-cap credits aren't applied; the receipt shows "Host tried to add $X more than possible: ignored".
3. **A purchase must deliver:** `Spend` applies only as an atomic pair (cash − price, level +1 or count +n). The price must equal the
   catalog price for the content version (±25% if the host's content version is newer). And one of: (a) the `requestId` was
   issued by the owner's own game, **single-use, expiring, and HMAC-signed with the owner's per-session secret** so a host can't
   mint or replay approvals; (b) the actor is a Member within the daily allowance (MP §3.6) and non-owner spending is ≤25% of the
   session's starting cash; (c) the owner co-signed it.
4. **Fines and debits** (rip, hazard, hospital, tow, litter): a per-event cap, plus **10% of the company's cash per host PlayerKey
   per real day** (not per session, so kick-and-rejoin doesn't reset it).
5. **An item removal needs a pair:** a sale, a pawn (credit ≥30% of appraisal), compacting, an owner-requested use, or Sticky
   Fingers (always on the receipt). Anything else is rejected.
6. **Never accepted from a host that isn't the owner:** set or overwrite anything, a level or rank down, clearing inventory,
   resetting counters, rename or recolour, member or role changes, founder transfer, deleting the company.

**Worst case, a hostile host** can make allowance-sized purchases that still deliver real upgrades, fine you up to 10% a day, and
give you capped credits. Every line is on the Visit Receipt next to **"Restore from before this visit"**.

### 7.4 Cloud Code `CommitCompany` (diff against the stored version)

**Rejected (structural):** caller doesn't own the company; stale epoch; `baseVersion` ≠ stored (write-lock conflict → the client
runs §5.6); schema doesn't parse or the blob is over 1 MiB inflated; guid changed; an upgrade level went down; an upgrade level went up
without payment (price table for that content version); running totals went down; rank inconsistent with Rep and Clean Days;
duplicate item uids; item values outside their `ItemDef` range; members or founder changed outside the dedicated endpoints.

**Clamped (plausibility):** earned cash above `cap(tier, server-measured window)` is trimmed and logged in `audit.clampedCents`.
The window is the server time since the checkout or last commit; for offline commits it's `min(wall clock, 8 h per calendar day)`.
Rep is capped the same way; new Clean Days ≤ in-game days in the window; new items ≤ bags handled × 8. **Clamp rather than reject**
because there are no leaderboards, so a false positive (a lucky sale) costs more than a cheater does.

**Content versions (red-team R19):** a build step exports the price, rank and `ItemDef` tables keyed by content version, and they
deploy with the module. **A commit from a content version the module doesn't know is clamp-only and never rejected**, so a client
patch that ships before the module is redeployed can't break honest saves.

**Abuse limits (red-team R14):** commits ≥30 s apart except leave/quit, ≤30 per hour per player; `ImportLegacy` only for an
allowlist of playtester SteamIDs in the module's config, switched off at launch; production accepts **Steam sign-ins only**
(if UGS can't scope identity providers per environment, *unverified*, Cloud Code refuses accounts with no Steam link instead).

**Endpoints (module `RubbishVault`):** `CreateCompany`, `ImportLegacy`, `DuplicateCompany`, `DeleteCompany`, `RestoreDeleted`,
`PurgeTrash` (swept at sign-in), `CheckoutCompany` (new epoch, 15-minute lease, `.prev` when the Town isn't the owner's own),
`CommitCompany`, `CheckinCompany`, `TakeBack` (new epoch), `RestoreVersion` (new epoch; current version saved first),
`CommitProfile`, `ExportMyData`, `DeleteMyData`, plus a trigger on Authentication "account deleted" that wipes every key.
Later: `TransferCompany` (M7, "Hand over the keys"), `VerifyCheckout` or a checkout seal (M8).

### 7.5 Messages from other players are hostile input (red-team R11)

- Every compressed payload is read through **`BoundedGzip`**, which aborts above 1 MiB inflated (the same cap as a saved record; a real company is ~100–300 KB raw). JSON gets depth and array-size
  caps and a schema check **before** anything is applied.
- The company mirror message (`rb.company`) is accepted only from the server's client ID.
- On the host, **every** named message and RPC goes through one `NetGuard` helper with per-client rate and size limits.

### 7.6 Identity at the door (red-team R10)

- The host verifies the Steam session ticket with `BeginAuthSession` inside approval (`Pending`, 5 s timeout). The ticket is bound
  to the host's `SteamNetworkingIdentity`, so it can't be replayed to someone else.
- The SteamID must be a member of the host's lobby. Display names come from Steam, never from the join request.
- **Release builds reject `dev:` keys and any `steam:` key without a valid ticket.** This ships in M1 (wave 3), not with the
  later Steam transport, because the ticket rides in the join request over any transport.

### 7.7 Trust table (replaces MP §6.4)

| Who | Can | Can't |
|---|---|---|
| A host | anything in their own Town; change a brought company's *in-session* state | write any guest's company or Crew Card; wipe or downgrade them; credit them beyond the caps; stop a restore-from-before-the-visit |
| A guest | send requests | anything the host doesn't validate; claim another player's identity (release builds) |
| A player editing their local copy | change what they see offline | get it into the cloud past Cloud Code (clamped or rejected); bring an unsynced company into someone else's Town |
| A modded client | local visuals; its own movement (logged or snapped back, MP §4) | see bag contents early; touch any economy value; forge another player's commits |
| A Community server owner (M8) | accept travelling companies that are **cloud-consistent** (the copy really is the cloud copy) | prove the progress was *earned*: a patient cheater can still commit at the cap, so Community servers default to resident or fresh companies (red-team R28) |

---

## 8. Anti-duplication

| Way to duplicate | How it's closed |
|---|---|
| One company in two Towns (two PCs, or two copies of the game) | cloud lease + epoch; a same-PC lock with a liveness check; the "on the road" badge; Take it back starts a new epoch |
| Crash timing (sell, crash, load an older copy) | deltas are journalled **before** they're acked; loading replays the journal; commits carry `baseVersion` |
| Two legacy files recovering to different points | one shared `gen`; recovery picks a matching pair (§5.1) |
| A delta batch replayed after a reconnect | `(sessionId, seq)` applied set; unique item uids; each listing pays once |
| Offline play on two PCs | conflict card; one version wins, **no merge** |
| Restore after spending | restoring reverts cash and items *together* and starts a new epoch |
| Moving value between companies | not possible: items thrown into another company's bay go back to the thrower (MP §4.6); Pocket never becomes company cash |
| Duplicate a slot | deliberate, costs a slot, harmless without trading; `lineage` lets a Community server refuse copies |
| Hand over the keys (M7) | `TransferCompany`: both online and confirming; atomic; new epoch |
| Bring a locally edited company | bringing needs a fresh cloud checkout; unsynced companies can't be brought |

---

## 9. Backups and rollback

| Layer | Keeps | Restored by |
|---|---|---|
| Local generations | latest ×2, 10-minute, session start (+ previous), 7 dailies, pre-migration copies forever | automatic when a file won't parse; **Restore…** on the company card (M1) |
| Cloud session ends | last 5 (`.end0..4`) | Restore… |
| Cloud dailies | 7 (`.day0..6`) | Restore… |
| Before this visit | `.prev`, at every checkout into someone else's Town, 7 days | "Restore Dot's Disposal from before visiting Maple Hollow" |
| Conflict loser | `.conflict`, 7 days | Restore… |
| Deleted company | `trash.<guid>`, 30 days | Recently deleted |
| Legacy files | forever, untouched | re-import |
| Town | local generations + cloud latest + 1 cloud daily | Town ⋯ Restore |
| Whole project | monthly UGS export | Nick, by hand |

Every restore starts a new epoch and is itself undoable. M1 gate: after a scripted bug zeroes cash mid-session, restoring the
session-start copy brings it back.

---

## 10. Offline

- **Solo and hosting work with no internet.** Solo uses the loopback transport, no sign-in (MP §1.3).
- Changes queue in `sync.json`; the company card shows "not synced". The next commit sends `baseVersion` (§5.6).
- **Joining never waits on the cloud.** If UGS is down you can still join (as crew, or "Just me" from M5). Only *bringing* a
  company needs a checkout. The same rule as voice (MP §7).
- Offline earnings are clamped on the next commit to `min(wall clock, 8 h of play per calendar day)` at the tier rate.

---

## 11. Privacy and deletion

- **Settings → Privacy (M1.5):**
  - **Download my data:** `ExportMyData` returns every key; the game writes `rubbish_my_data_<date>.zip` and opens the folder.
  - **Delete my data:** type DELETE → `DeleteMyData` removes every key, Protected included → `AuthenticationService.DeleteAccountAsync()`
    → the local account folder is deleted → back to first launch.
- **Backstop:** a Cloud Code trigger on Authentication "account deleted" wipes the saved data, because deleting the sign-in
  account alone doesn't remove Cloud Save data.
- **Keep as little as possible:** no persona names, avatars or friends lists are stored. "On the road" shows the Town name, not
  the host's name.
- **Guests' IDs in Towns** (red-team R29): the host's **local** Town file keeps plain SteamIDs (the host already sees them in-game).
  The **cloud** copy of a Town drops `players{}` (rejoin memory is cosmetic) and keeps only the ban list (SteamID64 + the company
  name last seen), which the privacy policy discloses. Salted hashes with the salt in the same file would protect nothing.
- **Retention:** 30 days for soft deletes, 7 days for visit and conflict copies, 5 session-end versions, 7 dailies. No automatic
  purge of inactive accounts at launch (Nick may add one at 24 months).
- **Duties:** a privacy policy naming Unity (UGS, Vivox) and Valve as processors, and Unity's data processing agreement accepted at
  organisation level (§14).

---

## 12. Security and operations for a solo developer

| Concern | Rule |
|---|---|
| Publisher Web API key (red-team R15) | created in a **dedicated Steamworks permission group** that contains only Rubbish, with no publishing, financial or user-management rights. Stored only in the Unity dashboard. Never in chat, the repo or a build. Rotation: create a new key, paste it into the dashboard, then revoke the old one. |
| Accounts | 2FA on both the Steamworks account (Steam Guard) and the Unity account |
| Logs | never log tickets, tokens or keys. Players paste `Player.log` on Discord. Enforced by the `secrets` lint rule (MP §8.7.8). |
| Environments | `development` (anonymous sign-in allowed; agents may deploy) and `production` (Steam only; Nick deploys) |
| Deploys (red-team R19) | agents deploy the module to **development** through the UGS CLI after Nick runs `ugs login` once with a development-only service account. **Release checklist:** export content tables → deploy module to development → smoke test → deploy to production → flip the Steam branch. |
| Cost | usage or budget alerts to Nick's email (if the dashboard offers them, *unverified*); cost model in §2.3 |
| Relay fallback | off in release builds unless a remote flag turns it on; usage alerts (MP §1.3) |
| Vendor risk | `IAccountBackend` boundary, local-first saves, monthly export, Supabase as the documented exit |
| Agent safety | agents never write to Nick's data root (§1.5); a local checkpoint commit before every wave merge and `Setup_*` run (MP §8.7.4, W0 CK) |

---

## 13. Phasing

| Phase | What lands | Blocked on |
|---|---|---|
| **L0 hotfix** (lead, ~1 day) | load-failure guard + quarantine; the `SaveGuard` latch; `SaveRoot` for both legacy files; atomic writes with time-tiered generations and a shared `gen`; writes ≤1 per 2 s; ebae offers tick on host/offline only; guarded `ResetAll(true)` | Progression A releasing `GameSession.cs` and `EbaeMarket.cs` |
| **M0** | identity (`IPlayerIdentity`, `dev:`/`steam:` rules), approval and slots by PlayerKey, `ISaveStore`/`IAccountBackend` + local backend and leases, bots that never touch real data | W0 rails (MP §8.7) |
| **M1** | cents; timers as durations; the DTO save model, migrations and import; Town/Company/Profile stores; Continue / Change… / Invite friends menu; Restore… from local generations; Steam session-ticket check at the door | C2 (Progression A done with `GameSession`) |
| **M1.5** "Accounts and cloud" (M, 7–10 days) | UGS Steam sign-in; `RubbishVault` module + content tables; `UgsAccountBackend`; lease and epoch in the cloud; conflict card; sync badges; privacy screens; the account-deleted trigger; dev and production environments. Can overlap M2 (different files). | **Nick's steps 9–15 (§14).** UGS Steam sign-in can't be tested on AppID 480. |
| M4 | Crew Card stats via `ProfileDelta`; Steam lobby polish, rich presence text (needs our App ID) | — |
| **M5** | Moonlighting (Bring / Just me), the Guard (Crew Card + Moonlighting kinds), journal | the DTO company model from M1 |
| **M7** | bring a company onto Depot Row: blob stream, full Guard, Visit Receipt, restore-before-visit, CLOSED signs, `TransferCompany` | — |
| M8 | `VerifyCheckout` or a seal for Community servers ("cloud-consistent") | — |

**M1.5 gate:** warm sign-in < 3 s and joining unaffected by a Cloud Code cold start; the same company on two PCs shows "on the
road", Take it back works and the old session's commit is rejected; killing the host loses ≤60 s locally and ≤5 min in the cloud;
offline edits on two PCs produce the conflict card and the loser can be restored; forged commits (a downgrade, an unpaid upgrade,
+$1M) are rejected or clamped; a commit from an unknown content version is clamped, not rejected; Delete my data leaves 0 keys.

---

## 14. User steps (only Nick can do these), in order

**For M0 and M1 (approvals only; no accounts):**

1. **Approve an agent checkpoint.** Either let the lead run `git init` in the project with a `.gitignore` (Library, Temp, Logs,
   Builds, obj, UserSettings, `*.csproj`, `.plastic`, `Build.zip`) and add one line, `.git`, to Plastic's `ignore.conf`, or
   install the Plastic `cm` command-line tool. Local only, never pushed. It's the only way to undo a bad agent edit.
2. **Approve a ~10-minute package window** while you're not in Play mode: Steamworks.NET 2025.164.1 from its git URL, Netcode for
   GameObjects 2.13.3 and Multiplayer Services 2.3.3 (each skipped if not on the registry), and `steam_appid.txt` containing 480.
3. **Approve the build copy** at `~/Dev/Rubbish-build` (~3 GB), and say whether background builds may run while you play.
4. **Multiplayer Play Mode, once:** Window ▸ Multiplayer Play Mode ▸ enable Player 2. No tag is needed (B5's `CoopAutoStart`
   tells the main Editor from a clone by itself); `Tools/dev/test_data_root.sh on --autohost` arms the autostart, and Player 2
   always plays on its own throwaway save.
5. **Steam for testing:** install Steam on this Mac and log in. For the two-player Steam tests, a **second free Steam account** on
   any second device (a Steam Deck, an old laptop or a VM), friended with yours. Spacewar (480) is free on any account.
6. **Optional, today:** copy `~/Library/Application Support/DefaultCompany/Rubbish/` somewhere safe. Until L0 lands, a save that
   fails to load is overwritten with a blank one when you quit.
7. **Pick the studio name** for Edit ▸ Project Settings ▸ Player ▸ Company Name (now "DefaultCompany") before M1 ships. It moves
   the save folder; the importer checks the old one too.
8. **Decisions** in §15 (each has a default the build uses if you don't answer).

**For M1.5 cloud saves (doesn't block M0/M1):**

9. **Steamworks:** sign up at partner.steamgames.com, accept the Steamworks Distribution Agreement, enter identity, tax and bank
   details, and pay the Steam Direct fee (~$100 per app, *unverified*) to get Rubbish's **App ID**. Tell the lead the App ID (it
   isn't secret).
10. **Turn on 2FA** for the Steamworks account (Steam Guard) and the Unity account (id.unity.com).
11. **Publisher key in a fenced group:** Steamworks ▸ Users & Permissions ▸ Manage Groups ▸ create a group with **only the Rubbish
    app** and no publishing, financial or user-management permissions ▸ **Create WebAPI Key** in that group. Never paste it into
    chat or the project.
12. **Unity dashboard** (cloud.unity.com, org `choppeduncs`, project Rubbish `e8cc7715-…`): add a **`production`** environment
    next to `development`.
13. **Player Authentication ▸ Identity providers ▸ Add Steam:** the App ID and the publisher key (or Edit ▸ Project Settings ▸
    Services ▸ Authentication). Keep Anonymous for development; switch it off for production if the dashboard allows per-environment
    providers. Add any later Playtest or Demo App ID as an "Additional App ID".
14. **Turn on Cloud Save and Cloud Code** and accept their terms; accept Unity's **data processing agreement** at organisation
    level; add billing details before launch; set usage or budget alerts to your email if offered.
15. **Deploy access:** create a UGS **service account limited to the development environment** with Cloud Code and Cloud Save
    edit rights, then run `ugs login` once yourself in Terminal. Agents can then deploy to development without seeing the key.
    Production deploys stay with you (Editor ▸ Services ▸ Deployment, environment `production`). In the Cloud Code dashboard,
    enable the "account deleted" trigger the lead supplies.
16. **Steamworks app settings:** Steam Cloud quota small (10 MB / 50 files) with an Auto-Cloud path for `settings.json` only; upload
    the rich-presence localisation file the lead writes.
17. **Privacy policy:** approve and host the draft the lead writes (names Unity and Valve, describes Download/Delete my data) and
    put its URL on the store page.
18. **Before launch:** run the scripted 45-minute two-device checklist (invite, Join Game, cold start from an invite, Steam relay
    soak, Take it back across PCs). **After launch:** run the monthly export script.

---

## 15. Open decisions for Nick (the build uses the default unless you say otherwise)

1. **Slots:** 3 companies and 5 Towns per account. *Default: yes.*
2. **Crew Town size before Depot Row:** 4 (one company, four truck spots, the four Dumplings). Depot Row (M7) brings 8 per Town.
   *Default: 4.* Raising it to 8 in one crew now is one constant plus a second hi-vis colour for slots 5–8.
3. **Moonlighting at M5** (Bring by default, Just me as the alternative, capped at your own tier's pace). *Default: yes.* The
   alternative keeps MP as first written: wages always go to Pocket, and your save does nothing in friends' Towns until M7.
4. **Allow Duplicate company.** *Default: yes.*
5. **Cloud save every 5 minutes while something changed** (the cost driver). *Default: yes.*
6. **Clamp, not reject,** over-cap earnings. *Default: clamp.*
7. **Crew Card shared across the whole account.** *Default: yes.*
8. **30 days in "Recently deleted".** *Default: yes.*

---

## 16. Red-team review (round 2): every attack and what changed

29 attacks (2 critical, 9 high, 15 medium, 3 low). **27 accepted, 2 partly accepted, none rejected outright.**
"L0", "C1", "C2", "W0"–"W5" and package IDs refer to MP §8.7.

| # | Attack | Sev. | Verdict | What changed (where) |
|---|---|---|---|---|
| R1 | A truncated save is overwritten with Day 1 / $0 on the next quit, because `loaded = true` is set before the parse (`GameSession.cs:498`, `EbaeMarket.cs:599`) | Critical | **Accepted** | L0: `loaded` only after a good parse; quarantine `.corrupt-<utc>`; fall back to a generation; otherwise block saves and toast. M0 gate: random truncation and a 0-byte file stay byte-identical. (§5.1) |
| R2 | The guest guard isn't sticky (quit order, host crash), and nothing reloads the player's own data after co-op, so a friend's company can be saved as your own | Critical | **Accepted** | L0 `SaveGuard` latch, set at Join and `OnClientStarted`, cleared only by `ReloadOwn` (leave, join failure, `OnClientStopped`). Four M0 gate scenarios. (§6.1) |
| R3 | Backups rotate on every write, so all three are seconds old | High | **Accepted** | Time-tiered generations in L0's `SafeFile`; writes ≤1 per 2 s; Restore… UI in M1 (B14); restore-after-zeroed-cash gate. (§5.1, §9) |
| R4 | Two legacy files with separate backups recover into an inconsistent pair (dupe or loss) | High | **Accepted** | One shared `gen` on every save; recovery picks a matching pair; M1 merges them into one company record; mixed-generation test. (§5.1) |
| R5 | Identity follows whether Steam is running, splitting the account folder | High | **Accepted** | Identity fixed per build: release = Steam required; dev builds and Editor `dev:` unless explicitly opted in; opt-in without Steam is an error, never a fallback. (§1.2) |
| R6 | Agent play tests and bots write Nick's real save; the M0 bot gate can't measure what it claims | High | **Accepted** | `SaveRoot` + `-dataroot` in L0 (not W3); agent PLAY runs on a seeded scratch root, restored in `finally`, with a watermark; the bot audit fails if the real folder changes. (§5.1, MP §8.7.6) |
| R7 | `EbaeMarket` capture/restore was in W4 but W3 needs it | High | **Accepted** | Moved into C2 (lead, agreed with Progression A). |
| R8 | `GameSession.Net.cs` would be edited by three W3 builders at once | High | **Accepted** | The lead implements all of it in C2; W3 builders only call it. |
| R9 | No rollback available to agents (Plastic without `cm`; not a git repo) | High | **Accepted** (user step 1) | W0 "CK": local git checkpoint or `cm`; commits at every wave merge and before every `Setup_*` run; the build sync excludes `.plastic` and `.git`. |
| R10 | Identity in M1 is whatever the client claims | High | **Accepted** | `SteamAuthVerifier` moved from the transport spike (B13) to W3 (B10); host-bound tickets; lobby membership; release rejects `dev:`; names from Steam. (§7.6) |
| R11 | Decompression bomb in the company snapshot; unbounded named messages | Medium | **Accepted** | `BoundedGzip` (1 MiB cap), JSON caps + schema check before apply, server-only `rb.company`, `NetGuard` limits on every message and RPC (C1 API, enforced from B7). (§7.5) |
| R12 | `Ledger` posts entries for guest writes that were dropped, so observers count phantom income | Medium | **Accepted** | C2 adds `GameSession.TryAddCash(…, out appliedCents)` (the frozen `AddCash` keeps its signature); `Ledger` posts only applied cents; income observers use `Ledger.Posted`, never `CashChanged`; bot assertion: a guest posts 0 entries. |
| R13 | The Guard's caps don't stop inflation; honest Moonlighting outpaces solo by ~15×; fines reset per session; request IDs can be replayed | Medium | **Partly accepted** | Moonlighting capped at 1.5× the guest tier's typical rate, no Clean Days; fines capped per host per real day; request IDs single-use, expiring, HMAC-signed. **Rejected:** also multiplying wages by guest-tier ÷ host-tier, which would make helping a friend pay ~15% of solo income and break "adding a friend never lowers anyone's pay". (§3.4, §7.3) |
| R14 | Anonymous production sign-ins, uncapped `ImportLegacy`, no per-player rate limit | Medium | **Accepted** | Production Steam-only; commit spacing and hourly cap; `ImportLegacy` allowlist, off at launch; usage alerts. (§7.4, §14) |
| R15 | The publisher key has the permissions of its group; tickets end up in `Player.log` | Medium | **Accepted** | Dedicated minimal group, 2FA, rotation steps, `secrets` lint rule. (§12, §14) |
| R16 | The accounts doc and the build contract disagree on the save model | Medium | **Accepted** | §0.1 decision table; cents, durations, 5 Towns, `dev:editor` and deferred Moonlighting are in C2/M1. |
| R17 | Moonlighting in M1 has no company model to land in | Medium | **Accepted** | Deferred to M5; picker hidden until then; the DTO company model is an M5 prerequisite. (§3.4) |
| R18 | Newtonsoft on runtime types (`ItemInstance.Def`), thin fixtures, IL2CPP | Medium | **Accepted** | Opt-in DTOs in `Rubbish.SaveModel`, mapping in Assembly-CSharp, a generated maximal fixture, unknown-field round-trip tests. IL2CPP: noted (Standalone is Mono today). (§5.2) |
| R19 | Every balance patch needs a Cloud Code redeploy only Nick can do; honest commits rejected in between | High | **Accepted** | Content tables keyed by content version, unknown version = clamp-only, release checklist, development-only service account. (§7.4, §12) |
| R20 | The cost estimate ignores commit fan-out; a Relay fallback could cost ~$2k a month | Medium | **Accepted** | Lease inside the company key (1 read + 1 write per commit), history only at session boundaries, commit only when dirty, per-hour cost model; SDR on by default is a launch gate; Relay behind a remote flag. (§2.3, §5.4) |
| R21 | Gates need two real Steam accounts | Medium | **Accepted** | Second free account on any device or VM; a single-machine `SteamGameServer` spike (*unverified*); `CreateSocketPair` loopback; `dev:` keys for approval, slot and lease tests; one scripted 45-minute checklist. (MP §8.7.7) |
| R22 | The test assembly can't reach the riskiest code (import, migrations in Assembly-CSharp) | Medium | **Accepted** | DTOs, migrations, legacy parsers and the Guard live in the pure assembly; T2's NUnit proof is a W0 blocker. (§5.2) |
| R23 | Nobody owns building the `NetSession` prefab for W1/W3 gates and bot builds | Medium | **Accepted** | `[NetSessionComponent]` discovery in `Setup_Coop`; the build script runs `Setup_Coop.Run` in the copy before each bot build; the lead adds C2 stubs. (MP §8.7) |
| R24 | EditorPrefs are machine-wide, so a crashed agent can redirect Nick's saves | Medium | **Accepted** | Per-project, expiring `UserSettings/RubbishDev.json` (excluded from sync) + watermark. (§5.1) |
| R25 | "Why is my cash different?" | Medium | **Accepted** | Company-labelled wallet, join toast, leave card, last-Town on cards. (§3.4) |
| R26 | Three decisions before playing; a crash locks your company for 15 minutes | Medium | **Accepted** | Continue / Invite friends with pickers behind Change…; local lock liveness check. (§3.2, §5.1) |
| R27 | Several machines before the cloud exists fork the imported company | Low | **Accepted** | Imported guid from the legacy files' hash; Upload / Keep local / Export for local-only companies. (§5.6, §5.7) |
| R28 | Seals overclaim "verified progress" | Low | **Accepted** | "Cloud-consistent"; Community servers default to resident/fresh companies; no hash chain or seal before M8. (§7.7) |
| R29 | Lint glob misses B4's folder; `nolocalrng` duplicates `random`; HMAC with an in-file salt protects nothing | Low | **Partly accepted** | One `save` rule with the right glob; `nolocalrng` dropped. Privacy handled differently: plain IDs locally, `players{}` dropped from cloud Town copies, bans kept and disclosed. (§11) |

---

## 17. Changes this makes to other docs

- **Multiplayer.md:** done in the same pass (§0, §1.3, §1.5, §2.1, §2.3, §2.4, §2.6, §3.1, §3.4, §3.5, §4.7, §5.1, §6.1–6.4, §7,
  §8, §9, §10, §11, §12).
- **At the M0 gate (W2), lead:** Character.md §14 (variant = roster slot written to `variantOverride`; `OwnerClientId % 4` is only
  the offline fallback); CoreLoop.md (`GameSession` partial with guest guards and the mirror; `Net/**` and `Save/**` ownership;
  the pure assemblies; never save Bootstrap).
- **At the M1 gate (W5), lead, after Progression's build has landed:** Progression.md §9.2 / §9.8 / §9.9 (cash stored as cents,
  stamps become durations at the Town boundary, `dex.*` counters move to the Crew Card, v2 is the last format `GameSession` owns
  itself, host-only systems check `!GameSession.IsGuest`); LivingWorld.md §1 (7 AM applies to new Towns; the clock and curbs
  persist in the Town).
