#!/bin/bash
# Tools/bots/store_torture.sh: the strict kill-during-write test of the account store and its leases (Docs/Multiplayer.md §8.7.4
# B4 gate, M0 gate 10). B5's savetorture.sh drills records + checksums through BotDriver; this one also checks acknowledged
# writes, version/data binding and lock liveness after every kill (StoreTorture, Rubbish.Net.Core).
#
#   Tools/bots/store_torture.sh [-n 1000] [--app PATH | --cmd 'mono harness.exe'] [--out DIR]
#                              [--min-ms 0] [--max-ms 400] [--seconds 30] [--no-sandbox] [--keep-going]
#
# Each cycle launches `RubbishBots -batchmode -nographics -nosteam -dataroot <out>/data -storetorture run`, waits until it is
# writing (it creates <out>/data/store-torture/running-<pid> after verifying what the previous cycle left), sleeps a random
# min..max ms and SIGKILLs it. On launch, every cycle first verifies the last one: each acknowledged record still verifies
# (main, a finished temp file or a generation), no acknowledged write was lost, payload and version landed together, no main
# file was ever damaged, and the killed process's company lock is free at once with a higher epoch. A failed check makes the
# process exit 1 before writing; the script stops there (unless --keep-going) and keeps that cycle's log.
# A final `-storetorture verify` must exit 0. Results: <out>/summary.json and <out>/data/store-torture/report.jsonl.
# Every process runs under sandbox-exec with writes to Nick's real save folders denied, and those folders are snapshotted
# before and after (Tools/bots/audit.py): any change there fails the run.
# --cmd runs another command instead of the app (e.g. the plain-Mono harness of the same sources); its args are appended.
# Exit: 0 pass, 1 fail, 2 usage.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
exec python3 - "$HERE" "$@" <<'PY'
import argparse, json, os, random, shlex, shutil, signal, subprocess, sys, time

HERE = sys.argv[1]
sys.dont_write_bytecode = True
sys.path.insert(0, HERE)
import audit  # noqa: E402  (real roots + snapshots)

COPY = os.path.abspath(os.path.expanduser(os.environ.get("RUBBISH_BUILD_COPY", "~/Dev/Rubbish-build")))
ap = argparse.ArgumentParser(prog="store_torture.sh")
ap.add_argument("-n", type=int, default=1000, help="kill cycles (default 1000)")
ap.add_argument("--app", default=os.path.join(COPY, "Builds", "DevHeadless", "RubbishBots.app"))
ap.add_argument("--cmd", default=None, help="command prefix to run instead of the app")
ap.add_argument("--out", default=None)
ap.add_argument("--min-ms", type=int, default=0)
ap.add_argument("--max-ms", type=int, default=400)
ap.add_argument("--seconds", type=int, default=30, help="a launch quits by itself after this long (default 30)")
ap.add_argument("--start-timeout", type=float, default=90.0)
ap.add_argument("--no-sandbox", action="store_true")
ap.add_argument("--keep-going", action="store_true")
ap.add_argument("--seed", type=int, default=None)
try:
    a = ap.parse_args(sys.argv[2:])
except SystemExit as e:
    sys.exit(0 if e.code == 0 else 2)

def die(msg, code=2):
    print(f"store_torture: {msg}", file=sys.stderr); sys.exit(code)

if a.n < 1 or a.min_ms < 0 or a.max_ms < a.min_ms: die("bad -n / --min-ms / --max-ms")
if a.cmd:
    base = shlex.split(a.cmd)
    unity = False
else:
    exe = a.app
    if exe.endswith(".app") or os.path.isdir(exe):
        exe = os.path.join(exe, "Contents", "MacOS", "RubbishBots")
    if not os.access(exe, os.X_OK):
        die(f"no RubbishBots executable at {exe} (build it: Tools/bots/sync_build.sh --build)")
    base = [exe, "-batchmode", "-nographics", "-nosteam"]
    unity = True

stamp = time.strftime("%Y%m%d-%H%M%S")
out = os.path.abspath(a.out or os.path.join(COPY, "Builds", "StoreTorture", stamp))
data = os.path.join(out, "data")
tort = os.path.join(data, "store-torture")
os.makedirs(tort, exist_ok=True)
roots = audit.real_roots()
for r in roots:
    rr = os.path.realpath(r)
    if os.path.realpath(data).startswith(rr + os.sep) or os.path.realpath(data) == rr:
        die(f"--out is inside a real save folder ({r})")

sandbox = None
if not a.no_sandbox and shutil.which("sandbox-exec"):
    paths = sorted({os.path.realpath(r) for r in roots} | set(roots))
    esc = lambda s: s.replace("\\", "\\\\").replace('"', '\\"')
    sandbox = "(version 1)(allow default)(deny file-write* " + " ".join(f'(subpath "{esc(p)}")' for p in paths) + ")"

rng = random.Random(a.seed)
before = audit.snapshot(roots)
log = os.path.join(out, "player.log")

def launch(mode):
    args = base + ["-dataroot", data, "-storetorture", mode, "-storetorture-seconds", str(a.seconds)]
    if unity: args += ["-logFile", log]
    cmd = (["sandbox-exec", "-p", sandbox] + args) if sandbox else args
    so = open(os.path.join(out, "stdout.txt"), "w")
    return subprocess.Popen(cmd, stdout=so, stderr=subprocess.STDOUT, start_new_session=True), so

def last_report():
    try:
        with open(os.path.join(tort, "report.jsonl")) as f:
            lines = [l for l in f if l.strip()]
        return json.loads(lines[-1]) if lines else None
    except Exception:
        return None

def keep_log(tag):
    for src in (log, os.path.join(out, "stdout.txt")):
        if os.path.exists(src):
            shutil.copy(src, os.path.join(out, f"{tag}-{os.path.basename(src)}"))

t0 = time.time()
fails, killed, self_exit, startup = [], 0, 0, []
print(f"store_torture: {a.n} cycles -> {out} ({'sandboxed' if sandbox else 'NOT sandboxed'}; {'app' if unity else 'cmd'})", flush=True)
for n in range(1, a.n + 1):
    p, so = launch("run")
    marker = os.path.join(tort, f"running-{p.pid}")
    t = time.time()
    while not os.path.exists(marker) and p.poll() is None and time.time() - t < a.start_timeout:
        time.sleep(0.005)
    if os.path.exists(marker):
        startup.append(time.time() - t)
        time.sleep(rng.uniform(a.min_ms, a.max_ms) / 1000.0)
        if p.poll() is None:
            os.kill(p.pid, signal.SIGKILL); killed += 1
        else:
            self_exit += 1
        p.wait()   # reap now: a zombie would still look alive to the lock check
    else:
        if p.poll() is None:
            os.kill(p.pid, signal.SIGKILL); p.wait()
            fails.append({"cycle": n, "why": f"no running marker within {a.start_timeout}s"})
        else:
            rep = last_report()
            fails.append({"cycle": n, "why": "exited before writing", "exit": p.returncode, "report": rep})
        keep_log(f"fail-{n}")
    so.close()
    if fails and not a.keep_going: break
    if n % 50 == 0 or n == a.n:
        print(f"  cycle {n}/{a.n}: killed {killed}, {len(fails)} failed, {time.time() - t0:.0f}s", flush=True)

# Final verify of the last kill.
p, so = launch("verify")
try:
    code = p.wait(timeout=max(60, a.start_timeout))
except subprocess.TimeoutExpired:
    os.kill(p.pid, signal.SIGKILL); p.wait(); code = -9
so.close()
final = last_report()
if code != 0 or not final or not final.get("ok"):
    fails.append({"cycle": "final-verify", "exit": code, "report": final}); keep_log("final")

reports = []
try:
    with open(os.path.join(tort, "report.jsonl")) as f:
        reports = [json.loads(l) for l in f if l.strip()]
except Exception as e:
    fails.append({"cycle": "summary", "why": f"report.jsonl unreadable: {e}"})
after = audit.snapshot(roots)
real_changes = audit.diff_snapshots(before, after)
real_note = None
if real_changes and sandbox:
    # The kernel denied every write there to our processes, so this is someone else (Nick playing in the Editor).
    real_note = "real save folder changed during the run, but every torture process was sandboxed away from it: not us"
    print(f"store_torture: note: {real_note}: {real_changes[:5]}")
elif real_changes:
    fails.append({"cycle": "real-roots", "why": "Nick's real save folder changed during an unsandboxed run", "changes": real_changes[:20]})

checked = reports[1:]  # the first launch had nothing to verify
summary = {
    "out": out, "cycles": a.n, "killed": killed, "exitedBeforeKill": self_exit, "seconds": round(time.time() - t0, 1),
    "launches": len(reports), "verifiesOk": sum(1 for r in reports if r.get("ok")),
    "verifiesFailed": sum(1 for r in reports if not r.get("ok")),
    "killsMidWrite": sum(r.get("tmpLeft", 0) for r in checked),
    "recoveredFromTemp": sum(r.get("fromTmp", 0) for r in checked),
    "recoveredFromBackup": sum(r.get("fromBackup", 0) for r in checked),
    "corruptMainFiles": sum(r.get("corrupt", 0) for r in checked),
    "leaseFreeAtOnce": sum(1 for r in checked if r.get("leaseFreeAtOnce")),
    "maxEpoch": max([r.get("epoch", 0) for r in reports] or [0]),
    "acks": (reports[-1].get("acks") if reports else 0),
    "startupSec": {"min": round(min(startup), 3), "max": round(max(startup), 3), "mean": round(sum(startup) / len(startup), 3)} if startup else None,
    "sandboxed": sandbox is not None, "realRootsUnchanged": not real_changes, "realRootsNote": real_note, "fails": fails[:20],
}
summary["pass"] = not fails and summary["verifiesFailed"] == 0 and summary["leaseFreeAtOnce"] == len(checked)
with open(os.path.join(out, "summary.json"), "w") as f:
    json.dump(summary, f, indent=1)
print(json.dumps({k: v for k, v in summary.items() if k != "fails"}, indent=1))
if fails:
    print("store_torture: FAIL", json.dumps(fails[:5], indent=1))
print(f"store_torture: {'PASS' if summary['pass'] else 'FAIL'} ({summary['launches']} launches verified, {killed} kills, "
      f"{summary['killsMidWrite']} landed mid-write, {summary['recoveredFromTemp']} recovered from a finished temp file)")
sys.exit(0 if summary["pass"] else 1)
PY
