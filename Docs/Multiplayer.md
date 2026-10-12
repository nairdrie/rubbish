# Rubbish Online: multiplayer plan

> **4 Oct 2026 — shared-grip revision:** §4.6 now replaces the original rhythm tug and separate auto-follow heavy lift.
> The older milestone/approval records below are historical: beat UI, button mashing, possession advantage, rip billing,
> and forced out-of-step drops are no longer the current design. Two players carry and contest using normal movement.

> **Status: approved 30 Sep 2026 (Nick's answers are in §11). Building starts now, in the order in §8.7.**
> Built so far: `Tools/coop_lint.sh` (§9 rule 14) and a first guest-save guard in `GameSession` (not yet sticky; hotfix L0 in
> §8.7 finishes it). This doc is the source of truth for co-op and online play, the same way `CoreLoop.md`, `LivingWorld.md` and
> `Character.md` are for their areas. **`Accounts.md` is the source of truth for identity, accounts, save slots and saved data**;
> where the two overlap (§2.1, §6), Accounts.md wins. `Progression.md` must follow §9.
>
> **Second pass (30 Sep 2026):** Nick asked for Steam-linked accounts with saves in our own database. That added Steam/UGS research,
> an accounts design, an M0 + M1 build contract (§8.7) and a second red-team review (29 attacks; verdicts in `Accounts.md` §16).
>
> **How it was made:** three research reports (Unity netcode, precedents from shipped games, and a codebase audit
> with file:line citations), three competing designs, a merged plan, then a red-team review. §12 lists every red-team attack
> and whether it was accepted or rejected. Dollar figures are estimates from vendor list prices as of 30 Sep 2026. Bandwidth
> and frame numbers are estimates until they're measured at the milestone gates.
>
> **User ask (verbatim):** "how can we make this online/collaborative? im thinking like roblox style multiplauyer where you play
> alongside other players, maybe can fight over trash bags, or work together, someone can stand on ther back of the truck the
> other remains driver... but how can this work technically... how do we sort out worlds? how can multiple people have a shop in
> the same world? think through the interaction details on how this can be fun, fair, and technically sound"

---

## 0. The answer in ten bullets

1. **Netcode: keep what we have.** Netcode for GameObjects (NGO) 2.x in **host mode**: one player's machine (the "Boss") runs the
   world and everyone else connects to it. On Steam that costs **$0**. We're not switching netcode libraries and not running servers.
2. **A world is a Town.** A Town is one record in the host's account (on their machine, synced to the cloud). Solo play is a Town
   nobody else is in. Friends join by Steam invite, "Join Game" on the friends list, or a 6-character code. **Your account is your
   Steam account**, with 3 company slots and up to 5 Towns, and any company can be loaded into any Town (`Accounts.md`).
3. **Ship "Crew Up Lite" first (about 10–11 focused weeks, including ~2 weeks for save safety and save slots).** 1–4 friends,
   one company, one truck, one shared wallet, with carry, throw and pass, riding the back of the truck, and proximity voice. The first
   fun session over the internet lands around **week 7**. Cloud accounts (M1.5) run alongside once Nick's Steamworks account exists.
4. **The truck is the stage.** It has a driver, a shotgun seat and two rear steps. Riders hang on solidly: only a crash (a tree, a
   wall, another truck, a rear-ender) throws them off, so every fling is still the driver's doing.
5. **Sharing or fighting over bags.** A second grab takes the other side: move together to carry, pull harder to take it,
   or sustain equal opposing pulls to tear it. No special UI, button timing, or company advantage. Depot work permissions still apply.
6. **Money adds, it never splits.** The company gets 100% of every bag. Helpers get a 15% wage **created on top**, split between
   whoever picked the bag up, whoever fed it into the hopper and whoever drove it home. Adding a friend never lowers anyone's pay.
7. **Several shops in one world = Depot Row.** A service road holds 4 depot plots, and each company gets its own facade, truck,
   wallet and Magnus. **Your company lives in your Steam-linked account (our database, cached on your PC) and comes with you** when
   you visit a friend's Town. Everyone keeps their own shop, Roblox-tycoon style, and we don't have to run any game servers. Before
   Depot Row you join friends as crew; from M5 your picked company earns from it ("Moonlighting", `Accounts.md` §3.4).
8. **Rivalry lasts one session ("League Night").** Customers are dealt out fresh each shift to the companies that are present.
   Taking a rival's on-time bag pays half. Rescuing a rival's late bag pays in full. Nobody's company can be gutted while they're offline.
9. **Always-on worlds = Community Town Servers.** Players run the same game headless on a spare PC or a cheap VPS, so Nick runs
   nothing. Official servers stay off the roadmap unless the game is a hit and there's a team to run them.
10. **Strangers come later, carefully.** Launch is friends-only. Public listings arrive only once there are enough players (more than
    about 500 online at once), and they come with crew-only voice and names chosen from a word list.
11. **Start today:** every system being built now follows the rules in §9 (all money goes through `Ledger`, no singletons, no
    `Time.timeScale`, host-side content rolls). **The first code:** hotfix L0 (§8.7), which stops a corrupt save being wiped on quit
    and stops a guest ever writing the host's world into their own save, even after a crash or disconnect.

---

## 1. Multiplayer model and topology

### 1.1 Decision: NGO 2.x host mode, on the stack we already have

We keep NGO 2.13 (upgrade to 2.13.3), Unity Transport and Multiplayer Services (upgrade to 2.3.3), with a player acting as host.

**Why host mode:**

1. **One machine judges the physics.** Rubbish's fun is contested physics: tugs, bonks, throwing into a moving hopper, a truck
   against bags and people. Host mode gives exactly one referee. Distributed Authority has no single physics simulation, by Unity's
   own documentation. It would also hit NGO issue #2903 (velocity is lost when ownership changes) on every hand-off.
2. **The code already points this way.** `CurbTrashSpawner` runs on the host only. `ChessController` + `MagnusChessBot` are the
   pattern to copy: the host holds state in NetworkVariables, clients send `ServerRpc` requests, `ClientRpc` plays cosmetics, and late
   joiners get an explicit sync.
3. **$0 on Steam.** Players host, and traffic goes over the Steam Datagram Relay, which is free at any scale and hides player IPs.
4. **It can run headless.** The same server code runs as a Community Town Server (§1.4) as long as no code assumes the host has a
   player (rule 8 in §9).

**Rejected, and when to look again:**

| Option | Why not now | Reconsider if |
|---|---|---|
| Distributed Authority | Its only advantage is surviving the host leaving. The costs: no single referee, easier cheating, and bug #2903. | Hosts quitting mid-session becomes the top complaint *and* Community servers don't solve it. |
| Switch to Photon Fusion 2 or FishNet | Switching is cheapest today (6 NetworkBehaviours), but neither fixes our real problems: who owns world state, and how scenes are structured. Fusion's predicted physics is the only real gain, and it costs a rewrite plus a monthly bill. | The M2 feel test (grab and throw at 150 ms round trip) fails even with instant local animation. |
| Official dedicated servers | Operations work, on-call duty and money. Vendors are churning: Unity Multiplay closed 31 Mar 2026 and Hathora closed 5 May 2026. | The game is a hit, there's a team, and Community servers can't meet demand (§8.5). |
| Couch split-screen | A very large refactor: input, cameras, HUD, cursor and about a dozen statics. | Never, for this game. |

**The cost we accept: no host migration.** If the Boss leaves, the shift ends for everyone. The Town autosaves, and guests keep
everything they earned (§2.6). That's normal for the genre (Lethal Company, R.E.P.O., PEAK).

### 1.2 Who decides what

1. **The host owns the truth:** money, saved records, ownership, status effects (knocked out, rabies), refereeing contested actions,
   the clock, the AI, anything that pays, and **what's inside every bag**.
2. **You own your hands.** Your own movement and animation, the item you're holding or have just thrown, and the truck while you
   drive it are simulated on your machine. The player doing an action never waits on the network to see it happen.
3. **Data at rest, objects in motion.** Curb bags, piles and truck cargo are small records (about 24–40 bytes) in synced lists. A record
   becomes a networked physics object only when someone touches it, and turns back into a record when it lands in a pile or is used up.
4. **Whoever simulates an object detects its events, and the host confirms them.** A bag you threw is simulated on your machine, so
   your machine reports "it went in the hopper", "it hit Dot" or "Dot caught it". The host checks each claim against its history of
   where things were (lag compensation, §4.12). Hoppers, bins and catch zones ignore other players' copies, so nothing pays twice.
5. **Nothing that pays or hurts is decided by two networked physics objects touching each other.** Different machines disagree about
   those collisions. §5.2 sets out what collides with what.

### 1.3 Transport

```
INetTransportAdapter            (the Steam lobby's rb_net field says which one a session uses)
 ├─ LocalDirect / Loopback  solo, bots, Multiplayer Play Mode: Unity Transport on 127.0.0.1; no sign-in, no internet
 ├─ Steam relay (SDR)       the shipped game: free, hides IPs, no port forwarding. Invites, "Join Game", rich presence.
 ├─ Unity Relay             development, and a fallback a remote flag can switch on (rb_net=relay, rb_relay=<code>)
 └─ Unity Transport UDP     Community Town Servers (a VPS has a public IP; listed through Steam's game-server list)
```

- **Steam library: Steamworks.NET** (2025.164.1, SDK 1.64, installed by git URL). It's what Unity's Steam sign-in docs use and what
  the transport below is built on. Facepunch.Steamworks can't be mixed with it.
- **Steam transport:** copy the community `SteamNetworkingSocketsTransport` (MIT) into `Net/Steam/Transport/` and fix 7 defects
  found by reading it: it never closes connections after the peer leaves, allocates on every message, treats NGO's sequenced
  unreliable messages as plain unreliable (out of order), accepts any connection, uses `NetworkManager.Singleton`, has no fake
  lag/loss options, and references an assembly from Unity Transport 6.6 (we're on 2.x). **Budget 3–5 days** (M1 wave 4, package B13).
- **Until it passes a two-machine soak,** Steam invites and Join Game run over Unity Relay through the lobby (`rb_net=relay`). **The
  Steam relay being on by default is a launch gate**, because a Relay-only launch could cost ~$2,000 a month at 1,000 concurrent
  players (§7). In release builds the Relay fallback is off unless a remote flag turns it on, with usage alerts.
- **Joining checks identity at the door:** the host verifies a Steam session ticket bound to its own identity (`BeginAuthSession`,
  approval `Pending`) on every transport (`Accounts.md` §7.6).
- **Two version numbers, not one.** The **protocol version** changes only when the netcode messages change, and a mismatch blocks
  joining. The **content version** (item tables, prices, houses) can differ: the host's values win, and anything a guest doesn't
  recognise shows as a "Mystery Item". A balance patch therefore doesn't split friend groups (§6.5).
- **Tick rate** stays at 30 Hz.

### 1.4 What ships when

| Stage | Name | Who hosts | Players | How you find it | Default rules | When (focused weeks from "go") |
|---|---|---|---|---|---|---|
| 0 | Solo | you | 1 | none | none | today |
| 1 | **Crew Up Lite** | a player | 1–4, one company | Steam invite, friends-list join, code | Crew | **~wk 11** |
| 1b | Crew Up (full kit) | a player | 1–4 | same | Crew | ~wk 18 (each batch gated by a playtest) |
| 2 | **Depot Row** (Rival Towns) | a player | 2–8, up to 4 companies | same | Rival | ~wk 24 |
| 3 | **Community Town Server** | a player's spare PC or VPS, headless | 8 by default, 12–16 after profiling | Steam server list, favourites, IP | set by the server owner | ~wk 28 |
| 4 | Public listing of player-hosted Towns | a player | ≤8 | Steam lobby list | Open | only above ~500 concurrent players sustained |
| — | Official servers | us | — | — | — | **not planned** |

A Crew Town becomes a Rival Town as soon as the host allows rival companies and someone brings or founds one. That's a Town
setting, not a separate mode or code path.

### 1.5 An honest note on "Roblox-style"

Roblox works because Roblox pays for trusted servers and keeps every player's progress in its own cloud. We get most of that feel
for $0:

- a shared street, several shops side by side, drop-in play and fighting over bags (host-owned Towns);
- **your shop follows you into your friends' Towns**, because your company lives in your Steam-linked account (§3.1, `Accounts.md`);
- always-on worlds that friend groups and communities run themselves (Community Town Servers).

**Accounts now exist** (Nick's call, 30 Sep 2026): saves live in UGS Cloud Save and only our own Cloud Code writes them, so from M8
a Community server can accept a travelling company that is **cloud-consistent** (provably the real cloud copy, with earnings clamped
to a rate cap). What we still **don't** get is *verified* progress: with players hosting, the cloud never sees the gameplay, so a
patient cheater can commit at the cap. That's acceptable only because there are no leaderboards, and Community servers default to
resident or fresh companies.

---

## 2. How worlds are sorted

### 2.1 Terms and save files

```
ACCOUNT       = your Steam account (PlayerKey steam:<id64>; dev:<name> in development). Cloud data keyed by UGS player ID.
 ├─ CREW CARD   1 per account (cloud, Protected + local copy): Pocket, cosmetics, titles, stamps, lifetime stats,
 │              Career level (cosmetic), block list. Settings stay on the machine (Steam Auto-Cloud).
 ├─ COMPANIES   3 slots: the "saves" you pick. The shop: name, colour, logo, cash (cents), rank, permits, upgrades,
 │              inventory, ebag listings, pile, truck, Magnus, members + roles, Neighbor Rating, ledger tail.
 │              Written only by its OWNER's game, through our Cloud Code. Goes into any Town you host or (M5/M7) visit.
 │   └─ MEMBER    1–4 per company; role Founder / Member / Helper
 └─ TOWNS       up to 5: the worlds you host. Map state, clock, curb records, houses, rules preset, ban list,
                Town Board, plot table, players{}. Written only by the HOST's game.
```

The key rules: **a Town owns a world, and a company owns a shop. A Town no longer has a "home company":** hosting means picking a
Town *and* a company (default **Continue**), and the host's company is loaded the same way a visitor's is. The host's Town never
keeps a guest's company; it only remembers which plot that company's sign was on, so the "CLOSED" sign can show the right name
(§3.5). Full detail, storage layout and every field's new home: `Accounts.md` §3–§5.

### 2.2 Town sizes and why

| | Crew Town | Rival Town | Community Town Server |
|---|---|---|---|
| Players | 1–4 | 2–8 | 8 by default, 12–16 after bot profiling |
| Companies | 1 (the Boss's) | 2–4 | 1–4 |
| Trucks | 1 (a second "Fleet" truck is a late unlock) | 1 per company | 1 per company |
| Host upload needed | ~0.2 Mbps | ~0.9 Mbps | ~2.7 Mbps at 16 (fine for a VPS) |

- **4 per company** matches the truck's four seats and the four Dumpling variants.
- **8 is a hard cap for player-hosted Towns.** NGO sends every object at one rate to every client and has no built-in interest
  management. Beyond 8 players, host upload and main-thread cost aren't viable on a home connection or on Wi-Fi.
- Public Roblox games that stay pleasant run 5–16 players per server. Bigger isn't better here.
- **Nick's call (30 Sep 2026): 8 per Town, 4 per company.** Until Depot Row (M7) a Town holds one company, so a Crew Town holds 4.
  Letting 8 friends into one crew before M7 is one constant (`NetConstants.CrewTownCap`) plus a second hi-vis colour for slots
  5–8; it's an open option (`Accounts.md` §15), not the default.

### 2.3 Menu and join flow

```
MAIN MENU
 ├─ CONTINUE         [Maple Hollow · Day 14 · RUBBISH CO. $8,210]   [Change…: company cards (3 slots) × Towns (≤5)]
 ├─ INVITE FRIENDS   the same Town, opened to friends (Steam invite / Join Game / code)
 ├─ JOIN A FRIEND ▸  invites | friends playing | 6-character code | Community servers (stage 3)
 │     M1–M4: you join as crew (nothing to pick)
 │     M5+:   ▸ Bring RUBBISH CO. (Moonlighting: your wage share + half the Rep go to your company, capped) | Just me
 │     M7+ (Rival Town): ▸ Bring RUBBISH CO. onto Depot Row | Work for a company | Found a new one
 └─ CREW CARD ▸ your Dumpling: outfit, titles, stamps, stats, Pocket          SETTINGS ▸ Privacy (Download / Delete my data)

In-game pause menu: "Open Shop to Friends" (solo → hosted without a reload; 2–3 s "Opening the shutters..." fade)
                    · Town Rules · Invite · Kick
```

Details of the company cards (rename, recolour, duplicate, restore, delete) and what players see on join and leave ("You're on
Nick's shift. Rubbish Co. is safe at home.", a company-labelled HUD wallet) are in `Accounts.md` §3.

### 2.4 Connection approval, slots and identity

Connection approval is **off today** and gets turned on in M0 (configured at runtime by `NetBoot`; Bootstrap is never edited).
A joining player sends a `JoinRequest` of **at most 1,200 bytes** (exact layout in §8.7.3):

- their protocol version and content version;
- their PlayerKey (`steam:<id64>`, or `dev:<name>` in development builds);
- a Steam session ticket **bound to the host's Steam identity**, so it can't be replayed to another host;
- a cosmetic loadout of at most 64 bytes;
- from M5, their choice: crew, Just me, or `{companyGuid, version, blobHash}` of a company to bring. **Never the company itself**:
  a brought company is streamed after approval (M7).

The host checks, with approval set to `Pending` while Steam answers (5 s timeout): the protocol matches; the Steam ticket is valid
(`BeginAuthSession`, from M1 wave 3); the SteamID is a member of the host's lobby; the player isn't banned; there's room (≤8 per
Town, ≤4 per company). **Release builds reject `dev:` keys.** Display names come from Steam, not from the request. Rejections come
with a readable reason ("Crew is full (4/4)", "Update your game").

**Slots and colours.** The host gives each player a **slot within their company**: the lowest free number 0–3, keyed by PlayerKey, so a
player who reconnects gets the same slot. The Dumpling variant (Chuck, Dot, Ozzie, Bert) is that slot. This replaces Character.md
§14's `OwnerClientId % 4`, which breaks on reconnect because NGO client IDs keep counting up. Companies are told apart by a
**company tint** on the hi-vis rings, the cap badge and the truck paint. Eight Dumplings on screen read as "P1–P4 colours in two
company colours".

### 2.5 Joining a game in progress

- **Download:** about 20–40 KB for a Crew Town, 50–80 KB for a Rival Town, done in under a second. It covers world records, company
  states, clock offset, the curb, pile and cargo lists, ebag listings, seats, at most 200–300 items in motion or held, and board state.
  Bag contents cost nothing: guests don't get them until a bag is opened (§6.3).
- **On arrival:** you spawn at your company's time clock, and everyone sees "BEEP: Ozzie clocked in 2:14 PM (LATE)". If the truck is
  out, you get a HUD arrow to it and a **"Taxi!"** whistle that pings the driver.

### 2.6 Session lifecycle

| Event | What happens |
|---|---|
| **A guest leaves or crashes** | **Held item:** returned to the host, left where it was. **Seat:** freed. **If they were driving:** the host takes the truck over using the last synced velocity (synced by us at 10 Hz, because of #2903), then the handbrake stops it. **Their brought company (M7):** the host sends the owner the final ledger deltas, and the depot shows "CLOSED" (§3.5). **Their Crew Card and rabies timer:** Crew Card gains arrive as deltas the guest's own game applies and saves; the rabies timer is held in the Town, keyed by PlayerKey. **On the guest's machine,** leaving reloads their own saves (the `SaveGuard` latch, `Accounts.md` §6.1). |
| **A guest reconnects** | Same company, slot, colour and rabies timer. |
| **The Boss quits** | Final save, then every guest sees **"The Boss went home"** + the Shift Report + "Saved to your Crew Card" → back to the menu. |
| **The Boss crashes** | Guests detect the lost connection and see the same card, reworded as "The Boss's truck broke down". The Town loses at most 60 s. Guests lose at most the last unsent delta batch (≤10 s), and **their own files are never touched by the host's world**: the latch blocks every save from the moment they clicked Join until their own data is reloaded. |
| **Host migration** | None. Possible later: "Hand over the keys", where the host exports the Town to another member. The two copies then drift apart, and nobody merges them. |

**Autosave:** the Town every 60 s of real time, at day rollover, when a truck enters or leaves a bay, when a company arrives or
leaves, and on quit; a company at most every 2 s while it has changes. Writes go to a temp file, then an atomic rename, with
**time-tiered generations** (latest, 10 minutes, session start, 7 dailies, pre-migration), not "the last three writes". The cloud
gets a copy every 5 minutes while something changed (`Accounts.md` §5).

### 2.7 Finding a game

- **At launch:** Steam friends, rich presence "Join Game", invites, join codes, and a Discord "looking for crew" channel.
- **Stage 3:** Community Town Servers in Steam's server list, with favourites and join by IP.
- **Stage 4 (conditional):** a public lobby list of player-hosted Towns. It's built only once more than ~500 concurrent players are
  sustained. A browser showing three Towns, one of them with a rude name, signals a dead game.

---

## 3. How multiple people have a shop in the same world

### 3.1 Companies own shops, players join companies, companies travel with their founders

A **company** owns a depot, a truck, a wallet, a Magnus, a laptop (Scrapazon and ebag), a Business Rank, district permits and a
Neighbor Rating. Players join companies.

| Situation | How it works |
|---|---|
| **Crew Town** | Exactly one company: the Boss's. Everyone works for the Boss, and that's where the friend-slop chaos comes from (one truck, one wallet, shared blame). Guests earn stats onto their Crew Card. **From M5 ("Moonlighting", default):** a guest who picked **Bring** has their wage share and half the Rep of bags they lifted credited to *their own* company instead of their Pocket, capped at 1.5× their own tier's typical income per minute and with no Clean Days, so it never outpaces their own shop. **Just me** keeps wages in the Pocket. |
| **Rival Town, "Bring your company"** (friends' default) | Each founder checks their company out of their account (a lease, so it can't be in two Towns at once) and streams it to the host after approval. The host loads it onto a free plot for the session and sends **ledger deltas** back, which the owner's game applies through the Guard (`Accounts.md` §7.3). **Dot's depot appears on any Depot Row she visits**, with her cash, upgrades, Magnus and trophy window. |
| **Rival Town, "Fresh Start League"** (host option) | Everyone founds a company for this session only, with the starter kit. It's a fair race, and nothing is written back except stamps and stats. |
| **Community Town Server** | Companies are **resident**: stored on the server, because it's always on and a stranger's edited file shouldn't come in. Offline companies are frozen (§3.5). |

**Why companies travel with their owners.** Keeping a guest's company inside the host's save (the first draft of this plan) meant
Dot's company only existed while Nick hosted, and its customers opened to everyone within a minute of her leaving. A company that
lives in its owner's account means:

- nobody can gut your shop while you're offline;
- your shop is really *yours*, on any PC you sign in to;
- rivals meet on neutral terms each session.

**Trust, now that accounts exist:** only our Cloud Code writes a company to the cloud, and it rejects structural cheats and clamps
implausible earnings. A host can't write a guest's company at all; it sends deltas the owner's Guard checks, and every visit can be
undone with "Restore from before this visit". A player can still edit their *local* copy, but it can't be brought anywhere until it
syncs through Cloud Code. Community servers still default to resident companies (`Accounts.md` §7).

### 3.2 Depot Row map

Today's map is a ring-road neighbourhood (37 houses) with one road running south to the RUBBISH CO. lot (x −18..18, z −24..12),
surrounded by empty tree line. Depot Row extends that south road into a service road:

```
                    [ ring-road neighbourhood: 37 houses (District 1), today's map ]
                                          │ south road
 ════════════════════════════ DEPOT ROW  (new two-lane service road) ════════════════════════════
  ┌──────────────┐ ┌──────────────┐  ┌──────────────────────┐  ┌──────────────┐ ┌──────────────┐
  │ PLOT 1       │ │ PLOT 2       │  │ TOWN YARD            │  │ PLOT 3       │ │ PLOT 4       │
  │ RUBBISH CO.  │ │ "FOR LEASE"  │  │ time clock · spawn   │  │ DOT'S        │ │ "FOR LEASE"  │
  │ (host's home │ │ or a visiting│  │ Town Board (League   │  │ DISPOSAL     │ │              │
  │  company)    │ │ company      │  │  Night winners)      │  │ (visiting)   │ │              │
  │ facade + bay │ │              │  │ Town Dump (30%)      │  │              │ │              │
  │ door         │ │              │  │ Big Job board        │  │              │ │              │
  └──────┬───────┘ └──────────────┘  └──────────────────────┘  └──────┬───────┘ └──────────────┘
    apron + 12 m safe zone each                                                  gate ▸ District 2
         │ roll-up door = fade + teleport (truck and riders go through together)
         ▼
  ╔═════════════ POCKET INTERIORS (one additive "DepotRow" scene, y = −200, 150 m apart) ═════════════╗
  ║ [Plot 1 interior] [Plot 2 interior] [Plot 3 interior] [Plot 4 interior]                           ║
  ║ each one: bay · TIP IT pad · sort table (2 stations) · compactor · bins · KEEP crate · laptop ·    ║
  ║ Magnus + chessboard · locker · cot. Built from ShopScene by Setup_DepotRow at EDIT time, hidden    ║
  ║ until a company occupies the plot. Lights are on only on machines whose camera is inside.         ║
  ╚═══════════════════════════════════════════════════════════════════════════════════════════════════╝
```

**What sits on the street side** (physical, and seen by everyone): the company sign and colour, the roll-up door, an office door, a
trophy window showing the 6 best finds (props driven by company state), a "HELP WANTED" / "BRB" / "CLOSED" sign, and a cheap animated
Magnus waving in the window.

**What the Town Yard adds:**
- **Town Board:** League Night winners (cleanest company, most on-time bags, most rescues, best find). The winner's sign glows gold
  until the next dawn.
- **Town Dump:** tip unsorted bags for a flat 30% of their sort value, so a crew with no sorter still earns something.
- **Big Job board:** one per shift, e.g. "Street party mess on Elm", "Alley dump behind the laundromat": 15–25 bags in one place,
  announced an in-game hour ahead. It gives idle crews something to do, and it's either co-op or a brawl.
- **Magnus vs Magnus:** challenge a rival's Magnus at their office chessboard. His skill comes from their upgrades.

### 3.3 Scenes and instancing

| Stage | Structure | Why |
|---|---|---|
| **Crew Up (Lite and full)** | Keep today's ShopScene ⇄ Neighborhood swap, but make it a **group ritual**: "ROLL OUT" and "CLOCK OUT" wait for the crew (§4.3). Fix the audited bugs: guests stuck in the garage, and respawns. | Merging the two scenes everyone is building in right now would sit on the critical path to the first fun session. A crew moves together anyway. |
| **Depot Row** | Neighborhood stays loaded (it's the active scene and owns global lighting). **One** additive `DepotRow` scene holds 4 pocket interiors at an offset. Doors and bay doors fade and teleport. Nobody is ever dragged by someone else's door. | NGO loads scenes for every client, so a per-player interior scene is impossible. Loading ShopScene 4 times additively would duplicate in-scene NetworkObject IDs. One scene with 4 edit-time interiors avoids both problems. |
| **Physical drive-in bays** | Only if playtests ask for them. | Costs lighting work and about 4k GameObjects on the host, for a small gain. |

**Rules that make this work:**
- **Never spawn a `DepotPlot` at runtime.** In-scene NetworkObjects nested inside a runtime-spawned prefab (chess pieces, stations,
  laptop, compactor, Magnus) won't spawn on clients. `Setup_DepotRow` places all 4 at edit time. "Founding" or "arriving" only
  reveals them and binds them to a company ID.
- **Lighting is local.** Each machine enables the interior lights and sets `RenderSettings` ambient based on where *its own* camera
  is. `DayNightSky` stops writing global lighting while the local camera is inside.
- **Chess:** the 33 in-scene chess NetworkObjects per depot collapse into one board-state variable (`SyncBoardClientRpc` already
  implies this). Otherwise 4 depots would mean 132 of them.

### 3.4 Arriving, founding and claiming plots

- **Plots always equal the maximum number of companies (4).** Land is never scarce, sold or rationed. FFXIV's housing lottery is the
  warning. Nothing is reserved for anyone.
- **Bring your company:** when you join, the host assigns you the free plot nearest your last one. A Scrapazon drone "drops" your
  sign (a 5-second clip moment; it only reveals the plot the setup script already placed). Your truck rolls out of the bay.
- **Found a company:** walk up to a "FOR LEASE" sign, pick a name and colour, and press Found. You get the starter kit: tier-0 truck,
  $250, and a customer deal (§4.6). The new company takes **a slot in your account**; the starter state is built by the same
  `StarterCompany()` function the cloud uses, so nobody can found a rich company.
- **Small Business Grant** (late founders in "Bring your company" Towns): a one-time grant of 25% of the median present company's
  lifetime earnings, capped at the tier-2 permit price. **One per PlayerKey per Town, recorded in the Town save**, so it can't be
  farmed by founding and disbanding.
- **Existing saves:** today's `rubbish_save.json` + `ebae_save.json` are imported as company slot 1 "Rubbish Co." and Town 1
  "Maple Hollow" (`Accounts.md` §5.7). The old files are never edited or deleted.

### 3.5 When people leave

- **A visiting company's founder leaves:** the founder's game gets the final deltas, shows the Visit Receipt, commits and checks the
  company back in. The plot shows **"CLOSED, back tomorrow"** with their sign, the truck parks inside and the door locks. Their
  session customers go back to open curbs. Their *company* loses nothing, because it isn't stored in the Town.
- **Members without the founder:** a company is present only while its owner is online. Members who turn up without the founder work
  as Helpers for any present company, or for the Boss. The founder can **Hand over the keys** (`TransferCompany`: both online and
  confirming, the company moves between accounts in one step, with a new epoch).
- **Community servers (resident companies):** when every member of a company is offline, its customers are **frozen**: their bags don't
  go late and can't be poached or rescued. A company goes dormant only after **14 real days** without a member online. Dormant means
  a "CLOSED FOR RENOVATION" sign and customers returned to open curbs. Nothing is deleted. The server owner can evict a company,
  and a snapshot is kept.
- **Open (public) Towns:** strangers' companies last for the session only. They dissolve when their last member leaves, and the name
  and colour are saved to the founder's Crew Card as a brand preset. No ghost companies squatting plots.

### 3.6 Roles, permissions and spending

| Role | Drive | Shotgun / rear step | Sort, TIP IT, laptop | Company purchases | Rename, remove, rules |
|---|---|---|---|---|---|
| Founder | yes | yes | yes | yes | yes |
| Member | yes | yes | yes | within their daily allowance; above it needs a co-signer | no |
| **Helper** (the default for anyone new, including outsiders who clock in at "HELP WANTED") | only if the founder allows | yes | sorting only; earns wages | no | no |
| Visitor | no | only if "Hitchhikers welcome" is on | no | no | no |
| Bay-banned | no | no | no; hits a wall only they can see | no | no |

**Spending rules:**
- **Daily allowance per member:** 10% of company cash at dawn (at least $150). Anything beyond it prints a **Purchase Order**
  that another online member or the founder must **co-sign within 30 s**. The founder never needs a co-signer.
- ebag listings below 50% of the estimate, and pawning anything worth over $200, also need a co-signer. Nobody pawns the wedding
  ring out of spite.
- Every purchase is announced: "Dot bought X-Ray Eyes for Magnus −$1,200". Good fuel for blame.
- **Open Towns:** any two members can demote a third to Helper.

### 3.7 Map size for Rival Towns

The starter map (37 houses, ~30–40 bags per 20-minute day, a curb cap of ~60) is too thin for 4 companies. So:

- **Only District 1 open:** Rival Towns allow 2 companies.
- **3–4 companies:** District 2 opens as "Rival overflow" for everyone. Anyone can collect there, at District 2 rates if their company
  holds the permit and District 1 rates if it doesn't.
- **Spawn *rate* scales with player count**, not just the day's total budget (§4.7), plus the Big Job each shift.

---

## 4. Interaction ruleset

Key: **H** = the host decides and validates; **O** = the owner's machine simulates; **L** = local cosmetics only.
Every number here must feel right at **150 ms round trip, 20 ms jitter, 2% packet loss** and a 30 Hz tick (the "Crew Bad Wi-Fi" preset).

### 4.1 Fairness principles

1. **Only the hopper pays.** Touching a bag isn't owning it.
2. **The street is contested. The depot, truck and hopper are safe.**
3. **Pay is added, never split.** Adding a player never lowers anyone's pay per bag.
4. **Short punches can combo into a knockdown.** The first two landed punches briefly stumble a player; a third quick punch
   knocks them down for 3.5 s. Knocked-down players can be carried by anybody, but regain hit immunity after recovery and can
   wriggle out of a carry. Voluntary flop, riding, hopper entry and rabies still have their own recovery rules.
5. **Every mean action has four things:** a cooldown, a counter, a visible cause and a funny failure. A rejected action should look
   like slapstick, not lag.
6. **Chaos is a dial the host sets** (§4.2), shown on the Town's listing.
7. **Contests outlast latency.** Tugs last about 2 s. Contested grabs have a heave window. Neither ping nor being the host decides a fight.
8. **Skill beats spam.** Mashing never wins. It rips the bag, and you pay for it.
9. **Rivalry is personal, consequences are accountable.** Stats, titles and wages are personal. Litter is charged to whoever made it.
10. **Every co-op role is a job, not a spectator seat.** Automation takes the boring part, never the people.

### 4.2 Town rule presets (the chaos dial)

One `NetworkVariable<TownRules>` bitfield (4 bytes). **Every host check goes through a single `Permits(actor, target, verb)`**
that considers the preset, safe zones, the Safety Vest, blocked pairs and crew membership. Clients only read the preset to hide prompts.

| Toggle | **Crew** (friends, default) | **Rival** (friends, competitive) | **Open** (public, stage 4) |
|---|---|---|---|
| Bonk or punch crewmates | on | on | on |
| Carry or throw a flopped or knocked-down player | on | on, including competitors | on, including competitors |
| Bonk or punch players from other companies | n/a | on | on |
| Carry or throw players from other companies | n/a | **on when downed** | **on when downed** |
| Tug over bags with other companies | n/a | on | on (this is the core rivalry) |
| Rip fines | shame only (receipt) | on | on |
| Session turf, cones, poach vs rescue | n/a | on | on |
| Raccoon Crate sabotage | n/a | on (once per company per night) | off |
| Rabies Tag, Sticky Fingers, PACK, Compactor Reboot | on | crew only | off |
| Rival trucks collide with yours | n/a | **no (ghosted)** | **no (ghosted)** |
| Safety Vest | n/a | available | available, **on by default** for new joiners |
| Voice with non-crew players | on | on | **off** (ping, quick-chat and emotes only) |
| Kick | host | host | host + votekick |

**Current implementation:** punch and downed-player carry checks allow any player in reach, regardless of employer. The
future preset permission layer described in this table is not yet wired into those two verbs.

### 4.3 Co-op roles: everyone has a job

| Where | Role | What they do | Why a friend beats an upgrade |
|---|---|---|---|
| Truck | **Driver** | steer, throttle, brake, horn; a crash throws every rider off | decides the comedy (§4.4) |
| Truck | **Shotgun** | **navigator:** while the seat is occupied, shotgun sees the full route map and drops pings on the driver's HUD, and the driver's big next-stop beam turns into those pings (a solo driver keeps the beam). **Spotter:** when backing into the bay, shotgun's wave shows as distance beeps. Also horn cord, lights, radio station, and **side-loader arm operator** once it's bought | the arm is operated, never automatic |
| Truck | **Rear steps** (2) | snag bags as the truck rolls past (§4.4); BANG the truck to signal | at 2.5 m/s or slower, a rider beats the tier-1 side-loader arm |
| Street | **Runner** | grab bags beyond step reach, PASS them up the line | the relay chain (§4.5) |
| Depot | **Sorters** (2 stations) | rip bags, sort into bins, find the loot | Magnus steps back when humans are at the table (below) |
| Depot | **Floor** | compactor, hazard barrel, KEEP crate, TIP IT, fetching bags from the pile to the table | hazards in the compactor are fined against whoever slammed it |
| Depot | **Laptop** | list finds on ebag, answer offers, buy from Scrapazon, file co-signs | offers ping everyone; the laptop person haggles |

- **Magnus with humans:** when 2 or more people are sorting, Magnus takes only the junk and the leftovers. Valuables and hazards go to
  the humans first; he waits 10 s, then handles them. When fewer than 2 are sorting, he works at full strength as he does today.
  Solo play keeps its automation, and crews keep their jobs.
- **Overlapping phases (Depot Row, where crews can split):** a crew can tip half a load and head straight back out while one or two
  people sort. The pile acts as a buffer.
- **Rejected:** unlocking a second "Fleet" truck early. It splits the crew and the clip stage in two, so it stays a late unlock.

#### "ROLL OUT" and "CLOCK OUT" (Crew Up scene swap as a ritual)

- **Leaving:** driving at the garage door shows "ROLL OUT: waiting for crew (2/4)... HONK to call". It goes immediately when
  everyone is seated. Once more than half the crew is aboard, a 10 s countdown starts.
- **Stragglers come along anyway:** they arrive tumbling out of the hopper at the other end ("HITCHHIKER!"). That's a clip, not a
  punishment.
- **Coming back:** "CLOCK OUT" works the same way at the depot door.
- This replaces "a door yanks everyone" and fixes the audited bug where guests can't leave the garage.

### 4.4 The truck is the stage: driver, shotgun, rear steps

```
              FRONT
        +---------------+
        | [0]      [1]  |  0 DRIVER   steer, throttle, brake, horn; body hidden in the cab
        | DRV    SHOTGUN|  1 SHOTGUN  navigator, spotter, horn cord, lights, radio, side-loader arm operator
        |---------------|
        |     BODY      |
        |   HOPPER [H]  |  H hopper seat (Crew preset; you climb in yourself or flop): waist-deep, booped by every bag
        +-[2]-------[3]-+  2/3 REAR STEPS: visible, one hand on the rail; PACK lever between them
              REAR
```

#### Boarding and leaving

| Action | Rule |
|---|---|
| Enter the cab | truck at 1.0 m/s or slower, within 1.5 m of the door |
| **Catch the truck** (rear step or side ladder) | **E anywhere near the back** (12 Oct 2026): behind it, round the rear corners or alongside the side ladders, looking anywhere, with the truck at 4.5 m/s or slower. You go onto the nearest free hold, your own side first; a full truck answers "No room on the back". The host re-checks the spot on its own copy with 1.5 m of lag slack (and takes the next nearest hold if yours went meanwhile). Sprint speed is 5.3 m/s, so you can catch a rolling truck empty-handed. **Above 4.5 m/s:** no prompt. |
| Switch spot | **Shift** (pad RB) while riding: shuffle round the back to the next free hold (left ladder → rear-left step → rear-right step → right ladder → round again). The host checks the hold is still free. |
| Hop off | E or Space, any time. **Up to 3 m/s:** land on your feet with the truck's speed. **3–6 m/s:** stumble 0.5 s. **Above 6 m/s:** tumble 1.2 s. |
| Driver bails while moving | roll-out tumble. The truck coasts, and the handbrake engages after 2 s. |
| Driver swap | only at 0.5 m/s or slower, via "Scoot over". Nobody can be yanked out. |
| AFK driver | seat released after 90 s with no input while stopped |
| Driver disconnects | the host takes over, coasts to a stop and applies the handbrake. `Truck V2.prefab` gets `DontDestroyWithOwner = true`. |
| Lock | drivers are always company members. Riders: "Crew only" (default) or "Anyone". |

#### Staying on, and getting thrown off: the driver decides the comedy

- **Riders stay on solidly** (Nick, 12 Oct 2026: "completely remove the grip mechanic ... should be solidly on unless they hit
  something solid"). No grip meter, no leaning, no HOLD ON, no shoving each other off. Corners, hard braking, drifting, curbs and
  potholes never throw anyone.
- **A crash throws every rider off at once:** the truck's horizontal velocity changing by **5 m/s or more within ~0.06 s** (three
  physics steps), from any speed: driving into a tree, a wall or another vehicle, or being rear-ended while parked. Locked-wheel
  braking changes it by ~0.4 m/s a step and a hard corner by ~0.2, so ordinary driving stays far below it.
- **Safety Bar** (Scrapazon; any rider flips it with B): while it's down, crashes under 8 m/s throw nobody. **Padded Step:** +1.5 /
  +3 m/s on top. A crash they soak up is a jolt and "The Safety Bar held!".
- **FLUNG.** The rider flies with the truck's velocity plus 4 m/s outward and 4.2 m/s up, then is knocked out for a moment (a hedge
  catches softly: no knockout). Whatever they were holding flies too, sometimes straight into the hopper. (Riding is opt-in, so
  flings are exempt from the 1.5 s rule, but immunity still follows.)

- **BANG signal:** a rear rider slaps the truck, the way real crews do. One bang means go, two means stop. "You TOLD me to stop!"
- **Rear-step pickups:** curb bags sit 1.1–1.8 m from a curb-hugging truck, and step reach is 1.9 m. At 2.5 m/s or slower, a rider
  snags bags as the truck rolls past and feeds them into the hopper beside them.
- **The roof and the hopper lip are slippery.** Anyone who lands there snaps to a free anchor or slides off. Nobody ever stands on a
  moving rigidbody (the desync trap Drive Beyond Horizons fell into).

#### How the truck works on the network

- **Seats:** a host-written `NetworkList<Seat{byte seat; byte playerSlot}>` replaces the single `driverClientId`.
  - `ClaimSeat(seat, pressTick, myPos, truckLocalOffset)`. The host checks permissions, that the seat is free, and the per-seat speed
    limit, then runs a **lag-compensated range check**: it rewinds its 1-second pose history of that truck to `pressTick − the claimant's
    interpolation delay` and checks the claimant's reported position against its own history of that player (≤1 m + speed × 0.1 s slack).
  - While waiting, the client plays a "reach for the handle" animation. A rejection plays a "slipped off!" flop, so the rejection
    itself is the joke.
- **Riders are pinned locally, not parented through NGO.** Every machine pins each seated rider to the seat anchor **on its own copy of
  the truck** in `LateUpdate`. Position sync for seated riders pauses (~0 bytes). Rider and truck always come from the same local copy,
  so there's zero jitter between them. This also removes the broken client-side `player.transform.parent = this.transform` at
  `TruckControllerV2.cs:207`, which NGO reverts on non-hosts because `Player.prefab` has `AllowOwnerToParent: 0`.
- **A crash is detected where the truck is simulated and applied where each rider sees it.** Only the driver's machine (or the host,
  for an empty truck) has the truck's true velocity, so its sensor reports a crash as `{tick, Δv}`; while anyone rides it also sends
  a 10 Hz pose sample (`{tick, speed, position}`, ~21 B) so every machine can measure how far its displayed truck lags the driver's
  clock. Each rider **applies the crash when their interpolated truck reaches that tick** (against the truck's replicated Safety Bar
  and Padded Step), so you're never thrown before the hit you see; the rider's fling event carries that tick and its world start, so
  every machine flies the same arc.
  - *Rejected alternative:* each rider detecting the crash from their own interpolated truck pose. Second derivatives of jittery
    interpolated positions produce false flings.
- **Switching spots** is a request to the host (`RideSwitchRpc`): it checks the rider rides that truck and the hold is free, then
  moves `BodyNet.step`; every machine walks the rider round the back of its own copy of the truck.
- **Rider camera:** remote trucks are **dead-reckoned** between snapshots using the 10 Hz velocity sync, with a bigger interpolation
  buffer than players. The rider camera is damped in the truck's local space. The driver's own truck gets Rigidbody interpolation
  (it's `m_Interpolate: 0` in `Truck V2.prefab` today).
- **Ownership follows the simulation, not the seat.** A driver who bails keeps simulating the empty truck until it stops, and only then
  does the host take it back. *Built 12 Oct 2026:* a guest who gets out parks the truck on their own machine at once (parking brake,
  WASD no longer drives it, no grade-assist downforce, exactly as on the host) and stays its owner; the host takes it back once its
  own copy has held still for 0.5 s (within 1 cm / 0.5°; at most 10 s), starting it at rest (`TruckHandbackWatch`,
  `TruckControllerV2.ServerHandbackTick`). If they get back in before then, nothing changes hands at all; `ServerForceReleaseDriver`
  (Depot Row, payroll) still takes it back at once. The exiting player's move to the door is sent as a `NetworkTransform` teleport.
- **Collisions** are covered in §5.2. In short: players collide with trucks **one-way on their own machine**; rival trucks are
  **ghosts** to each other in Rival and Open presets; loose bags get nudged by the host.

### 4.5 Hands: carry, pass, catch, bonk, punch, players as cargo

- **One carryable at a time:** a bag, a loose item, an armful of litter, the Goblin Crown, or a flopped friend. The existing weight
  slowdown applies (45% slower at 40 kg or more). A friend weighs ~60 kg.
- **PASS (the relay backbone):** look at a crewmate **within 8 m** (15° aim-assist cone) and tap E. A scripted lob (0.35 s at 3 m, up
  to 0.6 s at 8 m) lands in their hands. The host moves the holder. The item is kinematic while carried or lobbed, so bug #2903 can't
  touch it. **Relays between friends never depend on physics catches.**
- **CATCH (free throws):** **the thrower's machine detects catches**, the same machine that detects bonks and hopper hits. If the bag
  passes within 0.9 m of an empty-handed crewmate's chest in the thrower's view while that crewmate faces it (±70°), it's a catch. The
  host validates it (bag still thrown, not yet counted, within 1.6 m in its history), and the catcher only plays the animation.
  **An empty-handed crewmate facing the throw always catches and is never bonked**, unless they hold G to dodge.
- **RELAY:** counts when at least 2 crew members' hands touched the bag, each leg was 3 m or more, and it reached the hopper within 6 s
  of leaving the curb. Reward: **"RELAY ×N!"** and +5 Rep per extra hand (up to 4). It pays Rep, not cash, so it can't be farmed for money.
- **BONK** (a thrown item hits a player):

  | Impact speed | Effect |
  |---|---|
  | under 6 m/s | a boop: squash and a sound |
  | 6–11 m/s | stumble 0.5 s; whatever they carry pops loose |
  | 11 m/s or more (max throw is 14 m/s) | knocked out 1.2 s, dizzy stars |

  Bags never burst on players (the existing `IsPlayer` guard). A knocked-out victim has hit immunity but is carryable while down;
  a brief combo-building punch does not grant hit immunity. The same thrower can bonk the same victim at most once every 4 s.
  **Net:** the thrower's machine claims `BonkClaim(victim, item, speed, tick)`. The host checks the item is thrown and owned by the
  claimant, within 1.5 m of the victim in its history at that tick, at 15 m/s or less, and allowed by `Permits()`. The **victim's own
  machine** applies the knockback, reusing `CritterKnockback` generalised to `PlayerKnockback`.
- **PUNCH (RMB / RB):** 1.6 m range, 60° cone, 0.58 s cooldown. Two landed bare-fist hits briefly stumble the same target;
  the third within 2.8 s knocks them down for 3.5 s. A miss or target switch breaks the combo. The host validates the range;
  the victim's machine applies knockback. Swinging a held item is slower (1.5 s) and does not build the fist combo.
- **Players as cargo** (teammates or competitors):
  - **Who can be picked up:** any nearby player who voluntarily **flopped** (Z / R3) or is **knocked down**. E / X picks them up;
    their hit immunity does not prevent pickup during the knockdown window. The host rejects a grab during a no-regrab window.
  - A carried player escapes with 5 Space / A taps (~0.8 s). Carries drop automatically after 10 s. An escaped player can't
    be re-grabbed for 3 s.
  - **Throwing a friend** sends them 3–6 m and knocks them out for 1.0 s (then immunity). Landing in the hopper puts them in the hopper seat.
  - **PACK** arms only if the victim is in the hopper **by choice** (climbed in, or was thrown in while flopped). Holding the rear lever
    for 1.5 s arms the packer while the victim sees "PACKER ARMED". If they don't jump out in time: **FLATTENED** (a pancake for
    3 s), then they pop out the back.
  - **Compactor Reboot:** the same deal. The victim comes out **BALED** (a cube for 3 s).
  - **Net:** the host sets `AttachedTo{truck or compactor, slot}` and uses the same seat pinning. A thrown friend simulates their own
    flight on their own machine, so it looks smooth to them, and the thrower's 0.4 s heave animation hides the round trip. Limb
    ragdolls are local cosmetics; only the root position is synced.

**Maximum time without control:**

| State | Duration |
|---|---|
| Stumble | 0.4–0.5 s |
| Knockout | 1.0–1.2 s from a thrown hit; 3.5 s from a fist combo |
| Faceplant | 1.0 s |
| Fling (opt-in: you chose to ride) | 2 s at most |
| Carried (voluntary flop or involuntary knockdown) | 10 s at most, and you can wriggle free |
| Flattened or baled (opt-in, after an escape window) | 3 s |
| Passed out (you ignored the rabies) | about 4 s: the world tips, fade, hospital bill, you wake where you fell (no lie-down to wait for a jab since 2026-10-12) |

Hit immunity prevents repeat bonks after a full knockdown, but it deliberately does not block a pickup while the player is down.
Short combo-building punches also do not trigger the long immunity period.

### 4.6 Sharing and fighting over trash bags

**Current implementation, 4 Oct 2026.** `HELD → second grab → SHARED GRIP → one holder, safe drop, or rip`.
The network retains the legacy `Tug`/`TugNet` names, but there is no rhythm minigame or tug screen.

#### Grab and heave

- **Pressing E** plays your heave (lift to shoulder, 0.35 s) and a stand-in bag rises on the same frame. `RequestGrab` goes to the host.
- **The host confirms ownership immediately**, spawning the networked bag already owned by you, so no transfer is needed later.
  - **If no other player is within 3 m of the bag** (in the host's view, plus speed × 0.3 s), the bag is HELD at once. At 150 ms round
    trip the confirmation lands inside your heave animation, so the uncontested case, which is almost every grab, has **no added delay**.
  - **If someone is near,** the bag stays in **HEAVE for 250 ms of host time.** A qualifying second grab in that window starts a tug
    instead of "too slow". **You can't throw or pass until HELD arrives.** A throw pressed during the heave is buffered and fires then,
    worst case ~50 ms after the animation ends at 150 ms round trip. **A thrown bag never snaps back.**
- **What counts as a qualifying second grab:** within 1.5 m + lag slack + 0.5 m, normal/on-foot body state, empty hands,
  no other shared grip, not driving/chess/cubed or externally controlled, and both players allowed to work in that interior.
  Ordinary grab throttling remains; an ended pair has a short 0.8 s re-grab cooldown. At most two people hold one item.
- **The host's zero ping can't win races outright.** Its grabs go through the same window. **Test gate:** the host wins at most 55%
  of simultaneous contested grabs.
- **Before tugs exist (Crew Up Lite):** a heave-window tie goes to the earlier client-stamped press, with the stamp clamped to
  [arrival − that client's round trip − 50 ms, arrival]. The loser gets "YOINK!", the stand-in snaps back, and they flinch for 0.3 s.
- **E on a held bag or its holder takes the other side.** Heavy-bag handles and grabber-pole snatches enter this same interaction.
  Employment and first possession give no strength advantage. Street competitors and coworkers follow identical rules;
  this does not grant access to trash in a depot where you lack work permission.

#### Shared movement (host referees; both players keep control)

- **Move together:** walking, following, turning and sprinting in a similar direction are cooperative, with no strain timer.
  Heavy bags use both grips at 85% unloaded movement speed instead of the solo half-speed drag. Nobody auto-follows or gets
  an OUT OF STEP penalty for sprinting. Both avatars pose their hands toward the bag.
- **Pull apart:** compare each player's camera-relative movement intent projected outward along the line between them.
  Effort is 2 walking / 4.5 sprinting, smoothed on the host. Intent rather than measured velocity lets a player pull against
  resistance; network jitter, camera motion alone and slipping physics don't manufacture a winner. A collision-aware soft
  leash resists separating beyond 1.45 m (the characters' arm reach) without freezing movement or rotating the camera.
- **One pull is stronger:** advantage accumulates symmetrically, starting at zero. A sprinting player pulling away from an
  idle player takes the bag after roughly a second including smoothing/grace, whichever player grabbed first.
- **Similar opposing pulls:** strain builds instead of awarding possession. Equal running tears an ordinary bag in about
  2 s of sustained effort (heavy about 2.5 s); equal walking takes about 3.2 s (heavy 4 s), plus initial grace/smoothing.
  Bag stretch, shake and faster rustling warn in-world. Easing off dissipates strain at 0.6/s. No meter, beat prompts, mash,
  automatic loser stagger, or arbitrary "masher" fine.
- **G lets go:** the other person keeps the bag. If both let go it drops. Release one grip before using ordinary single-holder
  toss/delivery controls. A non-bag carryable under maximum strain drops safely rather than being destroyed.
- **Network/edge rules:** movement intent at 15 Hz, participant/sender/sequence/finite-vector validation, host rules at 20 Hz.
  Stale intent pauses judgement after 0.65 s; 2 s without updates releases that grip. Disconnect/invalid body releases it;
  both invalid or separation beyond 5 m drops safely. Sharing occupies both participants' hands, blocking extra pickup,
  catch and grabber use. Starting another grip is not a way to hold multiple bags.

| Situation | Result |
|---|---|
| Both walking/running together, even at different speeds | keep carrying; no strain |
| Both stationary, or moving toward one another | keep holding; no contest timer |
| One runs away, the other stands still | runner keeps the bag |
| Opposing run vs walk | runner wins |
| Similar sustained opposing effort | bag rips and spills |
| Stop pulling before it tears | strain relaxes; keep both grips |
| Either player presses G | other player keeps it; both G drops it |

#### What comes out when a bag bursts

- The host opens the original bag contents once through `TrashBag.NetBurstAt` and publishes the spilled loose items.
  Guests use those authoritative contents, never a fresh local roll. `FlagBurst | FlagRip` marks the original bag gone.
- The previous valuables/armful aggregation proposal is not the current implementation. Conservation is checked by exact item
  UID in the `sharedgrip` two-peer regression brain. `SharedGripCheck.Run()` covers deterministic movement/strain rules in Editor.

#### Verification — 4 Oct 2026

- Unity Editor compilation and `Tools/coop_lint.sh` pass; `SharedGripCheck.Run()` passes 82 deterministic checks.
- Two actual Editor peers using `crew_bad_wifi`: all seven core scenarios pass (heavy walk/run with either initial holder,
  both takeover directions, exact three-item rip conservation, strain recovery, voluntary handoff). Visual inspection led
  to the shorter arm-reach leash and a near-hand grip for side-by-side carrying rather than twisting both arms behind the body.
- A focused `sharedgrip-edges` run passes all four additional scenarios on both peers: both release, large teleport,
  extra pickup rejected locally and on the host, and a knocked-down participant leaving the other player holding the bag.
  Earlier combined-run failures were fixture failures (guest remained at the previous position and was correctly rejected
  as too far away); the harness now settles setup teleports, handles guest clock corrections monotonically, and supports
  the focused rerun. These are automated Editor checks, not a completed human feel/Internet/Windows playtest.
- No player build generated. The live Editor used a temporary empty test save root, removed after testing with real-save
  integrity checked. Entry points: `SharedGripEditorProbe.Guest` in the isolated project copy; `-gripedges` selects the four
  focused scenarios. Both peers must run matching source/builds; pre-change executables retain the old mechanic.

#### Turf in Rival Towns: League Night

Turf lasts **one session**, and nobody can lose turf while offline:

- **The deal:** at each dawn, or when a Rival session starts, customer houses are dealt equally to the companies present, round-robin
  by district. Customers show a mailbox flag in their company's colour. **At most 60% of houses are dealt; at least 40% stay open
  curbs** (grey flag, free for all).
- **Arriving mid-shift:** a company gets a share dealt from the open curbs. **Leaving:** its customers return to open curbs, and its
  progress is safe in its owner's account.
- **Traffic cone:** a truck that stops (0.5 m/s or slower) within 8 m of an open curb drops a cone that claims it for 60 s, or until the
  truck is 15 m away. One cone per truck. You can't be sniped at the stop you're working, and nobody can lock down a whole street.
- **Poach vs Rescue:** the rule that makes rivalry clean the city.

| You take a bag from… | Name | Pay | Side effects |
|---|---|---|---|
| your own customer, an open curb, or your cone | normal | 100% on the lateness curve + $2 curb-clean bonus | none |
| a rival's customer or cone, **on time** (≤ 4 in-game hours = 3 min 20 s real time) | **POACH** | **50%**, no bonus | −2 Neighbor Rating; the rival gets "POACHED by Dot!"; you're **outlined in their colour and 15% slower** while carrying it, and anyone can shove it loose |
| a rival's customer, **late** | **RESCUE** | 100% of the late fee + bonus | +Trust; **3 rescues at one house in one shift flip it to your colour for the rest of the shift** ("Customer stolen!") |

- **League Night scoring** (Town Board): most on-time bags, cleanest company (fewest litter points), most rescues, best find. Winners
  get the gold sign until dawn and a stamp on every member's Crew Card. **Neighbor Rating** earned or lost goes home with each company.

#### What can never be stolen

- Hopper contents (a data list, not objects), piles, tables, bins, the KEEP crate, company inventory, parked trucks.
- **Items thrown into another company's bay** poof back to the thrower's feet ("Returned to sender").
- **The only spill:** a truck hitting the static world above 8 m/s spills 1–2 bags. That's your own reckless driving. Rival trucks
  are ghosts to each other, so ramming gains nothing.
- **Sabotage (Rival preset only): the Raccoon Crate.** $60 from your personal Pocket. It releases 2 raccoons that don't bite and don't
  carry rabies, and they rummage a rival customer's bags. Once per company per night, at least 30 m from any depot. The crate has your
  company's name on it, a rival who carries it back to your depot gets +10 Rep, and using it costs −3 Neighbor Rating.
- **Never in the game:** stealing from depots or trucks, trapping players, damaging trucks, paid stealing. (Grow a Garden's paid
  stealing made players quit.)

#### Shared goals across rivals

- **City Cleanliness is town-wide as a display, but litter is billed per company.** Each company has a **litter tab** (rips, bursts,
  street spills). The midnight Clean Streets bonus pays each company according to the town meter minus its own tab, so one
  griefer can't shrink everyone's bonus.
- **Pile Goblin evictions:** everyone within 20 m who cleared at least one bag gets the full bonus (Guild Wars 2 style).
- **The Goblin Crown:** the evicted goblin drops one Crown, worth $150 on ebag or +50 Rep on your trophy shelf. Its holder is 20% slower,
  outlined, pinged on the map, and loses Safety Vest protection. **Anyone can knock it loose, but not from the same holder more than
  once every 10 s.** You bank it by carrying it into your own bay. The goblin fight is co-op; the crown run is every company for itself.

### 4.7 Money: add, never divide

| Bucket | Stored in | Earned by | Spent on |
|---|---|---|---|
| **Company Cash** | the company record (owner's account), in integer cents | 100% of pickup pay, recycling, compactor scrap, ebag sales, the Clean Streets bonus; from M5, a visiting owner's **Moonlighting** wages (capped, `Accounts.md` §7.3) | truck, depot, Magnus, permits, hutch kibble, fines |
| **Pocket** (personal) | your Crew Card (account-wide) | **wages created on top** (below) when you join as "Just me"; finder's fees; Sticky Fingers sales | rabies shots, repellent, headlamp, Raccoon Crate, emotes, outfits. **It can never be moved into company cash**, so an edited Crew Card can't buy a Town anything. |
| **Crew Card record** | your Crew Card | nothing; it's a record | cosmetics, titles, stamps, lifetime stats, Career level |

**Wages: 15% created on top, split by who did the work, so hopper-campers don't take everything:**

| Company earning | Wage split (percent of the pay) |
|---|---|
| Pickup pay for a bag | **8%** to whoever lifted it from the curb · **4%** to whoever put it in the hopper · **3%** to the driver of the truck it rode home in |
| Sort pay (bins, compactor) | **15%** to the sorter |
| ebag sale of a find | **10% finder's fee** to whoever lifted that bag from the curb ("Picked up by Dot"). The sorter who opened it gets a "Revealed by" Trash-dex stamp. |

- One person doing several steps gets several shares. Anyone idle for 90 s or more earns no wages.

**Supply scales with the crew, prices never do:**
- **Bags per day** = base × (1 + 0.4 × (online members − 1)). Four friends get 2.2× the bags.
- **Spawn rate scales too**, not just the day's total, so a fast crew doesn't clear the street and then wait.
- In Rival Towns every company after the first brings 85% of its budget, so the street runs about 15% short on purpose.
- Curb records are capped at min(60 × companies, 200).

**Progression pacing (a rule for Progression.md):**
- Prices never scale with player count. Adding a friend never makes anything cost more.
- **District permits require Business Rank, and Rank comes from "Clean Days".** A day counts as clean when the company collected a
  **percentage** of its customers' bags on time. Because it's a percentage, it doesn't care about crew size.
- So a 4-person crew earns cash ~2× faster and buys upgrades sooner (fun), but can't blow through districts faster than about one
  rank per in-game day. Solo isn't a slog, and crews don't break the curve.

**Everything else:**
- **Finds:** bag contents are rolled by the host (§6.3), so nobody gets a personal roll. Every screen in the depot shows
  "DOT PICKED UP: Grandma's Dentures (Epic)!"
- **Sticky Fingers** (Crew preset): once per shift, whoever reveals a find can pocket it as a personal ebag listing. It **always**
  appears on the printed Load Receipt under UNACCOUNTED ITEMS. Betrayal is allowed; secrecy isn't.
- **Load Receipt:** a paper prop printed at every TIP IT, showing bags per player, relays, poaches and rescues, rips (with who paid),
  falls off the truck, bonks, packs, unaccounted items and wages.
- **Shift Report** at 7 AM: MVB (Most Valuable Binman), Litterbug, Frequent Flyer, Butterfingers. The MVB wears a paper crown the next
  day. The shame awards are made to be screenshotted.

### 4.8 Mixed progression

**Principle:** your shop is yours, and in someone else's shop you use their gear.

| Situation | Rule |
|---|---|
| A veteran joins a rookie's Crew Town | plays with the rookie company's upgrades, and shows their own rank title and cosmetics. **Mentor:** Career XP ×1.25, plus a Mentor stamp when the rookie hits a milestone. |
| A rookie joins a veteran's late-game Crew Town | rides the Golden Truck and sees Billionaire Bluffs. **Ride-Along:** Career XP ×1.5, plus Trash-dex stamps for finds they reveal. Wages go to their Pocket, or to their own company with Moonlighting (M5), capped at their own tier's pace; the company's money stays in the Boss's company. |
| Established and new companies in one Rival Town ("Bring your company") | **Underdog:** the company with the lower Business Rank gets a customer deal ×1.5 and **Local Hire** (+25% on open curbs in the lowest district), plus the Small Business Grant (§3.4) if it was founded here. A veteran's side-loader arm never grabs a rival's on-time bags. |
| Rival Town, "Fresh Start League" | everyone starts equal; only stamps and stats go home |
| District gates | **Permits belong to the company.** A gate's collider exists only on the **driver's** machine when that company lacks the permit (worked out from synced company state, with no extra messages). The host's hopper pays District 1 rates for unlicensed bags from Rival overflow districts (§3.7). Anyone can walk in on foot, and riding in a permitted truck earns normally. |

**Career level** is portable and purely cosmetic (outfits, emotes, titles), capped at one level per real hour. **Business Rank**
lives in the company record.

### 4.9 Sorting together

- **Two first-person stations per table.** Each station has a host-owned "occupant" value, claimed by request. This replaces the
  racy 0.25 s proximity scan in `SortStation.cs:387-405`. Other players see the sorter at the table; the sorter's body is hidden only
  on their own screen.
- **Station rotation:** a station hands over automatically after 8 bags if someone is waiting, or after 30 s of no activity.
- **"Next bag!" (R)** asks the host, which serves a specific pile record to that station and **sends its contents** at that moment. Two
  sorters can never pull the same bag. *(Superseded 2026-10-08: the "next bag" key was removed. Sorters carry bags to the table,
  or grab them from the Bag Belt's queue rack; every grab is the ordinary host-confirmed pickup, so two sorters still can't
  take the same bag. With `sorting_table` L2 a table has two sorting positions: `Progression.md` §6.2.)*
- **Table items belong to the sorter** (at most 40, as today), so sorting has no lag and friends can watch.
- **Bins and compactor:** the sorter's machine detects the drop, and the host checks the item came from a bag opened at that station by
  that player, then pays.
- **Line Combo** (not built; the personal streak is now the x1.5..x10 `SortLadder`, 3.0 s down to 1.8 s between sorts, Progression.md §3.6; reconcile before building this): correct sorts at *either* station within 2.5 s of each other chain together. Payout = max(personal combo, line combo),
  still capped at ×2. Co-op makes the cap easier to hold, not higher.
- **Blame on the receipt:** "Contaminated by Ozzie", or a hazard in the compactor ($10 fine) logged against whoever slammed it.
- **HitStop and slow-mo become local camera effects.** Today `Juice.HitStop` changes the global `Time.timeScale` from ~10 call sites,
  and on a host that would freeze physics for everyone.
- **Laptop:** the UI stays per player; every button sends a request to the host. ebag offers are simulated only on the host, and
  listings sync as a list. If two players buy the last upgrade at the same moment, exactly one succeeds.

### 4.10 Night shift together

- **Critters chase the nearest player on foot**, not just `PlayerController.Local` (`CritterWorld.cs:261-286`, `Critter.cs:97-104`).
- **Critter count** × (1 + 0.25 × (players − 1)), at most 8 in a Crew Town and 12 in a Rival Town.
- **Critters can't enter depot plots** (excluded from the navmesh, plus a leash radius), so nobody can lead rabid raccoons into a
  rival's bay.
- **LATCH:** a rabid raccoon clamps onto a leg for up to 4 s, slowing the victim 40%. Any crewmate frees them instantly with a bonk,
  a shove, or a honk within 12 m. Otherwise the normal 50% infection roll happens. "Stick together at night" becomes a real rule.
- **Rabies is per player:** a host-written infection time on each player, replacing the single global in `GameSession`.
- **Rabies Tag** (Crew preset): infected players with symptoms get **CHOMP** (1.5 m range, 10 s cooldown, 50% infection chance).
  **The biter pays** $35 from their Pocket for the victim's shot. A growl filter goes on the infected player's voice.
- **Passing out:** you lie where you fell for up to 45 s. Your crew can revive you three ways: a syringe shot; **Hopper Express**
  (toss you in the hopper, drive home and TIP IT, $50); or a **Compactor Reboot** ($40). If nobody helps, you wake in your depot with
  the $100 hospital bill.

### 4.11 Anti-grief toolkit

| Tool | Rule |
|---|---|
| **Safe zone** | your bay, your interior, and a 12 m apron. Other companies can't shove, bonk, snatch, tug, chomp or carry there. Items thrown in are returned to sender. Visiting and chess are fine. |
| **Safety Vest** | put it on at your locker (60 s cooldown). You can't toggle it while holding contested loot (GTA's passive-mode lesson). While worn: immune to cross-company shoves, bonks, snatches and heave-window tugs, and you can't do any of those either. **It costs something:** −10% pay, no poaching, and no customer steals. Holding the Crown removes it for 60 s. |
| **Block = Ghost** (per pair of players, no backend needed) | the host refuses every interaction between the two, their voice to each other is muted, they see each other 40% transparent, and their throws pass through each other. Stored in the blocker's Crew Card, so it applies in every Town. |
| **Helpers by default** | newcomers can't drive, spend or list until the founder promotes them (§3.6) |
| **Bay ban** | the founder bans a player from the bay and truck: a wall only they bump into |
| **Kick / votekick** | the host can always kick. **Votekick (public Towns only):** passes with 60% or more of other players and at least 3 yes votes. 5-minute cooldown for whoever starts it. The kicked player is locked out of that Town for 30 minutes. The host can't be vote-kicked. |
| **Rate limits (host-enforced)** | grab 5/s · shove once per 3 s · horn 4/s · bang 3/s · chomp once per 10 s · tug pulls 1 per beat · purchases 2/s |
| **AFK** | 90 s releases a seat or station; 5 minutes in a public Town sends you to the menu |
| **Litter tab** | rips, bursts on the street ($2 in public Towns) and spills are billed to the company that caused them (§4.6) |
| **Ghosted rival trucks** | trucks from other companies don't collide with yours in Rival or Open presets; they're 40% transparent while overlapping, as in racing games. You can't wall off a road or a rival's door. |
| **Tow truck** (Rival and Open) | a non-crew truck that spends 45 s **in total within 3 minutes** inside someone else's apron is towed home, and a $25 fee is paid to the blocked company. It never applies on public roads. (Another clip.) |
| **Voice** | proximity voice up to 32 m, plus a crew radio. **Public Towns: crew radio only.** Strangers get pings, quick-chat and an emote wheel. Per-player mute everywhere. |
| **Names in public Towns** | company and Town names come from a **word picker** (Adjective + Noun + "Waste Co."), with preset logos only. Free text stays in friend Towns. |
| **Report** | Steam's own block and report, host kick and Block = Ghost. No custom moderation backend (nobody would read it). |

### 4.12 Latency fairness

1. **The heave window** on contested grabs: 250 ms of host time, only when someone is near.
2. **Contests last seconds:** tugs (~1.8 s) and the 1.5 s arming of PACK, each with an escape window.
3. **Your own actions run on your machine:** body, throw and truck feel instant at any ping.
4. **Instant local animation, with slapstick when the host says no:** "slipped off!", "YOINK!", the snatch flinch.
5. **The victim moves themselves:** nobody sees their own body teleported by another machine.
6. **Lag compensation for everything checked against something moving.** The host keeps a 1-second pose history for every truck and
   player (cheap at 8 players and 4 trucks). Claims carry the press tick and the claimant's own position. The host checks them against
   poses rewound to `press tick − the claimant's interpolation delay`, plus a small slack of speed × 0.1 s.
   **Gate:** at 4 m/s and 150 ms, guests catch the truck within 5 percentage points of the host's success rate.
7. **The tug beat is judged locally**, against a beat every machine derives from server time.

**What's left:** the host keeps a small edge in instant reflex verbs like shoves. It's capped by cooldowns and immunity, not removed.
We deliberately don't add artificial delay to the host's inputs.

### 4.13 Clips these rules produce

| Moment | Why it lands |
|---|---|
| **"WAAAAH!"** The driver clips a tree at 10 m/s, both rear riders fly into a hedge, and one bag still sails into the hopper. | The driver *chose* it: corners and stops never throw anyone, only a hit does. |
| **Catch the Truck:** a sprint and dive for the step at 5 m/s ("RISKY"), then a faceplant. | victory-or-faceplant |
| **Bang-Bang-BRAKE:** a rider signals stop, wasn't holding on, and gets launched. | a ready-made argument |
| **Bucket Brigade:** curb → hand → hand → hopper. "RELAY ×4!" | a satisfying team combo (and it works at any ping, because PASS is scripted) |
| **The Perfect Tug:** four beats in rhythm, the thief staggers back. | skill, not spam |
| **Diaper Rain:** a greedy masher rips a 10 kg bag, the receipt says "LITTERBUG: Ozzie (paid $14)", and everyone dives for the one Rolex. | the fight both sides lose, and one person pays for |
| **Wrong Truck!** Your throw is intercepted and lands in a rival's hopper, and they get paid. | an instant rivalry meme |
| **HITCHHIKER!** You were late for ROLL OUT and arrive tumbling out of the hopper. | the ritual has a punchline |
| **Hopper Hitchhiker:** "PACKER ARMED"… too slow, pancake. | slapstick with consent and a countdown |
| **Compactor Reboot:** a passed-out friend comes back as a cube. "Revived. $40." | the absurd rescue is also the cheap one |
| **Rabies Tag:** a growly Ozzie chomps Dot, and the receipt says "Ozzie bit Dot (paid $35)". | zombie tag with accountability |
| **Crown Heist:** three companies evict the goblin together, then chase the Crown across town. | co-op turns into rivalry in one beat |
| **Receipt Betrayal:** "UNACCOUNTED ITEMS: Bert, wedding ring". | friend drama on a paper prop |
| **Customer Stolen!** A third late rescue flips the mailbox flag to your colour. | petty competition that also cleans the city |
| **Drone Drop:** Dot's whole depot gets dropped onto your Depot Row as she joins. | "my shop came with me" |

---

## 5. Authority and network

### 5.1 Every system

| System | State lives in | Decides | How it's synced | Client sends | Host checks | Bandwidth |
|---|---|---|---|---|---|---|
| Session, slots, identity | `SessionService` (host) + `NetRoster` | H | connection approval (`Pending` while Steam verifies); roster list for slot, company, cosmetics | `JoinRequest` ≤1,200 B | protocol, host-bound Steam ticket, lobby membership, space, bans; release builds reject `dev:` keys | once |
| Town rules preset | `WorldState` | H | `NetworkVariable<TownRules>` (4 B) | host UI only | — | ~0 |
| Clock and day | `WorldState` | H | NGO server time + offset (double) + base day (int) | — | — | 0 |
| Cleanliness, litter tabs, Neighbor Rating | `WorldState` / `CompanyState` | H | NetworkVariables | — | — | ~0 |
| Curb records | `WorldState` `NetworkList<CurbBagNet>` (id, house, slot, putOutAt, flags; **no contents**) | H | list deltas | — | — | ≤10 KB on join |
| Turf deal, Trust, cones | `WorldState` | H | NetworkList | — | bag age, rescue count | ~0 |
| Bag contents (manifest) | host only, until opened or burst | H rolls (integer weights) | ~8 B per item, sent when a bag is opened at a station or bursts | — | — | tiny |
| Pile records | `CompanyState` NetworkList | H | list deltas | "Next bag" | station occupant | bytes |
| Bag or item at rest | a record, or a pooled host-owned object | H | nothing while asleep | `RequestGrab(id, tick)` | range 2.2 + 0.5 m, empty hands, rate, facing, `Permits()`, heave window | ~60 B spawn |
| Held item | `NetCarryable.holder` | holder (O) | holder ID only; posed at the hand on each machine | drop, throw, `Pass(target)` | 8 m pass range, aim cone | 0 while held |
| Thrown item | owner-synced position (half precision, 30 Hz while moving) | thrower (O) | position sync; host takes it back after 0.5 s asleep | — | speed ≤ 15 m/s | ~0.4 KB/s each |
| Catch | holder change | thrower detects, H confirms | holder change | `CatchClaim(item, catcher, tick)` | still thrown, catcher's hands empty, ≤1.6 m in history | event |
| Tug | `TugState` NetworkVariable | H referees | ≈12 B per beat, only during tugs | `Pull(beatIndex, good)` | one per beat, not in the future, ≤350 ms late, within 2 m | tiny |
| Bonk | none | thrower claims, H decides, victim applies | `Knock` message to the victim | `BonkClaim(victim, item, speed, tick)` | owned, ≤1.5 m in history, ≤15 m/s, immunity, `Permits()` | event |
| Shove | none | H; the victim applies | `Knock` to the victim | `Shove(target)` | ≤2.0 m, 60° cone, cooldowns, `Permits()` | event |
| Player movement and animation | owner-synced position + facing (half precision); `OwnerNetworkAnimator` | O | 30 Hz | — | speed and teleport sanity (logged in friend Towns; snapped back in public Towns) | ~0.6 KB/s each |
| Body state (knocked out, flopped, carried, flattened, immune) | `PlayerNet.state` + `AttachedTo` + `immuneUntil` | H sets; the victim animates | NetworkVariables | `Flop`, `Wriggle` | preset, crew, consent | ~0 |
| Pose history | host memory, 1 s per truck and player | H | none | — | used by every range check | 0 |
| Driving | owner-synced truck position + our own 10 Hz velocity | driver (O) | 30 Hz + velocity for dead reckoning | `ClaimSeat(0, …)` | seat rules; speed ≤ tier max + 15% (public Towns) | ~0.9 KB/s |
| Seats and riders | `TruckSeats` NetworkList | H | list; riders pinned on each machine | `ClaimSeat`, `LeaveSeat` | per-seat speed limit, lag-compensated range, lock, not knocked out | ~0 while seated |
| Riding (stay on, switch spot) | `BodyNet.step` (the hold); 10 Hz pose samples `{tick, speed, pos}` from the truck's simulating machine while anyone rides (lag matching) | H seats and moves riders; every machine pins them on its own copy | ~21 B at 10 Hz per ridden truck | `Hitch(truck, hold)`, `RideSwitch(truck, hold)` | in the boarding area round the back (+1.5 m lag slack), hold free | ~210 B/s |
| Crash fling | the truck's simulating machine detects `{tick, Δv ≥ 5 m/s}` | driver → H → riders; each rider's machine flings at that tick | crash event + `Fling` to everyone | fling report | rides that truck, tick and start plausible | event |
| Horn, lights, radio, bang, shotgun pings | truck NetworkVariables + events | H relays | NetworkVariables + events | `Honk`, `Bang`, `SetLights`, `Ping` | seat, rate | tiny |
| Hopper intake and truck cargo | `TruckCargo` NetworkList of bag IDs + total weight | thrower detects, H confirms | list deltas | `HopperEnter(bag, truck, tick)` | owns the bag; ≤3 + 0.75 m of the truck's rewound pose; capacity; permit; not already counted | bytes |
| Rear-step feed | none | H | scripted 0.45 s arc event | `Feed(bag)` | rider is on a step and holding it | event |
| TIP IT | pile records | H | one ~200 B seeded event; the tumble plays locally (cosmetic) | `TipIt` | own bay, parked (<0.3 m/s), member | event |
| Sort stations, table items | `StationSeat` values; items owned by the sorter | H seats + queue; sorter owns items | NetworkVariables; items synced only while moving | `Claim`, `Next` | occupancy, rotation | ≤40 items |
| Bins, compactor, hazard barrel | `CompanyState` ledger | sorter detects, H confirms and pays | `WorldFx` event | `Sorted(uid, bin)` | the item came from a bag this player opened at this station | event |
| Company wallet, upgrades, permits, inventory | `CompanyState` (copied into the `GameSession` facade) | H | NetworkVariables and lists | `Buy`, `CoSign`, `SetRule` | funds, allowance, co-sign window, rank, founder rules | ~0 |
| Scrapazon, ebag | `CompanyState` / Pocket | H (offers simulated on host) | ledger + NetworkList | `Buy`, `List`, `Accept`, `Decline` | funds, ownership, price band, co-sign | small |
| Pocket, wages, finder's fees | Crew Card (owner's account) | H decides the amount; the owner's Guard checks and records it | `ProfileDelta` batches every 10 s | ack by sequence | idle check | ~0 |
| Host company mirror to guests (M1) | `CompanyState` | H | `CashCents` variable + one gzipped DTO snapshot as a named message, debounced 1 s, ≤16 KB, host-private contents stripped; guests read it through `BoundedGzip` and accept it only from the server | — | — | tiny |
| Company deltas to owner (M5 Moonlighting, M7 brought companies) | the company record in the owner's account | H produces ledger deltas; the owner's Guard applies them | `CompanyDelta` batches, ~0.1–1 KB per 10 s, at once for purchases and sales ≥ $500 | ack by sequence | — | tiny |
| Plots, roles, bans | `WorldState` `NetworkList<PlotState>` + role lists | H | lists | `Found`, `Bring`, `JoinRequest`, `Promote`, `Ban` | free plot, founder rights | ~0 |
| Critters | `NetCritter`: host-synced position (15 Hz, half precision) + kind, seed, state | H runs the AI | position sync; body built on each machine | — | — | ~0.2 KB/s each |
| Rabies | `PlayerNet.infectedAt` (double) | H | NetworkVariable | `Chomp(target)`, `UseShot` | symptomatic, 1.5 m, 10 s cooldown, preset | ~0 |
| Magnus and chess | `ChessController` (one board-state variable from M7) | H | existing | existing | existing | small |
| Voice | behind `IVoiceService` (Vivox first) | voice service | proximity + crew channels | position at 4 Hz | — | not on our transport |
| Floaters, sounds, particles, hit-stop, limb ragdolls, litter debris | local | L | `WorldFx` event (≤16 B) or nothing | — | — | 0 to tiny |
| Saves | host: Town; owner: company and Crew Card (through Cloud Code) | H / owner | — | — | a host never writes anyone else's records; a guest's saves are latched off from Join until their own data is reloaded | — |

### 5.2 Collision matrix (what's allowed to touch what)

The rule: **every machine resolves only collisions it can agree on.** Two networked dynamic objects owned by different machines never
push each other, because on each machine the other one is kinematic and they'd disagree.

| This… | …against this | Result |
|---|---|---|
| **In-flight item** (new `InFlight` layer, simulated by the thrower) | static world | normal physics; **bursts are only ever caused by static-world impacts** (`TrashBag.BurstImpactSpeed = 9`) |
| | trucks | bounces off (the truck is a wall on the thrower's machine and is never pushed by bags) |
| | players, critters | **by rule:** the thrower claims bonk or catch, the host confirms, the victim applies |
| | hopper and bin triggers | the thrower detects, the host confirms |
| | other bags, items, piles, litter | **passes through** |
| **Resting bag or item** (host-owned, asleep) | players, trucks, critters | no physical contact; the host gives it a small **nudge** when something overlaps it (a kick or bump effect) |
| **Carried item** | anything | none (kinematic, attached to the hand) |
| **Player** (CharacterController) | trucks | **one-way, on the player's own machine:** your body is pushed out along the truck's velocity; above ~3 m/s it becomes a local "bonked by truck" stumble. The driver's machine ignores players. No messages needed. |
| | other players | normal character blocking, locally |
| **Truck** | static world | normal |
| | same-company truck (late Fleet unlock) | collide: each sees the other as a kinematic wall and stops; nothing spills |
| | rival truck (Rival and Open presets) | **ghosted:** no collision, 40% transparent while overlapping |

### 5.3 Bandwidth budget (estimates, to be measured at M2, M3 and M7)

Half precision and compressed rotations are turned on in M0. `Player.prefab` and `Truck V2.prefab` both have
`UseHalfFloatPrecision: 0` today.

| Download per client | 4-player Crew Town | 8-player Rival Town |
|---|---|---|
| Other players | 3 × 0.6 = 1.8 KB/s | 7 × 0.6 = 4.2 KB/s |
| Trucks (+ velocity) | 0.9 | 4 × 0.9 = 3.6 |
| Moving items | ~6 × 0.4 = 2.4 | ~10 × 0.4 = 4.0 |
| Critters at night | 6 × 0.2 = 1.2 | 8 × 0.2 = 1.6 |
| Synced values, lists, events | 0.5 | 1.0 |
| Overhead (~10%) | 0.7 | 1.4 |
| **Total per client** | **~7.5 KB/s (~60 kbps)** | **~16 KB/s (~130 kbps)** |
| **Host upload** | **~23 KB/s (~0.18 Mbps)** | **~110 KB/s (~0.9 Mbps)** |

- **Caps:** networked items ≤200 (Crew) / ≤300 (Rival); moving at once ≤24 / ≤36 (thrown items are forced to sleep after 6 s);
  critters ≤8 / ≤12; local litter debris ≤150 per machine (oldest fade out).
- **Spikes handled locally:**
  - TIP IT: one ~200 B event instead of 48 networked bags (~69 KB/s for 5 s).
  - Bursts: only valuables are networked objects; the rest is one litter pile record.
- **NGO has no per-client send rates.** If we ever need more than 8 players on a player host, the fix is to replace NetworkTransform
  for players with one custom batched snapshot message with per-client detail levels (an M-sized job). NetworkObject visibility
  (`NetworkShow`/`NetworkHide`) can hide distant flying items and critters as a coarse interest filter.
- **Host frame budget at 60 fps:** physics ≤3 ms, netcode ≤1.5 ms, gameplay ≤3 ms.

---

## 6. Persistence and cheating

### 6.1 Saved data (summary; `Accounts.md` §4–§5 is the source of truth)

```
<data root>/accounts/<steam_7656… | dev_editor>/          local copy; the cloud (UGS Cloud Save) is the truth from M1.5
  profile.json         CREW CARD  owner writes (cloud: Protected, via Cloud Code)
  companies/<guid>.json COMPANY   owner writes (cloud: Protected, via Cloud Code only)   ×3 slots
  towns/<guid>.json    TOWN       host writes (cloud: Default, host's game directly)      ≤5
  *.gen/               time-tiered generations: latest ×2 · 10 min · session start · 7 dailies · pre-migration
```

- **Town:** meta (name, schema, content version), rules + toggles, ban list, grant recipients, clock (`worldHours`, `day`;
  LivingWorld's "fresh server = 7 AM" applies to **new** Towns only), world (curb records + host-private contents, cleanliness,
  houses, ContentRng state), `plots[4].lastSeen`, `companyPoses`, `players{PlayerKey → last company, slot, rabiesAt, lastSeen}`.
- **Company:** id, name, colour, logo, founder, members + roles, cash in **cents**, rank, Clean Days, permits, upgrades,
  consumables, inventory, ebag listings, pile, truck + cargo, Magnus, Neighbor Rating, timers as **remaining in-game hours**, a
  ledger tail. Portable between Towns by design.
- **Crew Card:** Pocket (cents), cosmetics + loadout, titles, Career level, Trash-dex, trophies, lifetime stats, block list.
- **Sizes:** Town 10–60 KB, company 10–30 KB gzipped. Format: Newtonsoft DTOs in a pure `Rubbish.SaveModel` assembly that also
  compiles into the Cloud Code module; migrations v1 → v2 → v3 are tested on fixtures (including copies of Nick's real files) on
  every build.
- **Import:** the first M1 build imports `rubbish_save.json` + `ebae_save.json` into company slot 1 "Rubbish Co." + Town 1
  "Maple Hollow" + a Crew Card. Nobody loses progress, and the old files are never edited or deleted.
- **The data-loss bugs in today's code** (hotfix L0, §8.7): `GameSession` and `EbaeMarket` set `loaded = true` *before* parsing,
  so a truncated save is overwritten with a blank one on the next quit (`GameSession.cs:498`, `EbaeMarket.cs:599`); the guest
  guard (`GameSession.cs:375`) isn't sticky, so after a host crash or during quit a guest can write the host's world into their own
  save; `EbaeMarket.Save()` (`:575`) has no guard at all; both files are overwritten in place with no backup.
- **Visiting never touches your own records** except, from M5/M7, a company you chose to bring, and only through ledger deltas your
  own game checks. Your Town and every other company must be byte-identical after any co-op session (a test gate).

### 6.2 A brought company, step by step (M7; M5 Moonlighting uses every step except streaming the company, with wage and Rep entries only)

1. **Checkout:** the owner's game asks Cloud Code for a **lease** on the company. That bumps an **epoch** counter, stores
   "on the road in Maple Hollow", and keeps a **pre-visit copy** (7 days). It runs in parallel with the scene fade; if the cloud is
   unreachable the player can join as "Just me" instead. **Joining never waits on the cloud.**
2. **Joining:** the `JoinRequest` carries only `{companyGuid, version, blobHash}`. After approval the owner streams the company to
   the host in 4 KB chunks; the hash must match; the host reads it through bounded decompression, validates the schema and loads it
   onto a plot.
3. **During the session:** the host sends **ledger deltas** (not whole snapshots) every 10 s, and at once for purchases and sales
   ≥ $500. The owner's game applies each entry through **the Guard** (credit caps, purchases must deliver, fine caps per host per day,
   no destructive edits), journals it, saves locally, and commits to the cloud every 5 minutes.
4. **Leaving:** final deltas, then a **Visit Receipt** on the owner's screen (cash change, purchases, finds sold, rating, every
   ledger line; rejected or clamped lines marked). Commit and check in.
5. **Safety net:** "Restore Dot's Disposal from before visiting Maple Hollow" is one click for 7 days, on any PC.
6. **Crashes:** a host crash costs the owner at most the last unsent batch (≤10 s). An owner crash is finished on next launch from
   the journal.
7. **Double-booking is impossible:** a company on the road can't be brought elsewhere or hosted. **Take it back** on another PC
   bumps the epoch, and the old session's commits are refused. A same-PC lock frees itself as soon as its process is gone.

### 6.3 Content rolls: the host rolls, clients never know early

- **Every bag's contents are rolled by the host**, in one `ContentRng` service, with **integer weights and fixed-point values**.
  Contents are sent (~8 B per item) only when a bag is **opened at a station** or **bursts**.
- **This fixes three problems at once:**
  - Floats that differ between Mac and Windows (`Mathf.Pow` and `Mathf.Lerp` at `ItemCatalog.cs:496-503`) can't desync anything.
  - A mod can't highlight the Epic bags on the street.
  - Most of the determinism work goes away.
- **X-Ray Eyes** becomes a host-granted reveal, sent only to that company's members.
- **Item IDs** become `{bagId}#{i}`, assigned by the host, instead of `GameSession.NewUid()` (`ItemCatalog.cs:500`). `dayFound` is
  passed in explicitly (`:506`). Raccoon spills become flags on the record instead of edits to its contents (`Raccoon.cs:278-298`).
- **Only cosmetics are generated locally:** litter debris, the TIP IT tumble and particle seeds.
- **Development builds** run a NetAudit overlay:
  - **ledger balance:** every cash change has a ledger entry;
  - **bag conservation:** curb + loose + held + cargo + pile + consumed = total spawned;
  - **a world-state hash** compared every 10 s.

### 6.4 Trust model (`Accounts.md` §7.7 is the full table)

| Who | Can | Can't |
|---|---|---|
| The host of a Town | anything in their own Town: edit its save, give their own company cash | write any guest's company or Crew Card; credit a brought company beyond the Guard's caps; stop "Restore from before this visit" |
| A guest | send requests | get anything the host doesn't validate (range, cooldowns, one item per hand, rate limits, ownership, co-sign, permits, lag-compensated checks); claim another player's identity (host-bound Steam ticket; release builds reject `dev:` keys) |
| A player editing their local save | change what they see offline | get it into the cloud past Cloud Code (structural cheats rejected, earnings clamped); bring an unsynced company into anyone's Town |
| Someone editing their Crew Card locally | the same | move Pocket money into any company; affect any Town's economy. No leaderboards, so nothing to corrupt. |
| A modded client | local visuals; speed-hacking their own movement (logged in friend Towns, snapped back above tier max + 15% in public ones) | see bag contents early; touch any economy value; forge another player's commits |

- **No kernel anti-cheat.** It hurts friend-slop word of mouth and mod culture, and it can't stop a host editing their own world anyway.
- **No leaderboards.** Steam achievements are fine (they're client-side anyway). **Mods are welcome** (the Lethal Company precedent).
- **Every message from another player is hostile input:** bounded decompression (1 MiB), JSON caps and a schema check before
  applying, and per-client rate and size limits on every named message and RPC (`NetGuard`).

### 6.5 Versioning

- **The protocol version** (netcode messages) blocks joins when it differs. It changes rarely.
- **The content version** may differ: the host's values win, and guests show unknown item IDs as a "Mystery Item" with the host's name
  and price.
- **Branches:** one public branch and one opt-in beta.

---

## 7. Costs (per month, estimates)

**Assumptions:** CCU = average concurrent players, with peak about 2× average. We launch **Steam-only** (Nick's call), and Unity
Relay is used for development and as an off-by-default fallback only.

| Line item | 100 CCU | 1,000 CCU | 10,000 CCU |
|---|---|---|---|
| Steam relay, player-hosted | $0 | $0 | $0 |
| Unity Relay (development only; free tier: 50 CCU) | $0 | $0 | $0 |
| Community Town Servers (run by players) | $0 | $0 | $0 |
| Voice: Vivox (free up to 5,000 peak concurrent users, then ~$2,000 per 5,000) | $0 | $0 | ~$6k, or **$0** by swapping to Steam voice or Dissonance (~€110 one-time) behind `IVoiceService` |
| Steam Cloud (settings only), lobbies, invites, auth tickets | $0 | $0 | $0 |
| **Accounts backend** (UGS Authentication + Cloud Save + Cloud Code; per *monthly* player, 12 h each; `Accounts.md` §2.3) | $0 at 1k MAU | ~$11 at 10k MAU | ~$190 at 100k MAU |
| **Total** | **~$0** | **~$0–11** | **$0–6k + ~$190** |
| *If we ever sell off Steam (itch, Epic) and ~20% of players use Unity Relay* | ~$30 | ~$400 | ~$4k |
| *Official servers (not planned): Gameye / Edgegap / bare metal* | ~$460 / $850 / $130+ | ~$4.6k / $8.5k / $1k+ | ~$46k / $85k / $9.5k+, plus operations work |

- 10,000 average concurrent players for a ~$10 game means roughly a million or more sales, so even the voice line is trivial.
- **Voice dependency:** Vivox needs Unity Authentication sign-in. **Joining a game must never wait on voice signing in.** If Unity
  auth is down, you play without voice rather than failing to join. The same rule holds for cloud saves: joining never waits on the
  save backend.
- **Accounts backend columns are per monthly active player, not concurrent.** The cloud copy is written every 5 minutes while
  something changed (≈ $0.00013 per played hour past the free tier); every 60 s would cost ~5×.
- **Relay must stay a fallback:** if every friends session ran on Unity Relay instead of the Steam relay, ~$2,000 a month at 1,000
  CCU. "Steam relay on by default" is a launch gate (§1.3).

---

## 8. Roadmap from today's code

### 8.1 First moves

- **Now, no game code:** adopt §9 for every system being built in parallel, Progression included. Send the in-flight builders the
  four notes in §8.7.9. Start wave 0 (lint upgrade, test rig, the interfaces-only contract).
- **First game code (hotfix L0, the moment Progression A releases `GameSession.cs` and `EbaeMarket.cs`):** a load-failure guard,
  a sticky "these statics aren't mine" latch for guests, a redirectable data root, and atomic writes with time-tiered backups.
  It removes today's worst hazard: a destroyed save. **Until L0 lands, don't co-op on real saves.**
- **Then M0 and M1,** packaged for parallel builders in §8.7, after the in-flight content workflows land (Nick's "go for it").

### 8.2 Milestones

> **2 Oct 2026:** the co-op model changed (Nick): every player runs their own business next door on Depot Row, players can hire
> each other, and everyone roams freely. **§13 supersedes the Crew, Moonlighting and Rival Towns parts below and re-orders the
> build (N1–N5).** M0 below is done; the rest stays as reference for the netcode details.

Sizes, for one developer working with agents: **S** = 2–4 days, **M** = 1–2 weeks, **L** = 2–4 weeks. Every gate runs on the "Crew Bad
Wi-Fi" network-simulator preset (150 ms round trip, 20 ms jitter, 2% loss) with 2–4 players, unless noted.

**Crew Up Lite (M0–M4): shared shift, carry/throw/pass, the truck as a stage, voice.** It deliberately leaves out tugs and rips,
co-sign, Line Combo, PACK, Compactor Reboot, Rabies Tag, Latch, the Crown, Sticky Fingers, receipts and the full Crew Card screen.

| # | Milestone | Size | What changes in the codebase | Test gate |
|---|---|---|---|---|
| **M0** | Netcode hygiene, identity, safe local saves + test harness | M (with L0 and wave 0) | **Executable contract: §8.7 (hotfix L0, waves W0–W2).** In short: L0 save safety; connection approval with `Pending`, slots by PlayerKey, protocol vs content version; transports behind `INetTransportAdapter` (LocalDirect solo, Relay = players − 1); `PlayerInput` off on non-owners; prefab hygiene via `Setup_Coop`; Steamworks.NET + identity rules; `ISaveStore`/`IAccountBackend` with local files and leases; headless bots + NetAudit. | §8.7.7 M0 gate: host and join, both move; 10-minute bot run with 0 errors; rejoin keeps slot and colour; **guest files byte-identical after a normal leave, a host crash, a quit while connected and "leave → play solo"**; a corrupt save survives a launch and quit untouched; identity doesn't change with Steam's state. |
| **M1** | Shared shift + save slots | M–L (+~1 wk for slots) | **Executable contract: §8.7 (C2, waves W3–W5).** In short: `GameSession` guest guards + cents + the one-way mirror; `WorldState` (curbs without contents, clock offset + base day) and `CompanyState` (cash, snapshot, ledger) with `WorldFx`; Town/Company/Profile stores on a DTO save model, migrations and the legacy import; Continue / Change… / Invite menu with company cards and Restore…; ROLL OUT / CLOCK OUT; Steam lobby, invites, Join Game and the Steam ticket check at the door; `ContentRng`; laptop requests; the Steam relay transport spike. | §8.7.7 M1 gate: 4 bots for 30 minutes on Crew Bad Wi-Fi with 0 hash mismatches and balanced ledgers (in cents); killing the host loses ≤60 s; any company loads into any Town with timers intact; legacy and maximal fixtures import exactly; a hostile snapshot can't crash a guest; a guest can ROLL OUT, CLOCK OUT and buy from the laptop. |
| **M1.5** | Accounts and cloud saves | M (7–10 days) | UGS Steam sign-in; the `RubbishVault` Cloud Code module (C#, shares the save model) with content tables per content version; `UgsAccountBackend` passing the local backend's contract tests; cloud lease + epoch, Take it back, conflict card, sync badges; Download / Delete my data and the account-deleted trigger; development and production environments. Can overlap M2 (different files). **Blocked on Nick's Steamworks and Unity dashboard steps** (`Accounts.md` §14, steps 9–15). Package list written at the M1 gate. | `Accounts.md` §13: warm sign-in < 3 s; joining unaffected by a cold start; the same company on two PCs shows "on the road" and Take it back works; host kill loses ≤60 s local / ≤5 min cloud; forged commits rejected or clamped; unknown content version clamped, not rejected; Delete my data leaves 0 keys. |
| **M2** | Grab & Yeet Lite | L | • `NetCarryable` / `NetTrashBag` / `NetLooseItem`, pooled.<br>• Grab with heave window (tie → earlier stamped press); ownership-following throws; `InFlight` layer + collision matrix.<br>• Thrower detects hopper, catch and bonk; the host confirms using pose history.<br>• PASS up to 8 m with aim assist; bonk and shove with `PlayerKnockback` + 5 s immunity.<br>• Litter pile records; `Juice.HitStop` made local (`FeelJuice.cs:167-180` and its call sites).<br>• Files: `TrashCarrier.cs`, `Carry.cs`, `TrashBag.cs`, `CarryReceiverTrigger.cs:49-59`, `TruckHopper.cs`. | The reach animation starts on the same frame as the key press.<br>200 scripted simultaneous grabs (including pairs 100 ms apart) end with exactly one holder; **the host wins ≤55%**; **no thrown bag ever reverts**.<br>No double pay; bag count conserved over a 20-minute, 4-bot run.<br>**Feel test at 150 ms: if it fails, run a 1-week Fusion Host Mode spike.**<br>**→ First fun internet session (~wk 7).** |
| **M3** | Truck Is the Stage | L | • Seat list + local pinning, replacing `driverClientId` and the parenting at `TruckControllerV2.cs:207` (`:39-43,73-82,605-626`).<br>• Grip with tick-stamped `RideStress`; fling; bail-out; 10 Hz velocity sync + dead reckoning; rider camera damping.<br>• **Brake torque to ~1 g.**<br>• Lag-compensated `ClaimSeat` / `HopperEnter` / `Feed`; one-way player-truck collision.<br>• Networked horn, lights, radio, BANG, shotgun pings (`:482-495,559-563,633-667`); `TruckCargo`; seeded TIP IT event (`TruckUnloader.cs:157-200`).<br>• Visible rear-step riders (`PlayerController.cs:286-291`). | 4 players complete a full route.<br>Riders drift ≤2 cm on every screen; flings seen everywhere within 250 ms, and **never before the corner on the rider's screen**.<br>**Guests catch the truck within 5 percentage points of the host at 4 m/s.**<br>Driver disconnects at 10 m/s: the truck coasts to a stop, cargo intact.<br>A driver swap at rest moves the truck less than 10 cm. |
| **M4** | Voice + Steam polish = **CREW UP LITE** | M | • `IVoiceService` over Vivox: proximity + crew radio, muffled cab, mouths that flap with voice (the Vivox spike can start after M1).<br>• Steam relay transport **on by default** (built in M1 wave 4; on once the two-device soak passes); rich-presence text (needs our own App ID).<br>• A minimal Crew Card (stats via `ProfileDelta`, Pocket). | **External playtest:** 4 people, 2 regions, 60 minutes, 0 disconnects, clear voice, at least 4 of 5 "would invite a friend".<br>**Then ship Crew Up Lite to friends / playtest branch.** |

**Crew Up full kit (M5–M6):** ship in batches, each gated by a playtest. Cut any verb that doesn't produce clips.

| # | Milestone | Size | What changes | Test gate |
|---|---|---|---|---|
| **M5** | Sort & Squabble | L | • Station seats + rotation; the host picks the next bag and sends its contents; sorter-owned table items; the host confirms bins and compactor; Magnus steps back for humans.<br>• **Rhythm tug** (Nick: "whatever you think would be more fun") + rip + rip billing; catch polish; RELAY.<br>• Wages split (8/4/3, 15%, finder's fee); daily allowance + co-sign; Line Combo.<br>• **Moonlighting:** the Bring / Just me picker; `CompanyDelta`/`ProfileDelta` to owners; the Guard (wage, Rep, stats kinds) with the 1.5×-own-tier cap; the owner's journal (`Accounts.md` §3.4, §7.3).<br>• Load Receipt + Shift Report; `SortStation.Current` removed. | Two sorters for 10 minutes: no bag pulled twice, ledger balances.<br>Two players buy the last upgrade at once: exactly one succeeds.<br>Tug playtest: a masher never wins; at least 70% of testers say tugs feel fair.<br>Moonlighting: a rookie working a Bluffs host's shift for an hour gains ≤1.5× their own tier's typical income and 0 Clean Days; a hostile-host bot's oversize credits are ignored and shown on the receipt. |
| **M6** | Night Shift Together + spicy toggles | M | • `NetCritter`; nearest-player targeting; critters kept out of depots; Latch; per-player rabies (`Raccoon.cs:397-405`, `RabiesStatus.cs:369-376`); the three revives; Goblin Crown.<br>• **Then, last:** Rabies Tag, PACK, Compactor Reboot, Sticky Fingers. | A raccoon chases and bites a guest; the rabies timer survives a reconnect.<br>8 critters cost ≤1.5 KB/s.<br>Verify the punch-combo knockdown (3.5 s), hit immunity and carried escape with a guest. |

**Depot Row, then Community servers (M7–M8):**

| # | Milestone | Size | What changes | Test gate |
|---|---|---|---|---|
| **M7** | **Depot Row / Rival Towns** | L+ (4–6 wk) | • Remove single-truck and single-depot assumptions (~20 sites: `TruckRequester.cs:6-19,77-82`, `FlowUtil.FindTruck`, `CritterWorld.Truck`, `TruckUnloader:64`, `Compactor:250,631`, `SortStation.Current`, `TrashRoute.Current`, …).<br>• Neighborhood stays loaded + one additive `DepotRow` scene with 4 edit-time pocket interiors (`Setup_DepotRow`); street facades; fade-and-teleport doors; per-machine interior lighting; zone triggers replace scene-name checks (`CritterWorld.cs:54`, `FeelAmbience.cs:21-24`, `TruckRequester:61`).<br>• **Bring your company** (cloud checkout lease + epoch, blob streamed after approval, ledger deltas through the full Guard, Visit Receipt, restore-before-visit, `TransferCompany`) + Fresh Start League.<br>• Session turf deal, cones, poach/rescue, League Night board, Big Job.<br>• Safe zones, Safety Vest, ghosted rival trucks, apron tow; Underdog + grant; per-company permits, driver-only gates, Rival overflow district; Raccoon Crate; litter tabs.<br>• Chess collapsed to one board-state variable; 8-player cap; company tint. | 4 companies and 8 players (bots allowed) for 30 minutes: no crosstalk between trucks, ledgers balance.<br>**Host at 60 fps or better, host upload ≤1 Mbps.**<br>Brought companies round-trip exactly (only Guard-accepted ledger changes).<br>A founder who crashes mid-session loses ≤10 s (journal).<br>Hostile-host bot (set cash, downgrade, oversize credit, unpaired item removal): the owner's company changes only by accepted entries. |
| **M8** | **Community Town Server** | M–L (3–4 wk) | • Dedicated-server build target + Multiplayer Roles stripping; server mode with no host player (rule 8).<br>• Unity Transport UDP + Steam game-server listing; a server config file (rules, admins, bans, resident companies; optionally accept **cloud-consistent** travelling companies via `VerifyCheckout`).<br>• Real-time frozen customers + 14-day dormancy.<br>• Custom snapshot message if 12–16 players need it. | 72-hour soak with no leaks.<br>12–16 bots within budget on a $5–10 VPS.<br>A server restart loses ≤60 s. |
| M9 | Public listing (conditional) | M | • Steam lobby list + filters; word-picker names; crew-only voice for strangers; votekick; AFK kick; push-to-talk; Safety Vest on by default; session-only companies. | **Only** once more than ~500 concurrent players have been sustained for 1 month. Griefing playtest: every abuse has a counter. |

### 8.3 Timeline

These are focused weeks from "go". They compete with Progression and content work, so calendar time will run longer.

| Weeks | Milestone | What you get |
|---|---|---|
| 0 | L0 + wave 0 | saves can't be destroyed; lint, test rig and contract ready |
| 1–2 | M0 | friends can join and move; identity and slots by PlayerKey; bots that never touch real saves |
| 3–5 | M1 | same curbs, wallet and clock; ROLL OUT together; **save slots: any company in any Town**; Steam invites |
| (parallel) | M1.5 | **Steam-linked accounts, cloud saves**, Take it back, Download/Delete my data, once Nick's accounts exist |
| 6–7 | M2 | **first fun internet session:** clean a street together, throw, pass, bonk |
| 8–10 | M3 | ride the back of the truck; the fling clips |
| 10–11 | M4 | **CREW UP LITE ships** (voice + Steam) |
| 12–15 | M5 | tug-of-war, sorting together, receipts, wages, **Moonlighting** (your picked save earns in friends' Towns) |
| 16–18 | M6 | night shift together; spicy toggles |
| 19–24 | M7 | **DEPOT ROW:** rival companies, your shop appears in your friends' Towns |
| 25–28 | M8 | Community Town Servers (always-on worlds) |
| later | M9 | public listing, only if the population is there |

### 8.4 Test rig and editor etiquette

- **Don't test in Nick's live editor.** A headless harness runs **development builds** with `-batchmode -nographics -bot N`
  flags, launched N times from a script on the "Crew Bad Wi-Fi" preset, writing NetAudit results to a log.
- **Builds come from a synced copy of the project** (e.g. `rsync` to `~/Dev/Rubbish-build` and batchmode Unity there), so a build
  never touches or locks the Editor Nick is playing in.
- **Multiplayer Play Mode** (2.0.2, already installed) is only for short interactive checks, following the existing rules: check
  `editor_status` first, use the `scratchpad/editor.lock` lock, never interrupt a live play session, compile only after play ends, and
  only one agent runs scene setups at a time. All scene edits go through idempotent `Setup_*` scripts.
- **Agents never touch Nick's saves.** Bots run with `-dataroot <run>/<bot>`; agent play tests in the live Editor switch the data
  root to a scratch folder seeded with *copies* of Nick's files (a per-project, expiring `UserSettings/RubbishDev.json`, never
  EditorPrefs) and switch back in `finally`; a "TEST DATA ROOT" watermark shows while it's on. The bot audit fails if Nick's real save
  folder changes during a run.
- **A local checkpoint** (git or Plastic's `cm`, Nick's choice) is committed at every wave merge and before every `Setup_*` run, so
  a bad agent edit can be undone.
- **Internet playtests** use standalone builds with Relay (development) or Steam (playtest branch). Two-device Steam tests are one
  scripted 45-minute checklist (§8.7.7).

### 8.5 Decision gates

| After | Gate | If it fails |
|---|---|---|
| M2 | Grab, throw and pass feel good at 150 ms | 1-week Photon Fusion Host Mode spike, then decide: keep tuning NGO, or rewrite |
| M4 | External playtest passes | Iterate on Crew Up Lite before any more features |
| M5, M6 | Each batch produces clips in playtests | Cut the verbs that don't |
| M7 | 4 companies + 8 players within budget | Cap Rival Towns at 3 companies |
| M9 | More than ~500 concurrent players sustained for a month | Stay friends + Community servers only |
| Official servers | A hit, a team, and evidence that Community servers can't meet demand | Stay on player hosting |

### 8.6 Documentation changes (once approved)

- **Accounts.md:** written 30 Sep 2026 (identity, backend, save slots, persistence, backups, anti-cheat on saves, user steps).
- **Character.md §14** (lead, at the M0 gate): variant = roster slot within the company written to `ChuckPuppet.variantOverride`,
  plus a company tint; `OwnerClientId % 4` stays only as the offline/preview fallback.
- **LivingWorld.md** (lead, at the M1 gate): the clock and day persist in the Town, and 7 AM applies only to new Towns. Curb records
  carry no contents on clients. Rabies is per player. Critters target the nearest player on foot.
- **CoreLoop.md** (lead, at the M0 gate): the "single-player, local statics" note gets replaced by §9; `GameSession` is partial with
  guest guards and the mirror; `Net/**` and `Save/**` ownership; the pure assemblies; never save Bootstrap.
- **Progression.md** (lead, at the M1 gate, after Progression's build lands): company-scoped progression, district pacing from
  percentage-based Clean Days, prices never scale (§4.7); cash stored as cents; stamps become durations at the Town boundary;
  `dex.*` counters move to the Crew Card; host-only systems check `!GameSession.IsGuest`.

### 8.7 M0 + M1 build contract (executable)

> Approved 30 Sep 2026. This replaces the detail of the M0 and M1 rows in §8.2. It was checked against the code at 20:49 that day
> (after Progression Step 0 landed) and revised for red-team round 2 (`Accounts.md` §16). Nick's ordering: **in-flight content
> workflows land first, then M0, then M1.** Wave 0 touches nothing those workflows are editing, so it can start now. Hotfix L0 edits
> two of Progression A's files, so it starts the moment A hands their persistence regions over.
>
> **Rules for every builder:** follow CoreLoop.md (global namespace; scene and prefab changes only through idempotent `Setup_*`
> scripts; never save Bootstrap). **Edit only the files listed for your package and create new files only in your folders. Re-read
> any shared file immediately before editing it. Never revert someone else's change. No play mode outside the editor lock. Never
> write to Nick's data root** (§8.7.6). Before reporting done: your package's gate, a clean compile, and
> `Tools/coop_lint.sh --files '<your globs>'` (every new hit fixed or marked `// coop:allow(<rule>) <reason>` and listed).

#### 8.7.1 Ground truth from the code (each finding is handled by a package below)

| # | Finding (file:line) | Handled by |
|---|---|---|
| 1 | `Progression/Ledger.cs` landed (20:47). `Ledger.MoveCash` (:72-78) calls `GameSession.AddCash`, then **posts unconditionally**, so a write that's dropped on a guest still posts. `Economy` (`Core/RubbishTypes.cs:135`) is the tuning-constants class. | D1, C2 |
| 2 | `Core/GameSession.cs` (~575 lines) is `public static class`, not partial. Step 0 added `AddRep`, `AddCleanDay`, `GrantKeyToCity`, `SetDistrictTrust`, `SetCounter`, `SetStamp`, depot deliveries and `SaveData` v2. `IsGuest` :375 isn't sticky. `Save` :422 overwrites in place (:446). `SaveSoon` :456 writes on the first change of every frame. `SaveFlusher.OnApplicationQuit` :492 forces a save. **`Load` :496 sets `loaded = true` at :498, before parsing**, so a truncated file is replaced by defaults on quit. `ResetAll(true)` :546 deletes the file unguarded (:555). | L0, C2 |
| 3 | `Ebae/EbaeMarket.cs`: `Save` :575 has **no guest guard** and overwrites in place (:592). `Load` :597 sets `loaded = true` at :599 before parsing. `ResetAll(false)` :644 calls `Save()`; `ResetAll(true)` deletes (:649). `EbaeMarketRunner.cs` ticks offers (:10) and saves on pause and quit (:14-19) on every machine. | L0, C2 |
| 4 | `MainMenuManager.cs` (class `MenuManager`): Join loads the start scene locally **before** `StartClient` (:39-40) and again after (:43). Host passes `maxPlayers: 10` (:32). | L0 (latch call), B1 |
| 5 | `GameStartup.cs`: anonymous sign-in with no `IsSignedIn` guard or try/catch (:41, :55); Relay allocation = 10 (:42; `maxPlayers = 10` at :15). Solo needs Relay, i.e. internet. | B1 |
| 6 | `Bootstrap.unity`: `ConnectionApproval: 0`, `ProtocolVersion: 0`, tick 30, UTP max payload 6144. It may hold Nick's unsaved changes. | `NetBoot` (runtime config). **Never edit or save Bootstrap.** |
| 7 | `DefaultNetworkPrefabs.asset` lists a dangling GUID `fd2bce17fc0b98f4b85ff5d759b9dbe6`; the live list is `Prefabs/RubbishNetworkPerfabsList.asset`. | B2 `Setup_Coop` |
| 8 | `Player.prefab` and `Truck V2.prefab` have `UseHalfFloatPrecision: 0` and no quaternion compression; the truck has `DontDestroyWithOwner: 0` and Rigidbody `m_Interpolate: 0`. `Player.prefab` is being changed by the Chuck integration. | B2 (player part after Chuck lands) |
| 9 | `PlayerSpawner.cs:183` `slot = 1 + clientId % 4`; `Character/ChuckPuppet.cs:381` `OwnerClientId % 4` (Chuck's in-flight file); `ChuckPuppet.variantOverride` is public and re-read every frame (:212). | B1, B2 (roster slot → `variantOverride`; no Character edit) |
| 10 | `PlayerController.OnNetworkSpawn` (:41-64) never disables `PlayerInput`/`StarterAssetsInputs` on remote copies. The host's copy spawns first on a guest and pairs the keyboard, so the guest can't move. | B2 |
| 11 | `SceneController.cs:81-88`: only the server may transition (guests at the door get a toast); `:161-164` NGO `LoadScene(Single)`. | B9 |
| 12 | `GameClock.cs:127` takes `baseDay` from the **guest's own** `GameSession.Day`; `TotalHours` restarts at 7 AM every session (:15, :30-47). | B6 |
| 13 | `CurbTrashSpawner.cs`: `System.Random(Environment.TickCount)` (:227); bagId = `stopId/NewUid()` (:285); curbs cleared at session start (:229-232). `ItemCatalog.RollItem` uses `NewUid` and `GameSession.Day` (:500-506). | B11 (wraps `RollBag`; `ItemCatalog` untouched) |
| 14 | No assembly definitions, no tests, no `Assets/Tests`; `playModeTestRunnerEnabled: 0`; test-framework 1.6.0 installed. | T2 (NUnit proof is a W0 blocker) |
| 15 | Saves live in `persistentDataPath` = `~/Library/Application Support/DefaultCompany/Rubbish/` (`companyName: DefaultCompany`), shared by the Editor, MPPM clones and **any build with product name "Rubbish"**. Nick's files: `rubbish_save.json` 388 B, `ebae_save.json` 2.2 KB. | L0 `SaveRoot`; bot builds are named `RubbishBots`; per-account folders |
| 16 | Progression's integration test (`Progression.md` §9.6 step 5) uses scratch `host.cs`/`host2.cs` that call raw `StartHost()` on local UTP. | B1 keeps it working (host self-approval); regression gate |
| 17 | The Yeet slice owns `Core/Carry.cs`, `Trash/**`, `Feel/**`. It adds `ShiftLedger` (an observer); `TrickShot` pays through `GameSession.AddCash`, keeps records in its **own JSON file**, and triggers its receipt "while in ShopScene". | §8.7.9 notes (rules 1, 6, 9) |
| 18 | `ItemInstance.Def => ItemCatalog.Get(defId)` (`Core/RubbishTypes.cs:51`): Newtonsoft would try to serialize a ScriptableObject. | C2 DTOs (opt-in serialization) |
| 19 | The project is a Plastic workspace (`.plastic/`, `ignore.conf`) with no `cm` CLI, and isn't a git repo (`git` is installed). Agents have no undo. | W0 CK |
| 20 | Standalone builds use Mono (only Android is IL2CPP). Newtonsoft 3.2.2 is present through the services packages. | no `link.xml` needed now |

#### 8.7.2 Architecture decisions

- **D1. One money call: `Ledger`.** §9 rule 1. `GameSession.AddCash` stays the single network choke point; from C2 it routes
  through a new `TryAddCash(…, out appliedCents)` that applies on the host or offline and refuses on a guest, and `Ledger` posts only
  what was applied, in whole cents. The source player comes from `LedgerActor.As(playerKey)` set by host request handlers.
  `ShiftLedger` (Yeet), `CompanyLedger` (M1) and `NetAudit` only *observe* `Ledger.Posted`.
- **D2. One-way mirror.** On the host and offline, `GameSession`'s statics stay the gameplay truth, so the ~40 gameplay call sites
  don't change. On the host, `WorldState` and `CompanyState` observe the existing events (`CurbTrashChanged`, `CashChanged`,
  `UpgradeChanged`, `RepChanged`, `DayStarted`, `Changed`) and replicate. On guests they write into the statics through
  `GameSession.NetMirror` with `Applying = true`, firing the same events so Feel and the HUD work unchanged. Any other `GameSession`
  mutation on a guest is dropped, logged once per operation and counted (`NetAudit.GuestWritesDropped`).
- **D3. What a guest can do in M1:** move; see the same curbs, wallet, clock and day; join ROLL OUT / CLOCK OUT (ready, honk,
  hitchhike); use the laptop through requests (B12); drive the truck. **Not yet:** grab, throw or sort (`CoopGate` hides those prompts;
  grabbing is M2, sorting M5). M1 is an internal milestone; the first fun session is still M2.
- **D4. The host's company is mirrored as one DTO snapshot:** a `CashCents` network variable (instant), a few HUD variables, and one
  gzipped `CompanyDto` + `EbaeDto` JSON sent as named message `rb.company` on change (debounced 1 s) and to late joiners. Host-private
  contents (depot deliveries, truck bag contents) are stripped. ≤16 KB compressed; guests inflate it through `BoundedGzip` (1 MiB
  cap) and accept it only from the server.
- **D5. No scene edits for networking.** `NetBoot` (`RuntimeInitializeOnLoadMethod`) configures `NetworkManager` at runtime
  (approval on, callbacks, disconnect reasons). `Setup_Coop` builds `Assets/Prefabs/Net/NetSession.prefab` from **every class marked
  `[NetSessionComponent]`** (so packages never edit `Setup_Coop`), registers it in `RubbishNetworkPerfabsList` and in `RubbishAssets`
  as `net/session`. The host spawns it with `Spawn(destroyWithScene: false)`. The bot build script runs `Setup_Coop.Run` in the build
  copy before every bot build.
- **D6. Transports behind `INetTransportAdapter`** (§1.3): `LocalDirect` (127.0.0.1) for solo, bots and MPPM, so solo needs no
  internet or sign-in; `UnityRelay` (today's code, guarded) for development and as a remote-flag fallback; in M1 a Steam
  friends-only lobby carries `rb_net=relay` + `rb_relay=<code>`, which gives invites and Join Game before the Steam transport exists;
  `SteamSdr` is the wave-4 spike.
- **D7. Identity is fixed per build** (`Accounts.md` §1.2): release = Steam required; development builds and the Editor use
  `dev:<name>` unless explicitly opted in (`-steam`, `RUBBISH_EDITOR_STEAM`); opting in without Steam is an error, never a fallback.
  The host verifies Steam session tickets from W3 (B10); release builds reject `dev:` keys.
- **D8. Saves.** Data root = `SaveRoot.Path` (L0). Accounts under `<root>/accounts/<PlayerKey.FolderName>/` behind
  `ISaveStore`/`IAccountBackend` with a local file store and local leases (M0); Town/Company/Profile stores on DTOs (M1). **Host flow
  = pick a Town × a company slot** (default Continue). **Join flow in M1 = join as crew**, with no picker (Moonlighting is M5).
  `SaveGuard` latches a guest's saves off from Join until their own data is reloaded (L0).
- **D9. Caps:** `NetConstants.TownCap = 8`, `CompanyCap = 4`, `CrewTownCap = 4` (until M7), `MaxCompanies = 4`. Relay allocates
  `TownCap − 1 = 7` connections.
- **D10. Pure assemblies.** `Assets/Scripts/Net/Core/Rubbish.Net.Core.asmdef` (UnityEngine, `Unity.Netcode.Runtime`,
  `Unity.Collections`): slot table, join codec, file store, leases, hashes, `BoundedGzip`, `NetGuard`, transport helpers.
  `Assets/Scripts/SaveModel/Rubbish.SaveModel.asmdef` (`noEngineReferences: true`, Newtonsoft only): DTOs, migrations, legacy parsers,
  later the Guard; the same source compiles into the Cloud Code module. Tests: `Assets/Tests/EditMode/Rubbish.Net.Tests.asmdef`
  references both. Gameplay-facing net code lives in Assembly-CSharp. Steam code lives under `Net/Steam/**` behind
  `#if !DISABLESTEAMWORKS`, written only after the package window.
- **D11. Rules.** `WorldState.Rules` defaults to `TownRules.CrewDefault` (Nick: "lets make it chaotic"): bonk and shove crewmates,
  carry flopped friends, spicy toggles on, rip fines as shame only. `TownRulesUtil.Permits(actor, target, verb)` ships as a stub for
  M2. The rhythm tug is M5.
- **D12. Other players' bytes are hostile.** Every named message and RPC on the host goes through `NetGuard` (per-client rate and
  size); every compressed payload through `BoundedGzip`; JSON is schema-checked with depth and array caps before it's applied.

#### 8.7.3 Contract steps and APIs

**L0: save-safety hotfix (lead, ~1 day).** Runs as soon as Progression A releases `Core/GameSession.cs` and `Ebae/EbaeMarket.cs`
(ask A now for an early hand-off of the persistence regions; it's a live data-loss bug).

- **Edits** (persistence regions only): `Core/GameSession.cs` (:387-575), `Ebae/EbaeMarket.cs` (:550-652), `Ebae/EbaeMarketRunner.cs`,
  `MainMenuManager.cs` (2 lines: `SaveGuard.MarkForeign("join")` first thing in `OnJoinClicked`; `SaveGuard.ReloadOwn("join failed")`
  on failure).
- **Creates:** `Net/Core/Save/SaveRoot.cs`, `Net/Core/Save/SafeFile.cs` (no Assembly-CSharp references, so they move into
  `Rubbish.Net.Core` when T2 adds the asmdef), `Save/SaveGuard.cs`, `Save/LegacySave.cs`.
- **Behaviour:** `loaded = true` only after a good parse or when the file is missing; on a bad parse, quarantine to
  `.corrupt-<utc>.json`, fall back to the newest generation that parses, else block saves for the session and toast. One
  `LegacySave.WriteBoth()` writes both files with the same `gen`, at most once per 2 s, flushed on quit, pause and scene change.
  Atomic writes with time-tiered generations (`Accounts.md` §5.1). `SaveGuard` latch. `ResetAll(true)` refused while latched or
  unloaded. Ebae offers tick only on the host or offline.

```csharp
public static class SaveRoot { public static string Path { get; } public static bool IsOverridden { get; } public static string Source { get; } }
  // persistentDataPath | -dataroot <dir> | Editor only: UserSettings/RubbishDev.json {dataRoot, owner, expiresUtc} while unexpired (≤2 h)
public static class SafeFile {
  public static bool WriteAtomic(string path, byte[] data, DateTime nowUtc, string sessionId);   // tmp → flush → rename, then generations
  public static bool TryReadNewest(string path, Func<byte[], bool> parses, out byte[] data, out string source);
  public static IReadOnlyList<(string tier, string file, DateTime savedUtc)> Generations(string path);
  public static void Quarantine(string path, DateTime nowUtc);                                   // → <name>.corrupt-<utc>.json
  public static void KeepPreMigration(string path, int fromSchema); }
public static class SaveGuard {
  public static bool Foreign { get; }                  // latched: the statics show someone else's world
  public static bool WritesAllowed { get; }            // !Foreign && both legacy files loaded cleanly
  public static void MarkForeign(string reason);       // Join click (before any scene load) + NetworkManager.OnClientStarted backstop
  public static void ReloadOwn(string reason);         // reset both statics while latched → Load both → clear; on leave, join failure, OnClientStopped
  public static event Action<string> Blocked; }        // NetAudit + toast
internal static class LegacySave { public static void WriteBoth(bool force); }   // GameSession.BuildSaveJson(gen) + EbaeMarket.BuildSaveJson(gen)
```

**C1: M0 contract (lead; new files only; can run now).** Compile-green stubs; builders fill the bodies.

```csharp
// Assets/Scripts/Net/Core/   (asmdef Rubbish.Net.Core, added by T2)
public static class NetConstants { public const int TownCap = 8, CompanyCap = 4, CrewTownCap = 4, MaxCompanies = 4, TickRate = 30;
  public const ushort DefaultPort = 7777; public const int JoinRequestMaxBytes = 1200, InflateMaxBytes = 1024 * 1024; }
public static class NetVersion { public const ushort Protocol = 3; public const ushort Content = 2; /* bump with balance patches */ }
public enum NetMode : byte { Offline, LocalDirect, UnityRelay, SteamSdr }
public readonly struct PlayerKey : IEquatable<PlayerKey> { public readonly string Value; public bool IsSteam { get; } public ulong SteamId64 { get; }
  public string FolderName { get; }                                       // steam_7656… | dev_editor
  public static PlayerKey Steam(ulong id64); public static PlayerKey Dev(string name); public static bool TryParse(string s, out PlayerKey k); }
public enum JoinReject : byte { None, ProtocolMismatch, TownFull, CompanyFull, Banned, AuthFailed, AuthTimeout, NotLobbyMember,
  DevKeyNotAllowed, BadRequest, HostLeaving, SoloOnly }
public static class JoinRejectText { public static string For(JoinReject r); }   // "Update your game", "Crew is full (4/4)", …
public struct JoinRequest { public ushort proto, content; public string playerKey /*≤64*/; public byte[] authTicket /*host-bound*/;
  public byte[] loadout /*≤64*/; public byte choice /*0 crew, 1 just-me, 2 bring (M5+)*/; public string companyGuid; public long companyVersion;
  public byte[] blobHash /*32 B or empty*/; public byte flags;
  public byte[] Encode();                                                 // 'RBJ1' header, ≤ JoinRequestMaxBytes
  public static bool TryDecode(byte[] data, out JoinRequest r, out JoinReject why); }
public sealed class SlotTable { public SlotTable(int townCap, int companyCap);
  public bool TryAssign(PlayerKey p, int companyIndex, out int slot, out JoinReject why);   // lowest free; same slot on rejoin
  public void Release(PlayerKey p); public void Forget(PlayerKey p);                        // Release keeps the slot remembered
  public bool TryGet(PlayerKey p, out int companyIndex, out int slot);
  public int Count { get; } public int CountIn(int companyIndex);
  public void Restore(IEnumerable<(PlayerKey key, int company, int slot)> remembered); }
public interface IPlayerIdentity { PlayerKey Key { get; } string DisplayName { get; } bool IsSteam { get; }
  byte[] GetJoinTicket(ulong hostSteamId); void CancelJoinTicket(); }
public interface IPlayerAuthVerifier { void BeginVerify(ulong clientId, in JoinRequest r, Action<bool, JoinReject> done); void End(ulong clientId); }
public static class PlayerIdentity { public static IPlayerIdentity Local { get; } public static void Register(Func<IPlayerIdentity> provider, int priority); }
public interface INetTransportAdapter { NetMode Mode { get; }
  Task<HostPrep> PrepareHostAsync(Unity.Netcode.NetworkManager nm, int maxPlayers, CancellationToken ct);
  Task<bool> PrepareClientAsync(Unity.Netcode.NetworkManager nm, JoinTarget t, CancellationToken ct);
  void OnShutdown(Unity.Netcode.NetworkManager nm); }
public readonly struct HostPrep { public readonly bool Ok; public readonly string JoinCode, Error; }
public sealed class JoinTarget { public NetMode mode; public string joinCode, address; public ushort port; public ulong steamLobbyId, hostSteamId; }
// saves (async-first; synchronous fast path for quit)
public interface ISaveStore { string Scope { get; }
  Task<SaveRead> ReadAsync(string key, CancellationToken ct = default);
  Task<SaveWrite> WriteAsync(string key, byte[] data, long expectedVersion /* -1 = any */, CancellationToken ct = default);
  Task<IReadOnlyList<SaveKeyInfo>> ListAsync(string prefix, CancellationToken ct = default);
  Task<bool> DeleteAsync(string key, CancellationToken ct = default);
  bool TryWriteNow(string key, byte[] data); }
public readonly struct SaveRead { public readonly bool Found; public readonly byte[] Data; public readonly long Version; public readonly DateTime SavedUtc; public readonly string Source; }
public readonly struct SaveWrite { public readonly bool Ok; public readonly long Version; public readonly SaveError Error; }
public enum SaveError { None, VersionConflict, NotFound, Io, Offline, TooLarge, Denied, StaleEpoch }
public readonly struct SaveKeyInfo { public readonly string Key; public readonly long Version; public readonly DateTime SavedUtc; public readonly int Bytes; }
public enum AccountState { Offline, SigningIn, SignedIn, Failed }
public readonly struct CompanyLease { public readonly bool Granted; public readonly string CompanyGuid, SessionId, HeldBy, TownName;
  public readonly long Epoch; public readonly DateTime ExpiresUtc; }
public interface IAccountBackend { AccountState State { get; } string AccountId { get; } bool IsCloud { get; } event Action<AccountState> StateChanged;
  ISaveStore Store { get; } Task<bool> SignInAsync(IPlayerIdentity id, CancellationToken ct = default);
  Task<CompanyLease> CheckoutCompanyAsync(string companyGuid, string sessionId, string townName, CancellationToken ct = default);
  Task<SaveWrite> CommitCompanyAsync(CompanyLease lease, byte[] blob, long baseVersion, CancellationToken ct = default);
  Task CheckinCompanyAsync(CompanyLease lease, CancellationToken ct = default);
  Task<CompanyLease> TakeBackAsync(string companyGuid, CancellationToken ct = default);    // new epoch; the old session's commits fail
  Task<bool> RestorePreviousAsync(string companyGuid, CancellationToken ct = default); }
public static class SaveKeys { public const string Profile = "profile"; public static string Company(string g); public static string CompanyPrev(string g);
  public static string Town(string g); public static string Lock(string g); }
public static class WorldHash { public const ulong Seed = 1469598103934665603UL; public static ulong Fnv(ulong h, long v); public static ulong Fnv(ulong h, string s); }
// hostile input
public static class BoundedGzip { public static byte[] Deflate(byte[] raw); public static bool TryInflate(byte[] gz, int maxBytes, out byte[] raw); }
public sealed class NetGuard { public NetGuard(float perSecond, int burst, int maxBytes); public bool Allow(ulong clientId, int bytes, double nowSeconds); public void Forget(ulong clientId); }
[AttributeUsage(AttributeTargets.Class)] public sealed class NetSessionComponentAttribute : Attribute { public int Order; }

// Assembly-CSharp stubs (bodies work offline; B1/B5 fill them)
public enum NetSessionState { Idle, Preparing, Hosting, Joining, Connected, Leaving, Failed }
public sealed class HostOptions { public NetMode mode = NetMode.Offline; public string townGuid, companyGuid; public int maxPlayers = NetConstants.CrewTownCap;
  public string firstScene = "ShopScene"; public bool steamLobby; }
public static class NetSession { public static NetSessionState State { get; } public static string JoinCode { get; } public static string SessionId { get; }
  public static string LastError { get; } public static event Action<NetSessionState> StateChanged; public static event Action<string> JoinCodeReady, Failed;
  public static Task<bool> HostAsync(HostOptions o, CancellationToken ct = default);
  public static Task<bool> JoinAsync(JoinTarget t, CancellationToken ct = default);                   // calls SaveGuard.MarkForeign first
  public static void Leave(string reason = null);                                                     // always ends in SaveGuard.ReloadOwn
  public static void RegisterAdapter(INetTransportAdapter a); }
public static class SessionService { public static SlotTable Slots { get; } public static bool TryGetPlayer(ulong clientId, out PlayerKey k);
  public static bool TryGetClient(PlayerKey k, out ulong clientId); public static event Action<PlayerKey, ulong, int> PlayerJoined;
  public static event Action<PlayerKey, ulong> PlayerLeft; public static void Kick(ulong clientId, string reason);
  public static void RegisterVerifier(IPlayerAuthVerifier v); public static NetGuard GuardFor(string messageName); }
public struct RosterEntry : INetworkSerializable, IEquatable<RosterEntry> { public ulong clientId; public FixedString64Bytes playerKey;
  public FixedString32Bytes displayName; public byte company, slot, flags; }
[NetSessionComponent] public class NetRoster : NetworkBehaviour { public static NetRoster Instance { get; } public NetworkList<RosterEntry> Entries { get; }
  public bool TryGetByClient(ulong clientId, out RosterEntry e); public event Action Changed; }
public static class NetAudit { public static int GuestWritesDropped { get; } public static void Event(string kind, object payload); public static string SnapshotJson(); }
```

**C2: M1 contract (lead; after Progression A confirms it's finished with `GameSession.cs`, `EbaeMarket.cs` and `Ledger.cs`).**
The lead writes **all** of `GameSession.Net.cs` and the ebag capture itself (red-team R7, R8); wave-3 builders only call them.

```csharp
// Core/GameSession.cs: three edits
//  1. `public static partial class GameSession`
//  2. the first line of EVERY mutator (incl. Progression's): `if (BlockGuestWrite("<Op>")) return …;`  (TrySpend returns false)
//  3. cash backing store `static long cashCents`; `Cash` stays a float property (frozen API); Save/Load route through SaveSlots once active
public static bool TryAddCash(float delta, string reason, Vector3? worldPos, out long appliedCents);  // AddCash(…) now calls this
public static long CashCents { get; }
public static bool WritesBlocked { get; }                         // a guest outside NetMirror.Applying, or SaveGuard latched
// Core/GameSession.Net.cs (new, lead, fully implemented)
static bool BlockGuestWrite(string op);                            // counts NetAudit.GuestWritesDropped
public static CompanyDto CaptureCompany(bool forNetwork);          // forNetwork strips depot-delivery items and truck bag contents
public static void RestoreCompany(CompanyDto c, bool fromNet);
public static TownDto CaptureTown(); public static void RestoreTown(TownDto t);  // clock, curbs WITH contents, poses, route sets, rabies (global until M6)
//   company timers (stamps) ⇄ remaining in-game hours at this boundary; money in cents
public static class NetMirror { public static bool Applying { get; }
  public static void SetCashCents(long cents); public static void RaiseCashFx(long deltaCents, string reason, Vector3? pos);
  public static void SetDay(int day); public static void SetCurbs(IReadOnlyList<CurbBag> records); public static void SetTruckHud(float kg, int bags);
  public static void ApplyCompanySnapshot(CompanyDto c, EbaeDto e); }
// Ebae/EbaeMarket.cs (additive, agreed with Progression A)
public static EbaeDto CaptureDto(); public static void RestoreDto(EbaeDto d);   // listings (offers' remaining seconds), history, totals
// Progression/Ledger.cs (3 lines, agreed with A): MoveCash → TryAddCash; Post(appliedCents / 100f) only when applied;
//   ApplyRep returns 0 without posting when GameSession.WritesBlocked
// Core/GameClock.cs (additive; owned by B6 after C2)
public static void SetWorldTime(double totalHoursNow, int dayNow); internal static void ApplyNetClock(double offsetSeconds, int baseDay);
// Assets/Scripts/SaveModel/ (asmdef Rubbish.SaveModel): DTO classes, fields only, [JsonObject(MemberSerialization.OptIn)]
SaveEnvelope { kind, schema, content, guid, version, savedAt, writer, ext }   CompanyDto   TownDto   CrewCardDto   EbaeDto   ItemDto   BagDto   CurbDto
// Stubs, bodies in wave 3/4 ([NetSessionComponent] on the NetworkBehaviours; the lead runs Setup_Coop once they exist)
public struct CurbBagNet : INetworkSerializable, IEquatable<CurbBagNet> { public uint id; public ushort house; public byte slot, flags; public int putOutMin; public half massKg; }
[NetSessionComponent] public class WorldState : NetworkBehaviour { public static WorldState Instance { get; }
  public NetworkVariable<int> BaseDay; public NetworkVariable<double> ClockOffsetSeconds; public NetworkVariable<uint> StateVersion;
  public NetworkVariable<TownRules> Rules; public NetworkList<FixedString32Bytes> HouseIds; public NetworkList<CurbBagNet> Curbs;
  public static string BagIdFor(in CurbBagNet b); }                                                   // "{stopId}/{id:x}"
[Flags] public enum TownRules : uint { None = 0, BonkShoveCrew = 1, CarryFlopped = 2, RipFines = 4, RabiesTag = 8, Pack = 16,
  CompactorReboot = 32, StickyFingers = 64, HitchhikersWelcome = 128, CrewDefault = BonkShoveCrew | CarryFlopped | RabiesTag | Pack
  | CompactorReboot | StickyFingers | HitchhikersWelcome /* rip fines off: shame only */ }
public static class TownRulesUtil { public static bool Permits(PlayerKey actor, PlayerKey target, string verb); }   // M2 fills
[NetSessionComponent] public class CompanyState : NetworkBehaviour { public static CompanyState Home { get; } public int Index { get; }
  public NetworkVariable<FixedString64Bytes> CompanyGuid, DisplayName; public NetworkVariable<Color32> Tint;
  public NetworkVariable<long> CashCents; public NetworkVariable<float> TruckLoadKg; public NetworkVariable<ushort> TruckBagCount;
  public NetworkVariable<uint> SnapshotVersion; }
public struct LedgerLine { public uint seq; public int tick; public long deltaCents, balanceCents; public string reasonId, reason; public byte sourceSlot; }
public static class CompanyLedger { public static long OpeningCents { get; } public static IReadOnlyList<LedgerLine> Tail { get; }
  public static bool Balances(out string why); }
public static class LedgerActor { public static IDisposable As(PlayerKey p); public static PlayerKey Current { get; } }
public enum FxKind : byte { CashFloat, Toast, Sfx, Burst, Announce, ClockIn, Ritual }
public static class WorldFx { public static void Cash(long deltaCents, string reason, Vector3? pos); public static void Toast(string text, ToastStyle s, float secs = 2.5f);
  public static void Sfx(string id, Vector3? pos, float vol = 1, float pitch = 1); public static void Announce(string text); public static event Action<FxKind> Played; }
public enum RitualKind : byte { RollOut, ClockOut }
[NetSessionComponent] public class CrewRitual : NetworkBehaviour { public static CrewRitual Instance { get; } public bool Active { get; }
  public RitualKind Kind { get; } public int Ready { get; } public int Crew { get; } public float CountdownLeft { get; } public event Action Changed;
  public void RequestStart(RitualKind k, GarageDoorController door, bool driving); public void SetReady(bool ready); public void Call(); }
public static class CoopGate { public static bool GuestMayInteract(IInteractable i); }
public enum RngStream : byte { CurbPutOut, BagSeed, Critters, Ebae, Misc }
public static class ContentRng { public static void Init(ulong townSeed, string savedStateJson); public static string CaptureState();
  public static uint NextU32(RngStream s); public static int Range(RngStream s, int min, int maxEx); public static bool Chance(RngStream s, int permille);
  public static int PickWeighted(RngStream s, IReadOnlyList<int> weights); public static string NewBagId(string stopId);
  public static int BagSeed(string bagId); public static void StampItemIds(BagContents bag, int dayFound); }   // uid = $"{bagId}#{i}"
public sealed class TownMeta { public string guid, name, lastCompanyGuid; public int day, schema; public DateTime lastPlayedUtc; }
public sealed class CompanyMeta { public string guid, name, onTheRoadIn, lastTownName; public Color32 tint; public long cashCents; public int rank, schema;
  public DateTime lastPlayedUtc; public bool synced; }
public static class SaveSlots { public const int MaxCompanySlots = 3, MaxTowns = 5; public static Task InitAsync();
  public static IReadOnlyList<TownMeta> Towns { get; } public static IReadOnlyList<CompanyMeta> Companies { get; }
  public static Task<TownMeta> CreateTownAsync(string name); public static Task<CompanyMeta> CreateCompanyAsync(string name, Color32 tint);
  public static Task<bool> RenameAsync(string guid, string name); public static Task<CompanyMeta> DuplicateCompanyAsync(string guid);
  public static Task<bool> DeleteTownAsync(string g); public static Task<bool> DeleteCompanyAsync(string g);     // soft delete, 30 days
  public static IReadOnlyList<(string tier, DateTime savedUtc)> Backups(string guid); public static Task<bool> RestoreAsync(string guid, string tier);
  public static Task<bool> ActivateAsync(string townGuid, string companyGuid); public static TownMeta ActiveTown { get; } public static CompanyMeta ActiveCompany { get; }
  public static (string townGuid, string companyGuid) Continue { get; }
  public static void SaveCompanySoon(); public static void SaveTownSoon(); public static void FlushNow(); public static bool EnsureActiveForDirectPlay(); }
public static class ProfileStore { public static CrewCardDto Card { get; } public static Task LoadAsync(); public static void SaveSoon();
  public static T GetSection<T>(string id) where T : new(); public static void SetSection<T>(string id, T v); }
```

#### 8.7.4 Waves and packages

**Wave 0: rails (start now; no game files except L0).**

| ID | Owner | Creates / edits | Gate |
|---|---|---|---|
| **CK** | lead, **after Nick approves** (`Accounts.md` §14 step 1) | `git init` at the project root (never pushed); `.gitignore` (Library, Temp, Logs, Builds, obj, UserSettings, `*.csproj`, `.plastic`, `Build.zip`); one line `.git` in `ignore.conf`. Or Nick installs `cm` instead. | first commit exists; `git status` clean; Plastic doesn't see `.git` |
| **L0** | lead | §8.7.3 | **(a)** for each legacy file, truncated at 20 random offsets and at 0 bytes: launch and quit (under the lock, on a test data root) → bytes unchanged, `.corrupt-*` written, toast logged; with a generation present, it recovers from it. **(b)** EV probes: `MarkForeign` → mutate → every save path (`Save`, `SaveSoon`, pause, quit, both `ResetAll(true)`) writes nothing; `ReloadOwn` → statics equal the files. **(c)** with an overridden data root, Nick's real folder is unchanged (sha256 + mtime). **(d)** tiers rotate as specified over a scripted hour of fake clock. |
| **T1** | tooling builder | `Tools/coop_lint.sh`, `Tools/coop_lint.baseline.json`, `Tools/coop_lint_fixtures/**` | a plain run prints the same OK on today's tree; each new rule fires on its fixture; `--files` filters; exit codes and baseline format unchanged (§8.7.8) |
| **T2** | tooling builder | `Tools/bots/sync_build.sh`, `Tools/bots/run_bots.sh`, `Tools/bots/audit.py`, `Tools/dev/test_data_root.sh`, `Assets/Scripts/Editor/RubbishBuild.cs`, `Assets/Scripts/Net/Core/Rubbish.Net.Core.asmdef` + `CoreSmoke.cs`, `Assets/Tests/EditMode/Rubbish.Net.Tests.asmdef` + `CoreSmokeTests.cs` | **Blocker for W1:** `sync_build.sh --tests` runs `CoreSmokeTests` green in `~/Dev/Rubbish-build` (NUnit proven). `RubbishBuild.DevHeadless` produces `Builds/DevHeadless/RubbishBots.app` (product name **RubbishBots**, development build). `-batchmode -nographics -botsmoke` exits 0 within 60 s. The sync excludes Library, Temp, Logs, Builds, obj, **UserSettings, .plastic, .git, Build.zip**, and runs `Setup_Coop.Run` before bot builds once it exists. `test_data_root.sh on|off` seeds a scratch root with *copies* of Nick's two files, writes the expiring `UserSettings/RubbishDev.json`, and restores on `off` or expiry. |
| **C1** | lead | §8.7.3 C1 files (new only) | compiles green; `unity command eval 'return NetConstants.TownCap;'` returns 8 |
| **G-PKG** | lead, in a window Nick approves (not in Play mode, nobody compiling) | `Packages/manifest.json`: `"com.rlabrecque.steamworks.net": "https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2025.164.1"`; NGO → 2.13.3, Multiplayer Services → 2.3.3 (each verified on the registry first, skipped if absent); `steam_appid.txt` (480) at the project root | packages resolve; zero compile errors; `editor_status` ready |

**Wave 1: M0 core (5 builders).** Starts when T2 and C1 are green, L0 has landed, CK exists (or Nick explicitly declined it), and
the in-flight content workflows have landed
(Nick's ordering). B3 also needs G-PKG; B2's `Player.prefab` part needs the Chuck integration landed. Full package text, ready to hand
out, is in the table below; the gates are in the last column.

| ID | Creates (new files) | Edits (owned this wave) | What | Gate |
|---|---|---|---|---|
| **B1 Session, transports, approval** | `Net/Session/NetSession.cs`, `NetBoot.cs`, `SessionService.cs`, `NetRoster.cs`, `Adapters/LocalDirectAdapter.cs`, `Adapters/UnityRelayAdapter.cs`; `UI/Menu/NetErrorPanel.cs`; `Net/Core/Session/SlotTable.cs`, `JoinRequest.cs`; tests `Assets/Tests/EditMode/Session/**` | `GameStartup.cs` (delegates to `NetSession`; keeps `InitializeHost/InitializeClient` as wrappers; `IsSignedIn` guard, try/catch, error screen; Relay = TownCap − 1), `MainMenuManager.cs` (Solo / With Friends split; no client-side `LoadScene`; readable errors; keeps L0's latch calls), `JoinCodeUI.cs`, `GameConfig.cs`, `PlayerSpawner.cs` (slot spacing from the roster replaces `:183`) | Approval with `Pending` support for verifiers; the host's own connection auto-approved (raw `host.cs`/`host2.cs` keep working); protocol check; caps (Town 8, company 4, Crew Town 4); slot memory by PlayerKey; disconnect reason shown, then back to the menu; solo = LocalDirect refusing remote clients (`SoloOnly`); `NetSession.Leave`/`OnClientStopped`/join failure always end in `SaveGuard.ReloadOwn`; approval payload ≤1,200 B through `NetGuard` | UT `SlotTable*` (lowest free slot; rejoin keeps slot; 5th in a Crew Town → `CompanyFull`; 9th in a Town → `TownFull`; `Release` vs `Forget`); UT `JoinRequest*` (round trip; 1,200 B max; bad header → `BadRequest`; proto 999 → `ProtocolMismatch`); BOTS 3 × 2 min LocalDirect: roster has 3 entries with slots 0, 1, 2; a client with `-proto 999` is rejected and logs "Update your game"; PLAY (lock, test root): `host.cs` + `host2.cs` still host into ShopScene; Relay smoke: 1 host + 1 bot over Relay (code read from the host's audit log) |
| **B2 Player and prefab hygiene** | `Net/Player/CoopPlayerHooks.cs`, `RosterView.cs` (writes `ChuckPuppet.variantOverride = slot`), `TruckNetFeel.cs` (Rigidbody interpolation on for the driving owner only); `Editor/Setup_Coop.cs` (menu Rubbish/Setup/Coop + `public static void Run()` for `-executeMethod`) | `PlayerController.cs` (`OnNetworkSpawn`: non-owner → disable `PlayerInput` and `StarterAssetsInputs`; owner → re-pair keyboard and mouse a frame later; call `CoopPlayerHooks.OnSpawned`) | `Setup_Coop` (idempotent): truck `DontDestroyWithOwner = 1`, half precision + quaternion sync and compression, add `TruckNetFeel`; player (after Chuck lands) the same NetworkTransform flags and `PlayerInput` off by default; remove the dangling GUID; build `NetSession.prefab` from every `[NetSessionComponent]` class (found with `TypeCache`) and register it | `Setup_Coop` run twice → identical prefab hashes; EV `prefab_probe.cs` reads back every flag; MPPM2: the guest process has exactly 1 enabled `PlayerInput` whose devices include the keyboard, and the guest moves > 3 m under `AutoWalk`; roster slot == `ChuckPuppet.Variant` on both machines |
| **B3 Steam core + identity** | `Net/Identity/DevIdentity.cs`, `PlayerIdentityBoot.cs`; `Net/Steam/SteamBootstrap.cs`, `SteamIdentity.cs`; `Editor/SteamBuildPostprocess.cs` (copies `steam_appid.txt` next to **development** builds only) | none | Identity rules of `Accounts.md` §1.2: release builds require Steam (`RestartAppIfNecessary` only when `!DEVELOPMENT_BUILD && appId != 480`); development builds and the Editor are `dev:<name>` unless `-steam` / `RUBBISH_EDITOR_STEAM`; opted in without Steam → error screen, **no fallback**; never Steam in batchmode, MPPM clones or with `-nosteam`. Dev name: `-playerid`, else MPPM tag, else `editor` in the Editor, else a per-install GUID in the data root. `SteamAPI.RunCallbacks` every frame; shutdown on quit and on leaving play mode; `SteamNetworkingUtils.InitRelayNetworkAccess()` when Steam is on. Display names live from Steam, never saved. | UT dev-name resolution order; EV in the Editor with Steam running **and** with Steam closed: `PlayerIdentity.Local.Key == "dev:editor"` both times; dev build with `-steam` and Steam running → `steam:` key; with `-steam` and Steam closed → error screen, no `dev:` key; batchmode bot logs "Steam skipped" |
| **B4 Save store, local accounts, leases** | `Net/Core/Save/LocalFileStore.cs` (on L0's `SafeFile`; version and data land in one atomic write), `Net/Core/Save/LocalAccountBackend.cs` (lock file with epoch, session, PID and process start time; a lock whose process is gone is free at once; `.prev` copy; take-back bumps the epoch); `Save/AccountService.cs` (picks the backend; `<SaveRoot>/accounts/<PlayerKey.FolderName>/`); tests `Assets/Tests/EditMode/Save/**` including an abstract **`AccountBackendContract`** fixture that M1.5's cloud backend must also pass | none | 1 MiB cap per record; recovery from a leftover temp file; time-tiered generations through `SafeFile` | Contract fixture: version conflict; write survives a simulated crash (temp left behind); generations rotate per tier; double checkout → `Granted = false` with `HeldBy`; a lock held by a dead process → granted at once; take-back bumps the epoch and a stale commit fails with `StaleEpoch`; > 1 MiB → `TooLarge`; two keys write to disjoint folders; 1,000 kill-during-write cycles (bot `-savetorture`, killed by script) never leave a record without a parseable latest or fallback |
| **B5 Bots, NetAudit, MPPM autostart** | `Net/Dev/BotDriver.cs` (`-bothost` / `-botjoin host:port` / `-botminutes` / `-botbrain walk\|idle\|laptop\|ritual\|grief` / `-netsim crew_bad_wifi` / `-auditlog` / `-kill-self-at` / `-savetorture`), `NetAudit.cs` (impl), `NetAuditNet.cs` (`[NetSessionComponent]`, hash probes), `NetSimPresets.cs` (UTP `SetDebugSimulatorParameters(75, 10, 2)` ≈ 150 ms round trip, ±10 ms, 2%), `CoopAutoStart.cs` (`#if UNITY_EDITOR`: MPPM tags `AutoHost` / `AutoJoin` / `AutoWalk`); `Tools/bots/**` (extends T2), `Tools/bots/eval/*.cs` probes | none | NetAudit writes JSONL `{t, kind: error\|exception\|roster\|hash\|ledger\|ritual\|save\|reject, …}`, including blocked saves. Bots drive `StarterAssetsInputs` on their owned player only, always with `-dataroot <run>/<bot> -playerid dev:bot-N -nosteam`. `audit.py` fails the run if Nick's real save folder changed. | BOTS 3 × 10 min on `crew_bad_wifi`: 0 errors, ≥ 50 hash probes; `audit.py` exits 1 on an injected exception and on a touched real save folder; MPPM2 tags start both sides automatically |

**Wave 2: M0 integration (lead, under the editor lock).** Commit a checkpoint; run `Setup_Coop` (after `Setup_Character` if
pending); run the **M0 gate** (§8.7.7); re-baseline the lint; doc edits for Character.md §14 and CoreLoop.md.

**C2 (lead)** once Progression A confirms `GameSession.cs`, `EbaeMarket.cs` and `Ledger.cs` are final. Checkpoint first.

**Wave 3: M1 early (5 builders).** Starts when the M0 gate and C2 are green. The five consume C2's stubs, so they don't wait on each
other.

| ID | Creates | Edits | What | Gate |
|---|---|---|---|---|
| **B6 World state + clock** | `Net/State/WorldState.cs` (impl), `CurbBagNet.cs`, `HouseIds.cs`, `WorldHashWorld.cs` | `Core/GameClock.cs` (C2's additive API; `ElapsedSeconds` adds the replicated offset; the ticker's `baseDay` comes from `WorldState` while listening) | Host: diff `GameSession.CurbBags` on `CurbTrashChanged` into `Curbs`; fill `HouseIds` once; `StateVersion++` per change. Guests: `Curbs.OnListChanged` → `NetMirror.SetCurbs` with placeholder `BagContents{bagId, foundAt, sackVariant, mass, no items}` so `PickupStop.cs:222` materialises them. Clock offset replicated (debug skips included); day replicated; rules default `CrewDefault`. | UT `CurbBagNet` round trip and `BagIdFor`. BOTS 3 × 10 min: guests' curb ids and slots equal the host's at every matched `StateVersion`; \|guest hours − host hours\| < 0.01; `GameClock.DebugSkipHours(3)` on the host shows on guests within 1 s; same day |
| **B7 Company state, ledger mirror, WorldFx, message guards** | `Net/State/CompanyState.cs`, `CompanyLedger.cs`, `LedgerActor.cs`, `CompanySnapshot.cs`; `Net/Fx/WorldFx.cs`, `WorldFxNet.cs`, `FxIds.cs`; `Net/Session/MessageGuards.cs` | none | Host: `Ledger.Posted` → ledger line (actor slot) + `CashCents` + `WorldFx.Cash`; `Changed`/`UpgradeChanged`/`RepChanged` → snapshot (debounced 1 s) via `rb.company` + a targeted send on connect (the ChessController late-join pattern). Guests: `NetMirror.SetCashCents`, `RaiseCashFx`, `ApplyCompanySnapshot` through `BoundedGzip` + schema/size caps, only from the server. Every named message and RPC registered through `MessageGuards` (`NetGuard`). Ledger tail (500) saved in the company. | BOTS 3 × 10 min, host `laptop` brain: `CompanyLedger.Balances` holds on every process (in cents); guest cash == host cash at matched versions; floaters fire on guests; snapshot ≤ 16 KB; no depot-delivery items in the network JSON; **guest `Ledger.Posted` count == 0**. `grief` brain on a guest calls `GameSession.AddCash(999)`: dropped, `GuestWritesDropped == calls`, host unchanged. A modded host bot sends a 16 KB gzip that inflates to 1 GB and a 1M-entry inventory: the guest rejects both without running out of memory and logs it. |
| **B8 Save model, stores, slots, import** | `SaveModel/Migrations/*.cs` (v1 → v2 → v3, pure `JObject` steps), `SaveModel/Legacy/*.cs` (legacy JSON → DTO); `Save/DtoMapping.cs` (runtime ⇄ DTO), `Save/TownStore.cs`, `CompanyStore.cs`, `ProfileStore.cs`, `SaveSlots.cs`, `LegacyImport.cs`, `Autosave.cs`; `Tools/bots/eval/make_maximal_fixture.cs`; fixtures `Assets/Tests/EditMode/Fixtures/save_v1.json`, `save_v2.json`, `ebae_v1.json` (copies of Nick's files), `maximal_*.json`; tests `Assets/Tests/EditMode/Save/Migrations/**` | none (C2 already routes `GameSession` Save/Load through `SaveSlots`) | Stores per `Accounts.md` §4–§5 (cents; timers as remaining hours; ebag folded into the company). Import → company 1 "Rubbish Co." + Town 1 "Maple Hollow" + Crew Card, guid from the legacy pair's sha256, legacy files never touched. Company saved ≤ every 2 s while dirty; Town every 60 s, at day rollover and on quit (`TryWriteNow`). Lock taken on Activate, released on leave or quit. | UT: v1, v2 and maximal fixtures import to exact expected output; unknown fields round-trip; a mixed-generation legacy pair recovers to a matching pair. Activate Town B × company A, play, leave, then Town A × company A: the company arrives identical **including a gnome timer** (Towns at different `TotalHours`); untouched records byte-identical (sha256). A second process hosting company A sees "on the road in Maple Hollow"; after killing the first process, the lock is free at once. A scripted bug zeroes cash; restoring the session-start generation brings it back. Kill-host test (§8.7.7 M1-3). |
| **B9 Rituals, flow, guest gate** | `Flow/CrewRitual.cs`, `Net/CoopGate.cs` | `GarageDoorController.cs` (`OnInteract` → `CrewRitual.RequestStart`), `SceneController.cs` (ritual-driven transitions; HITCHHIKER arrivals; watchdog kept), `SceneTransitionData.cs` (hitchhiker list), `PlayerSpawner.cs` (stragglers at the hopper pose with a local tumble; "BEEP: X clocked in" on late join), `TruckRequester.cs` (only if needed), `PlayerController.cs` (`GatherOptions` filters through `CoopGate`) | Ready = seated, or inside the door's `InteractionZone` / within 6 m. All ready → go now; more than half → 10 s countdown. Honk while gathering = Call (polls the horn key; no `TruckControllerV2` edit). Anyone may start; the host executes. Driving through the door is host-only in M1. `CoopGate` hides `Carryable`, `SortStationEntry` and pile interactions on guests ("co-op grabbing lands next update"). | BOTS 3, `ritual` brain: 3 ROLL OUT / CLOCK OUT round trips; every client ends in the target scene within arrival timeout + 2 s; a straggler logs `hitchhiker`; the half-ready countdown fires at 10 s ± 0.5; a guest-started gather works. MPPM2: the guest sees "ROLL OUT 2/2" and never "Only the host" |
| **B10 Steam lobby, presence, auth at the door** | `Net/Steam/SteamLobby.cs`, `SteamPresence.cs`, `SteamJoinRouter.cs`, **`SteamAuthVerifier.cs`**; `Net/Core/Steam/LobbyInfo.cs`, `ConnectArgs.cs` (pure: parse `+connect_lobby <id>`, encode/decode `rb_*` keys) | `Net/Session/NetSession.cs` (a Friends host also creates the lobby; join by lobby id reads `rb_net`/`rb_relay` and picks an adapter; registers the verifier) | Friends-only lobby, cap = Crew Town cap; keys `rb_game=rubbish`, `rb_proto`, `rb_content`, `rb_net`, `rb_relay`, `rb_town`, `rb_session`; always filter by `rb_game` (480 shares lobby space). Rich presence `connect`; overlay invite; handles `GameLobbyJoinRequested_t`, `GameRichPresenceJoinRequested_t` and the cold-start command line. **Auth:** the guest's ticket comes from `GetAuthSessionTicket` bound to the host's `SteamNetworkingIdentity`; the host runs `BeginAuthSession` inside approval (`Pending`, 5 s timeout → `AuthTimeout`), requires lobby membership, `EndAuthSession` on leave; display names from Steam; release builds reject `dev:` keys (`DevKeyNotAllowed`). | UT `ConnectArgs`, `LobbyInfo`. EV on 480 (one machine, Steam running): create a lobby, read back the `rb_*` keys, set presence, leave. UT/EV with a fake verifier: a ticket bound to another host fails; a `dev:` key fails under the release-auth switch; a missing verifier answer times out at 5 s. Two-machine invite and Join Game: the checklist (§8.7.7). |

**Wave 3b: menu (1 builder), as soon as any wave-3 builder finishes** (keeps ≤ 5 at once).

| ID | Creates | Edits | What | Gate |
|---|---|---|---|---|
| **B14 Menu, company cards, HUD labels** | `UI/Menu/MainMenuCoopUI.cs` (runtime uGUI built with the `FeelUi` helpers), `UI/Menu/CompanyCardView.cs`, `UI/Menu/RestorePanel.cs`, `UI/Hud/CompanyWalletLabel.cs`, `UI/Hud/SessionToasts.cs` | `MainMenuManager.cs` (hosts `MainMenuCoopUI`; old buttons hidden), `JoinCodeUI.cs` | `Accounts.md` §3.2–§3.4: **Continue / Invite friends / Join a friend / Crew Card**; Change… with company cards (new, rename, recolour, duplicate, soft delete, Restore… from local generations) and Towns (new, rename, delete); HUD wallet labelled with the company name and tint; join toast "You're on Nick's shift. Rubbish Co. is safe at home."; leave card. No Bring picker (M5). | PLAY (lock, test root): Continue reaches ShopScene with the last Town and company; Change… → New Town, New company, Play solo all work; Restore… lists tiers and restores one; quit writes the Town. MPPM2: the guest HUD shows the host's company name on the wallet and the join toast |

**Wave 4: M1 late (3 builders).** Starts when Progression A–D **and** the Yeet slice have landed and passed their own acceptance
checks (the files below are theirs while in flight).

| ID | Creates | Edits | What | Gate |
|---|---|---|---|---|
| **B11 ContentRng + curb persistence** | `Net/Content/ContentRng.cs`, `Net/Core/Content/Pcg32.cs`; tests | `Trash/CurbTrashSpawner.cs` (`bagId = ContentRng.NewBagId(stopId)`; seed `ContentRng.BagSeed(bagId)`; the `RollBag` overload Progression D shipped; then `ContentRng.StampItemIds(bag, GameSession.Day)`; put-out rolls through `RngStream.CurbPutOut`; `BeginSession` no longer clears curbs when the Town has a saved clock), `Critters/Raccoon.cs` (spill decision `:291-298` through `ContentRng.Chance`, authority only) | The Town stores the `ContentRng` state. `ItemCatalog` untouched. | UT: the same Town seed and call sequence give identical bag manifests; uids are `{bagId}#{i}`; no `NewUid`/`Guid` in content paths (lint). BOTS 3 × 30 min: hash compares hold. Host restart: curbs return with the same ids, ages continuous, `putOutAtHours` monotonic |
| **B12 Laptop requests** | `Net/Requests/ShopRequests.cs`, `EbaeRequests.cs` | `Shop/ScrapazonApp.cs` (`:689` guest → `ShopRequests.Buy(id)`; result to the requester), `EbaeUI.cs` (`:668, 689, 811, 831, 843, 854` guest → requests), `LaptopController.cs` (only if needed) | The host runs `ScrapazonStore.TryBuy` under `LedgerActor.As(sender)`; `WorldFx` announces "Dot bought X −$1,200"; 2 requests/s per client via `NetGuard`; ebag accept / decline / list / pawn are requests. (Capture/restore moved to C2.) | BOTS: a guest `laptop` brain buys 5 upgrades; levels mirror everywhere; ledger balances. Two guests buy the last level in the same tick: exactly one `Ok`. A guest's ebag accept applies once. MPPM2: a guest Scrapazon purchase toasts on both |
| **B13 Steam relay transport (spike, 3–5 days)** | `Net/Steam/Transport/SteamSocketsTransport.cs` (copied from the community repo, MIT header kept), `SteamSdrAdapter.cs`; `Net/Core/Transport/SeqWindow.cs`, `BufferPool.cs`; tests | `Net/Session/NetSession.cs` (adapter registration only) | Fixes the 7 defects (§1.3): `CloseConnection` on peer close; pooled buffers + 64-message batch receive; 16-bit sequence for `UnreliableSequenced`; accept lobby members only; the `Initialize` instance, not the singleton; Steam fake lag/loss options; drop the `NetcodeInterop` reference. (Auth moved to B10.) | UT `SeqWindow` (wraparound, stale drop) and `BufferPool` (no per-message allocation, via `GC.GetAllocatedBytesForCurrentThread`). One machine on 480: `CreateSocketPair` loopback carries 10k messages with 0 loss, in order for sequenced, handles closed after disconnect. Two devices: the checklist's 30-minute soak |

**Wave 5: M1 integration (lead).** Checkpoint; the **M1 gate** (§8.7.7); re-baseline the lint; doc edits (LivingWorld.md,
Progression.md, once Progression has landed). Then write the M1.5 package list (`UgsAccountBackend` passing B4's contract fixture;
the `RubbishVault` module + content-table export; sync, conflict card and badges; privacy screens + the account-deleted trigger),
which starts as soon as Nick's steps 9–15 are done.

#### 8.7.5 Dependency graph

```
CK (Nick approves) ─┐
T1, T2(NUnit), C1 ──┼─► G-PKG (window) ──► B3
Progression A hands │
over 2 files ─► L0 ─┤
in-flight content   ├─► W1: B1, B4, B5 ─────────┐
workflows land ─────┤                           ├─► W2 M0 gate ─► C2 (A done with GameSession/EbaeMarket/Ledger)
Chuck lands ────────┴─► W1: B2 ────────────────-┘                  └─► W3: B6 B7 B8 B9 B10 ─► W3b: B14 ─┐
Progression A–D + Yeet landed and accepted ──────────────────────────────────────────────────────────────┴─► W4: B11 B12 B13 ─► W5 M1 gate
Nick's Steamworks + Unity dashboard steps ──────────────────────────────────────────────────────────────────────────► M1.5 (overlaps M2)
```

#### 8.7.6 Commands and the test rig

- **CG (compile):** `unity command recompile` → poll `unity command recompile_status` → `unity command console --level error --tail 50
  --json`. Zero errors in your files; someone else's errors: wait and retry.
- **UT (unit tests):** `Tools/bots/sync_build.sh --tests --filter <Fixture>` (rsync to `~/Dev/Rubbish-build/`, then
  `unity test ~/Dev/Rubbish-build --mode EditMode --filter <Fixture> --report-format junit --output ~/Dev/Rubbish-build/Logs/ut-<pkg>.xml`).
  Exit 0 required. **Never run batch tests against the live project.** One build or test at a time (`mkdir scratchpad/build.lock`).
- **EV (read-only eval):** `unity command eval_file --file <scratch>.cs`. No play mode, no saves.
- **PLAY (under the lock):** `unity command editor_status` must show `playMode == "stopped"` (never interrupt Nick) → `mkdir
  scratchpad/editor.lock` → **`Tools/dev/test_data_root.sh on`** → `editor_play` (from Bootstrap) → probes → `editor_stop` →
  **`test_data_root.sh off`** (in `finally`) → `rmdir`. The "TEST DATA ROOT" watermark must be visible while it's on.
- **MPPM2:** PLAY with Player 2 active (no tag needed since B5; tags `AutoHost`/`AutoJoin`/`AutoWalk` still override); the
  main editor auto-hosts through the same per-project `UserSettings/RubbishDev.json` (`autoHost: true`, expiring; written by
  `Tools/dev/test_data_root.sh on --autohost`), never EditorPrefs. Agents can switch Player 2 on and off from an eval
  (reflection on `MultiplayerPlaymode.PlayerTwo.Activate/Deactivate`); pass `--project-path` to `unity command` while a clone
  runs, or the CLI may talk to the clone. Probe `Tools/bots/eval/mppm_probe.cs` returns
  `NetAudit.SnapshotJson()`.
- **BOTS:** `Tools/bots/sync_build.sh --build` (runs `Setup_Coop.Run`, then `RubbishBuild.DevHeadless`, in the copy);
  `Tools/bots/run_bots.sh -n 4 -m 30 -p crew_bad_wifi [--kill-host-at 600] [--rejoin bot2@120] [--brain …]` launches
  `RubbishBots.app/Contents/MacOS/RubbishBots -batchmode -nographics -nosteam -dataroot <run>/<bot> -playerid dev:bot-N …`;
  `Tools/bots/audit.py <run>` checks the results and exits non-zero on failure.

#### 8.7.7 Milestone gates

**M0 gate (W2):**
1. MPPM2: host and guest connect through the menu / autostart path; both move; one keyboard-paired `PlayerInput` per process.
2. BOTS 3 × 10 min idle/walk on Crew Bad Wi-Fi: 0 errors or exceptions in every log.
3. `--rejoin bot2@120`: same slot and Dumpling variant after rejoining.
4. **Guest data unchanged.** The guest bot's data root is pre-seeded with the v2 and ebag fixtures; sha256 identical after each of:
   a normal leave; the host killed + the guest left running 60 s; `Application.Quit` on the guest while connected. And after
   "leave → Play solo → earn $10 → quit", the files equal the originals plus that $10 only. No file changes under Nick's real
   folder during any bot run.
5. **Corrupt save survives:** L0 gate (a) repeated on the integrated build.
6. Readable rejections: protocol mismatch ("Update your game"); 5th crew member ("Crew is full (4/4)"). `TownFull` is covered
   by B1's unit tests until a Town can hold more than one company (M7).
7. `host.cs` / `host2.cs` regression passes.
8. Relay smoke passes.
9. **Identity stable:** the Editor's key is `dev:editor` with Steam open and closed; Steam boot on and off are both clean.
10. 1,000 kill-during-write cycles: every record still loads (latest or a fallback generation).

**M1 gate (W5):**
1. BOTS 4 × 30 min on Crew Bad Wi-Fi (host `laptop`; guests `walk` + `laptop` + `ritual`): ≥ 150 matched-version hash compares,
   0 mismatches; ledgers balance **in cents** on every process; 0 errors; `GuestWritesDropped` = 0 (no grief brain); guests post 0
   `Ledger` entries.
2. Same curbs, wallet and clock: covered by 1, plus a clock delta < 0.01 h.
3. `--kill-host-at 600`, then relaunch the host on the same data root: the Town's `savedUtc` is within 60 s of the kill; company cash
   equals the last ledger balance; curbs restored.
4. ROLL OUT and CLOCK OUT round trips (B9) inside the 4-bot run.
5. Save slots and import (B8): any company into any Town with timers intact; fixtures exact; untouched records byte-identical;
   restore from session start.
6. A guest laptop purchase (B12).
7. Hostile input (B7): the decompression bomb and the oversized inventory are rejected without a crash.
8. Steam at the door (B10): a forged PlayerKey or a ticket bound to another host is rejected; `dev:` keys rejected under release auth.
9. Caps: the 5th bot joining a Crew Town is rejected with "Crew is full (4/4)" and returns to the menu with its own saves reloaded.
10. **The two-device checklist** (≈ 45 minutes, Nick plus a second device with a second free Steam account; development build with
    `-steam` on both): (a) A hosts with friends; B sees A "playing" and uses **Join Game**; (b) A sends an invite (overlay, where it
    works); B accepts it; (c) cold start: B quits, A invites again, B launches from the invite (`+connect_lobby`); (d) 15 minutes of
    play over `rb_net=relay`; (e) 30 minutes over `rb_net=sdr` with 0 disconnects and a logged round-trip time (needs B13; if it
    slips, (e) moves to the M2 playtest; "Steam relay on by default" is a launch gate, not an M1 gate); (f) B launched with a
    spoofed `-playerid steam:<A's id>` is rejected. M1.5 adds: Take it back across the two PCs.
    *Cheaper paths for most of this:* `CreateSocketPair` loopback (B13), `dev:` keys for approval / slots / leases, and a
    single-machine `SteamGameServer` spike as a second SteamID (*unverified*; also the M8 code path).

#### 8.7.8 `coop_lint` upgrade (T1) and baseline policy

- **New features:** a `// coop:allow(<rule>) <reason>` suppression (parsed separately, because the scanner drops `//` comments);
  `--files <glob>…`; `--json`.
- **New rules:**
  - `save`: `File\.(Write|Append|Replace|Delete|Move|Copy)|PlayerPrefs\.Set|persistentDataPath` outside
    `Assets/Scripts/(Net/(Core/)?)?Save/**` (+ `Core/GameSession*.cs` and `Ebae/EbaeMarket.cs` until M1) (rule 9);
  - `netsingleton`: `NetworkManager.Singleton` inside `Net/**` except `NetBoot.cs` (rule 3);
  - `steamapi`: `using Steamworks|Steamworks\.` outside `Net/Steam/**`;
  - `secrets`: a `Debug.Log*` line mentioning `ticket|token|apikey|secret|password` (case-insensitive) (rule 17).
  - *(Dropped `nolocalrng`: the existing `random` rule already matches `new System.Random(`.)*
- The `cash` rule's message points at `Ledger.*`.
- **Backward compatible while other workflows are in flight:** same baseline format, exit codes and default output; new rules start
  at today's counts.
- **Only the lead re-baselines,** only at a wave merge, after reading the full output. A re-baseline may only lower counts, or raise
  them by allows the lead approved in that wave's report (`cash`, `guid`, `timescale` and `scene` allows in new files always need
  explicit approval). Hits that M0/M1 remove (MainMenuManager `find`/`scene`, PlayerSpawner, CurbTrashSpawner `guid`/`random`,
  ItemCatalog `guid` via ContentRng, Raccoon `random`) must show as removals.

#### 8.7.9 In-flight work: conflicts, notes to send now, order

| Conflict | With | Resolution |
|---|---|---|
| Three money paths (`Ledger`, the planned `Economy.Credit`, `TrickShot` paying through `GameSession.AddCash`) | Progression A, Yeet | `Ledger` is the one call (D1) |
| `TrickShot` records in their own JSON file | Yeet | breaks rule 9 and would be invisible to slots and accounts |
| Receipt triggered "while in ShopScene" | Yeet | rule 6 |
| `GameSession.cs`, `EbaeMarket.cs`, `Ledger.cs` | Progression A | L0 edits only the persistence regions after A hands them over; C2 after A confirms they're final; re-read before editing |
| Host-only systems (raccoon crews, auto-tipper, belt feeder, Magnus sorter, midnight, arm targeting) run on guests | Progression B–D | rule 15 |
| `Trash/**`, `Items/**` | Progression D, Yeet | all M1 edits there (B11) wait for W4; `ContentRng` never edits `ItemCatalog` |
| `ScrapazonStore/App`, `EbaeMarket` | Progression A | B12 waits for W4 and calls A's `TryBuy` (with its `Locked` results) on the host |
| `TruckControllerV2.cs` | Progression B | M0/M1 don't edit it (`TruckNetFeel` is a new component; the horn is polled). For M3: check B's stock brake against the ~1 g braking prerequisite. |
| `Player.prefab`, `ChuckPuppet` | Chuck integration | B2's player part waits; the variant goes through `variantOverride` |
| Package installs reload the shared Editor | everyone + Nick's play sessions | one G-PKG window Nick approves |
| `coop_lint` changes mid-flight | Progression, Yeet | backward compatible (§8.7.8) |
| Progression's `host.cs`/`host2.cs` integration test | Progression | B1 keeps raw `StartHost` working; regression gate |

**Notes to send to the in-flight builders now:**
1. **Yeet:** `TrickShot.Pay` calls `Ledger.Credit(bonus, "Style: " + name, LedgerIds.Style, mouth)`, not `GameSession.AddCash`.
   `ShiftLedger` subscribes to `Ledger.Posted` (not `CashChanged`).
2. **Yeet:** store personal bests with `GameSession.SetCounter("ts_best_cm")` / `("ts_best_streak")`, not a new JSON file (B8 later
   moves them to `ProfileStore.SetSection("trickshot", …)`).
3. **Yeet:** trigger the receipt from a depot zone flag or the TIP IT event, not a scene-name check (or justify with
   `coop:allow(scene)` until M7).
4. **Progression A–D:** host-only systems (raccoon crews, auto-tipper, belt feeder, Magnus picks, midnight, arm targeting) decide only when
   `!GameSession.IsGuest`. And **A:** please hand the persistence regions of `GameSession.cs` (:387-575) and `EbaeMarket.cs`
   (:550-652) to the lead as early as you can for hotfix L0, and tell the lead when `GameSession.cs`, `EbaeMarket.cs` and `Ledger.cs`
   are final (that starts C2).

**Recommended order:**
1. **Now:** T1, T2, C1; book G-PKG; send the four notes; ask Nick for CK.
2. **L0** the moment Progression A hands over the two persistence regions (don't wait for everything else).
3. Chuck integration lands (unblocks B2's player part).
4. Progression B–D and Yeet land (W1 could overlap them by file ownership, but Nick's ordering waits).
5. **W1 → W2** (M0 gate).
6. Progression A confirms its three files are final → **C2 → W3 (+W3b)**.
7. Progression A–D and Yeet acceptance passes → **W4 → W5** (M1 gate).
8. **M1.5** once Nick's Steamworks and Unity dashboard steps are done (`Accounts.md` §14, steps 9–15); it can overlap M2.

**M2 (Grab & Yeet Lite)** comes after M1. It overlaps the Yeet slice most heavily (`Carry.cs`, `TrashBag.cs`, `TruckHopper.cs`,
`FeelJuice` hit-stop), so it needs Yeet fully landed.

#### 8.7.10 Progress: M0 integrated (W2, 1 Oct 2026)

**Landed (W0 + W1):** T1, T2, C1, L0 (lead), G-PKG (lead), B1, B2, B3, B4, B5, **B10 pulled forward** from W3 (Steam lobby,
presence, join routing and auth at the door, built with B3 for the Windows playtest), and a new package **W** (Windows playtest
prep: `Docs/WindowsBuild.md`, `RubbishBuild.BuildWindowsPlaytest`, the `Windows Playtest` build profile, the
`RUBBISH_PLAYTEST` define). **CK:** Nick uses Unity Version Control without the `cm` CLI, so agents can't check in; every
"checkpoint" is Nick's check-in. Still to come: C2, then W3 (B6–B9; B10 is done), W3b (B14), W4 (B11–B13).

**M0 gate (§8.7.7), run on the integrated tree on this Mac:**

| # | Result | Evidence |
|---|---|---|
| 1 | PASS | MPPM2 (Player 2 switched on from an eval): main hosted LocalDirect, the clone joined by itself; roster `dev:editor` slot 0, `dev:mppm` slot 1; exactly one keyboard-paired `PlayerInput` per process; slot == Dumpling variant on both; the guest walked 15+ m (AutoWalk), the host walked 5.6 m and the guest saw it; hash probes 10/10; 0 errors on both; the clone's own save untouched after quitting while connected. |
| 2 | PASS | `run_bots -n 3 -m 10 -p crew_bad_wifi` with **no** error allow-list (after the W2 cloth fix below): 0 errors, 0 exceptions, 476 hash compares (238 objects + 238 roster), 0 mismatches, every bot exit 0 after ~600 s, guests walked ~790 m each. (The first run, before the fix, had only the 6 allow-listed cloth errors.) |
| 3 | PASS | `--rejoin bot-2@120` (twice): bot-2 left slot 1 (client 2) and came back as client 3 in slot 1, so the same Dumpling (RosterView variant = slot); audit PASS with 0 errors and 188 matched hash compares. |
| 4 | PASS | Guests seeded with a settled v2 + ebag pair: sha256 identical after Application.Quit while connected (SIGTERM, "quit refused: saving is paused"), a normal leave + quit, and the host SIGKILLed at 60 s with guests running 120 s more. Editor as a guest of a bot host: files unchanged through join, a dropped `AddCash(999)`, and leave; then Play Solo + `Ledger.Credit(10)` + quit changed only `cash` +10, `totalCashEarned` +10, the `st0_credits` stat and `gen`. Nick's real folder unchanged in every run. |
| 5 | PASS | L0 gate (a)+(b) harness on the integrated code: 567/567 checks (42 truncations × with/without a generation, the mixed-gen pair, the latch, plus 5 new W2 checks). Live: a truncated `rubbish_save.json` recovered from `latest.1` with the toast and a `.corrupt-*` copy; quit rewrote both files whole at the next gen. |
| 6 | PASS | `-p none`: proto 999 → "Update your game: you and the host are on different versions."; 5 bots with `--guest-stagger 4` → bot-5 refused with "Crew is full (4/4)." (`CompanyFull`). `TownFull` stays covered by B1's unit tests. |
| 7 | PASS | `host.cs` + `host2.cs`: StartHost=True into ShopScene, adopted as a LocalDirect session, roster `dev:editor@0 host`, player spawned, 0 errors. |
| 8 | PASS | `run_bots --relay -n 2 -m 1`: Unity Relay code RMK6LM read from the host's audit log; guest joined; 11/11 hash compares. |
| 9 | PASS (Steam half untestable here) | Editor: `[Identity] dev:editor as "editor" (Editor; Dev (Editor default); Steam off)` in every play session; the decision doesn't read Steam's state. Bots log "Steam skipped". No Steam client on this Mac, so "Steam open" is Nick's PC test. |
| 10 | PASS | `Tools/bots/store_torture.sh -n 1000` on the integrated player: 1,001 launches verified, 1,000 SIGKILLs (47 mid-write, 30 recovered from a finished temp file), lock free at once 1,000/1,000, 77,062 acknowledged writes, real folders unchanged. |

**Single-player regression (W2, Play Solo, test data root):** drove out, picked up a curb bag and tossed it in the hopper
(+$6), drove back in, TIP IT, ripped the bag on the sorting table (5 items), sorted Junk Mail into PAPER (+$0.11); progress
saved and reloaded in the next session; 0 errors, 0 warnings.

**Changed at W2 (integration fixes):**
- `Save/LegacySave.cs`: a quit/pause/scene-change flush with nothing dirty and a state equal to what the main files hold
  (gen ignored, ebag offer timers ignored) no longer rewrites them under a new gen. That made gate 4 possible and stops an
  idle quit from churning Nick's backups. A recovered or missing file is still rewritten at the next flush.
- `Editor/RubbishBuild.Windows.cs`: the Windows playtest build runs `Setup_Coop` first (no changes when up to date), so a
  friends' build never ships a stale NetSession prefab (B2's suggestion).
- `Tools/bots/run_bots.sh`: `--guest-stagger SEC` (makes "the 5th to knock" deterministic) and a rejoined bot plays only the
  minutes left in the run.
- Content: `Assets/Models/Office.fbx` Read/Write on, so ShopScene's cloth works in builds (it logged "Cannot initialize
  Cloth" in every player; B5's note blamed `office.obj`, but the cloth's mesh is `Office.fbx`'s `group2013451613`).
  `Tools/bots/known_errors.txt` is empty again, so bot audits need no allow-list.
- Docs: `WindowsBuild.md` and the friends README (no passenger seats yet; per-machine trash; the Steam self-test; check-in
  list), `CoreLoop.md` (multiplayer status, ownership, assemblies, never save Bootstrap, setup order), `Character.md` §14
  (variant = roster slot), `Accounts.md` §14 step 4 and §8.7.6 (no MPPM tag needed).
- Lint re-baselined (only lowered): `Core/SafeFile.cs|save` (the file moved to `Net/Core/Save/`), `MainMenuManager.cs|find`,
  `MainMenuManager.cs|scene`, `Status/RabiesStatus.cs|cash`. Allows approved at W2: `NetSession.cs` `HostOptions.firstScene`
  [scene] (until B9's ZoneId flow), `BotBrains.cs` grief brain [cash] (B7's gate needs the forbidden call), `DevIdentity.cs`
  [save], `CoopAutoStart.cs` [save], `NetAudit.cs` [save], `SaveTorture.cs` [save]; `Ledger.cs` [cash] was approved 30 Sep.

**What moved or differs from the plan text above:**
- B5: NGO 2.13's `SetDebugSimulatorParameters` does nothing; `NetSimPresets` adds UTP's simulator stage instead. MPPM needs
  no tags. M0 gate 10 uses B4's `Tools/bots/store_torture.sh` (B5's `savetorture.sh` is the lighter drill).
- T2: the build lock is `~/Dev/Rubbish-build.lock`; `Tools/bots/sync_build.sh` replaces the lead's `Tools/sync_build_copy.sh`
  (retire it: its unanchored excludes broke the copy's PackageCache).
- B3/B10: playtest and development builds only *warn* at the door ("would refuse …"); release builds enforce.
  `RUBBISH_PLAYTEST` players use Steam unless `-nosteam`; the Editor is always `dev:editor` unless `RUBBISH_EDITOR_STEAM`.
  `Rubbish.exe -steamselftest` checks the 480 lobby path on a PC with Steam.

**Fixed after W2 (fixer, 1 Oct 2026):**
- Guest earnings no longer leak into the player's own daily stats. `DailyStats` cached today's numbers and
  `SaveGuard.ReloadOwn` didn't refresh them, so a guest's local cash (C2's `BlockGuestWrite` isn't in yet) reached
  `st0_credits` after they left (W2 saw $1,009 for a $10 solo credit). Now `DailyStats` and `Progression`'s fact handlers
  ignore cash/Rep while `GameSession.IsGuest` or (in play mode) `SaveGuard.Foreign`, a guest's Rep can't promote them, and
  `ReloadOwn` re-reads `DailyStats` and `WorkOrders` (new `WorkOrders.Reload()`). Re-run of gate 4's Editor-as-guest case:
  the guest's `AddCash(999)` + `AddRep(5)` left `st0_credits`/`st0_rep` at 0 and both files byte-identical; after leaving,
  Play Solo + `Ledger.Credit(10)` + quit changed only `cash` +10, `totalCashEarned` +10, `st0_credits` 0 → 1000, and `gen`.
- `SafeFile` (Windows): every file step of a save (temp file, replace/move, generation rotation) is tried up to 5 times
  (retries 20–80 ms apart, ≤ 0.2 s, only after a failure) when it hits a sharing violation or "access denied", which on
  Windows is usually Defender or the search indexer holding a file we just closed. Missing files, too-long paths and a full
  disk aren't retried. Without this, a scan could turn an ordinary save into "Recovered your save from a backup" on the next
  launch.
- `NetSession`: Host, Join and Play Solo refuse with "Rubbish needs Steam. Start Steam and sign in, then press Try again."
  while `PlayerIdentityBoot.SteamMissing`, and `LocalPlayerKey` never caches a dev fallback then (Accounts.md §1.2: no
  silent dev identity in a Steam build).
- `SteamAuthVerifier` + `PendingAuthTable.TryTakeOver`: the same SteamID joining again while its old, approved connection is
  still open now ends the old auth session and checks the new one (B1's newest-connection-wins), in Enforce mode too. Two
  joins at the door at once are still refused.
- `Editor/Setup_Ebae.cs` "Wipe ebag Save" only works in play mode (through `EbaeMarket.ResetAll(true)`, backups kept); in
  edit mode it explains instead of deleting the file.
- `WindowsBuild.md` §1.0 check-in list: `Assets/Models/Office.fbx.meta` (W2's cloth fix), not `office.obj.meta`.
- Verified on the final tree: dry compiles clean for the Editor and for the Windows playtest player (`UNITY_STANDALONE_WIN`,
  `RUBBISH_PLAYTEST`, `DEVELOPMENT_BUILD`); edit-mode tests 136/136; bot regression with `--rejoin` and the gate 4
  Editor-as-guest case pass; `store_torture` 400 (Mono) + 200 (player) kill cycles pass; no workspace file name or path that
  Windows or Plastic would reject.

**Open items carried forward (owner):**
- Readable refusal text can arrive after the disconnect on lossy links, so the guest sees the generic "turned away" card
  ("…crew may be full (4 max), or you're on different versions"). NGO sends the reason and the disconnect in the same frame;
  a fix needs a delayed disconnect after a refusal (B1/C2). Low impact: the fallback sentence names both likely causes.
- A guest notices a crashed host only after UTP's 30 s `DisconnectTimeoutMS` (Bootstrap; `NetBoot` could shorten it at
  runtime, but a shorter timeout also drops guests while a slow PC loads a scene).
- `TruckNetFeel` frees a disconnected driver's seat by reflection; Progression B should add `TruckControllerV2.ServerReleaseDriver()`.
- When a joining SteamID takes over an old connection and then fails Steam's check (Enforce only), the old connection stays
  in the game without an open auth session until it times out (it was already verified when it joined).
- Nick's real data folder holds a stray `TestResults.xml` from a T2 test run (harmless; delete by hand).
- Not testable on this Mac, so first run on Nick's PC: the Windows player itself, Steam lobby / presence / invites / Join
  Game / auth proofs, the overlay, Windows file locking under Defender, and a real Windows guest's keyboard.

---

## 9. Rules for new code, starting now

These cost nothing today and save weeks of rework later. They apply to every agent and every system being built in parallel,
**Progression included**.

1. **All cash, Rep and unlock changes go through one call: `Ledger.Credit/Charge/Spend/Rep`** (Progression's `Ledger`, which
   landed 30 Sep 2026; it replaces the planned `Economy.Credit/Spend`, and `Economy` stays the tuning-constants class). No new direct
   `GameSession.AddCash` calls. The source player comes from an ambient `LedgerActor` scope set by host request handlers; the company
   from `CompanyState` (M7 adds a company scope). Things that count income (daily stats, receipts, Work Orders) listen to
   `Ledger.Posted`, never `CashChanged`.
2. **Anything that decides value or an outcome is rolled in one host-side RNG service (`ContentRng`, M1) with integer weights.** No `UnityEngine.Random`,
   `System.Random` or `Guid` in gameplay content. Purely cosmetic randomness (particles, debris, wobble) may be local. Never call
   `GameSession.NewUid()` for content.
3. **No `FindFirstObjectByType` or `static Current`** for the truck, depot, station, route or laptop. Pass references or a company ID.
4. **No `Time.timeScale` for juice effects.** Use local camera or animation freezes.
5. **Separate "decide" from "present".** Gameplay decisions go in methods that could run on the host only; cosmetics go in separate
   methods that can become `WorldFx` handlers.
6. **No scene-name checks.** Use zone triggers or a `ZoneId` (Street, District n, Depot n).
7. **Per-player state lives in a per-player struct keyed by PlayerKey**, never in a global (rabies is the counter-example today).
8. **`PlayerController.Local` is for camera, HUD and input only.** Never use it for AI targeting, pay or rules, and never assume the
   host has a player.
9. **Saved data goes through `TownStore`, `CompanyStore` or `ProfileStore`** (on `ISaveStore`/`IAccountBackend`), never through
   `File.*`, `PlayerPrefs` or `persistentDataPath` directly (lint rule `save`). Until M1's stores exist, it goes into `GameSession`
   (`SetCounter`, `SetStamp`) so it rides in the company. World data goes in the Town, shop data in the company, personal data in the
   Crew Card. **Money is stored in integer cents; company timers as remaining in-game hours.** Every new field has a default and
   survives a round trip through a game that doesn't know it. A host never writes another player's data; it sends ledger deltas.
   No new per-machine JSON files.
10. **New NetworkBehaviours copy the ChessController pattern:** the host holds state in NetworkVariables; `ServerRpc(RequireOwnership
    = false)` for requests, with validation; `ClientRpc` for cosmetics; explicit sync for late joiners.
11. **Progression is company-scoped.** Rank, cash, districts and permits live in the company record. Only Career level and titles are
    portable, and they're cosmetic. **Prices never scale with player count.** District pacing comes from percentage-based Clean Days.
12. **Every new player-vs-player or physical action declares, before it's built:** its cooldown, its counter, its maximum helpless
    time, its preset toggle, and which machine detects it.
13. **No gameplay outcome may depend on two networked dynamic rigidbodies colliding.** Thrown things collide with the static world,
    trucks and triggers. Hits on players and critters are decided by rule (§5.2).
14. **Before reporting a task done, run `Tools/coop_lint.sh --files '<your globs>'`** (or grep your diff) for `GameSession.AddCash`,
    `UnityEngine.Random`, `Guid.NewGuid`, `NewUid(`, `FindFirstObjectByType`, `.Current`, `GetActiveScene`, `.name ==` on scenes,
    `Time.timeScale`, `PlayerController.Local`, file writes, `NetworkManager.Singleton` inside `Net/**`, `Steamworks` outside
    `Net/Steam/**`, and logged tickets or tokens. Fix every new hit or mark it `// coop:allow(<rule>) <reason>` and list it in your
    report. Only the lead re-baselines (§8.7.8).
15. **Host-only systems decide only on the host.** Anything that spawns, rolls, pays or ticks the world (the spawner, raccoon crews, the
    auto-tipper, the belt feeder, Magnus picks, midnight, ebag offers, TrickShot pay) runs only when `!GameSession.IsGuest`. On a
    guest the mirror would drop its writes anyway, but anything it spawned locally would be a duplicate.
16. **Guests never mutate `GameSession`.** A guest sends a request (an RPC through `NetGuard`), and the host applies it. Guest-side
    mutators are dropped and counted (`NetAudit.GuestWritesDropped`).
17. **Never log secrets.** No `Debug.Log` of Steam tickets, UGS tokens, keys or session secrets (lint rule `secrets`). Players
    paste `Player.log` on Discord.

---

## 10. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Schedule.** The codebase has ~39 files touching `GameSession`, ~35 uses of `PlayerController.Local`, ~22 singleton or `.Current` lookups, dozens of scene checks, `SortStation.cs` at 2,046 lines, and parallel features that keep adding more. | Crew Up + Depot Row is ~6 months of focused work, not 10 weeks | Crew Up Lite first; batches gated by playtests; §9 rules now; a lint baseline |
| **Networked carryables changing hands** (M2) | The core verb feels laggy or glitchy | Ownership moves only at grab, pass, catch, tug start and settle; the heave window; thrower-detects; lag compensation; the Fusion spike if the gate fails |
| **Today's data-loss bugs:** a truncated save is overwritten with a blank one on quit (`loaded` set before the parse); the guest guard isn't sticky (host crash, quit order); `EbaeMarket.Save` has no guard; files are overwritten in place | A crash or a co-op session wipes real progress | Hotfix L0 first (§8.7); don't co-op on real saves before it lands |
| **Identity or data-root mix-ups** (Steam open one day, closed the next; agents or bots writing Nick's save) | "My progress is gone" | Identity fixed per build, never by whether Steam is running; `-dataroot` and an expiring per-project test root with a watermark; the bot audit fails if the real save folder changes (`Accounts.md` §1.2, §5.1) |
| **No rollback for agents** (Plastic workspace, no `cm` CLI, not a git repo) | A bad `Setup_*` run or merge can only be undone from Nick's last check-in | Local checkpoint (git or `cm`) committed at every wave merge and before every `Setup_*` run (§8.7, W0) |
| **Cloud backend operations for a solo dev** (Unity service churn, redeploys on every balance patch, abuse, cost) | Honest saves rejected after a patch; surprise bills; a vendor exit | Content tables per content version (unknown = clamp-only); Steam-only production sign-in, rate limits; usage alerts; `IAccountBackend` + monthly export + Supabase exit (`Accounts.md` §12) |
| **A hostile host** (modded) in a friend's Town | Inflating or draining a visitor's company | Hosts never write guests' records; the owner's Guard caps credits and fines; "Restore from before this visit"; Cloud Code clamps (`Accounts.md` §7) |
| **No host migration** | The session ends when the Boss leaves | Autosave, Crew Card gains within 10 s, company snapshots ≤60 s, a friendly Shift Report; Community servers for always-on worlds |
| **Brought companies were trusted files** | A friend brings a hacked company | Now only Cloud Code writes companies (structural cheats rejected, earnings clamped) and an unsynced company can't be brought; the host can still choose Fresh Start; Community servers default to resident companies; no leaderboards |
| **The two-scene structure** survives until M7 | Crew can't split up before Depot Row | The ROLL OUT ritual makes it a feature; M7 switches to Neighborhood + one additive DepotRow scene |
| **Additive interior lighting** | Interiors look wrong when the Neighborhood owns `RenderSettings` | Per-machine interior mode (lights + ambient set by where the local camera is); `DayNightSky` yields indoors |
| **Weak host upload** at 8 players (~0.9 Mbps) | Rival Towns lag on bad connections | Half precision in M0; caps; seeded local TIP IT and bursts; an 8-player hard cap; Community servers for more |
| **Game-feel prerequisites:** ~2.1 g braking, global `Time.timeScale` hit-stop, critters targeting only the local player | Riders fly off at every stop; hit-stop freezes everyone | Fixed in M3, M2 and M6 respectively |
| **Balance:** co-op cash ~2× faster than solo | Solo feels slow, or crews break the curve | Prices tuned for solo; district pacing from percentage-based Clean Days; tune once Progression.md exists |
| **Griefing and souring friend groups:** tugs, Raccoon Crate, the Crown, Rabies Tag, PACK, Sticky Fingers | Some groups stop playing | Presets and toggles; consent states; immunity; rip billing; litter tabs; receipts that always show who did what; spicy verbs last |
| **Strangers and minors** (a "Roblox-style" pitch attracts under-15s) | Moderation and legal duties (UK Online Safety Act, EU DSA) a solo developer can't staff | Friends-only launch; public listing gated at >500 CCU; crew-only voice with strangers; word-picker names; Steam report; a privacy policy before any public listing |
| **Vendor and service dependencies** (Vivox and cloud saves need Unity Auth; the Steam transport is a community package with 7 known defects) | Voice or save-sync outage; transport bugs | `IVoiceService` with a Steam-voice or Dissonance fallback; joining never waits on voice or the save backend; 3–5 days budgeted to fix the transport, Relay as a remote-flag fallback, "Steam relay on by default" as a launch gate |
| **Testing load vs Nick's play sessions** | Agents block the editor | A headless bot harness on builds from a synced copy; MPPM only under the editor lock |
| **Estimates are estimates** | Bandwidth and frame numbers are unmeasured | Measured at M2, M3 and M7 before anything depends on them |

---

## 11. Decisions (answered 30 Sep 2026)

**Nick's answer (verbatim):** "yes lets have your shop travel with you. each players account (can we connect it to their steam
profile and store their save data in our own database?) so like they join a world with friends (host a world or join a world) and
pick a save they can load into any world. yeah bonk shove and carry a flopped friend are all great lets make it chaotic. steam only
launch. 8 per town is perfect. whatever you think would be more fun on tug of war. go for it."

| # | Question | Decision | Decided by |
|---|---|---|---|
| 1 | Does your shop travel with you? | **Yes, and further than proposed:** every player has a **Steam-linked account** whose saves live in our own database (UGS Cloud Save, written only by our Cloud Code), cached locally. 3 company slots + up to 5 Towns; any company loads into any Town you host; joining friends: as crew (M1–M4), Moonlighting (M5), your shop on their Depot Row (M7). Design: `Accounts.md`. | Nick |
| 2 | Crew Up Lite scope (scene swap as a ROLL OUT ritual; tugs, night co-op and spicy verbs in playtest-gated batches) | **Yes, as recommended.** | lead, under "go for it" |
| 3 | Default chaos in friend Towns | **Crew preset, chaotic:** bonk, shove, carry and throw a flopped friend, PACK; spicy toggles on. `TownRules.CrewDefault`. | Nick |
| 4 | Steam-only launch? | **Yes.** Steam relay for play; Unity Relay for development and as an off-by-default fallback. | Nick |
| 5 | Sizes | **8 per Town, 4 per company.** A Crew Town has one company until M7, so it holds 4 until Depot Row (raising it is one constant; `Accounts.md` §15). | Nick (8 per Town); lead (4 per company, per the plan) |
| 6 | Spicy toggles | **On by default in Crew Towns**, always on the receipt, always toggleable, built last (M6). | lead, under "lets make it chaotic" |
| 7 | Tug style | **Rhythm tug:** timing wins, mashing rips the bag, and the masher pays (§4.6). | lead ("whatever you think would be more fun") |
| 8 | Voice | **Vivox first behind `IVoiceService`**, as recommended. UGS sign-in is needed for accounts anyway, so it's one identity system. | lead, under "go for it" |
| 9 | Progression pacing | **Agreed as recommended:** prices never scale with player count; district permits gated by Business Rank from percentage-based Clean Days. | lead, under "go for it" |
| 10 | Strangers | **Friends-only at launch**, as recommended; Community servers are the "public" path. | lead, under "go for it" |
| 11 | `coop_lint.sh` now? | **Done.** `Tools/coop_lint.sh` + baseline exist; wave 0 extends it (§8.7.8). | built |

**Still open** (each has a default the build uses): 3 company slots and 5 Towns; Crew Town 4 vs 8 before Depot Row; Moonlighting
at M5; Duplicate company; 5-minute cloud cadence; clamp vs reject; account-wide Crew Card; 30-day "Recently deleted"; the studio name
in Player Settings. See `Accounts.md` §15.

---

## 12. Red-team review: what changed

The red team attacked the merged plan from five angles: (a) technical, (b) cost and operations, (c) griefing, (d) fun, (e) scope. Its
verdict was that the foundations hold (NGO host mode, host-owned Town, owner-simulated hands and truck, trash as records, Steam relay),
but that parts of the netcode contradicted each other, Rival Towns were unfair to non-hosts, and the schedule was about half of reality.

**Round 2 (30 Sep 2026)** attacked the accounts design and the M0 + M1 build contract: 29 attacks (2 critical: a corrupt save is
wiped on quit, and the guest save guard isn't sticky). All 29 were accepted or partly accepted; the table and what changed are in
**`Accounts.md` §16**. The build-plan fixes are folded into §8.7. The round-1 table follows.

| # | Attack | Severity | Verdict | What changed |
|---|---|---|---|---|
| T1 | A 250 ms tie window and instant owner throws can't coexist (a thrown bag would snap back into a tug) | High | **Accepted** | Grab = 0.35 s heave with throws locked; the host confirms at once; the contest window opens only if someone is within 3 m; throws are buffered until HELD. "No thrown bag ever reverts" is an M2 gate. (§4.6) |
| T2 | Catches detected on the catcher's machine lose to bonks, so relays glitch | High | **Accepted** | The thrower detects catches too; an empty-handed crewmate facing the throw always catches; PASS extended to 8 m with aim assist. (§4.5) |
| T3 | Range checks against a moving truck allow 0.15 s but the real gap is 0.3–0.4 s, so guests can't catch a host-driven truck | High | **Accepted** | 1 s pose history per truck and player; claims carry the press tick and position and are checked against rewound poses. New gate: guests within 5 points of the host. (§4.4, §4.12) |
| T4 | Grip drains before the rider sees the corner, and flings go the wrong way | Medium | **Accepted** (first option) | `RideStress` is tick-stamped and buffered to the rider's interpolation time; fling direction comes from the interpolated velocity. *Rejected* the per-rider derivative option (jitter would cause false flings). (§4.4) |
| T5 | Objects owned by different machines collide differently on each screen | High | **Accepted** | A collision matrix with an `InFlight` layer; bursts only from static-world hits; host nudges for resting items; rule 13. (§5.2) |
| T6 | Turning off truck-vs-player collision makes trucks ghosts | Medium | **Accepted** | One-way collision resolved on each player's own machine, with a stumble above ~3 m/s. (§5.2) |
| T7 | Float rolls differ between Mac and Windows, and every client knows every bag's contents | Medium | **Accepted, extended** | The host rolls **whole manifests** (not only valuables) with integer weights, sent on open or burst; only cosmetic litter is local; X-Ray Eyes is a host reveal. (§6.3) |
| T8 | NGO has no per-client send rates or interest management, so 12–24 player Towns don't hold | Medium | **Accepted** | Hard cap of 8 for player hosts; half precision in M0; 12–16 only on Community servers; custom snapshots if ever needed. (§2.2, §5.3) |
| T9 | Rider cameras judder on an interpolated remote truck | Low | **Accepted** | Dead reckoning from the 10 Hz velocity, a larger truck interpolation buffer, camera damping in truck space, Rigidbody interpolation for the driver. (§4.4) |
| T10 | A `DepotPlot` spawned at runtime leaves its nested NetworkObjects dead on clients | Low | **Accepted** | All plots placed at edit time and hidden; "founding" only reveals and binds. (§3.3) |
| O1 | Public Towns create a moderation job one person can't do (and bring legal duties around minors) | High | **Accepted** | Crew-only voice with strangers, word-picker names, Steam report instead of a custom backend, public listing conditional. (§4.11) |
| O2 | A build-hash join lock turns every patch into a support ticket | Medium | **Accepted** | Protocol and content versions split; host-rolled content makes content mismatch harmless; one public branch plus a beta; automated save-migration tests. (§6.5) |
| O3 | Our own servers (old tier 4) are the wrong path for a solo indie | Medium | **Accepted** | Replaced by player-run Community Town Servers ($0 to Nick). Official servers are off the roadmap. (§1.4, M8) |
| O4 | A public server browser won't have enough players at indie scale | Medium | **Accepted** | No browser at launch; friends, invites, codes and Discord. Public listing only above ~500 CCU. (§2.7) |
| O5 | The cost table overstates Relay and understates the voice dependency | Low | **Accepted** | Steam-only launch, Relay for development only; `IVoiceService` with a swap-in; joining never waits on voice. (§7) |
| G1 | One player can rip every bag another player touches | High | **Accepted, extended** | Rip billed to the side with more strain; level goes to the holder; stricter qualifying second grabs; 10 s pair cooldown; the Vest blocks heave tugs; **and the rhythm tug makes mashing rip immediately at the masher's cost**. (§4.6) |
| G2 | Rival trucks are unpushable walls, so griefers can block roads and doors | High | **Accepted** | Rival trucks are ghosts to each other in Rival and Open presets; the tow rule is cumulative and only applies inside someone's apron. (§4.11, §5.2) |
| G3 | Chained stuns or forced carrying become frustrating | High | **Revised by the October 2026 punch-combo decision** | Three quick punches cause a 3.5 s carryable knockdown. Earlier punches are short; after the knockdown, hit immunity prevents re-comboing, and a carried player can wriggle free. Guest playtesting must judge whether this chaos feels fair. (§4.5) |
| G4 | Strangers who join your company get the keys | High | **Accepted** | Helper is the default role; a daily allowance replaces the per-purchase limit; cheap listings need a co-signer; two members can demote a third in public Towns. (§3.6) |
| G5 | Grant farming, plot squatting and alt accounts | Medium | **Accepted** | One grant per player ID per Town; strangers' companies are session-only in public Towns; public Towns are Steam-only (an alt costs a copy of the game). (§3.4, §3.5) |
| G6 | Shared goals become a griefing lever (litter, critters led into bays, Crown bullying) | Medium | **Accepted** | Per-company litter tabs; critters can't enter depots; the Crown can be knocked loose from the same holder at most once every 10 s. (§4.6, §4.10) |
| F-credit | Friends compete for wage and finder credit (hopper-campers and station hogs) | Medium | **Accepted, own split** | Wages split 8/4/3 (curb lifter, hopper feeder, driver), 15% of sort pay to the sorter; finder = curb lifter, with a "Revealed by" stamp for the sorter; station rotation. (§4.7, §4.9) |
| F1 | Rival Towns punish anyone who isn't online when the host is (customers open after 50 real seconds, dormancy after ~4.7 h) | **Critical** | **Accepted** | Companies live on their founder's machine and travel with them; turf lasts one session (League Night); Community servers use real-time frozen customers and 14 real days to dormancy. (§3.1, §3.5, §4.6) |
| F2 | 1v1 tugs have no skill (both players hit the mash cap, so it always rips) | High | **Accepted** | The rhythm tug: GOOD pulls on the beat, sloppy pulls add strain, capped at 4 beats, level goes to the holder; plus an Auto-pull accessibility option. (§4.6) |
| F3 | Co-op turns into one driver plus idle friends, and upgrades automate friends away | High | **Partly accepted** | Shotgun gets navigator, spotter and arm-operator jobs; Magnus steps back when humans sort; the side-loader is operated; phases overlap on Depot Row. **Rejected:** an early second truck (it splits the crew and the clip stage). (§4.3) |
| F4 | The starter map is too small for Rival Towns and too thin for 4 players | High | **Accepted** | 2 companies on District 1; 3–4 opens District 2 as overflow; spawn rate scales with players; a Big Job each shift. (§3.7) |
| F5 | The Safety Vest always wins, so public fights disappear | Medium | **Partly accepted** | The vest costs 10% pay and blocks poaching and customer steals. **Rejected:** a 15% cost (it's an anti-harassment tool, and pricing it high pushes kids into PvP they didn't want). An opt-in contested Scrapyard zone waits until a public listing exists. (§4.11) |
| F6 | Co-op progression ~2.2× faster breaks solo balance | Medium | **Partly accepted** | **Rejected:** scaling prices (a friend joining should never make things cost more). **Accepted:** pacing districts by percentage-based Clean Days, which is crew-size neutral, as a rule for Progression.md. (§4.7) |
| S1 | The 10-week Crew Up estimate misses most of the single-player coupling | **Critical** | **Accepted scope cut; rejected the 4–5 week figure** | Crew Up Lite is about 8–9 focused weeks (the shared shift in M1 is real work), with the first fun internet session around week 5. The full plan is about 6 months. (§8) |
| S2 | M1 "One Town" is the riskiest step and blocks the first fun session | High | **Accepted** | Keep the scene swap for Crew Up as a ritual; Depot Row uses pocket interiors in **one** additive scene (not duplicate ShopScene loads); physical bays only if playtests ask. (§3.3) |
| S3 | Parallel agents won't follow the coding rules without enforcement | High | **Accepted** | Rule 14 (grep your diff) now; `coop_lint.sh` with a baseline is the first tool at "go", or now if you want it (question 11). (§9) |
| S4 | Multiplayer testing will collide with Nick's play sessions | Medium | **Accepted** | A headless bot harness on builds made from a synced copy of the project; MPPM only under the editor lock. (§8.4) |
| S5 | The ruleset is too big to tune | Medium | **Accepted** | Verbs ship in batches (Lite → Sort & Squabble → Night + spicy → Depot Row), each gated by a playtest; verbs that don't produce clips get cut. (§8.2) |

---

## 13. Neighbours: the co-op model from 2 Oct 2026 (supersedes the Crew / Moonlighting / Rival Towns parts of §8.2)

**Nick (verbatim):** "friends joining your world allows them to start a new garage business alongside yours. it dynamically expands
depot row and plops a shop down. people can customize their shop sign/business name. it would be "Nick's [in small] Rubbish Co. or
whatveer the current nsme is by default, and [So and so's] Rubbish Co. or whatever the current name is by default. next door
neighbors. people join as a different color. they get a truck that matches their color (4 truck colors and player colors pls). you
can HIRE someone working in your world to work for your company instead, it pays them hourly (in game hours). we can set their
salary. we can fire them. no need for a passenger seat on the truck, but employees get to drive the truck for another business.
employees get to help sort unload and smash within your shop. if youre not an emloyeed you can always hitch a ride on the back
(employee or not), and you can enter other shops, but not interact with trash inside. bags and trash are global. but depending on
the amount of people online, scale the rate at which trash is produced by the city. other people in the session can interact with
your listings on ebag (i can sell and trade valuables with people in the session. under the same accept offer and make offer rules
as NPCs)." … "and let people free roam between scenes."

### 13.1 The model in one paragraph

A Town is one shared world. Each player owns a **business** (their company, loaded from their save slot; a new player starts a fresh
one). Businesses sit **next door to each other on Depot Row**, one lot each, in the owner's colour. Trash is global: anyone can pick
up any bag, and the business whose truck or hopper receives it gets paid. Players can **hire** each other; an employee works for the
employer's business (drives its truck, unloads, sorts, smashes) for an hourly wage. Everyone can roam anywhere, visit any shop and hitch
a ride on the back of any truck; only the owner and their employees can touch the trash and machines inside a shop. Valuables can be
sold and traded between players on ebag.

This replaces "4 players in one company" (Crew) as the default. Joining a friend no longer means working in their company; it means
opening your own shop next door, and *choosing* to work for someone if you want to.

**Business-save parity (current):** the main menu asks HOW first and WHICH save second (Nick, 2026-10-09: "first choice
should be solo or play with friends THEN a separate choose a save or new menu. same for join"). **Play Solo**, **Host with
Friends** and every JOIN on the menu (code, a hosting friend, an invite card) open the same step: the original save and every
additional business (including those created while joining a friend), plus **Start a new save**; picking one goes straight
on. The **Continue** card is still one click on the last save. There is no separate "Choose business" button any more. A join
that starts outside the menu (Steam's Join Game or an invite accepted in the overlay) still asks once you are in the friend's
Town, as before, and so does a host's refusal ("that business is already open here").
The selected solo/host business is remembered across restarts; joining may bring a different business and leaving restores the
previous solo/host selection. Cash, upgrades, inventory, ebag and progression stay in that business's existing file. Session jobs
and another host's world are not imported. New menu-created businesses get a new slot; nothing resets the original business.

Storage remains backward compatible: the original `rubbish_save.json`/`ebae_save.json` pair stays in place; additional slots remain
atomic `accounts/<key>/businesses/<id>.json` blobs. A small account `selected-business.json` pointer selects the save routing.
Selection flushes old progress first, validates the destination identity and both payloads, and only commits the pointer after a
clean load. Missing/corrupt selected slots block saving instead of writing defaults; readable backups remain selectable. The picker
is paginated rather than silently omitting businesses after the fifth. No destructive migration or host-written guest save is used.

### 13.2 Depot Row and the shops

- **4 lots** on one street (Depot Row), one per colour. Lot 1 is the host's. A joining business claims the lowest free lot; the lot
  "plops": a crane drops the shop, dust burst, the sign bolts on, a truck rolls out of the door in the owner's colour. Rejoining the
  same Town gives the same lot.
- **Edit-time lots, revealed at runtime** (red-team T10): all 4 shop interiors exist in the scene from `Setup_DepotRow`, hidden until
  claimed. Each interior is a copy of today's ShopScene contents (sort station, compactor, bins, laptop, displays, upgrades) bound to
  its owner's business.
- **Signs and names.** Default sign: small "Nick's" above big "RUBBISH CO." (the owner's Steam name + the business name). The business
  name is editable on the laptop (≤24 characters, word filter, shown to everyone in the Town); the owner's name part always comes
  from Steam.
- **When an owner leaves:** their business leaves with them (it lives in their save). Their lot shows shutters and a "BACK SOON" sign;
  their truck parks inside. Employees are taken off shift and paid up to the minute.
- **Solo:** one shop, yours, on lot 1 of the same Depot Row. `DepotRow.unity` is loaded next to the town for its street side
  only: lot 1 shows the shop with your name, business name and colour; lots 2–4 are shuttered FOR LEASE units
  (`DepotFlowRules.LookFor`; set `LegacyVacantUnits` false for bare pads with only the lease board). Nothing plops in solo.

### 13.3 Colours and trucks

4 player/business colours (working names): **Hi-Vis Orange, Bin Teal, Grape Purple, Slime Lime**. Your colour tints your Chuck's vest,
cap and rings, your sign and your truck's paint. Each business has one truck in its colour (upgrades as today). Up to 4 players per Town,
so every player has a business and a colour (§13.10).

### 13.4 Free roam (no more "the host moves everyone")

- **One world, no scene swaps in co-op:** Neighborhood and Depot Row stay loaded together; every shop interior is a pocket in the same
  additive scene. A garage door is a short fade-and-teleport (on foot) or a drive-through (in a truck) for **only the player using
  it**. Everyone goes wherever they like, whenever they like.
- Solo keeps today's scene flow (garage = `ShopScene`, town = its own scene) until the one-world version is proven, then
  switches too. Since the coastal map has no separate depot building, solo uses Depot Row's lot 1 for it (8 Oct 2026):
  - **Which flow:** `DepotFlowRules.FlowFor(offline, forceLegacy)`. Play Solo (and the `-depotrow-off` dev hatch) = legacy
    scenes; any other host = one world. `DepotRowWorld.HostWantsOneWorld` / `HostFlowDecided` hold the answer for the session.
  - **The street in solo:** whenever the town is loaded in the legacy flow the host loads `DepotRow` additively through NGO
    (`DepotRowNet.ServerEnsureLegacyStreet`); the next Single load back into the garage unloads it. `SceneController` keeps
    the screen black until it is up. `DepotRowScene` calls `DepotLot.PresentLegacy` instead of `Present`: no pocket interior,
    pocket floor, indoor lighting, lot claims, business trucks or tints run in solo.
  - **One way in and out, in both flows: the shop's own roller door.** One world: fade-and-teleport into the pocket interior.
    Solo: lot 1's street door (`GarageDoorController.IsLegacyShopDoor`) swaps to `ShopScene` (`SpawnPoint_FromNeighborhood` /
    `_Truck`; the truck and its cargo come along exactly as before), and ShopScene's door brings you back. The other
    lots' doors are off in solo.
  - **Arrival poses** out of the solo garage come from the layout, not from scene markers (`DepotFlowRules.TryArrivalPose`):
    `DepotRowLayout.StreetOnFoot(0)` beside the door on foot, `StreetTruck(0)` on the apron when driving; both face the road and
    are the same poses the one world's door uses. The hospital and its free tow still wake you in the garage.
  - **Lot features in solo (8 Oct 2026: the garage annex is gone):** indoor decor stands in ShopScene's garage
    (`WorkshopWorld.EnsureLegacy`); the street pieces (sign, doorbell, bag hoop, spring launcher, the Monument) and the Critter
    Hutch stand on the legacy depot's lot in town (`WorkshopWorld.EnsureLegacyStreet`, `Progression.md` §6.9.5). The Hauler's
    walk-up hatch is one-world only. The Scavenger's fence spot and the map's depot marker are pure layout and identical in
    both flows.
  - Tests: `Assets/Tests/EditMode/Business/DepotFlowRulesTests.cs`.
- Cost: this is the big refactor (about 20 single-shop/single-truck assumptions: `SortStation.Current`, `TrashRoute.Current`,
  `FlowUtil.FindTruck`, `TruckUnloader`, `Compactor`, `CritterWorld.Truck`, scene-name checks; list in §8.2 M7).

### 13.5 Permissions

| | Owner | Employee (of this business) | Anyone else |
|---|---|---|---|
| Walk into the shop, look at displays | ✓ | ✓ | ✓ |
| Pick up / sort / bin / smash trash inside the shop | ✓ | ✓ | ✗ (hands pass through; "Not your shop!") |
| Unload (tip) this business's truck | ✓ | ✓ | ✗ |
| Drive this business's truck, or call it to where you stand (Esc > Unstuck on foot, −$25 to the caller, host-validated; UIPass §5.5) | ✓ | ✓ (on shift) | ✗ |
| Hitch a ride on the back of this truck | ✓ | ✓ | ✓ |
| Laptop: upgrades, Scrapazon, ebag selling, payroll, rename | ✓ | ✗ | ✗ |
| Keep crate, shelves, display cases | ✓ | put in only | ✗ |
| Curb bags and loose trash in the street | ✓ | ✓ | ✓ (global) |

- Money from pickups goes to the business **whose truck** took the bag; sort/recycle/compactor pay goes to the **shop's** business.
  Valuables an employee finds go into the employer's keep crate (company property).
- No passenger seat. **Hitching:** E anywhere near the back of any truck puts you on its nearest free rear step or side ladder;
  Shift switches spot (§14.1).

### 13.5b Chaos verbs work for everyone

**Nick:** "go ahead with bonking/fighting each other, picking up each other, tug of war on trash, throw and pass. all that works
whether youre coworkers (hired) or not."

- **Bonk / fight / shove** any player (§4 rules: 5 s immunity after any loss of control, never helpless > 1.5 s).
- **Pick up a player** who's flopped (knocked down, run over, passed out) and carry or throw them (§11 #3).
- **Tug of war** on any bag two players grab: the rhythm tug (§4.6), mashing rips the bag and the masher pays.
- **Throw and pass** bags and items to anyone (PASS with aim assist up to 8 m).
- Employment doesn't matter for any of these. The only limit is §13.5's shop rule: inside someone else's shop you can't touch their
  trash, so tugs over a shop's trash only happen between its owner and employees; in the street, anything goes.
- Build order: bonk, shove, carry players, throw and pass land in **N2**; the rhythm tug follows right after in **N2b** (it needs N2's
  grab ownership).

### 13.6 Hiring (payroll)

- **Offer a job:** from the laptop's Payroll app, and only there (it lists everyone in the Town; "Hire…" opens the Hire card). There
  is no prompt on the person themselves: the walk-up "Hire Bob" / "Fire Bob" zone was removed 2026-10-12 (Nick) because it took E
  away from picking up a flopped player and from hitching a ride. Set the **hourly wage** (per in-game hour, $1–$200; 1 in-game
  hour = 50 real seconds). They accept or decline.
- **On the job:** the employee wears your colour's vest over theirs, sees your business on their HUD ("Working for Nick's Rubbish
  Co., $12/hr"), and gets the permissions above. Their own business keeps its lot; they can go back any time by quitting.
- **Pay:** the host pays every in-game hour from the employer's cash to the employee's business, through the Ledger. If the employer
  can't pay, the job continues. The missed amount accumulates as unpaid wages on the employee HUD and Esc timesheet (and the
  employer's Payroll app) until they choose to quit or are fired; later successful paydays do not erase it or pay it back
  automatically. A partial current hour is paid when the job ends if the employer can afford it. Earlier missed wages remain
  unpaid and are stated in the end notice.
- **Fire / quit:** the employer fires from the laptop's Payroll app; the employee can quit from the Esc menu. Both are instant.
- A confirmed hire, fire, quit or job end gets a large center-screen announcement; the smaller persistent HUD tag says who
  employs you and your hourly wage (and any unpaid amount).
- One employer at a time. You can't hire yourself, and an employer can't be someone else's employee.
- **As built (N4, 2 Oct):** the job offer auto-declines after 30 s. The Payroll app is a "Payroll" tab on the laptop's app strip
  (a sheet over the laptop: your crew with Fire, everyone else with Hire… or why not). The "vest" is the employer's colour on
  the employee's cap/shirt/sleeves while on the job. The HUD line sits under the business tag (the employer sees
  "Payroll: Bob $12/hr"). Wages are a **charge** to the employer and a **credit** to the employee (Ledger ids `payroll_wages` /
  `payroll_wage`); a payday is missed when the employer's cash is below one hour's wage, the 2 misses must be in a row, and missed
  wages are not owed later. Shop pay an employee makes inside the employer's shop (sorting, recycling, compactor, wrong-bin
  fines) goes to the employer, and valuables they keep go to the employer's keep crate; employees can put things in the crate
  but not rearrange the display shelves. Final pay: fire, quit, the 2-miss release (no part hour) and a guest *choosing* to
  leave (it settles before disconnecting) pay the part hour; a guest that **drops** (crash, lost connection) ends its jobs
  without it, because the money could never reach that guest's own save.
- **Jobs are per session.** Nothing about a job is saved (who works for whom, wage, missed paydays); only the Ledger effects
  (wages paid and earned, shop pay) stay with each business.

### 13.7 Global trash, scaled by players

Curb bags, loose items and piles are one shared set, owned by the host (§5.1 "trash as records"). The city's put-out rate scales
with the number of **businesses** (trucks), so everyone has work but the town doesn't flood:

| Businesses online | 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| Put-out rate | 1.0× | 1.7× | 2.3× | 2.8× |

The bag cap scales the same way. Lateness pay, Pile Goblins and Clean
Days stay per bag/district, shared by everyone. Prices never scale (§11 #9).

### 13.8 ebag between players

- Your ebag listings appear to everyone in the Town under a **Neighbours** tab (with your name and colour), next to the NPC buyers.
- Buyers can **Buy now** or **Make offer**; the seller answers with the same **Accept / Counter / Decline** rules NPC offers use today.
  The host moves the item and the cash in one step through the Ledger (never half a trade).
- **Trades:** an offer can be cash, an item from the buyer's keep crate, or both.
- Listings disappear when the seller leaves the Town and come back when they rejoin.
- **As built (N5):** offers live 60–90 s like NPC offers; max 5 offers per listing, 6 open offers per buyer, one offer per
  buyer per listing. Unlike NPC buyers (who only counter after a decline), a neighbour seller can **Counter** (above the offer,
  at most asking) and the buyer can counter back once (between their offer and the seller's counter): 2 counters per deal, then
  Accept/Decline only. Each counter restarts the timer. Cash offers go from $0 (a straight swap) up to asking; ebag's fee
  (the seller's own, 1–8%) comes off the cash only. An item offered in a trade is locked in the buyer's keep crate (can't be
  listed or pawned) while the offer lives. A listing being settled can't be sold to an NPC, pawned or delisted for those
  few hundred ms.

### 13.9 Build order (replaces M1–M7 sequencing; sizes are rough, agent-assisted)

| # | Package | What you get |
|---|---|---|
| N1 | **Shared world** (was M1) | Global curb trash, bags and loose items; one clock and day; one wallet per business; Ledger in cents; trash rate scaling; save slots (any business in any Town). |
| N2 | **Hands on** (M2) | Grab, carry, throw, pass and bin shared bags with no snap-backs; bonk, shove, fight; pick up and throw flopped players; hitching on the rear step. First fun session. |
| N2b | **Tug of war** (from M5) | Rhythm tug on any contested bag, rips and rip billing. |
| N3 | **Depot Row + free roam** (M7 core) | One world, 4 lots, shop plop, colours and coloured trucks, signs and names, shutters when an owner leaves, permissions. |
| N4 | **Payroll** | Hire, wage, pay ticks, fire/quit, employee permissions and HUD. |
| N5 | **Neighbour ebag** | Cross-player listings, offers, counters, item trades. |

Voice (M4), sorting duels and shared-station rotation (M5), night-shift co-op (M6) and Community servers (M8) follow, unchanged.

### 13.10 Decisions (Nick, 2 Oct 2026)

1. **Worlds hold 4 players**, and every player always has a business and a colour (Town cap 8 → **4**; Relay max connections 3).
   A 5th player gets "Town is full (4/4)". The +0.25× per-player row in §13.7 is dropped.
2. **Joining:** pick one of your saved businesses or start a new one (keeps §11 #1, "your shop travels").
3. **Route pay when an employee drives:** the employer (it's their truck); the employee earns only the wage.
4. **Build all of N1–N5 now** ("all now pls"), in dependency order, with chaos verbs for everyone (§13.5b).

### 13.11 Progress (append-only; newest last)

- **N1 (2 Oct, started):** plan: caps 8→4; GameSession cash in cents + guest-write guard + `GameSession.NetMirror`; `Ledger`
  per-business routing (`Ledger.ToBusiness(id)` scope); `Businesses` facade + `BusinessRegistry` (NetworkList) + `BusinessColors`;
  business slots under `accounts/<key>/businesses/` with a guest hand-back; `WorldState` (curb records, claims, clock); guest
  claim / hopper / burst requests; trash rate scaling; join picker; HUD name; `curb` bot brain. Scratch: `<session scratch>/n1/`.
- **N3 (2 Oct, started):** one world for co-op only (solo keeps the ShopScene ⇄ Neighborhood swap). A non-solo host loads the
  town (Single) + additive `DepotRow.unity` through NGO (`DepotRowNet`, a `[NetSessionComponent]` on NetSession.prefab:
  `NetworkList<DepotLotState>`, host assigns the lowest free lot per PlayerKey, lot 1 = host, rejoin = same lot, plop serial,
  one truck per business spawned at its lot, parked inside + "BACK SOON" when the owner leaves, rename RPC). Lots = 4 pads on
  Minivan Mile's south side (`DepotRowLayout`; NeighborhoodBuilder lays the pads, clears verge props/trees, no RNG change);
  the old depot building becomes the Town Yard in the one world. `Setup_DepotRow` copies ShopScene's contents into 4 pocket
  interiors (x 100..550, y −20, z −400, under the far ground), strips chess NetworkObjects, untags markers, wires bays/doors;
  interiors are on only for their owner and whoever is inside. Doors = local fade + teleport (`DepotRowWorld`). Permissions:
  `Permissions.CanWorkIn/CanUseLaptop/CanDrive` (+ `EmployeeHook`/`DriverHook` for N4). Colours: `DepotColors` /
  N1's colour; Chuck `SetPlayerColor`, truck body paint. Adapter onto N1: `DepotBusinesses` (registry name/colour/owner name,
  `Businesses.AssignLot`). Scratch: `<session scratch>/n3/`.
- **N1 (2 Oct, code in):** caps 4; `GameSession` cents + guard + `NetMirror` (GameSession.Net.cs); `Ledger.ToBusiness`;
  `Net/Business/{Businesses,BusinessRegistry,BusinessPicker,BusinessHud}.cs`; `Net/Core/Business/BusinessColors.cs`
  (+ `BusinessIds`, `BusinessRates`); `Save/BusinessSlots.cs` + `LegacySave.WriteHandBack` + `EbaeMarket.LoadBusinessJson`;
  `Net/World/WorldState.cs`; `GameClock` net clock + saved hour; `TruckHopper.BusinessId/CollectCurbBag`; `curb` bot brain.
  Setup_Coop re-run (NetSession.prefab has BusinessRegistry + WorldState). Next: bot run, solo smoke.
- **N1 (2 Oct, bots):** `run_bots.sh -n 4 -m 5 -p none --brain curb --bot-args 'bot-3:-botbusiness new' --guests-may-write`
  PASS: 144 curb bags collected, none twice, 2 claim races resolved; every business's ledger balanced on host and guest;
  guests' hand-back cash = host's record = their saved file; hashes business/clock/curbs 180/180. Verifier:
  `<session scratch>/n1/verify.py <run dir>`. Next: 5-bot cap + seeded-guest run, Editor solo smoke.
- **N3 (2 Oct, done, first cut):** verified in the Editor (host) + headless bots: 4 lots claimed in join order, coloured
  trucks roll out, signs "bot-2's / RUBBISH CO.", doors on foot and driving, rename (filter works), "Not your shop!" on
  pickup/station/laptop/TIP IT/truck in a neighbour's shop, BACK SOON + truck parked inside on leave, rejoin = same lot.
  `Tools/bots/run_bots.sh -n 3 --brain depot` (new `depot` brain: doors, visit, drive in/out, permission probes) PASS.
  Solo legacy flow re-checked (door both ways with truck, TIP IT, station entry, laptop). APIs: **N4** sets
  `Permissions.EmployeeHook` / `DriverHook` (`Func<ulong clientId, int lot, bool>`); **N2** gates grabs with
  `Permissions.LocalCanWorkAt(pos)` (client) / `Permissions.CanWorkIn(clientId, DepotRowLayout.InteriorAt(pos))` (host).
  For N1: renames go through `DepotRowNet.RequestRename` (host-validated, replicated in `DepotLotState.businessName`) and the
  owner then calls `GameSession.SetBusinessName`; the registry's `name` isn't updated until the next Declare (add a registry
  rename if other UIs need it live). Gaps: visitors see the shop's machines but not the owner's (local) trash; guest shop
  automation (`GarageRig.Decides`) and guest cargo/pay follow N1's per-business rules; chess is scenery in lots.
- **N1 (2 Oct, done):** 5 bots + bad wifi + seeded guests PASS (seeded guests kept inventory/upgrades/day 9; cash = seed +
  posted); 5th player "Town is full (4/4)." (no netsim; with loss the guest may see the generic "turned away" line);
  Editor solo smoke (lock + test root): unload → sort → earn, drive out → grab → toss → pay, ledger balanced, save v3
  (cashCents, businessId, clockHours). Gaps for later packages: guest trucks' cargo/unload (N3: `Businesses.HostCargo`),
  N3 must set `TruckHopper.BusinessId` per lot truck; loose items/bursts stay local (N2); company timers (stamps) still
  absolute hours; picker UI not yet eyeballed with a real guest; the Town's lot memory isn't saved across host sessions.
- **N4 (2 Oct, payroll, code in):** `Assets/Scripts/Business/Payroll/` — `Payroll` (facade: rules, queries, requests,
  `WorksFor` = N3's `EmployeeHook`/`DriverHook`), `PayrollNet` (`[NetSessionComponent]`, `NetworkList<PayrollJob>`, offer /
  answer / fire / quit / shop-pay / keep RPCs, host pay ticks on `GameClock.TotalHours`), `PayrollUi` (Hire card, Payroll app
  sheet + laptop tab injected into ebag's app strip at runtime, HUD line, offer card, employer-colour vests; the `HireTarget`
  zones on other players were removed 2026-10-12, hire and fire are laptop-only), `PayrollBotBrain` (`payroll`). Integration fix:
  `World/DepotRow/DepotTruckCargo.cs` sets `TruckHopper.BusinessResolver` (lot truck → the lot's business by PlayerKey), so
  pickups pay the truck's business; and
  `DepotTruckCargoNet` (`[NetSessionComponent]`) hands a non-local truck's cargo (`Businesses.HostCargo`, or the host's
  `GameSession.TruckBags`) to whoever tips it, so a guest can unload its own truck and an employee the boss's.
  Additive edits outside N4 (all small): `Ledger.Redirect` (un-scoped credit/charge hook), `BusinessRegistry.Closing` (raised
  before a guest business closes), `NetSession.GuestLeaving` (last words before a guest disconnects, ≤1.5 s),
  `TruckUnloader` (status/prompt/tip from `DepotTruckCargo`; replaced 2026-10-08 by `TruckTipper` on the truck: the DRIVER
  holds G and asks `DepotTruckCargoNet.RequestDump(truck)`, the host checks the seat, `Permissions.CanWorkIn` for a bay, the
  speed and the ground, then tips onto the shop's `TippingFloor` or onto the street; protocol 15), `TruckControllerV2.DriverClientId`, `ChuckPuppet.SetVestColor`,
  `SortBin.Keep` (hand-over), `DisplayPiece` + `Permissions.LocalCanManageAt` (shelves owner-only), `PauseMenu` ("QUIT JOB").
  Setup_Coop re-run (NetSession.prefab: + DepotTruckCargoNet, PayrollNet). Known gap (N3's): each machine simulates its own
  copy of a shop's trash, so an employee sorts the bags *they* tipped, on their screen; the boss doesn't see that pile.

- **N5 (2 Oct, code in + bots):** `Net/Ebae/EbaeNeighbours.cs` (`[NetSessionComponent(Order = 30)]`, Setup_Coop re-run):
  host-written `NetworkList<NeighbourListing>` + `NetworkList<NeighbourOffer>`; owners publish their own `EbaeMarket` listings
  (item JSON, asking, their fee) once their business is in the registry and withdraw the ones that go; the host prunes
  listings/offers/settlements of businesses that leave (rejoin = republish). API: `RequestBuyNow`, `RequestOffer(seller,
  listing, cents, tradeUid)`, `Respond(offerId, ActAccept|ActCounter|ActDecline, cents)`. Settling: host locks the listing,
  asks the seller (and buyer, for a trade item) to hold, then in one frame `Ledger.Spend` on the buyer (refused = nothing moves)
  + `Ledger.Credit` net to the seller (`LedgerIds.Ebae`) and commit RPCs that swap the items; any missing piece → abort and
  release. Guest RPCs rate-limited (`rb.ebae.publish/trade/hold`), prices from the host's copy. Additive hooks in
  `EbaeMarket` (`EbaeListing.neighbourHold`, `IsReserved`, `FindListingById`, `CompleteNeighbourSale`, `ReceiveItem`,
  `ClearNeighbourHolds`, `Notify`): Accept/Delist/PawnNow/List refuse held/reserved items; solo unchanged (hooks inert).
  Laptop: **Neighbours** tab (`EbaeUI.Neighbours.cs`, built in code; hidden outside a Town): waiting-on-you deals, your deals,
  neighbours' listings with seller name + colour, Buy now, Make offer (cash + keep-crate item cycle), Accept/Counter/Decline;
  Listings tab notes neighbour offers/holds; badges + HUD toasts. Bot brain `ebag` (`Net/Ebae/EbaeBotBrain.cs`):
  `run_bots.sh -n 3 -m 3.5 -p crew_bad_wifi --brain ebag --rejoin bot-3@100 --guests-may-write` PASS (audit + N5 checker
  `<session scratch>/n5/verify.py`): listings visible to all 3, buy now, offer→seller counter→accept, decline, trade (item+$5),
  host trade (item only), two races → one winner each (losers refused "gone"), seller leaves → offer cancelled + listings back
  on rejoin, 18 seeded items conserved/no dupes, all business ledgers balanced. Not covered by bots: the "settling" lock path
  (races resolve before the second request lands locally) and buyer counter-back.
- **N2 + N2b (2 Oct, code in):** `Assets/Scripts/Net/Hands/` — `HandsNet` (`[NetSessionComponent]` on NetSession.prefab;
  partials `.Players`, `.Tug`), `NetCarry` (per networked bag/item), `PlayerBody` (added at runtime to every player copy),
  `HitchStep` (added at runtime to every truck), `TugHud`, `HandsBots` (`grabrace`, `hands` brains). Host publishes every
  bag/loose item it has (curb bags stay records until grabbed; `WorldState.HostMarkPublished` hides them at every curb);
  guests build copies from a contents-free `CarryDesc`. Grab = heave window (250 ms if someone ≤3 m; presses within 35 ms are
  a fair-coin tie), throws buffered until HELD (`TrashCarrier.NetCanRelease`), thrower simulates + streams, detects
  catch/bonk/receiver/burst, host confirms; guest deliveries run the host's real `Accept` (pay by truck business, N1).
  Hooks added to frozen contracts (additive): `Carryable.NetId/NetRemote/NetDeliver/NetConsumed/All`,
  `TrashBag.NetBurst/NetBurstAt`, `TrashCarrier.NetCanRelease/NetCandidates`, `TruckHopper.LastPay`. `Juice.HitStop` is a
  local camera freeze while other players are connected (`FeelJuice.CoopLocalOnly`). Keys: RMB shove/swing, Z flop, Space get
  up/wriggle/hop off, E pass/snatch/pick up/hitch. Audit: versioned hash domain `hands`, `revert` events (must be 0).
  First bot run (`grabrace`, 2 bots, bad wi-fi, 126 rounds): one holder every round, all machines agree, 62/63 100 ms pairs
  went to the earlier press, 0 reverts, 50/50 hash matches; host won 79% of simultaneous grabs → tie window added (rerun).
- **N4 (2 Oct, done, first cut):** bots `run_bots.sh -n 3 -m 6.5 -p none --brain payroll` (own build copy
  `~/Dev/Rubbish-build-n4`) PASS + N1's verifier PASS: bot-3's curb bag into its own truck paid bot-3's business; bot-2's into
  the boss's truck paid the boss; host handed 4 bags to bot-2 tipping the boss's truck and 1 to bot-3 tipping its own; hire,
  decline, 4 paydays, fire (final $3.59), quit (final $8.11), 2 missed paydays → released, a leave settled before disconnect
  (bot-3's hand-back = host record); wage lines net to 0; employee: work/drive yes, laptop/shelves no; shop pay (2) and a kept
  valuable went to the boss; hashes all matched. Editor host + `payrollguest` bot: hire card, Offer → bot accepts, $12 payday,
  Payroll tab/app, Fire (confirm), bot hires the host → offer card → Accept → "Working for bot-2's Rubbish Co., $12.00/hr" +
  teal vest, Esc → QUIT JOB (final pay). Solo smoke (N1's solo.cs) unchanged: tip, sort, earn, ledger balanced, save v3.

- **N2 + N2b (2 Oct, done, first cut):** shop trash added at the lead's ask (`HandsNet.Shop.cs`): guests publish what their
  machine spawns (tipped piles, station fetches, table spills) with real contents (bag/item JSON; non-curb bags carry contents
  to every copy), keep simulating until it settles; receivers inside a shop interior (bins, compactor, table, keep) run on the
  delivering machine (N3/N4 pay routing incl. `Ledger.Redirect`), the host only drops the item everywhere; a local
  `SetInFlight` on a copy (station fetch, compactor vacuum) claims it (`Carryable.NetScriptTake`); a leaving guest's interior
  items leave with them. Guard limits for poses/publish/pulls. Bots (`crew_bad_wifi`, build copy app in session scratch
  `n2/app`, checker `n2/verify_hands.py`): `grabrace` 2 bots × 126 rounds PASS (one holder every round, all agree, host
  50% of simultaneous, earlier of a 100 ms pair 58/63, 0 reverts); `hands` 4 bots × 7 min PASS (0 reverts, 240/240 `hands`
  hashes, 37 curb bags paid once, 27/27 guest deliveries, pass + catch, bonk, shove, carry/throw/wriggle, hitch 16/16, 3 rips
  billed to the masher's business); `shop` 3 bots PASS (130 guest-made items seen identically on all 3, 40 consumed by guests,
  69/69 hashes). Editor host + 2 bot guests (shots in `n2/shots*`): bonk KO, shove, carry, throw (tumble), wriggle free, pass
  + catch back, two riders on a truck's rear steps while it rolls, fair tug (HUD) and a mashing rip. Solo: hooks off, toss
  into truck, burst, station rip/sort ×4 with physics flat (0.01 ms) and frame 1.3–2.1 ms. Not done: InFlight layer
  (thrown items still collide with other loose items locally), grip/fling and lag-compensated pose history (M3), Auto-pull,
  Safety Vest/Permits() presets; an employee's station fetch/compactor claim path is code-only (not driven by a bot).
- **Unstuck calls your truck (12 Oct, built, not yet played in co-op):** on foot, Esc > Unstuck no longer teleports the player;
  it asks for their own business's truck (the employer's, while they are on someone's payroll) to be flown to the spot beside
  them (Docs/UIPass.md §5.5). Wire: `DepotRowNet.TruckCallRpc` guest → host (the caller's position, the landing spot the
  caller's machine found, a yaw; `NetGuard` 1 per 2 s, burst 2), `TruckCallReplyRpc` host → that guest (toast), `TowDroneRpc`
  host → everyone else, twice per delivery (start, end), so every machine shows the tow drone, which only follows the truck's
  replicated pose. The host (`TruckCall.ServerCall`) checks `Permissions.CanDrive`, that nobody drives the truck and it isn't
  mid-delivery, that it owns it (a driver who just got out still simulates it until it stands still), re-validates the landing
  (dry, level, clear, 1 m+ from the player) with its own physics, posts the −$25 tow fee to the caller's business through
  `Ledger.ToBusiness` (free with handling L1+; may go negative like the hold-R tow) and runs the drone (`TruckRecovery`):
  the truck is the host's to move while it is parked, so the existing owner-authoritative NetworkTransform carries the lift
  and carry; a truck parked in a shop bay is `Teleport`ed into the sky above the spot first. No new NetworkBehaviour
  (`DepotRowNet` became partial). Not done: a driver's own hold-R tow is still invisible to other machines.

---

## 14. Fun pack: riding, replays, bag personalities, night shift, events (Nick: "love it all. lets do", 2 Oct 2026)

Everything here works in solo and co-op, for owners, employees and visitors alike (same rules as §13.5b). Packages: **F1** riding,
**F3** bags & tools, **F4** night shift, **F5** events & weather. (**F2** replay is skipped for now.)

### 14.1 F1 Riding the truck (crash flings, drive-by grabs)
- **4 hold points:** left/right rear steps + a ladder rung on each side. **E anywhere near the back** (behind it, round the rear
  corners, alongside the ladders; no aiming) puts you on the nearest free one, your own side first; a full truck says "No room on
  the back". **Shift** (pad RB) shuffles you round the back to the next free spot (left ladder → rear-left → rear-right → right
  ladder → round again). E or Space hops off. Any player can hitch any truck, Curb Pup or rear-loader.
- **Riders stay on solidly** (Nick, 12 Oct 2026): no grip meter, no lean, no HOLD ON, no rider shoving or dominoes. Corners,
  braking, drifting, curbs and potholes never throw anyone off; yard dogs only bark at riders.
- **Crash fling:** a heavy hit (the truck's speed changes by ≥ 5 m/s within ~0.06 s: a tree, a wall, another vehicle, being
  rear-ended) throws every rider off at once: launched along the truck's momentum, flailing; bowls over pedestrians, knocks lamp
  posts/bins; hedges and trampolines catch. "AIR TIME 14.2 m!" readout + personal best.
- **Driver:** smooth driving earns a small "Crew Comfort" tip per stop while riders are aboard.
- **Upgrades (Scrapazon):** Padded Step (a crash needs +1.5 / +3 m/s more to throw riders), Safety Bar (while down, crashes under
  8 m/s throw nobody; B toggles). Grip Gloves are no longer sold (a save that bought them keeps the id; it does nothing).
- **Drive-by grabs:** below walking speed a rider can snag a curb bag within reach and lob it into the hopper without hopping off;
  consecutive snags build a combo ("STREAK x4!", small pay bonus).
- **Net:** crashes are detected where the truck is simulated, tick-stamped, and applied on each rider's machine when its own copy
  of the truck reaches that tick, so the fling starts at the same hit on every screen (§4.4, red-team T4); seat switches are
  host-checked (`BodyNet.step`).

### 14.2 F2 Instant replay — **skipped for now (Nick, 2 Oct 2026)**
- A rolling ~10 s buffer of nearby transforms (players, trucks, bags, items, critters, knockables) recorded locally at low cost.
- **Big moments** trigger a "REPLAY [R]" prompt: long fling, domino knock-off, legendary find, tug rip, compactor cube, raccoon
  heist bonk, Raccoon King. Replay plays the buffer with a slow-mo orbit/follow cam and a caption; Esc exits. Optional "save clip" =
  screenshot burst / GIF-friendly frames to the user's Pictures folder (no video encoding required at first).
- Stats: longest fling, total air time, biggest streak (shown on the HUD toast + saved per profile).

### 14.3 F3 Bag personalities and tools
- **Bag tells** rolled by the host with the contents: **leaky** (drips, leaves a slippery juice trail for ~60 s: players slip and
  slide, trucks lose grip), **heavy** (needs two players to lift: both grab, walk in step or it drops; solo can drag it slowly),
  **over-stuffed** (bursts if thrown hard), **buzzing/ticking mystery** (wobbles, ticks; contains something odd).
- **Compactor cube:** a player shoved/thrown into a shop's hopper and SMASHed pops out as a squished cartoon cube that waddles for
  10 s (harmless; no damage; never traps).
- **Bag cannon** (truck upgrade): lob bags into the hopper from ~10 m with an aim arc; misfires splatter (juice puddle + bonk).
- **Grabber pole** (tool; since Oct 2026 printed as *Grabby Hand* from the Blueprints tree in SolidWaste 3D, `Progression.md` §6.9, no longer sold on Scrapazon): extra reach for bags over fences, poking friends, snatching a held bag.

### 14.4 F4 Night shift together
- **Night pay ×1.5** for pickups between NightStart and dawn.
- **Headlamps** for players at night (light cone); **roof spotlight** a rider on the side rung can aim for the driver. The stock
  spotlight illuminates only: it never scares raccoons away. Bought headlights briefly flinch calm animals; angry/foamy ones ignore them.
  Headlight tier follows the truck's lot/business owner, including when an employee drives it. Protocol 3/content 2 adds one
  validated `BusinessUpgradeInfo.gearBits` byte: bit 0 is the owner's personal grabber pole; bits 1–2 are headlights (0–2),
  reserved bits/invalid tiers rejected. Both live changes and late joiners receive it; no host-save fallback for unknown lot trucks.
- **Raccoon heists:** raccoons raid curb bags and run with valuables (sparkle trail); bonk one to drop the loot. Count scales with
  players; they target the nearest player on foot.
- **Stowaways:** raccoons hop onto trucks and dig through the hopper (cargo shrinks) until a rider shoos them.
- **Latch:** a raccoon latches onto a player's face/back (scrambled controls); a friend bonks it off, or mash to shake it solo.
- **Provoked raccoons:** plain and foamy raccoons hiss, then chase and bite/latch if crowded, robbed or bonked. Horns/stomps buy a
  brief startle, not a permanent flee; bonked thieves and shooed stowaways can retaliate. Aggro remembers the provoking player,
  regardless of employment/business. Connected bites or an unremoved latch can infect that player (60% plain, 80% foamy/King),
  never by proximity. A 0.4 s lunge windup, 5 s per-player protection, 5.5 s bite spacing and two-bite encounter limit give escape windows.
- **Rabies per player** (foaming, goofy hiccups; everyone buys their own Rabies Shot on the laptop's Scrapazon, nobody can jab a
  friend on the street); optional **Rabies Tag** toggle (a rabid bonk passes it on), off by default.
- **Traps & bounties:** bait a trash-can trap; sell caught raccoons to Animal Control or keep one as a shop pet on a shelf.
- **Raccoon King:** rare night event: a giant raccoon raids Depot Row; all businesses team up; beating it drops a trophy piece.

### 14.5 F5 Events, poaching, dogs, weather
- **Big Cleanup:** the city announces a 10-minute rush (double put-out rate) with a live scoreboard of businesses (bags/kg); winner
  gets a trophy display piece and a cash prize. Roughly once per in-game day when ≥1 player is online (solo: beat your best).
  **Switched off 2026-10-09** (`CleanupTuning.Enabled = false`, see Docs/Campaign.md §14.2): it is never scheduled and the depot
  board stays hidden; only a forced event (the debug menu) still runs. The code, saves and wire format are unchanged.
- **Poaching:** collecting a curb bag on a street another business is actively routing shows "POACHED!" to the victim (fun callout,
  no penalty beyond the lost bag).
- **Dogs:** WorldLife yard dogs chase riders and players carrying bags, can snatch a dangling bag and run.
- **Weather:** wind gusts push loose trash and light bags around; rain makes surfaces slippery (players slide, trucks drift easier).

### 14.6 Progress (append-only; newest last)

- **F5 (2 Oct, started; API for F1/F3):** F5 publishes weather as a static facade `Weather` (`Assets/Scripts/Weather/`):
  `Weather.Kind` (Clear/Windy/Rain/Storm, host-chosen, replicated), `Weather.Wetness` 0..1 (eases in/out),
  `Weather.Wind` (horizontal m/s, gusts; same on every machine from a shared seed), `Weather.GripDrainMul` (1 dry, 1.35 rain,
  1.7 storm) and `Weather.Slip` (0..1: 0 dry, ~0.5 rain, ~0.8 storm), plus `Weather.Changed`. **F1:** please multiply your
  grip drain by `Weather.GripDrainMul` (or expose a multiplier and F5 sets it). **F3:** please scale your slipperiness by
  `Weather.Slip` (or expose a global scale F5 sets) and expose your bag weight-class query; until then F5 treats bags
  < 6 kg as light. F5 also needs F1's knock-rider-off call (dogs). F5 adds one host event to `TruckHopper`
  (`CurbBagCollected`, raised in `CollectCurbBag`) and an event rate multiplier to `CurbTrashSpawner`; dogs live in
  `WorldLife/Dogs/`.

- **F3 (2 Oct, code in; API for F4/F5):** `Assets/Scripts/Trash/Tells/` + `Assets/Scripts/Tools/` + `Sorting/CompactorCube.cs`.
  Tells are a pure hash of the bag id (`BagTells.Of(bagId|Carryable)`: leaky 12%, heavy 6%, over-stuffed 10%, mystery 3%), so
  every copy agrees with zero bandwidth; the host shapes contents in `ItemCatalog.RollBag` (`BagTells.ShapeContents`: heavy filler
  to 20–24.5 kg, +4–6 light items, 1–2 tier-99 odd items). **Hooks:** `BagTells.WeightClass(Carryable)` → Light (<3 kg) /
  Normal / Heavy (heavy tell or ≥15 kg) — **F5 wind: skip Heavy**; `Puddles.Spawn(pos, radius, kind, life)` (any machine; a guest
  asks the host), `Puddles.SlipAt(pos)` (0..1), `Puddles.RainWetness` (0..1 global slipperiness: players slide ×0.45 everywhere,
  puddles slipperier and longer-lived, trucks lose grip) — **F5: set `Puddles.RainWetness = Weather.Slip` each frame**;
  `TrashBag.Tell`, `TrashBag.BurstNow(point, normal)` (F4 raccoons can burst a bag through the network). No MomentBus (F2 skipped).

- **F1 (2 Oct, code in; API for F4/F5):** `Assets/Scripts/Riding/` (`RideCore`, `RideTruck`, `RideRider`, `RideFlight`,
  `RideHud`, `RideShop`, `RideBots`) + `Net/Hands/HandsNet.Ride.cs`; N2's `HitchStep` now has **4 holds** (0/1 rear steps,
  2/3 side ladder rungs; `HitchStep.StepLocal` kept, 4 entries) with side hitch zones. Solo works too (a truck needs a driver,
  so solo riding needs an autopilot: `TruckControllerV2.InputOverride`, the test rig's ghost driver). **Hooks:**
  `Riding.GripDrainScale` (float, 1 = dry; **F5: set it to `Weather.GripDrainMul`**); `Riding.RidersOn(truck, List<RiderInfo>)`
  (player, clientId, hold, `rung`, `side` −1/+1, `handPos`, grip for your own rider) and `Riding.AnyRiders(truck)` (**F4:
  spotlight = a rider with `rung`; stowaway shooing = any rider on that truck**); `Riding.KnockOff(clientId, push, word)` (forced
  fling) and `Riding.GripHit(clientId, cost, word, savable)` — call on the host (or solo); the rider's machine applies it
  (**F5 dogs / F4 raccoons**). Events: `RideEvents.Flung(player, metres, seconds)`, `Domino(cause, victim)`, `Snagged(streak)`.
  Net: the truck's simulating machine streams `RideSampleNet` 10 Hz (~26 B) + jolts while anyone rides; each machine measures
  how far its displayed truck lags the driver's clock by matching sample positions against the displayed path; flings carry the
  driver-clock tick + truck-local start, so every machine starts the same 60 Hz flight at the same truck pose. Upgrades
  (Scrapazon, appended by `RideShop.AddTo`): `grip_gloves` (gear, $250/$1,500), `padded_step` (truck, $300/$1,200),
  `safety_bar` (truck, $600; B toggles). Pay: streak bonus / Crew Comfort tip credited on the host to the truck's business
  (`ride_streak`, `crew_comfort`). Personal bests in the account store (`stats/ride`).

- **F4 (2 Oct, code in; API for F1/F3/F5):** `Assets/Scripts/Night/` + edits in `Critters/**`, `Status/RabiesStatus.cs`.
  **Critters go co-op:** the host simulates every critter; `NightNet` (`[NetSessionComponent(Order = 40)]`, Setup_Coop has it)
  streams 12-byte snapshots at 5 Hz (unreliable) and guests run the same `Raccoon`/`PileGoblin`/`RaccoonKing` classes as puppets
  (`Critter.NetPuppet`, same looks, state changes play their juice everywhere). Critters target the **nearest player on foot**
  (`NightPlayers`; drivers, riders, chess and anyone inside a shop don't count), never enter shop interiors or Depot Row lots
  (`CritterWorld.KeepOut`), count ×(1 + 0.25·(players−1)) capped at 8 in co-op. A bite on a guest is sent to the guest's machine
  (it knocks itself back, applies the host's rabies roll); a guest's throw that bonks a puppet asks the host's critter to take it;
  a guest's horn is forwarded. 5 s critter immunity after any critter knock or latch (`NightShift.CritterImmune`).
  **Night pay ×1.5** (`NightShift`: a `ProgressionEvents.BagCollected` handler credits +50% as `night_shift` inside the truck
  business's Ledger scope). **Headlamps** (`PlayerHeadlamp`: every player, real spot light for your own + the 2 nearest others,
  glow + faint beam cone for the rest; the Scrapazon headlamp replaces yours). **Roof spotlight** (`RoofSpotlight`, every truck,
  night while crewed + 30 s): a rider on a side rung aims it (uses F1's `Riding.RidersOn(..).rung`, 8 Hz aim RPC), else it
  sweeps/locks onto raccoons; the beam dazzles raccoons (host). **Heists** (`Raccoon.State.Heist`): a rummaging raccoon nicks the
  best thing in its bag's host-rolled contents (valuable always, metal/glass 30%), sparkle trail, bonk or run it over → shared
  loose item; escapes after 22 s / when alone. **Stowaways**: raccoons hop into a slow loaded truck's hopper (not on a lot) and
  eat a non-valuable item every 4.5 s (`NightCargo`: `GameSession.TruckBags` or `Businesses.HostCargo`); E near (`CritterPoke`,
  works from the rear step) or a bonk shoos it; the horn doesn't. **Latch** (rabid lunge, 40%): anchored on the victim's head,
  `RaccoonLatch` scrambles their movement (never frozen), mash Space ×6 to shake it, a friend's E / shove / bonk knocks it off
  (`HandsNet.HostKnocked`, a new additive hook), 4 s timeout = 50% rabies. **Rabies per player**: host `NetworkList<RabiesRec>`
  keyed by PlayerKey (each player reports their own; a rejoin restores it), everyone sees everyone's foam (`RabiesLook`); goofy
  hiccup hops; co-op pass-out = the same fade and hospital bill as solo, where you fell (no scene trip in the one world). Nobody can
  cure a friend: the "Jab Dot" interaction on a downed rabid player (`RabiesJab`, $35 on the jabber's business, plus a 30 s lie-down
  to wait for it) was removed 2026-10-12 and the first-aid shelf by every shop laptop (`RabiesShotKiosk`) on 2026-10-08, so everyone
  buys their own shot on Scrapazon (`ShopEffects`). `NightNet.ShotFor` is only the host-charged shot for yourself (no caller but the
  `nightextra` bot), and the host refuses any other target (`night.shot.refused`). **Rabies Tag**: host
  toggle in the Esc menu (and `Rubbish/Debug/Night`), off by default: a symptomatic player's knock passes it on (50%).
  **Traps** (Scrapazon "Humane-ish Raccoon Trap", $45, T to set; `RaccoonTrap`/`NightTraps`, host list): raccoons within 16 m are
  lured in; E = Animal Control bounty ($40, rabid $60, `animal_control`), F = keep it as a shop pet (`pet_raccoon`, tier-99
  valuable → keep crate → your display shelves). **Raccoon King** (`RaccoonKing`/`RaccoonKingEvent`, 25%/night, 22:00–03:00, on
  Depot Row's street): HP 8 + 4/player, royal swipes, boss bar; every hit counts per player, beaten → $150 + $100/player split
  by hits (`raccoon_king`) + a `raccoon_king_crown` trophy to each contributor. Additive edits outside F4: `HandsNet.HostKnocked`,
  `PauseMenu` (host's RABIES TAG sticker), `ScrapazonCatalog` (trap), `ItemCatalog.BuildNightDefs` + two `ItemVisuals` keys,
  `Keybinds` (T, mash). Bots: brain `night` (`Night/NightBots.cs`), checker `<session scratch>/f4/verify_night.py`.

- **F1 (2 Oct, done, first cut):** verified. Bots (`RideBots.cs`: `-botbrain ride` = everyone rides, the host ghost-drives
  its lot truck on a scripted lap — Minivan Mile westbound, curb bags, hard right onto Depot Rd, alternate hard/gentle laps;
  `ridedrive` = a guest drives its own truck for real), checker `<session scratch>/f1/verify_ride.py`. 4 bots, crew bad
  wi-fi, host-driven: audit PASS, 16 flings all seen on every machine, start spot median 5 cm / p95 0.9 m apart, corner flings
  up to 14 m, 6 dominoes, shoves, HOLD ON saves/misses, drive-by streaks to x6 paid by the host to the truck's business.
  Guest-driven truck: Crew Comfort tips paid to its business; flings median 12 cm / p95 1.7 m (NGO smoothing of a
  client-owned truck in a 10 m/s corner). Editor host + 3 bot guests (4 riders on one truck): PASS, median 7 cm / p95 0.4 m,
  `Riding.KnockOff` works. Solo (ghost-driven truck): bought all 3 upgrades, padded step / bar kit shows, streak x6, Safety Bar
  holds below 8 m/s then lets go when flipped up, corner fling "AIR TIME 7.9 m! NEW PERSONAL BEST!". Tuning: Truck V2 only
  reaches ~5.4 m/s² sideways at full speed, so sideways drain starts at 2.5 m/s² (45/s per m/s² over); a 5 m/s lap is free.
  Not done: rider camera damping / driver Rigidbody interpolation (T9), driver's grip pips, remote riders don't see your lean,
  solo has no autopilot (riding solo needs one), flings reach late machines ~0.2–0.8 s into the flight (fast-forwarded).
- **F5 (2 Oct, done, first cut):** events, poaching, dogs, weather. New code: `Assets/Scripts/Weather/` (`Weather` facade,
  `WeatherNet` `[NetSessionComponent(Order = 40)]`, `WeatherFx`, `WeatherWind`), `Assets/Scripts/Events/` (`BigCleanupNet` (41),
  `BigCleanupHud`, `CleanupBoard`, `PoachingNet` (42), `F5Bots` brain `f5`), `Assets/Scripts/WorldLife/Dogs/` (`YardDogs`,
  `YardDogsNet` (43)). Setup_Coop re-run (NetSession.prefab has all four).
  - **Weather:** host picks Clear/Windy/Rain/Storm every 2.5–5.5 min (storms 1.3–2.8; first change ≥ 90 s), one replicated
    `WeatherNetState` (kind, since, seed, serial); gusts, wetness, overcast are pure functions of it + server time (nothing
    streams). Wind pushes loose trash and non-heavy bags this machine simulates (F3 `BagTells.WeightClass`; never curb bags,
    never inside shop pockets) at 10 Hz within 70 m of a player. Rain: ≤ 900 instanced streaks round the camera, rain puddles
    (F3 `Puddles.Spawn(Rain)`), `Puddles.RainWetness` (players slide, trucks drift), `Riding.GripDrainScale` = `Weather.GripDrainMul`
    (F1), global shader float `_RubbishWetness` (no shader reads it yet); overcast greys the sun, ambient, fog and sky (storms:
    lightning + thunder); rain/wind noise loops; trees sway only if non-static (the town's are static, so none do). Bots
    `-f5weather SEC` cycle it. **Cost (Editor, measured in `WeatherFx.CostMsPerFrame`, wind physics included): 0.02 ms windy,
    0.06 rain, 0.07 storm** (budget 0.5).
  - **Big Cleanup:** once per in-game day at a hashed 08:00–11:00 (not in a session's first 150 s, not within 10 min of the
    last), 12 s announce (banner + siren) → 10 min at 2× put-out rate and caps (`CurbTrashSpawner.EventRateMul`) → 16 s
    results. Scores = curb bags + grams per business from the one host collect path (`TruckHopper.CurbBagCollected`, new). Rank:
    bags, then kg, then who got there first. Co-op winner (online): $100 + $4/bag (≤ $400) through `Ledger.ToBusiness`
    (`event_prize`, host-guarded once per serial) + the **Big Cleanup Cup** (new weight-0 Legendary `big_cleanup_cup`, appended
    after every other def) sent to the owner's machine → its keep crate (`EbaeMarket.ReceiveItem`, saved with the business,
    shown by the display line). Solo: beat your best (counter `cleanup_best_bags`) for $40 + $2/bag (≤ $150) + a ribbon.
    HUD strip under the top bar, board on Depot Row (west of lot 1, runtime-built). Bots `-f5cleanup START,RUN`;
    `BigCleanupNet.DebugStart/DebugFinish`. NetAudit domain `cleanup` is versioned (`CleanupState.ver`).
  - **Poaching:** host marks a street "worked" by a business while its truck crawls (≤ 4 m/s) within 22 m of a stop with trash
    there or it collects a bag there (90 s). Another business collecting on that street → "POACHED!" (poacher's name + colour,
    street) to the victim's owner and its employees; 25 s per pair, 10 s per victim. No penalty.
  - **Dogs:** the 30 cast dogs sit in their household's front yard when not out walking; host/solo sim (only within 45 m of a
    player): chase bag-carriers and riders inside their yard chain (≈ curb + a bit of street), snatch a dangling bag (co-op:
    `HandsNet.HostYank` (new additive partial `HandsNet.Snatch.cs`): the same networked bag becomes a host flight, so it's
    conserved and anyone can grab it back out of the mouth), run off, drop it when bonked by a thrown thing, caught (touched)
    or after 5.5 s; riders get nipped (`Riding.GripHit` 35, savable). Guests draw a 10 Hz pose stream. ≤ 6 rigs; 0.01 ms.
  - **Verified:** Editor solo smoke (lock + test root; `f5/solo.cs`): drive out, rain/storm/windy (pushes, puddles, cost), dog
    chase → snatch → catch → re-grab → deliver, rider nipped on a parked truck, Big Cleanup announce → 5 bags → "NEW PERSONAL
    BEST" $50 + ribbon in the keep crate, ledger balanced. Bots (own copy `~/Dev/Rubbish-build-f5`):
    `run_bots.sh -n 4 -m 6 -p crew_bad_wifi --brain f5 --bot-args 'bot-1:-f5cleanup 60,180 -f5weather 35'` audit PASS (every
    domain matched incl. weather 216/216, cleanup 180/180) + F5 checker `<session scratch>/f5/verify_f5.py` PASS (final board
    identical on 4 machines, prize paid once, trophy received by the winner, 11 weather states identical, 31 snatches all
    resolved, 73 poach callouts shown) + N1 verifier PASS (ledgers balanced, no bag paid twice) + N2 `verify_hands` PASS.
    Natural schedule seen (event at 200 s, storm at 184 s). Shots: `<session scratch>/f5/shots/`.
  - **Seen, not F5's (N2):** ~1 in 10–20 guest deliveries a guest re-publishes the just-delivered curb bag (`published-by-guest`
    with the delivered key right after its Gone): a phantom bag and a `hands` count mismatch on one guest. Also: rain wetness is
    global in F3's `Puddles.SlipAt` (shop pockets get slippery in rain; F3 could gate on `Weather.IsOutdoors(pos)`).

- **F1 follow-up (3 Oct 2026, verification pending):** Ordinary solo trucks can now be boarded while parked. A rider presses C
  to start an explicit low-speed cruise on a nearby open, correctly oriented road lane (≤ 2.35 m/s, so drive-by grabs work),
  and C again to park. The route follows directed road edges for up to 90 m; it brakes for obstacles and stops at the route
  end, on dismount/fling, on driver claim, or when the scene/UI takes control. It never overrides a human driver or the F1
  test rig. `RideSoloCruiseCheck.Run` is the focused isolated-copy route/control check. Reliable jolts arriving after a rider's
  displayed truck passed their tick are now consumed once instead of silently lost. The earlier T9 interpolation gap was
  already closed by `TruckNetFeel` on the Truck V2 prefab. Still pending: rider camera damping, driver's grip pips, remote
  lean visuals, and fully synchronized late-arrival fling presentation.

- **F1 rider look follow-up (3 Oct 2026):** The stock ride kit now has visible steel footholds for all four positions,
  with the Padded Step upgrade overlaying them. The puppet's post-Animator riding pose keeps boots planted, crouches and
  braces the torso against turn/brake stress, and uses two-bone IK to grip the rear bar or side ladder with both mitts on
  every player copy; a carried drive-by bag blends the hands back to the normal carry pose. `RidePosePreview.Run` renders
  a neutral baseline and all four holds in an isolated Editor copy, checking mitt-to-rail distance and boot drift. This
  visual check does not replace an in-scene manual ride or verify that remote players see the owner's A/D input itself.

- **3 Oct integrated recheck (isolated `Rubbish-build` copy):** `Tools/coop_lint.sh` and all 140 EditMode tests passed;
  the six-scene headless build and smoke test passed. `RideSoloCruiseCheck.Run` passed route/control/jolt receipt cases;
  `RidePosePreview.Run` rendered all four real ride holds with ≤0.062 m mitt-to-rail error and zero boot drift. A final
  4-bot ride run under `crew_bad_wifi` passed the network audit (114/114 matching core state hashes, 84/84 hands),
  occupied all four holds in one lap, and observed 5 host-accepted flings on all 3 peers, HOLD ON saves/misses and shoves.
  An earlier 4-minute ride run on the same gameplay path also exercised drive-by snags and streaks; the later ride-kit
  changes are renderer/pose only. F3's final 3-bot run passed the audit (66/66 matching core hashes): all 3 cannon shots
  fired from 9.3 m and the host cargo contained each exact bag key; pole snatch/tug/poke passed. The focused compactor
  timing bot passed both enter-during-slam and leave-before-slam cases. F4's 3-bot night run and F5's 4-bot event/weather
  run passed their audits; F5 saw one prize, matching scoreboards/weather, dog bag conservation and no N2 curb-bag
  re-publication. F3's rain slip is now outdoors-only and F5 wind can move handled/dropped solo curb bags. Instant replay
  remains deliberately unimplemented. Manual in-scene C cruise and remote rider visual QA are still worthwhile.

- **F1 extended recheck (3 Oct 2026, isolated `Rubbish-build` copy):** The Safety Bar position is now one host-owned
  `RideModsNet.barDown` value per truck (late joiners get it), with visibly different raised/down geometry; only a rider
  can request a flip. A 5 Hz grip stream supplies the driver's four hold pips, and rider A/D lean is now copied to remote
  puppet poses. The local riding camera follows a lightly smoothed proxy without changing gameplay transforms. Fling RPCs
  take an unreliable fast path plus a reliable fallback, de-duplicated by rider and truck tick. `RideFeatureCheck.Run` passed
  grip/upgrades, raised/down geometry, hedge soft catch and trampoline bounce; `RideSoloCruiseCheck.Run`, all 140 EditMode
  tests, the six-scene build/smoke and `RidePosePreview.Run` also passed (all four hands within 0.062 m of rails; zero boot
  drift). `rideworld` bot audit passed on the real town: lamp toppled, bin toppled, pedestrian hit. Four `rideverify` bots
  under `crew_bad_wifi` passed the network audit (105/105 core hashes, 75/75 hands): all clients saw the same raised bar in
  cycle 1 and down bar in cycle 2, observers received +1 and −1 remote leans and grip, and snags reached streak x6 with
  host-paid bonuses; flings, dominoes, shoves and HOLD ON saves/misses occurred. Three-bot `ridedrive,ride,ride` under the
  same net simulation passed (66/66 core hashes, 46/46 hands) and the host accepted a $0.75 Crew Comfort tip for a smooth
  leg with a rider aboard. Bot save roots were isolated; the real save folder stayed unchanged. **Still unproven:** manual
  in-scene [C] solo cruise and moving in-game HUD/pose appearance; the shared bar's below/above-threshold behavior has a
  pure focused check added after this snapshot, not yet an in-scene bot proof. The 4-bot run's 24 remote flings had median
  presentation delay 0.092 s but 6 exceeded the 0.25 s target (p95 0.386, max 0.531); one start differed by about 0.72 m
  from the owner because remote truck poses do not perfectly agree. Fast-forwarding the deterministic flight mitigates
  position after receipt, but does not make its first visible frame simultaneous. Do not mark M3's ≤250 ms / ≤2 cm targets
  complete on this evidence. `RideTuning.BarSaves` and the threshold assertions were added after this copy build and must
  be included in the next integrated sync/check.

- **4 Oct final fun-pack acceptance (isolated `Rubbish-build` Editor + headless app):** The final source passed
  `Tools/coop_lint.sh`, 140/140 EditMode tests, the six-scene development build/smoke, `RideFeatureCheck.Run`,
  `RideSoloCruiseCheck.Run`, `NightAcceptance.Run` (17/17), and `F5VisualCheck.Run` (daily schedule window,
  600 s / double bags, four keepsake meshes on an owned Trophy Shelf, idle/live/results board renders).
  `RidePosePreview.Run` rendered the four raised-grip hold poses with zero boot drift and at most 0.027 m
  mitt-to-grip error. All bots used isolated save roots; the real 25-file save snapshot stayed unchanged.
  - **F1:** Four `rideverify` bots on `crew_bad_wifi` passed the network audit (141/141 core hashes,
    111/111 hands). The owner-world fling anchor reached all three peers at the same logged point for all
    eight accepted flings (24/24 peer starts, max gap 0.000 m at millimetre log precision, no host adjustments).
    Grip, hold points, bars, lean, jolts, shoves/dominoes, drive-by grabs, Crew Comfort, upgrades and world
    impacts have focused/integrated evidence above. The temporal M3 target remains open: 7/24 remote fling
    presentations exceeded 250 ms under simulated bad Wi-Fi (median 194 ms, p95 411 ms, max 420 ms).
    Moving HUD/camera feel and the [C] solo cruise still merit hands-on in-scene playtesting.
  - **F3:** Final three-bot `tells-extra` run passed 48/48 core and 42/42 hands hashes. Over-stuffed burst,
    buzzing mystery/crowd, leaky trail, wheel slip, pole through a fence, host cargo, cannon misfire and the
    neighbouring player splat agreed on all three peers. Solo dragging a heavy bag moved 1.66 m; the prior
    co-op lift/walk/drop and 10 s Compactor Cube impact/timing probes passed. Natural truck-over-Juice feel,
    cannon splat alignment and mesh/crowd readability remain visual/manual checks.
  - **F4:** Final three-bot night extras passed the multiplayer audit and 15/15 focused receipts: two traps,
    exact $40 Animal Control bounty, pet inventory/shelf idle, friend latch rescue, Rabies Tag, friend carry,
    two host-authorized $35 shots and both cures. A host kiosk validation bug for hidden remote shop interiors
    was fixed. Natural King schedule/indoor gate/guest-outdoor spawn passed 6/6; earlier bots verified heists,
    stowaway cargo loss/shoo, solo latch escape, per-player rabies/rejoin, 1.5× night pay and King rewards to
    contributors. Headlamp/roof light render checks and live pet-idle previews passed; the King's new crown
    toss has compiled but not been visually observed in live play.
  - **F5:** Final focused tests passed before-arrival `POACHED!` at 41.6 m (unrelated street quiet), a real
    rider-dog nip with grip 100→30/HOLD ON, and physical wind/rain (loose item moved 10.01 m; rain puddles,
    slip 0.45, grip-drain scale 1.72). Four businesses under `crew_bad_wifi` saw identical final scoreboards
    (72/72 cleanup, 78/78 weather hashes), one $132 winning prize and one Cup; dog snatch/drop and victim
    poach callouts were observed. A separate solo event ran the full 600 s at 2× put-out rate, scored 59 bags,
    awarded one Ribbon and $150, then reset to 1×. A final unforced run announced at t=200.712 and entered
    Running at t=212.738 with 2× rate; all runs passed their save/network audits. F2 instant replay remains
    intentionally unimplemented by request.

- **12 Oct 2026, two prompts removed from other players (Nick: "remove that mechanic", "hire/fire should be from laptop only"):**
  both sat on every other player's body and took E away from "Pick up Dot" and "Hitch a ride". The friend's Rabies Shot jab is gone
  (`RabiesJab`, the "Jab Dot with a Rabies Shot ($35)" zone, and the 30 s co-op lie-down that waited for it): a co-op pass-out is
  the solo fade and hospital bill where you fell, `NightNet.ShotFor` serves only yourself (the host refuses other targets and audits
  `night.shot.refused`), and the `nightextra` bot now asks for a shot for a rabid, flopped, carried friend and expects the refusal
  (`Tools/bots/check_nightx.py`). The walk-up Hire/Fire zone (`HireTarget`, `PayrollHireZone`) is gone: hire and fire live only in the
  laptop's Payroll app (§13.6). What still publishes an interaction on another player: `PlayerBody` (pick up a flopped or downed
  player, grab the other side of their bag) and, while a raccoon is latched on them, `CritterPoke`. `PlayerBody` also carries a
  `PickUpZone` (an `InteractionZone` with the body as its target, 2.5 m either way, no line of sight): PlayerController's look-at and
  proximity scans resolve a hit with `GetComponentInParent<IInteractable>()`, which returns ONE component of the root, and the Grabby
  Hand / Compactor Pack (added to every player copy before `PlayerBody`, never interactable on a remote copy) came first, so "Pick up
  Dot" could be hidden behind them. A zone names its target directly, and PlayerController's +3 for a downed player still applies.

- **12 Oct 2026, a guest's truck jumped when they got out (Nick: "when guests on multiplayer get out of their truck it jumps and
  bounces sometimes unpredictably, not a problem for hosts"):** the host took the truck's physics back the moment a guest's "get
  out" reached it, and three things went wrong in that round trip. (1) The guest's machine still counted them as the driver until
  the host's answer came back, so the empty truck's parking brake was released again and it coasted, steered and accelerated with
  the WASD they walked away with. (2) The host's copy, which trails the guest's by half a round trip plus the interpolation delay,
  turned dynamic where it happened to be, from a standstill; the guest's copy then snapped to it. (3) The host's copy of the guest's
  player was still at the truck's root, inside the body's convex collider, with its CharacterController on, so the freshly dynamic
  truck was shoved out of that capsule (depenetration) and bounced, which the guest saw a moment later. A host hands nothing over, so its exits were calm. Now (§4.4) the guest parks and
  keeps simulating the truck until the host's copy has held still for 0.5 s, the move to the door is a teleport, and a Space press
  while seated (the drift key) no longer makes anyone hop as they get out. Compiles; `TruckHandbackWatchTests` added; **not yet
  checked in a real two-player session.**

- **12 Oct 2026, riding without grip (Nick: "hitching a ride on the back of the truck in coop is super hard like i have to be
  looking at just the right spot. should just be e anywhere near the back of the small or rear loader truck. need a key to switch
  seats ... lets completly remove the grip mechanic ... should be solidly on unless they hit something solid"):** boarding was four
  ~0.7 m pockets that also wanted the camera within ~49° of the hold. It is now one area round the back of either body
  (`RideHolds.BoardingArea`, from `ModelBodyBoxMetres` so the Curb Pup and the rear-loader both work: 3 m out behind the rear face,
  the body's width + 1.4 m each side, forward to 1 m past the side ladders; the cab doors and the front half of the flanks stay
  "Enter Truck"). One `InteractionZone` at score 2.05 sits above anything you can look at or stand by (≤ 2), so near the back E is
  the ride and the truck's own "Enter Truck" moves to F; a held bag's toss (3), a downed friend (3+) and `CritterPoke` (now 2.3)
  still win, and so does any bag or piece of trash you can reach (Nick, same day: "prioritize bags and trash over boarding ... dont
  want e to board or e to anything to stop people from being able to pick up trash"): `PlayerController.PickUpScore` squeezes a
  `Carryable`'s 0..2 scan score into a 2.1–2.19 band (on foot only: a driver's E stays "Exit Truck", a rider's "Hop off"), above the ride and every look-at / nearby prompt (so also above "Enter Truck"
  and "Not your truck"), under the dominant zone actions (workshop, heavy lift, hutch door, 2.2+) and a downed friend; with a bag on
  E, F goes to the best thing that is not another bag (the ride, the truck), since only one fits in your hands; with hands full
  the ride is not offered at all (the hopper's "Toss into truck" is the prompt back there). E puts you on the nearest free hold (`RideHolds.NearestFreeHold`); the host re-checks the area with 1.5 m of slack and
  takes the next nearest hold if yours went meanwhile; a full truck says "No room on the back". **Switch spot** (Shift / pad RB,
  `GameControls.Action.SwitchSpot`, `Keybinds.Defaults.SwitchSpot`, in both control sheets): `RideSwitchRpc` → the host moves
  `BodyNet.step` if that hold is free; every machine walks the rider 0.3–0.7 s round the back of its own copy of the truck
  (`RideRider.HoldPose`, on a path outside both bodies' corners; the mitts let go of the rails meanwhile and the local camera swings
  out for a ladder). **The grip system is gone:** the grip meter and its HUD (and the driver's crew-grip panel), lean (A/D, stick),
  HOLD ON, jolt costs, the stress stream, the lean/grip RPCs, rider shove (RMB) and dominoes, `Riding.GripHit` (yard dogs now only
  bark at riders), `Riding.GripDrainScale` / `Weather.GripDrainMul` (rain) and Grip Gloves (off Scrapazon; the id stays for old
  saves). **What still throws a rider off:** a crash, the truck's horizontal velocity changing by ≥ 5 m/s within three physics
  steps from any speed (the `RideTruck` sensor where the truck is simulated → `RideCrashRpc {tick, Δv}` → each rider's machine at
  that tick; the old "truck was already doing 5 m/s" gate is gone so being rear-ended while parked counts), and `Riding.KnockOff`
  (nothing calls it). The Safety Bar (down: crashes under 8 m/s throw nobody) and the Padded Step (+1.5 / +3 m/s) now raise that
  threshold (`RideTuning.CrashFlingDv`). The 10 Hz pose sample (~21 B) stays only for lag matching; the Crew Comfort tip still
  measures roughness locally on the driver's machine; riders' puppets sway with the displayed truck's acceleration (cosmetic).
  Solo riders no longer punch critters from the step (`CritterMelee`, as in co-op). Protocol 23. Bots: `ride` / `rideverify`
  laps cycle rough (full-speed corner and hard stop: everyone must still be aboard), gentle and crash (a 7 m/s hit on the
  straight; `rideverify`'s flipped bar alternates "held" and flung), riders board from a loose spot facing away and one switches
  spot; `f5riderdog` passes when the nipped rider stays on. `RideBoardingTests` cover the area on the production prefab (both
  bodies), hold choice, the switch order, the shuffle path, the crash sensor (braking, cornering, drifting, a curb: no crash; a hit
  and a rear-ender: crash) and the shop. Compiles; **not yet run in Unity (EditMode tests, bots) or played in a real two-player
  session.**
