#!/bin/bash
# Tools/bots/run_bots.sh: launch headless RubbishBots on this Mac and audit the run (Docs/Multiplayer.md §8.7.6).
#
#   Tools/bots/run_bots.sh -n 4 -m 30 -p crew_bad_wifi [--brain laptop,walk,laptop,ritual]
#                          [--kill-host-at 600 [--relaunch-host]] [--rejoin bot-2@120]... [--bot-args 'bot-3:-proto 999']...
#                          [--seed DIR] [--seed-guests DIR] [--audit '--min hash=50 --expect bot-3:Update']
#                          [--app PATH] [--out DIR] [--no-sandbox] [--stagger SEC] [--guest-stagger SEC] [--relay]
#   Tools/bots/run_bots.sh --smoke [--app PATH]   one `RubbishBots -batchmode -nographics -botsmoke`: exit 0 within 60 s
#
# bot-1 hosts (-bothost -botport P); the others join (-botjoin 127.0.0.1:P). With --relay (the Relay smoke; needs internet)
# bot-1 hosts over Unity Relay (-bothost -botrelay) and the others join with the code from its audit log
# (-botjoin relay:@<run>/bot-1/audit.jsonl). Every bot gets
#   -batchmode -nographics -nosteam -dataroot <run>/bot-N/data -playerid dev:bot-N -auditlog <run>/bot-N/audit.jsonl
#   -logFile <run>/bot-N/player.log -botminutes M [-netsim P] [-botbrain B]
# Those flags belong to BotDriver (package B5, Assets/Scripts/Net/Dev/BotDriver.cs): it hosts or joins over LocalDirect, plays
# the brain (walk, idle, laptop, ritual, grief) on its own player, leaves and quits (exit 0; 1 failed; 3 turned away at the door).
# More bot flags through --bot-args: -botexpectreject (exit 0 only if rejected), -kill-self-at SEC (a crash),
# -botinject exception@SEC|error@SEC (audit self-test), -proto N (B1). Kill-during-write drills: Tools/bots/savetorture.sh.
# NetAudit lines of kind hash compare host and guest state; audit.py fails on any mismatch (--audit '--max-mismatch N').
# Known non-networking log errors: --audit '--allow-file Tools/bots/known_errors.txt'.
# --kill-host-at SIGKILLs bot-1 (a crash); --relaunch-host starts it again on the same data root. --rejoin bot-N@SEC quits
# bot-N (SIGTERM) and relaunches it with the same data root and player id. --seed copies a folder's files into every
# bot's data root (--seed-guests: guests only); seeded guests must leave them byte-identical.
# Every bot runs under sandbox-exec with writes to Nick's real save folders denied (--no-sandbox to skip), and the run
# snapshots those folders before and after: audit.py fails the run if anything there changed.
# Runs go to <copy>/Builds/BotRuns/<stamp>/ (or --out). Exit code = audit.py's (0 pass, 1 fail), 2 = usage.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
exec python3 - "$HERE" "$@" <<'PY'
import argparse, json, os, shlex, shutil, signal, socket, subprocess, sys, time

HERE = sys.argv[1]
sys.dont_write_bytecode = True   # no __pycache__ inside the project
sys.path.insert(0, HERE)
import audit  # noqa: E402  (Tools/bots/audit.py: real roots, snapshots, the checks)

PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
COPY = os.path.abspath(os.path.expanduser(os.environ.get("RUBBISH_BUILD_COPY", "~/Dev/Rubbish-build")))

ap = argparse.ArgumentParser(prog="run_bots.sh", add_help=True)
ap.add_argument("-n", type=int, default=3, help="bots, including the host (default 3)")
ap.add_argument("-m", type=float, default=10, help="minutes per bot session (default 10)")
ap.add_argument("-p", default="crew_bad_wifi", help="-netsim preset, or 'none' (default crew_bad_wifi)")
ap.add_argument("--brain", default="", help="comma list of -botbrain per bot in order; the last one repeats")
ap.add_argument("--kill-host-at", type=float, default=None, metavar="SEC")
ap.add_argument("--relaunch-host", action="store_true")
ap.add_argument("--rejoin", action="append", default=[], metavar="bot-N@SEC")
ap.add_argument("--bot-args", action="append", default=[], metavar="'bot-N:ARGS'")
ap.add_argument("--seed", default=None, metavar="DIR")
ap.add_argument("--seed-guests", default=None, metavar="DIR")
ap.add_argument("--guests-may-write", action="store_true", help="don't require seeded guest files to stay identical")
ap.add_argument("--audit", default="", metavar="ARGS", help="extra audit.py arguments (one quoted string)")
ap.add_argument("--app", default=os.path.join(COPY, "Builds", "DevHeadless", "RubbishBots.app"))
ap.add_argument("--out", default=None, metavar="DIR")
ap.add_argument("--no-sandbox", action="store_true")
ap.add_argument("--stagger", type=float, default=3.0, help="seconds between the host and the first guest (default 3)")
ap.add_argument("--guest-stagger", type=float, default=0.0, help="seconds between one guest and the next (default 0; e.g. so bot-5 is the 5th to knock)")
ap.add_argument("--smoke", action="store_true")
ap.add_argument("--relay", action="store_true", help="host over Unity Relay; guests join by the code in bot-1's audit log")
try:
    a = ap.parse_args(sys.argv[2:])
except SystemExit as e:
    sys.exit(0 if e.code == 0 else 2)

def die(msg):
    print(f"run_bots: {msg}", file=sys.stderr); sys.exit(2)

exe = a.app
if exe.endswith(".app") or os.path.isdir(exe):
    exe = os.path.join(exe, "Contents", "MacOS", "RubbishBots")
if not os.access(exe, os.X_OK):
    die(f"no RubbishBots executable at {exe} (build it: Tools/bots/sync_build.sh --build)")
if a.n < 1 or a.n > 16: die("-n must be 1..16")

stamp = time.strftime("%Y%m%d-%H%M%S")
run = os.path.abspath(a.out or os.path.join(COPY, "Builds", "BotRuns", stamp + ("-smoke" if a.smoke else "")))
os.makedirs(run, exist_ok=True)

roots = audit.real_roots()
for r in roots:
    if os.path.abspath(run).startswith(os.path.abspath(r) + os.sep): die(f"the run folder can't be inside the real data root {r}")

def editor_playing():
    """True/False if the live Editor answers, else None. Never blocks the run for long."""
    try:
        p = subprocess.run(["unity", "command", "editor_status", "--no-banner", "--json"], cwd=PROJECT,
                           capture_output=True, text=True, timeout=8)
        return json.loads(p.stdout)["data"]["result"].get("playMode") == "playing"
    except Exception:
        return None

sandbox = None
if not a.no_sandbox:
    if shutil.which("sandbox-exec"):
        paths = sorted({os.path.realpath(r) for r in roots} | set(roots))
        esc = lambda s: s.replace("\\", "\\\\").replace('"', '\\"')
        sandbox = "(version 1)(allow default)(deny file-write* " + " ".join(f'(subpath "{esc(p)}")' for p in paths) + ")"
    else:
        print("run_bots: sandbox-exec not found; bots run unsandboxed (the before/after snapshot still applies)")

before = audit.snapshot(roots)
json.dump(before, open(os.path.join(run, "real.before.json"), "w"), indent=1)
info = {"mode": "smoke" if a.smoke else "bots", "app": a.app, "exe": exe, "started": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "realRoots": roots, "sandboxed": sandbox is not None, "editorPlaying": {"start": editor_playing(), "end": None},
        "args": sys.argv[2:], "bots": []}

procs = []           # live process records
def launch(bot, extra_label=""):
    n = len(bot["processes"]) + 1
    d = bot["dir"]
    log = os.path.join(d, "player.log" if n == 1 else f"player-{n}.log")
    args = [exe] + bot["args"] + ["-logFile", log]
    cmd = (["sandbox-exec", "-p", sandbox] + args) if sandbox else args
    out = open(os.path.join(d, "stdout.txt" if n == 1 else f"stdout-{n}.txt"), "w")
    p = subprocess.Popen(cmd, stdout=out, stderr=subprocess.STDOUT, cwd=d, start_new_session=True)
    rec = {"launch": n, "pid": p.pid, "log": log, "started": time.time(), "exitCode": None, "killedByScript": False,
           "timedOut": False, "durationSec": None}
    bot["processes"].append(rec)
    procs.append((bot, rec, p, out))
    print(f"run_bots: {bot['name']}{extra_label} pid {p.pid}: {' '.join(shlex.quote(x) for x in bot['args'])}")
    return rec

def reap():
    for bot, rec, p, out in procs:
        if rec["exitCode"] is None and p.poll() is not None:
            rec["exitCode"] = p.returncode
            rec["durationSec"] = round(time.time() - rec["started"], 2)
            out.close()

def alive(bot):
    return [(rec, p) for b, rec, p, _ in procs if b is bot and rec["exitCode"] is None and p.poll() is None]

def kill_all():
    for bot, rec, p, out in procs:
        if p.poll() is None:
            try: os.killpg(p.pid, signal.SIGKILL)
            except OSError: pass

def stop(bot, sig, mark="killedByScript"):
    for rec, p in alive(bot):
        rec[mark] = True
        try: os.killpg(p.pid, sig)
        except OSError: pass

def free_port():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind(("127.0.0.1", 0)); port = s.getsockname()[1]; s.close()
    return port

def copy_seed(src, dst):
    out = {}
    for dp, _, fns in os.walk(src):
        for fn in fns:
            if fn == ".DS_Store": continue
            full = os.path.join(dp, fn); rel = os.path.relpath(full, src)
            os.makedirs(os.path.dirname(os.path.join(dst, rel)), exist_ok=True)
            shutil.copy2(full, os.path.join(dst, rel))
            out[rel] = audit.sha256(os.path.join(dst, rel))
    return out

signal.signal(signal.SIGTERM, lambda *_: (kill_all(), sys.exit(130)))
rc = 1
try:
    if a.smoke:
        d = os.path.join(run, "smoke"); os.makedirs(os.path.join(d, "data"), exist_ok=True)
        bot = {"name": "smoke", "role": "smoke", "dir": d, "dataRoot": os.path.join(d, "data"), "processes": [],
               "args": ["-batchmode", "-nographics", "-botsmoke", "-nosteam", "-dataroot", os.path.join(d, "data")]}
        info["bots"].append(bot)
        rec = launch(bot)
        deadline = time.time() + 75
        while time.time() < deadline and alive(bot): time.sleep(0.25); reap()
        if alive(bot): stop(bot, signal.SIGKILL, "timedOut")
        time.sleep(0.5); reap()
    else:
        port = free_port()
        brains = [b for b in a.brain.split(",") if b] if a.brain else []
        extra = {}
        for spec in a.bot_args:
            name, _, rest = spec.partition(":")
            name = name if name.startswith("bot-") else f"bot-{name}"
            extra.setdefault(name, []).extend(shlex.split(rest))
        rejoins = []
        for spec in a.rejoin:
            name, _, at = spec.partition("@")
            name = name if name.startswith("bot-") else f"bot-{name.replace('bot', '')}"
            rejoins.append({"name": name, "at": float(at), "done": False})
        for i in range(1, a.n + 1):
            name = f"bot-{i}"; d = os.path.join(run, name); data = os.path.join(d, "data")
            os.makedirs(data, exist_ok=True)
            args = ["-batchmode", "-nographics", "-nosteam", "-dataroot", data, "-playerid", f"dev:{name}",
                    "-auditlog", os.path.join(d, "audit.jsonl"), "-botminutes", f"{a.m:g}"]
            if a.relay:
                args += ["-bothost", "-botrelay"] if i == 1 else ["-botjoin", "relay:@" + os.path.join(run, "bot-1", "audit.jsonl")]
            else:
                args += ["-bothost", "-botport", str(port)] if i == 1 else ["-botjoin", f"127.0.0.1:{port}"]
            if a.p and a.p != "none": args += ["-netsim", a.p]
            if brains: args += ["-botbrain", brains[min(i - 1, len(brains) - 1)]]
            args += extra.pop(name, [])
            seed_src = a.seed_guests if (i > 1 and a.seed_guests) else a.seed
            seed = copy_seed(seed_src, data) if seed_src else {}
            info["bots"].append({"name": name, "role": "host" if i == 1 else "guest", "dir": d, "dataRoot": data,
                                 "auditLog": os.path.join(d, "audit.jsonl"), "args": args, "processes": [], "seed": seed,
                                 "expectSeedUnchanged": bool(seed) and i > 1 and not a.guests_may_write})
        if extra: die(f"--bot-args for unknown bots: {sorted(extra)}")
        t0 = time.time()
        for i, bot in enumerate(info["bots"]):
            launch(bot)
            if i == 0 and a.n > 1: time.sleep(a.stagger)
            elif i > 0 and i < a.n - 1 and a.guest_stagger > 0: time.sleep(a.guest_stagger)
        deadline = t0 + a.m * 60 + 90
        host = info["bots"][0]; host_killed = False; relaunch_at = None
        while True:
            reap(); now = time.time(); t = now - t0
            if a.kill_host_at is not None and not host_killed and t >= a.kill_host_at:
                print(f"run_bots: t={t:.0f}s SIGKILL host (bot-1)"); stop(host, signal.SIGKILL); host_killed = True
                if a.relaunch_host: relaunch_at = now + 2
            if relaunch_at and now >= relaunch_at and not alive(host):
                launch(host, " (relaunch)"); relaunch_at = None
            for rj in rejoins:
                if not rj["done"] and t >= rj["at"]:
                    bot = next((b for b in info["bots"] if b["name"] == rj["name"]), None)
                    if bot is None: die(f"--rejoin names an unknown bot {rj['name']}")
                    print(f"run_bots: t={t:.0f}s {rj['name']} leaves (SIGTERM) and rejoins")
                    stop(bot, signal.SIGTERM)
                    end = time.time() + 10
                    while alive(bot) and time.time() < end: time.sleep(0.2); reap()
                    if alive(bot): stop(bot, signal.SIGKILL)
                    time.sleep(1); reap()
                    # the rejoined bot plays only the time that's left, so it leaves before the run's deadline
                    left = max(0.5, a.m - (time.time() - t0) / 60.0)
                    if a.m > 0 and "-botminutes" in bot["args"]: bot["args"][bot["args"].index("-botminutes") + 1] = f"{left:.2f}"
                    launch(bot, " (rejoin)"); rj["done"] = True
            if not any(alive(b) for b in info["bots"]) and relaunch_at is None and all(r["done"] for r in rejoins):
                break
            if now > deadline:
                print("run_bots: deadline passed; killing the bots still running")
                for b in info["bots"]: stop(b, signal.SIGKILL, "timedOut")
                time.sleep(1); reap(); break
            time.sleep(0.5)
    reap()
    info["ended"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    info["editorPlaying"]["end"] = editor_playing()
    json.dump(audit.snapshot(roots), open(os.path.join(run, "real.after.json"), "w"), indent=1)
    for b in info["bots"]: b.pop("dir", None)
    json.dump(info, open(os.path.join(run, "run.json"), "w"), indent=1)
    print(f"run_bots: run folder {run}")
    rc = subprocess.call([sys.executable, os.path.join(HERE, "audit.py"), run] + shlex.split(a.audit))
finally:
    kill_all()
sys.exit(rc)
PY
