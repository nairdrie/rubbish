# Windows playtest build

This guide covers building Rubbish on your Windows PC, testing it with Steam, and sending it to friends. Written on 1 Oct 2026
for the M0 connectivity playtest (`Multiplayer.md` §8.7). The Mac side is already prepared, so every step below happens on the PC
except the first.

> **Checked at the M0 integration (W2, 1 Oct 2026, about 03:15):** the button names, the Steam flow and the "what works"
> lists below match the integrated code (`MainMenuManager.cs`, `SessionPauseMenu.cs`, `SteamHud.cs`, `SteamLobby.cs`,
> `TruckControllerV2.cs`). If those change, update this file and `Tools/playtest/README_FRIENDS.txt` together.
>
> **Updated for the UI pass (2 Oct 2026):** B1's `SessionPauseMenu.cs` is retired; the Esc menu is `UI/Pause/PauseMenu.cs`
> (UIPass UP-3) and joining by code or from the Steam friends list is on the main menu's **Join a friend** page (UP-4/UP-5).
> The Esc, invite and join steps in §4 and §6.2 describe those.

---

## 0. The short version

1. **Mac:** check in with Unity Version Control (§1.0).
2. **PC, once:** install Git for Windows, then Steam, then Unity Hub with Unity **6000.3.25f1**. Download the workspace and open
   it. The first import takes 15 to 45 minutes (§1).
3. **PC:** **Rubbish > Build > Windows Playtest (zip for friends)** (§3).
4. Unzip it somewhere else, open Steam, and run `Rubbish.exe` to check it works (§4).
5. Upload the zip from `Builds\` to Google Drive or WeTransfer and post the link in Discord (§5).
6. Click **Host with Friends**. Friends start Rubbish first, then use **Join Game** on you in Steam, accept your invite (**F2**),
   or type your invite code and click **Join** (§6).

**Or build on the Mac (skips steps 1 to 3):** add **Windows Build Support (Mono)** to 6000.3.25f1 in Unity Hub (Installs > gear
icon > Add modules), **quit and reopen Unity** (a running Unity doesn't see new modules), then use the same menu item. The zip
lands in `Builds/`; copy it to the PC and carry on from step 4. Checked on 2 Oct 2026: 386 files, about 148 MB.

---

## 1. One-time setup

### 1.0 On the Mac: check in

Agents can't check in, so do this yourself in Unity Version Control (Plastic). Check in **all** pending changes, including
the new (private) files and folders, which Plastic won't add by itself. The M0 work added these; make sure each is in the
changeset with its `.meta` files:

- `steam_appid.txt` at the project root (it contains `480`)
- `Assets/Scripts/Net/` (Core, Session, Steam, Identity, Player, Dev), `Assets/Scripts/Save/`, `Assets/Scripts/UI/`
- `Assets/Scripts/Editor/`: `RubbishBuild.cs`, `RubbishBuild.Windows.cs`, `Setup_Coop.cs`, `Setup_WindowsPlaytest.cs`,
  `SteamBuildPostprocess.cs`
- `Assets/Tests/` (the edit-mode test assemblies)
- `Assets/Prefabs/Net/` (`NetSession.prefab`), plus the changed `Player.prefab`, `Truck V2.prefab`,
  `RubbishNetworkPerfabsList.asset`, `Assets/DefaultNetworkPrefabs.asset` and `Assets/Resources/RubbishAssets.asset`
- `Assets/Settings/Build Profiles/Windows Playtest.asset`, `Assets/Art/Icon/RubbishIcon.png`
- `Assets/Models/Office.fbx.meta` (Read/Write on, so the office cloth works in builds; Unity also upgraded the rest of that
  `.meta`, so the whole file shows as changed). `office.obj.meta` should show no change.
- `ProjectSettings/ProjectSettings.asset` (window, icon and graphics settings) and `Packages/manifest.json` /
  `packages-lock.json` (Steamworks.NET, NGO 2.13.3)
- `Tools/` (`bots/`, `dev/`, `playtest/`, `coop_lint*`) and `Docs/` (this file, `Multiplayer.md`, `CoreLoop.md`, `Character.md`)

**Added since M0 (also check these in, each with its `.meta` files):** `Assets/Scripts/WorldLife/` (residents, cars,
knockables, kids), `Assets/Scripts/Progression/`, `Assets/Scripts/Garage/`, `Assets/Scripts/Shop/`, `Assets/Scripts/UI/`
(Kit, Settings, Menu, Pause, Map), `Assets/Fonts/Fredoka/` (keep `OFL.txt` with the fonts), any new TextMeshPro font assets and
`Assets/Resources/` assets (town map, house directory, street graph), the rebuilt `Neighborhood.unity` and `ShopScene.unity`,
and all new `Assets/Scripts/Editor/Setup_*.cs` files. **Easiest:** in Plastic's Pending Changes, turn on "Show private items",
select everything under `Assets/`, `Docs/`, `Tools/`, `Packages/`, `ProjectSettings/` and `steam_appid.txt`, and check it all
in as one changeset.

If Plastic shows `Assets/Scripts/Core/SafeFile.cs` as deleted and `Assets/Scripts/Net/Core/Save/SafeFile.cs` as added, that's
right (it moved). Plastic ignores `Builds/`, `Library/`, `Logs/` and `UserSettings/`. That's intended, because the PC rebuilds
all of them.

### 1.1 Git for Windows (install this first)

Two packages download straight from GitHub through git: Steamworks.NET and NuGetForUnity. Without git, the first open fails
with *"No 'git' executable was found"* and Unity offers Safe Mode.

1. Download it from **git-scm.com/download/win** and run the installer.
2. The defaults are fine. On the *"Adjusting your PATH environment"* page, keep the recommended option, **"Git from the command
   line and also from 3rd-party software"**.
3. Check it worked: open a **new** Command Prompt and type `git --version`.
4. If Unity Hub was already running, quit it from the tray icon (right-click, Quit) and start it again so it sees git.

### 1.2 Steam

Install Steam from **store.steampowered.com/about** and sign in. Leave it running whenever you test. You don't need to
install Spacewar (see §6.1).

### 1.3 Unity Hub and Unity 6000.3.25f1

1. Install **Unity Hub** from **unity.com/download** and sign in with the same Unity account you use on the Mac. That
   account owns the Rubbish cloud project, which Relay invite codes use.
2. Install exactly **6000.3.25f1**, using any of these:
   - Paste `unityhub://6000.3.25f1/e1dba0a9aba4` into your browser's address bar. The Hub opens the installer for that
     exact version.
   - Or go to **Hub > Installs > Install Editor > Archive** and pick 6000.3.25f1.
   - Or add the project first (§1.4). The Hub sees the version is missing and offers to install it.
3. **Modules:** the Windows editor already includes **Windows Build Support (Mono)**, so there's nothing to tick.
   "Windows Build Support (IL2CPP)" is optional and not needed (see §9). "Microsoft Visual Studio Community" is optional too.
   It's only useful for editing code or for IL2CPP.
4. You need about 15 GB free: roughly 8 GB for the editor and 3 GB for the project's `Library` folder, plus the builds.

### 1.4 Get the project from Unity Version Control

Pick a short path with no spaces or odd characters, such as `C:\Dev\Rubbish`. Very long paths can break Windows builds.

- **Option A, Unity Hub:** **Projects > Add** (the arrow next to it) **> Add project from repository**. Sign in, pick the
  Rubbish repository and choose `C:\Dev\Rubbish`. The Hub downloads the workspace.
- **Option B, the Unity Version Control desktop app:** install it from **unity.com/products/unity-version-control** and sign
  in. Create a new workspace for the Rubbish repository in `C:\Dev\Rubbish`, then **Update workspace**. After that, in the
  Hub, use **Projects > Add > Add project from disk** and pick the folder.

Check that the folder contains `Assets`, `Packages`, `ProjectSettings`, `Tools` and `steam_appid.txt`.

### 1.5 First open

1. Open the project from the Hub. The first import builds `Library` from scratch: it downloads packages (Steamworks.NET
   comes from GitHub), imports about 1,400 assets and compiles shaders. **Expect 15 to 45 minutes.** Leave it running.
2. **If you see "Enter Safe Mode?"**, choose **Quit**. Then check git (§1.1) and open the project again. Safe Mode almost always
   means a git package failed to download.
3. When it finishes, the menu bar has a **Rubbish** menu, and **Window > Package Manager** lists **Steamworks.NET**
   (2025.164.1). A few yellow warnings in the Console are normal.
4. The **Windows Playtest** build profile finishes itself once the import settles. The Console shows
   `[Setup_WindowsPlaytest] Finished the Windows Playtest build profile on this PC` (§3.2 explains why). If you don't see it,
   run **Rubbish > Setup > Windows Playtest** once. The menu build does the same thing anyway.
5. Optional check: run **Rubbish > Setup > Windows Playtest** again. It should log `[Setup_WindowsPlaytest] already up to date`.

---

## 2. Check Steam in the Editor (optional)

The quickest check is to build and run the exe (§3 and §4). If you also want Steam inside the Editor:

- The Editor always plays as the test identity `dev:editor`, even when Steam is running. That's deliberate, so your Editor
  save never switches between two accounts (`Accounts.md` §1.2).
- **To opt in:** go to **Edit > Project Settings > Player > Other Settings > Scripting Define Symbols**, click **+**, add
  `RUBBISH_EDITOR_STEAM` and click **Apply**. With Steam open, open `Assets/Scenes/Bootstrap.unity` and press Play. The Console
  logs the Steam identity, and your Steam profile shows **Playing Spacewar**. The Steam overlay (Shift+Tab) never works inside
  the Editor; that's normal.
- **Remove the define afterwards, and don't check it in.** With the define set and Steam closed, the Editor shows the
  **"Rubbish needs Steam"** screen on purpose. It never falls back to `dev:editor`.

---

## 3. Build

### 3.1 From the menu (recommended)

**Rubbish > Build > Windows Playtest (zip for friends)**

This does the following:

1. Re-applies the Windows settings. Normally nothing changes; any change is logged.
2. Builds a **development build** with the `RUBBISH_PLAYTEST` define into `Builds\Windows-Playtest\Rubbish.exe`. That define
   makes the game sign in through Steam automatically, so friends just double-click the exe.
3. Puts `steam_appid.txt`, `README.txt` and `BuildInfo.txt` next to the exe.
4. Zips the folder to `Builds\Rubbish-Playtest-<version>-<date>-<time>.zip` and opens Explorer on it.

The first build takes 10 to 30 minutes, mostly shader compilation. Later builds take 3 to 10. The last Console line reads
`[RubbishBuild] Windows playtest ready: …zip (N files, X MB)`.

Each build replaces `Builds\Windows-Playtest\`, so close Rubbish first. Old zips are kept, each with its own date.

**Rubbish > Build > Windows Playtest (release config)** is the same build without development mode: no "Development Build"
watermark and no on-screen error log. Send friends the development build, because its logs are much more useful.

**Version:** **Edit > Project Settings > Player > Version** (currently `0.1.0`) goes into the zip name and `BuildInfo.txt`. Bump
it whenever you like.

### 3.2 From the Build Profiles window

Use **File > Build Profiles**, select **Windows Playtest** in the list, click **Switch Profile**, then **Build**. Pick (or create)
`Builds\Windows-Playtest`. The same steam_appid, README, BuildInfo and zip steps run automatically.

- The profile is already set to Development Build with the `RUBBISH_PLAYTEST` define. It was created on the Mac, which has no
  Windows Build Support, so the Development Build tick is filled in on the PC's first open (§1.5). If it ever looks unticked,
  run **Rubbish > Setup > Windows Playtest**.
- While this profile is active, the Editor still plays as `dev:editor`. The define only changes built players.

### 3.3 From the command line (Unity closed)

```bat
"C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe" -batchmode -quit -projectPath "C:\Dev\Rubbish" ^
  -executeMethod RubbishBuild.BuildWindowsPlaytest -logFile "C:\Dev\Rubbish\Logs\build-windows.log"
```

Exit code 0 means it worked. Add `-playtestRelease` for the release config, or `-buildOutput D:\somewhere` to build into another
folder. That folder must be empty or one this script built before; it refuses to clear anything else.

### 3.4 What's in the output

| In `Builds\Windows-Playtest\` | What it is |
|---|---|
| `Rubbish.exe` | The game. |
| `Rubbish_Data\` | Game data. `Plugins\x86_64\steam_api64.dll` is Steam's library. |
| `MonoBleedingEdge\`, `UnityPlayer.dll`, `UnityCrashHandler64.exe`, `D3D12\` | Unity's runtime. All of it is needed. |
| `steam_appid.txt` | Contains `480`. It tells Steam which app this is when the game isn't started by the Steam client. |
| `README.txt` | Instructions for friends (from `Tools/playtest/README_FRIENDS.txt`). |
| `BuildInfo.txt` | Version, build date, network protocol. Compare these when "Update your game" appears. |
| `Rubbish_BurstDebugInformation_DoNotShip\`, `.rubbish-playtest-build` | Debug info and a marker for the build script. Both are left out of the zip. |

In `Builds\`: `Rubbish-Playtest-0.1.0-20261001-1930.zip`, holding one folder of the same name with everything above in it.

---

## 4. Test it yourself first

1. Open Steam and sign in.
2. **Unzip the zip** somewhere else, such as your Desktop, rather than running `Builds\Windows-Playtest`. That way you test
   exactly what friends get.
3. Double-click `Rubbish.exe`.
   - If **"Windows protected your PC"** appears, click **More info**, then **Run anyway**. The exe isn't code-signed.
   - If **Windows Firewall** asks, allow it on **Private networks**.
4. Steam should show you as **Playing Spacewar**, and the menu shows a one-line Steam status. Shift+Tab should open the Steam
   overlay; if it doesn't, see §8. Steam messages ("Steam is up", join progress, problems) appear in the lower third of the
   screen.
   - **Quick Steam check (optional):** make a shortcut to `Rubbish.exe`, add ` -steamselftest` after the quotes in its
     **Target**, and run it with Steam open. After about 2 seconds "Steam self-test passed." appears near the bottom, and
     `Player.log` (§6.4) contains `STEAM SELFTEST OK`. If it says FAIL, keep that line from `Player.log`.
5. **Play Solo** for a minute to make sure the normal game works. Press **Esc** (solo play pauses), then **Save & quit to
   menu**.
6. Click **Host with Friends**. A message says the Steam lobby is open. Press **Esc** and click **Invite friends**: the
   **invite code** is on the yellow licence plate, hidden until you click **Show** because Streamer mode is on by default
   (**Copy** copies it without showing it; with Streamer mode off in Settings > Interface it's also at the top of the screen).
   **Invite Steam friends** on that page, or **F2**, should open Steam's invite dialog.

---

## 5. Share it

- **The zip is a few hundred MB**, and Discord's free upload limit is 10 MB. Upload it to **Google Drive**, **Dropbox**,
  **OneDrive** or **WeTransfer** (free up to 2 GB) and post the link in Discord.
- **Google Drive:** set sharing to "Anyone with the link". Friends will see *"Google Drive can't scan this file for
  viruses"* because the file is large; they click **Download anyway**.
- **Tip for friends:** before unzipping, right-click the zip, choose **Properties**, tick **Unblock** and click OK. That avoids
  repeated SmartScreen prompts.
- **One build at a time.** Whenever you make a new build, send the new zip to everyone. Mixed builds either refuse to connect
  ("Update your game") or behave strangely.
- Tell friends three things: **keep Steam open**, **unzip it fully**, and **read README.txt**.

---

## 6. Play

### 6.1 How friends run it

Friends follow the same steps as §4: Steam open and signed in, unzip, run `Rubbish.exe`, then the SmartScreen and Firewall
prompts. Steam must not be in Offline Mode, because lobbies and Join Game need it online.

**About "Spacewar":** until Rubbish has its own Steam App ID, the playtest borrows **480 (Spacewar)**, Valve's shared test app.
That has a few effects:

- Everyone appears as **playing Spacewar**.
- **Start Rubbish before you click Join Game or accept an invite.** If Rubbish isn't running, Steam tries to launch the real
  Spacewar instead (and may offer to install it). Just close that, start Rubbish, and try again.
- Other developers use 480 too. Rubbish only lists its own lobbies, and those are friends-only.

### 6.2 Hosting and joining

The host clicks **Host with Friends** on the main menu, then picks a save or **Start a new save**. That opens the shop over Unity Relay, opens a friends-only Steam
lobby, and makes an **invite code**. The code is on the **Esc** menu's **Invite friends** page, hidden until **Show** while
Streamer mode is on (the default); **Copy** works without showing it. Friends, with Rubbish already running, can get in these
ways (their main menu's **Join a friend** page also lists Steam friends who are hosting, with a **JOIN** button, and the
invites they got):

1. **Join Game:** in the Steam Friends list, right-click the host and choose **Join Game**.
2. **Invite:** the host presses **F2** (or **Esc** > **Invite friends** > **Invite Steam friends**), or presses **Shift+Tab** to
   open the Steam overlay, opens Friends, right-clicks a friend and chooses **Invite to Game**. The friend accepts the invite in
   Steam (or clicks **JOIN** on the invite card that slides in on their main menu).
3. **Invite code:** this is the fallback if Steam is being difficult. The friend clicks **Join a friend** on the main menu, opens
   the **Code** tab, types the host's code and clicks **Join**.

Joining from the main menu (a code, the friends list, an invite card) asks which save to bring, or a new one, before it
connects. Joining from Steam itself asks the same question once you are in the host's Town.

A crew holds up to 4 people; a fifth gets *"Crew is full (4/4)"*. **Esc** opens the in-game menu, a timesheet: **Resume**,
**Invite friends** (guests see the code they joined with), **Settings**, **Unstuck** (on foot it calls your own truck to you, −$25 unless Tony's Unflip Button is installed; it only moves *you* if you're really stuck), **Messages**, the **Crew** list (the host
can kick) and the **Town rules**, then **Close the shop** for the host (everyone goes home) or **Leave the Boss's shift** for a
guest, and **Quit to desktop**. Solo play pauses while it's open; co-op never pauses (the stamp says ON BREAK). Esc at the
laptop, the sorting table, the map or the chess board backs out of those first. The session lasts as
long as the host's game: when the host leaves, everyone goes back to the main menu with "The Boss went home". If the host's
game **crashes**, guests notice only after about 30 seconds ("The Boss's truck broke down").

### 6.3 What to test

Write down roughly when anything odd happens and who saw it.

1. **Launch:** the menu loads, Steam says Spacewar, and there are no error boxes.
2. **Solo:** click **Play Solo**, drive out, pick up a bag, come back and tip it. Everything should work as before. Quit, relaunch,
   and check your progress was kept.
3. **Join Game:** host plus one friend. Can both of you see each other walking around? Does each player get a different
   Dumpling colour?
4. **Invite and invite code:** try both of the other ways in.
5. **Three to four players,** then a fifth to see the "Crew is full" message.
6. **The truck:** there's one seat. Whoever gets in first drives, and everyone else sees it drive. There are no passenger
   seats yet. Only the host can drive (or walk) through the garage door to change scenes; a guest at the door gets a message.
7. **Leave and rejoin:** you should get the same colour back.
8. **Host quits mid-session:** guests should return to the menu with a message. Afterwards, guests check their own solo save is
   unchanged: the same money as before they joined, and the Today column on Scrapazon's Career page (laptop) doesn't
   include anything they earned as a guest.
9. **Alt+Tab on the host:** the game should keep running for guests while the host's window is in the background.
10. **Lag:** note rubber-banding, stutter or disconnects, and how far apart the players are (same city, different countries).

### 6.4 Logs: where they are and what to send

- **Folder:** press **Windows+R**, paste `%USERPROFILE%\AppData\LocalLow\Archway Games\Rubbish` and press Enter.
- **Send `Player.log`** (this run), plus **`Player-prev.log`** if the game was restarted after the problem. For connection
  problems, get the logs of **both** the host and the guest from the same session.
- **Crashes:** Unity's crash handler saves to `%TEMP%\Archway Games\Rubbish\Crashes\`. Zip the newest folder and send it.
- In a development build, errors also appear as red text in the bottom-left corner of the screen; a screenshot of that helps.
- Logs contain Steam names but no passwords or Steam tickets (lint rule `secrets`).
- Saves live in the same folder (`rubbish_save.json`, `ebae_save.json` and their `.gen\` backups). **Don't delete them.** If a
  save is ever unreadable, the game keeps a copy, stops saving and tells you.

---

## 7. Known limitations of this build (co-op N1–N5, 2 Oct 2026)

- **What's in:** every player runs their own business on Depot Row (4 lots, 4 colours), shared curb trash and pay by truck owner,
  free roam (no host-driven scene changes in co-op), hiring with hourly wages, ebag between players, and the chaos verbs (grab races,
  throw, pass, bonk, shove, carry flopped friends, rear-step hitching, rhythm tug). Details: `Multiplayer.md` §13.
- **4 players per world.** A 5th gets "Town is full (4/4)".
- **Not yet:** rear-step grip/fling, lag compensation for fast trucks, voice chat, night-shift co-op critters. A guest who crashes
  (rather than leaving) loses the last part-hour of wages. Lot numbers aren't remembered between host sessions.
- **Solo** still uses the old garage ⇄ street scene flow.
- **Steam shows Spacewar,** and invites can launch Spacewar if Rubbish isn't running (§6.1).
- **The exe is unsigned,** so SmartScreen warns once per download. **Development build:** watermark in the corner.
- **Connections go through Unity Relay.** Everyone needs working internet, and the host's upload speed matters most.
  (Steam's own relay network comes later; `Multiplayer.md` B13.)
- **Steam identity checks only warn** in development and playtest builds. They log problems but let friends in, and the lobby
  is friends-only anyway.

---

## 8. Troubleshooting

| Problem | Fix |
|---|---|
| First open: *"No 'git' executable was found"*, Safe Mode, or errors about `Steamworks` types | Install Git for Windows (§1.1), restart the Hub, reopen the project. |
| No **Rubbish** menu | Scripts didn't compile. Look for red errors in the Console. Usually it's git (above) or the wrong Unity version (needs 6000.3.25f1). |
| Build: *"StandaloneWindows64 isn't installed"* | Hub > Installs > 6000.3.25f1 > gear icon > Add modules. This normally can't happen on Windows. |
| Build: *"Rubbish.exe … isn't in …, so there's nothing to zip"*, or a zip with only three .txt files | Windows Build Support was installed while Unity was open. Quit Unity, reopen the project and build again. (The Console also shows *"Build target 'StandaloneWindows64' not supported"*.) |
| Build: *"…already has files in it that this build script didn't make"* | Empty `Builds\Windows-Playtest\` (or move it), then build again. |
| Build: *"Couldn't clear the previous build"* | Close `Rubbish.exe` and any Explorer window showing that folder. |
| Build fails with a path-too-long error | Move the project to a short path such as `C:\Dev\Rubbish`. |
| *"Recovered your save from a backup"* | The main save file couldn't be read at launch (a crash or power cut mid-save), so the game loaded the newest backup and kept any unreadable file as `*.corrupt-*.json`. Keep playing; send `Player.log` (and the `.corrupt` file if there is one). If it happens on every launch, add the save folder as an antivirus exclusion. |
| **"Rubbish needs Steam"** screen | Start Steam and sign in (not Offline Mode), then click **Try again**, or quit and start Rubbish again. There's no automatic fallback. |
| Not sure Steam works at all | Run `Rubbish.exe -steamselftest` with Steam open (§4). `Player.log` says `STEAM SELFTEST OK` or `STEAM SELFTEST FAIL: <why>`. |
| Testing without Steam | Right-click `Rubbish.exe` > Create shortcut. Open the shortcut's Properties and add ` -nosteam` after the quotes in **Target**. This plays with a test identity, and only invite codes work. |
| Crashes at start, or `DllNotFoundException: steam_api64` in Player.log | Antivirus removed `Rubbish_Data\Plugins\x86_64\steam_api64.dll`. Restore it from quarantine and add the game folder as an exclusion, or unzip again. |
| SmartScreen has no "Run anyway" button | Click **More info** first. On a work-managed PC, policy may block unsigned apps. |
| **Shift+Tab does nothing** | Check Steam > Settings > In Game > **Enable the Steam Overlay while in-game**. The overlay sometimes doesn't attach to games that Steam didn't start. Use the Steam Friends list instead (right-click > **Join Game** or **Invite to Game**), or the invite code. |
| **Join Game starts or installs "Spacewar"** | Start Rubbish first, then click Join Game (§6.1). |
| Friends don't see **Join Game** | The host must be hosting (not in solo), you must be Steam friends, and neither of you can be in Offline Mode or Invisible. Fall back to the invite code. |
| *"Update your game"* | Different builds. Everyone downloads the newest zip; compare `BuildInfo.txt`. |
| *"Crew is full (4/4)"* | Four is the most a crew holds in this build. |
| Relay or internet errors (can't sign in to Unity services, invite code not found, time-outs) | Check the internet connection and turn off any VPN. Type the code exactly. The host must still be in the session (codes die when the host quits). School and work networks sometimes block the UDP traffic Relay uses. Check status.unity.com. |
| You clicked Cancel on the firewall prompt | Relay usually still works. To fix it anyway: Windows Security > Firewall & network protection > **Allow an app through firewall** > tick **Private** for Rubbish. |
| Wrong monitor, or the window is too big | Alt+Enter switches to a window; Shift+Win+Left/Right moves it between monitors. |
| Black screen or very poor performance | Update the graphics driver. The game uses Direct3D 11; you can try `-force-d3d12` on a shortcut. |

---

## 9. Reference: how this is set up

For whoever touches this next.

- **Settings** come from `Assets/Scripts/Editor/Setup_WindowsPlaytest.cs`, which is idempotent (**Rubbish > Setup > Windows
  Playtest**; the build also runs it):
  - Run In Background on.
  - Fullscreen Window (borderless) by default, resizable, 1280x720 when windowed, Alt+Enter allowed.
  - Mono scripting backend for Standalone.
  - Windows graphics APIs Direct3D11 then Direct3D12. DX11 has the widest hardware support, and the Steam overlay attaches to
    it reliably when Steam didn't start the game.
  - Default icon `Assets/Art/Icon/RubbishIcon.png`, a placeholder drawn by `Tools/playtest/make_icon.py` (pure Python; run it
    again to tweak the icon).
  - The build profile.

  It only *checks* the product name (`Rubbish`), company (`Archway Games`), bundle id (`com.archwaygames.rubbish`), API
  compatibility level and scene order (Bootstrap first). It never changes them; the build copy runs as `RubbishBuildCopy` on
  purpose.
- **Build code:** `Assets/Scripts/Editor/RubbishBuild.Windows.cs`, a partial of T2's `RubbishBuild`.
  - The menu build uses `BuildPlayerOptions` with `extraScriptingDefines = { "RUBBISH_PLAYTEST" }`, so it works whatever
    state the profile is in.
  - `WindowsPlaytestPostprocess` (callback order 1000) handles builds started from the Build Profiles window.
  - `RubbishBuild.IsWindowsPlaytestBuild(target)` tells other build hooks whether a playtest build is running.
- **`RUBBISH_PLAYTEST`** (agreed with B3B10, `SteamPolicy.Decide`): a playtest player uses the Steam identity unless
  `-nosteam` is passed. The Editor, batchmode and MPPM clones are decided first, so they never use Steam because of it.
  `RestartAppIfNecessary` never runs for 480.
- **The build profile** is `Assets/Settings/Build Profiles/Windows Playtest.asset`. A Mac without Windows Build Support can
  store only the define, because Development Build lives in the Windows platform settings. The first Editor with Windows
  support switches Development on once, then records `rubbish:playtest-development-default-applied` in the asset's `.meta`
  `userData`, so a later manual change in the window is respected.
- **The friend README** is `Tools/playtest/README_FRIENDS.txt`, with the placeholders `{VERSION}`, `{BUILT}`, `{EXE}`, `{APPID}`,
  `{CONFIG}` and `{NAME}`. It isn't in `Builds/` because Plastic's `ignore.conf` ignores every `Builds` folder.
- **Steamworks.NET native plugins** (package import settings, checked):
  - `steam_api64.dll` is enabled for Standalone Win64 (x86_64) and the Windows Editor (x86_64).
  - The 32-bit `steam_api.dll` is excluded from Win64 (CPU None).
  - The package's `RedistCopy` postprocess writes `Rubbish_Data/Plugins/Steamworks.NET.txt`.
  - Its `RedistInstall` recreates `steam_appid.txt` (480) at the project root if it's missing.
- **IL2CPP (optional, not needed):** install "Windows Build Support (IL2CPP)" plus Visual Studio 2022 with the "Desktop
  development with C++" workload, then set **Player > Other Settings > Scripting Backend** to IL2CPP for Windows. Don't check
  that in: the Mac bot builds use Mono.
- **Testing on the Mac:** the Mac has no Windows Build Support, so the Windows player can only be built on the PC, and hadn't
  been built when this was written. The Mac checks covered compiling (including a compile with the Windows player settings and
  `RUBBISH_PLAYTEST`), the settings and the profile asset, a dry run of the finish and zip step on a fake build folder, and the
  M0 gate on Mac bot builds of the same code (`Multiplayer.md` §8.7.10). Steam itself (lobby, invites, Join Game, the auth
  check) has never run: this Mac has no Steam client, so your PC is its first real test.
