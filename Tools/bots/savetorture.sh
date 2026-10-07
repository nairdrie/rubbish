#!/bin/bash
# Tools/bots/savetorture.sh: kill-during-write drills on the save layer (Docs/Multiplayer.md §8.7.4 B4 gate, §8.7.7 M0-10).
#
#   Tools/bots/savetorture.sh [-c CYCLES] [--min-ms 20] [--max-ms 400] [--app PATH] [--out DIR] [--no-sandbox] [--keep-logs]
#
# Each cycle launches `RubbishBots -batchmode -nographics -nosteam -savetorture -dataroot <out>/data`, waits for its
# "TORTURE WRITING" log line, sleeps a random 20-400 ms and SIGKILLs it mid-write. The next launch first verifies every record
# the killed one was writing ("TORTURE VERIFY ok= bad= missing="): a record that exists but has no copy passing its checksum
# (latest or a fallback generation) is a failure, and so is a record that disappears once all four exist. A final
# `-savetorture verify` launch checks the last kill. Default 1000 cycles (about 30 minutes); exit 0 = every record always loaded.
# Records go through B4's LocalFileStore when the build has it, else straight through L0's SafeFile (the log says which).
# Bots run sandboxed from Nick's real save folders, like run_bots.sh. Logs of passing cycles are deleted unless --keep-logs.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
exec python3 - "$HERE" "$@" <<'PY'
import argparse, os, random, re, shutil, signal, subprocess, sys, time

HERE = sys.argv[1]
sys.dont_write_bytecode = True
sys.path.insert(0, HERE)
import audit  # noqa: E402

COPY = os.path.abspath(os.path.expanduser(os.environ.get("RUBBISH_BUILD_COPY", "~/Dev/Rubbish-build")))
ap = argparse.ArgumentParser(prog="savetorture.sh")
ap.add_argument("-c", type=int, default=1000, help="kill cycles (default 1000)")
ap.add_argument("--min-ms", type=int, default=20)
ap.add_argument("--max-ms", type=int, default=400)
ap.add_argument("--app", default=os.path.join(COPY, "Builds", "DevHeadless", "RubbishBots.app"))
ap.add_argument("--out", default=None)
ap.add_argument("--no-sandbox", action="store_true")
ap.add_argument("--keep-logs", action="store_true")
try:
    a = ap.parse_args(sys.argv[2:])
except SystemExit as e:
    sys.exit(0 if e.code == 0 else 2)

exe = a.app
if exe.endswith(".app") or os.path.isdir(exe):
    exe = os.path.join(exe, "Contents", "MacOS", "RubbishBots")
if not os.access(exe, os.X_OK):
    print(f"savetorture: no RubbishBots executable at {exe} (Tools/bots/sync_build.sh --build)", file=sys.stderr); sys.exit(2)
out = os.path.abspath(a.out or os.path.join(COPY, "Builds", "BotRuns", time.strftime("%Y%m%d-%H%M%S") + "-torture"))
data = os.path.join(out, "data")
os.makedirs(data, exist_ok=True)
roots = audit.real_roots()
for r in roots:
    if out.startswith(os.path.abspath(r) + os.sep):
        print(f"savetorture: {out} is inside the real data root {r}", file=sys.stderr); sys.exit(2)
sandbox = None
if not a.no_sandbox and shutil.which("sandbox-exec"):
    esc = lambda s: s.replace("\\", "\\\\").replace('"', '\\"')
    paths = sorted({os.path.realpath(r) for r in roots} | set(roots))
    sandbox = "(version 1)(allow default)(deny file-write* " + " ".join(f'(subpath "{esc(p)}")' for p in paths) + ")"
before = audit.snapshot(roots)

VERIFY = re.compile(r"TORTURE VERIFY ok=(\d+) bad=(\d+) missing=(\d+) via=(\S+)")

def launch(log, mode=None):
    args = [exe, "-batchmode", "-nographics", "-nosteam", "-playerid", "dev:torture", "-dataroot", data,
            "-savetorture"] + ([mode] if mode else []) + ["-logFile", log]
    cmd = (["sandbox-exec", "-p", sandbox] + args) if sandbox else args
    return subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, cwd=out, start_new_session=True)

def read(log):
    try:
        return open(log, encoding="utf-8", errors="replace").read()
    except OSError:
        return ""

def check_verify(text, label, state):
    m = VERIFY.search(text)
    if not m:
        return f"{label}: no TORTURE VERIFY line"
    ok, bad, missing, via = int(m.group(1)), int(m.group(2)), int(m.group(3)), m.group(4)
    state["via"] = via
    if bad:
        return f"{label}: {bad} record(s) had no loadable copy: {text[m.start():m.start() + 400].splitlines()[0]}"
    if state["all_present"] and missing:
        return f"{label}: {missing} record(s) vanished after all four existed"
    if ok == 4:
        state["all_present"] = True
    return None

fails, state, t0 = [], {"all_present": False, "via": "?"}, time.time()
rng = random.Random()
signal.signal(signal.SIGTERM, lambda *_: sys.exit(130))
for i in range(1, a.c + 1):
    log = os.path.join(out, f"cycle-{i:04d}.log")
    p = launch(log)
    deadline, writing = time.time() + 45, False
    while time.time() < deadline and p.poll() is None:
        if "TORTURE WRITING" in read(log):
            writing = True
            break
        time.sleep(0.02)
    if writing:
        time.sleep(rng.uniform(a.min_ms, a.max_ms) / 1000.0)
    try:
        os.killpg(p.pid, signal.SIGKILL)
    except OSError:
        pass
    p.wait()
    text = read(log)
    err = check_verify(text, f"cycle {i}", state)
    if not writing and not err:
        err = f"cycle {i}: never reached TORTURE WRITING (exit {p.returncode})"
    if err:
        fails.append(err)
        print("savetorture: FAIL " + err)
    elif not a.keep_logs:
        os.remove(log)
    if i % 50 == 0 or i == a.c:
        print(f"savetorture: {i}/{a.c} cycles, {len(fails)} failure(s), {time.time() - t0:.0f} s, via {state['via']}")

final = os.path.join(out, "final-verify.log")
p = launch(final, "verify")
try:
    p.wait(timeout=60)
except subprocess.TimeoutExpired:
    os.killpg(p.pid, signal.SIGKILL); p.wait()
    fails.append("final verify: timed out")
err = check_verify(read(final), "final verify", state)
if err: fails.append(err)
if p.returncode not in (0, None) and not err:
    fails.append(f"final verify: exit {p.returncode}")
changes = audit.diff_snapshots(before, audit.snapshot(roots))
if changes:
    fails.append("Nick's real save folder changed: " + "; ".join(changes[:5]))
print(f"savetorture: {'PASS' if not fails else 'FAIL'}: {a.c} kill cycles via {state['via']}, {len(fails)} failure(s) ({out})")
for f in fails[:20]:
    print("  FAIL  " + f)
sys.exit(0 if not fails else 1)
PY
