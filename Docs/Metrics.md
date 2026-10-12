# Rubbish: playtest metrics

Added 2026-10-09 with the first campaign slice (`Campaign.md` §14). The point: when friends play a zip build, nobody has to
remember what happened. Every session writes a log of what the player did, and `Tools/metrics/summarize.py` turns a pile of
those into the tables a balance pass needs (how long a contract takes, where holds break, what pays, what people buy and when).

## What it is, and what it is not

- **Local files, nothing else.** Each play session appends to one JSON-lines file under
  `<data root>/metrics/` (`%USERPROFILE%\AppData\LocalLow\Archway Games\Rubbish\metrics\` on Windows, the same folder as
  `Player.log` and the saves). Nothing is uploaded: there is no server. A friend zips the `metrics` folder and sends it.
- **Works in every build.** It is not gated on the Editor or on development builds, so the playtest zip records too.
  `Player.log` says where it is writing (`[Metrics] playtest metrics go to ...`).
- **No names.** A hashed player tag (`pid`, 8 hex characters of a hash of the Steam key) and no Steam id, no Steam name, no
  business name, no chat. The GPU, CPU and screen size are in the header so a slow machine can be spotted.
- **Off with `-nometrics`** on the command line. Batch mode (bots, CI) writes nothing unless `-metrics` is given.
- **Bounded.** A file stops growing at 24 MB; the newest 120 files are kept. A normal hour is under 1 MB.
- **Test data roots apply.** With `Tools/dev/test_data_root.sh on` (or `-dataroot`), metrics go under that root, not the real one.

Code: `Assets/Scripts/Save/Metrics.cs` (the writer and the `Metrics.Begin(...).Int(...).End()` / `Metrics.Count(...)` API; it lives
in `Save/` because the co-op lint keeps file I/O there) and `Assets/Scripts/Save/MetricsRunner.cs` (the listeners and the
snapshot). To record something new, call `Metrics.Begin("name")...End()` from the system that knows, or `Metrics.Count("key", n)`
for anything chatty.

## The file

One event per line: `{"e":"bag","t":<unix ms>,"rt":<real seconds into the session>,"gh":<game hours>,"d":<game day>, ...}`.
`gh % 24` is the hour of day. A session cut off by a crash simply has no `session_end` (the summary says so).

| event | when | fields |
|---|---|---|
| `session` | once, first | `schema, sid, app, unity, platform, build, editor, dev_build, protocol, content, os, cpu, cores, ram_mb, gpu, vram_mb, w, h, fullscreen, quality, target_fps, test_root` |
| `identity` | once Steam knows who it is | `pid, steam` |
| `snap` | every 30 real seconds, and at quit | money, rank, `players`, `mode` (the HUD mode), `dist` (see below), `camp`, `act`, `c`, frame stats, `gc_mb`, `errs` |
| `contract_start` / `contract_resume` | a contract's old bags go out | `dist, bags, owed, heaps, players` |
| `backlog_cleared` | the last old bag is gone | `dist, players` |
| `hold_start` | the district first reads 90% | `dist, attempt, clean, players` |
| `hold_break` | it dipped under, ending an attempt | `dist, attempt, held_h, clean, bags, late, neglected, oldest_h, litter, players` |
| `contract_win` / `contract_announced` | the hold is complete | `dist, unlocks, attempts, players, rank, cash` |
| `contract_focus` | the contract on screen changed | `district, left, start` |
| `bag` | a curb bag went into a truck or the raccoon crew took it | `dist, age (h), on (on time), arm, rac, pay, heap (an old bag)` |
| `treasure` | a valuable was kept | `item, rarity, value, magnus` |
| `crush` | a load was crushed | `bags, kg, treasures_lost` |
| `rank` | a promotion | `from, to, rep, clean_days, cash, players` |
| `sort_streak` | a hand-sort streak of 3+ ended (2026-10-12 pace pass) | `peak` (its length), `tier` (1..6 = x1.5 .. x10), `end` (`wrong` bin or `slow`: the clock cooled it all the way) |
| `buy` | a Scrapazon purchase | `id, lv, price, cash_after, rank, players` |
| `district_unlock` / `district_enter` / `key_to_city` | | `dist, rank, cash, players` |
| `midnight` | the midnight review | `night_of, clean_day, rep_earned, bags_into_truck, fines, clean{}, neglected{}, bonus{}, cash, rank, rep, players` |
| `night_fell` / `morning` | dusk / dawn | `dist` (every open district's state), `cash` |
| `workorder`, `ebae_sale`, `chess`, `incident`, `rabies`, `weather`, `big_cleanup` | | small |
| `tutorial_init` | the first time a save is seen by the walk-through | `experienced` (true: it only gets the contract cards), `mask` |
| `tutorial_cards` / `tutorial_page` / `tutorial_cards_end` | the contract cards opened / a page shown / closed | `dist, first, pages` / `dist, page, secs` / `dist, skipped, page, secs` |
| `tutorial_step` / `tutorial_done` | a lesson became the current one / was finished | `step, n` / `step, n, secs` (seconds it was on offer) |
| `tutorial_tab` | a laptop tab was clicked when the coach asked | `tab` (0 ebag, 1 Scrapazon, 2 Career, 3 SolidWaste 3D) |
| `tutorial_skip` | the player held U | `n` (the lesson they gave up on) |
| `tutorial_complete` | the whole walk-through is done | |
| `player_join` / `player_leave` / `net_state` | co-op | `players, biz, state, solo, host` |
| `scene` | a scene loaded | `name, mode` |
| `err` | the FIRST time an error or exception signature appears | `kind, msg, at` |
| `session_end` | quit | `minutes, totals{}, errors[{n,sig}], error_count, lines` |

`dist` in a snapshot, one entry per open district: `id`, `cl` (clean %), `bags` (curb bags), `late`, `neg` (neglected curbs), `old`
(oldest bag, hours), `tr` (Trust %), `sub` (customer houses), `hp` (old heap bags left). `camp`: the running contract
(`dist, left, start, hold, cl, ok`) or `null`. `act`: seconds in each HUD mode since the last snapshot (`Street` = on foot,
`Driving`, `Depot` = in the shop, `Station` = sorting, `Laptop`, `Map`, `Paused`). `ft`: nine frame-time bins
(<8.4, <11.2, <16.8, <22.4, <33.4, <50, <100, <250, >=250 ms).

`c` holds the counters since the last snapshot: `inc.<ledger id>` (income by source), `exp.<id>` (fines, fees, wages),
`spend.<id>` (purchases), `rep.<id>`, `bags.ontime|late|crew|arm`, `curbs_cleared`, `sort.ok.<bin>`, `sort.bad.<bin>`,
`sort.magnus`, `treasure.<rarity>`, `crew.bags|eaten|fees`, `rabies.resisted`, `errors_dropped`.

## Reading it

```bash
python3 Tools/metrics/summarize.py <folder of .jsonl files> --md report.md --csv out/
python3 Tools/metrics/summarize.py ~/Downloads/friends --no-editor
```

| section | the question it answers | what to turn if the answer is wrong |
|---|---|---|
| Campaign contracts | how long does the clean-up take, how many holds break, at what hour, with how much trash standing | `CampaignTuning` (`HoldHours`, `Threshold`, `AcresBacklog`, `MeadowsBacklog`, `MeadowsRateMult`) |
| Pacing | minutes to rank 2, to the Meadows, to the Commons; which upgrade people buy first | prices, rank gates, the backlog size |
| Economy | which source pays the money and how fast it comes in per game day | fees, bounty, Clean Streets |
| Pickups | on-time share and average age: are curbs being kept up | put-out rate, the work director |
| Where the time goes | driving vs on foot vs sorting vs menus | whether sorting or driving is the bottleneck |
| Performance | frame times by machine | the usual |
| Errors | what breaks on other people's computers | fix the top one |
| Tutorial (by hand: filter `tutorial_*`) | which lesson takes longest or is given up on (`tutorial_done.secs`, `tutorial_skip.n`), whether the cards are read or skipped (`tutorial_cards_end.skipped`) | the words in `TutorialBrain` / `ContractIntroScreen`, or the lesson itself |

Reading hints for the campaign: a hold that breaks in the same two or three hours of the night every time is the night put-out wave
(about half of a day's trash arrives between 20:30 and 05:00); if `bags` and `late` at the break are small but it broke anyway, the
threshold is too tight for the district's `dirtForZero`; if players spend most of the hold in `Depot`, the truck is too small or the
sorting is too slow, and the answer is gear, not the threshold.

## Rules for adding more

- One writer: `Metrics`. Nothing else opens a file here.
- Names and ids never go in. Use `Metrics.Tag(id)` if a stable anonymous tag helps.
- Prefer `Metrics.Count` over one line per tick; prefer facts that already exist (`ProgressionEvents`, `Ledger.Posted`) over new hooks.
- Never log secrets (the co-op lint's `secrets` rule applies to `Debug.Log`; this file is shared by hand).
- Keep the schema additive: new fields are fine, renaming one breaks `summarize.py`. Bump `Metrics.Schema` if it ever has to.
