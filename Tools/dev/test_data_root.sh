#!/bin/bash
# Tools/dev/test_data_root.sh: point the live Editor's saves at a scratch copy for agent play tests, so a test never
# writes Nick's real saves (Docs/Multiplayer.md §8.7.6, Docs/Accounts.md §5.1).
#
#   Tools/dev/test_data_root.sh on [--owner NAME] [--minutes N] [--empty] [--autohost] [--force]
#       Seeds a scratch root with COPIES of Nick's two save files (rubbish_save.json, ebae_save.json; --empty = none),
#       writes UserSettings/RubbishDev.json {dataRoot, owner, expiresUtc} (at most 120 min ahead; SaveRoot ignores it once
#       expired) and arms a watchdog that removes it at expiry. Refuses while another owner's override is live (--force).
#       --autohost also sets "autoHost": true for the MPPM2 autostart (§8.7.6).
#   Tools/dev/test_data_root.sh off [--owner NAME]
#       Removes the override (call it in `finally`) and checks that Nick's real save folder didn't change meanwhile
#       (exit 3 if it did). --owner only removes the override if NAME owns it.
#   Tools/dev/test_data_root.sh status
#       Prints the override, or clears an expired one.
#
# SaveRoot (hotfix L0) reads the file once per play session, so switch it on BEFORE entering play mode and check the
# "TEST DATA ROOT" watermark. Bots and builds never use this file; they get -dataroot. Scratch roots live under
# Logs/TestDataRoots/ (ignored by Plastic, never synced) and are pruned after a day. Override the base folder with
# RUBBISH_TEST_ROOTS.
set -euo pipefail
PROJECT="$(cd "$(dirname "$0")/../.." && pwd)"
exec python3 - "$PROJECT" "$0" "$@" <<'PY'
import json, os, secrets, shutil, subprocess, sys, time
from datetime import datetime, timedelta, timezone

PROJECT, SELF, ARGV = sys.argv[1], os.path.abspath(sys.argv[2]), sys.argv[3:]
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(PROJECT, "Tools", "bots"))
import audit  # real roots + snapshots, shared with the bot audit

FILE = os.path.join(PROJECT, "UserSettings", "RubbishDev.json")
BASE = os.path.abspath(os.path.expanduser(os.environ.get("RUBBISH_TEST_ROOTS", os.path.join(PROJECT, "Logs", "TestDataRoots"))))
SAVE_FILES = ("rubbish_save.json", "ebae_save.json")
MAX_MIN = 120

def usage(code=2):
    with open(SELF) as f:
        lines = f.read().splitlines()[1:]
    for l in lines:
        if not l.startswith("#"): break
        print(l[2:] if l.startswith("# ") else l[1:])
    sys.exit(code)

def iso(dt): return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
def parse_iso(s):
    try: return datetime.strptime(s, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except Exception: return None
def now(): return datetime.now(timezone.utc)

def read():
    try: return json.load(open(FILE))
    except FileNotFoundError: return None
    except ValueError: return {"_unreadable": True}

def live(cfg):
    exp = parse_iso((cfg or {}).get("expiresUtc", ""))
    return exp is not None and exp > now()

def opt(name, default=None):
    if name in ARGV:
        i = ARGV.index(name)
        if i + 1 >= len(ARGV): usage()
        return ARGV[i + 1]
    return default

def saveroot_present():
    for dp, _, fns in os.walk(os.path.join(PROJECT, "Assets", "Scripts")):
        if "SaveRoot.cs" in fns:
            try:
                if "RubbishDev.json" in open(os.path.join(dp, "SaveRoot.cs"), encoding="utf-8").read(): return True
            except OSError: pass
    return False

def check_real(cfg):
    snap_path = (cfg or {}).get("realSnapshot")
    if not snap_path or not os.path.exists(snap_path): return []
    before = json.load(open(snap_path))
    return audit.diff_snapshots(before, audit.snapshot(list(before.get("roots", {}).keys())))

def remove(cfg, why):
    try: os.remove(FILE)
    except FileNotFoundError: pass
    changes = check_real(cfg)
    print(f"test_data_root: off ({why}); the Editor uses the real saves from the next play session")
    if changes:
        print("test_data_root: WARNING Nick's real save folder changed while the test root was on:")
        for c in changes[:20]: print("    " + c)
        print("  (if Nick played during the window that's his own progress; otherwise something bypassed SaveRoot)")
        return 3
    return 0

def prune():
    if not os.path.isdir(BASE): return
    cutoff = time.time() - 86400
    cur = (read() or {}).get("dataRoot")
    for d in os.listdir(BASE):
        p = os.path.join(BASE, d)
        if os.path.isdir(p) and os.path.getmtime(p) < cutoff and os.path.abspath(p) != os.path.abspath(cur or ""):
            shutil.rmtree(p, ignore_errors=True)

if not ARGV or ARGV[0] in ("-h", "--help"): usage(0 if ARGV else 2)
cmd = ARGV[0]

if cmd == "on":
    owner = opt("--owner", os.environ.get("USER", "agent"))
    try: minutes = float(opt("--minutes", "60"))
    except ValueError: usage()
    if minutes > MAX_MIN:
        print(f"test_data_root: {minutes:g} min is over the {MAX_MIN} min limit; using {MAX_MIN}"); minutes = MAX_MIN
    if minutes <= 0: usage()
    cur = read()
    if cur and live(cur) and cur.get("owner") != owner and "--force" not in ARGV:
        print(f"test_data_root: {cur.get('owner')}'s test root is on until {cur.get('expiresUtc')} ({cur.get('dataRoot')}); "
              "wait, or pass --force", file=sys.stderr)
        sys.exit(3)
    prune()
    real = audit.real_roots()[0]
    safe_owner = "".join(c if c.isalnum() or c in "-_." else "_" for c in owner)[:40] or "agent"
    root = os.path.join(BASE, time.strftime("%Y%m%d-%H%M%S") + "-" + safe_owner)
    os.makedirs(root, exist_ok=True)
    if os.path.abspath(root).startswith(os.path.abspath(real) + os.sep): sys.exit("test_data_root: refusing a scratch root inside the real data root")
    seeded = []
    if "--empty" not in ARGV:
        for fn in SAVE_FILES:
            src = os.path.join(real, fn)
            if os.path.exists(src):
                shutil.copy2(src, os.path.join(root, fn)); seeded.append(fn)
    meta = os.path.join(root, ".testroot")
    os.makedirs(meta, exist_ok=True)
    snap_path = os.path.join(meta, "real.before.json")
    json.dump(audit.snapshot(), open(snap_path, "w"), indent=1)
    token = secrets.token_hex(8)
    expires = now() + timedelta(minutes=minutes)
    cfg = {"dataRoot": root, "owner": owner, "expiresUtc": iso(expires), "createdUtc": iso(now()),
           "token": token, "realSnapshot": snap_path}
    if "--autohost" in ARGV: cfg["autoHost"] = True
    os.makedirs(os.path.dirname(FILE), exist_ok=True)
    tmp = FILE + ".tmp"
    json.dump(cfg, open(tmp, "w"), indent=1)
    os.replace(tmp, FILE)
    # Watchdog: removes this override at expiry even if the caller never runs `off` (a crashed agent).
    secs = int(minutes * 60) + 5
    log = open(os.path.join(meta, "watchdog.log"), "a")
    subprocess.Popen(["/bin/bash", "-c", f'sleep {secs}; exec "$0" off --if-token {token} --why expired', SELF],
                     stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, start_new_session=True)
    print(f"test_data_root: on for {minutes:g} min (until {iso(expires)}), owner {owner}")
    print(f"  data root  {root}")
    print(f"  seeded     {', '.join(seeded) if seeded else '(empty)'} (copies of {real})")
    if not saveroot_present():
        print("  WARNING    SaveRoot (hotfix L0) isn't in the project: the Editor still writes the REAL saves. Don't play-test yet.")
    print("  Enter play mode now (not before) and check for the TEST DATA ROOT watermark. Always run `off` afterwards.")
    sys.exit(0)

if cmd == "off":
    cur = read()
    if cur is None:
        print("test_data_root: already off"); sys.exit(0)
    tok = opt("--if-token")
    if tok and cur.get("token") != tok:
        print("test_data_root: a newer override replaced this one; leaving it"); sys.exit(0)
    owner = opt("--owner")
    if owner and cur.get("owner") != owner and live(cur):
        print(f"test_data_root: the override belongs to {cur.get('owner')}, not {owner}; leaving it", file=sys.stderr); sys.exit(3)
    sys.exit(remove(cur, opt("--why", "off")))

if cmd == "status":
    cur = read()
    if cur is None:
        print("test_data_root: off (real saves)"); sys.exit(0)
    if cur.get("_unreadable"):
        print(f"test_data_root: {FILE} is unreadable; SaveRoot ignores it. Run `off` to remove it."); sys.exit(1)
    if not live(cur):
        sys.exit(remove(cur, f"expired at {cur.get('expiresUtc')}"))
    left = (parse_iso(cur["expiresUtc"]) - now()).total_seconds() / 60
    print(f"test_data_root: ON, owner {cur.get('owner')}, {left:.0f} min left (until {cur.get('expiresUtc')})")
    print(f"  data root  {cur.get('dataRoot')}")
    if cur.get("autoHost"): print("  autoHost   true")
    changes = check_real(cur)
    if changes: print(f"  WARNING    {len(changes)} change(s) in Nick's real save folder since `on`")
    sys.exit(0)

usage()
PY
