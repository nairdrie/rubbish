#!/usr/bin/env python3
"""Tools/bots/audit.py: check a bot run (Docs/Multiplayer.md §8.7.6) and guard Nick's real save folder.

  audit.py <run-dir> [options]      check a run made by run_bots.sh; exit 0 = pass, 1 = fail, 2 = usage
      --allow REGEX                 ignore log errors whose message matches (repeatable; also --allow-file FILE, one per line)
      --min KIND=N                  at least N NetAudit lines of KIND across all bots (e.g. --min hash=50)
      --expect BOT:REGEX            BOT's player.log must contain REGEX (e.g. --expect "bot-3:Update your game")
      --max-errors N                tolerate up to N log errors in total (default 0)
      --allow-real-change           don't fail when Nick's real save folder changed (an operator override; reported)
      --max KIND=N                  at most N NetAudit lines of KIND across all bots (e.g. --max disconnect=0)
      --max-mismatch N              tolerate up to N hash mismatches (default 0: any NetAudit hash line with match=false fails)
  audit.py snapshot [--out FILE]    sha256 + size + mtime of every file under the real data roots (JSON)
  audit.py roots                    print the real data roots, one per line

The real data roots are where the shipped game and the Editor keep Nick's saves on this Mac:
~/Library/Application Support/<companyName>/<productName> (from ProjectSettings) plus the pre-rename
~/Library/Application Support/DefaultCompany/Rubbish. Override with RUBBISH_REAL_ROOTS (colon-separated).
A run fails if any file there changed, appeared or vanished between the run's start snapshot and the audit.
"""
import hashlib, json, os, re, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, "..", ".."))
SKIP_TOP = {"Unity"}                 # Unity services caches under persistentDataPath, not save data
SKIP_NAMES = {".DS_Store"}

ERROR_STACK = re.compile(r"^UnityEngine\.Debug:(LogError|LogException|LogAssertion)\b")
EXCEPTION_LINE = re.compile(r"^([A-Za-z_][\w.]*Exception)(: |$)")
CRASH_LINE = re.compile(r"(Received signal SIG|Crash!!!|=================================================================\s*$|Native Crash Reporting)")


def project_setting(key, default):
    try:
        for line in open(os.path.join(PROJECT, "ProjectSettings", "ProjectSettings.asset"), encoding="utf-8"):
            m = re.match(rf"^\s*{key}:\s*(.*)$", line)
            if m and m.group(1).strip():
                return m.group(1).strip()
    except OSError:
        pass
    return default


def real_roots():
    env = os.environ.get("RUBBISH_REAL_ROOTS")
    if env:
        return [os.path.abspath(os.path.expanduser(p)) for p in env.split(":") if p]
    base = os.path.expanduser("~/Library/Application Support")
    company = project_setting("companyName", "DefaultCompany")
    product = project_setting("productName", "Rubbish")
    roots = [os.path.join(base, company, product), os.path.join(base, "DefaultCompany", "Rubbish")]
    return list(dict.fromkeys(roots))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def snapshot(roots=None):
    snap = {"taken": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "roots": {}}
    for root in roots or real_roots():
        files = {}
        if os.path.isdir(root):
            for dp, dns, fns in os.walk(root):
                rel_dir = os.path.relpath(dp, root)
                if rel_dir == ".":
                    dns[:] = [d for d in dns if d not in SKIP_TOP]
                for fn in fns:
                    if fn in SKIP_NAMES:
                        continue
                    full = os.path.join(dp, fn)
                    try:
                        st = os.stat(full)
                        files[os.path.relpath(full, root)] = {"sha256": sha256(full), "size": st.st_size, "mtime": st.st_mtime}
                    except OSError as e:
                        files[os.path.relpath(full, root)] = {"error": str(e)}
        snap["roots"][root] = {"exists": os.path.isdir(root), "files": files}
    return snap


def diff_snapshots(before, after):
    changes = []
    for root, b in before.get("roots", {}).items():
        a = after.get("roots", {}).get(root, {"exists": False, "files": {}})
        bf, af = b.get("files", {}), a.get("files", {})
        for rel in sorted(set(bf) | set(af)):
            if rel not in af:
                changes.append(f"removed  {os.path.join(root, rel)}")
            elif rel not in bf:
                changes.append(f"added    {os.path.join(root, rel)}")
            elif bf[rel].get("sha256") != af[rel].get("sha256") or bf[rel].get("mtime") != af[rel].get("mtime"):
                changes.append(f"changed  {os.path.join(root, rel)}")
    return changes


def scan_log(path, allows):
    """-> (errors[list of str], text) for a Unity player log."""
    if not os.path.exists(path):
        return None, ""
    text = open(path, encoding="utf-8", errors="replace").read()
    lines = text.splitlines()
    errors = []
    for i, line in enumerate(lines):
        s = line.strip()
        msg = None
        if ERROR_STACK.match(s):
            j = i - 1                      # the message is the last non-empty line above the stack
            while j >= 0 and not lines[j].strip():
                j -= 1
            # skip back over a multi-line message: stop at the previous blank line
            k = j
            while k - 1 >= 0 and lines[k - 1].strip() and not ERROR_STACK.match(lines[k - 1].strip()):
                k -= 1
            msg = " | ".join(x.strip() for x in lines[k:j + 1])[:300] if j >= 0 else s
        elif EXCEPTION_LINE.match(s):
            msg = s[:300]
        elif CRASH_LINE.search(s):
            msg = "CRASH: " + s[:280]
        if msg is not None and not any(a.search(msg) for a in allows):
            if not errors or errors[-1] != msg:
                errors.append(msg)
    return errors, text


def read_jsonl(path):
    out = []
    if not os.path.exists(path):
        return None
    for n, line in enumerate(open(path, encoding="utf-8", errors="replace"), 1):
        line = line.strip()
        if not line:
            continue
        try:
            out.append(json.loads(line))
        except ValueError:
            out.append({"kind": "error", "msg": f"unparseable audit line {n}: {line[:120]}"})
    return out


def audit(run_dir, opts):
    fails, warns, info = [], [], []
    rj = os.path.join(run_dir, "run.json")
    if not os.path.exists(rj):
        return {"ok": False, "fails": [f"{rj} missing (not a run_bots.sh run?)"], "warns": [], "info": []}
    run = json.load(open(rj))
    allows = [re.compile(a) for a in opts["allow"]]

    # 1. Nick's real save folder
    before_path = os.path.join(run_dir, "real.before.json")
    if os.path.exists(before_path):
        before = json.load(open(before_path))
        now = snapshot(list(before.get("roots", {}).keys()))
        changes = diff_snapshots(before, now)
        after_path = os.path.join(run_dir, "real.after.json")
        if os.path.exists(after_path):
            changes = sorted(set(changes) | set(diff_snapshots(before, json.load(open(after_path)))))
        if changes:
            hint = []
            ep = run.get("editorPlaying", {})
            if ep.get("start") or ep.get("end"):
                hint.append("the live Editor was in play mode during the run, so these may be Nick's own saves")
            if run.get("sandboxed"):
                hint.append("the bots were sandboxed from these folders and couldn't have written them")
            msg = "Nick's real save folder changed during the run: " + "; ".join(changes[:10]) + \
                  (f" (+{len(changes) - 10} more)" if len(changes) > 10 else "") + (f" [{'; '.join(hint)}]" if hint else "")
            (warns if opts["allow_real_change"] else fails).append(msg + (" [allowed by --allow-real-change]" if opts["allow_real_change"] else ""))
        else:
            info.append(f"real save folder unchanged ({sum(len(r['files']) for r in before['roots'].values())} files)")
    else:
        fails.append("no real.before.json: the run didn't snapshot Nick's real save folder")

    # 2. per bot
    total_errors, kinds, hashes, mismatches = 0, {}, {}, []
    for bot in run.get("bots", []):
        name = bot["name"]
        for proc in bot.get("processes", [bot]):
            log = proc.get("log")
            errors, text = scan_log(log, allows) if log else (None, "")
            label = f"{name}{'#' + str(proc['launch']) if proc.get('launch', 1) > 1 else ''}"
            if errors is None:
                fails.append(f"{label}: no player log at {log}")
            else:
                total_errors += len(errors)
                for e in errors[:5]:
                    fails.append(f"{label}: log error: {e}") if opts["max_errors"] == 0 else warns.append(f"{label}: log error: {e}")
                if len(errors) > 5:
                    (fails if opts["max_errors"] == 0 else warns).append(f"{label}: ... {len(errors) - 5} more log error(s)")
            code, killed = proc.get("exitCode"), proc.get("killedByScript")
            if proc.get("timedOut"):
                fails.append(f"{label}: still running at the deadline; killed")
            elif not killed and code != 0:
                fails.append(f"{label}: exited with {code}")
            if run.get("mode") == "smoke":
                if CORE_SMOKE_OK not in text:
                    fails.append(f"{label}: no '{CORE_SMOKE_OK}' line in the log")
                if (proc.get("durationSec") or 0) > 60:
                    fails.append(f"{label}: smoke took {proc.get('durationSec'):.0f} s (limit 60 s)")
                else:
                    info.append(f"{label}: smoke exit {code} in {proc.get('durationSec', 0):.1f} s")
        al = read_jsonl(bot.get("auditLog") or "")
        if al is not None:
            for e in al:
                k = str(e.get("kind", "?"))
                kinds[k] = kinds.get(k, 0) + 1
                if k == "hash":
                    d = str(e.get("domain", "?"))
                    row = hashes.setdefault(d, {"compares": 0, "matched": 0, "mismatched": 0})
                    row["compares"] += 1
                    if e.get("match") is False:
                        row["mismatched"] += 1
                        if len(mismatches) < 10:
                            mismatches.append(f"{name}: {d} probe {e.get('seq')} v{e.get('v')}: host {e.get('host')} ({e.get('hostN')}) "
                                              f"vs mine {e.get('mine')} ({e.get('n')})")
                    else:
                        row["matched"] += 1
                if k in ("error", "exception"):
                    m = str(e.get("msg") or e.get("message") or e)[:300]
                    if not any(a.search(m) for a in allows):
                        fails.append(f"{name}: NetAudit {k}: {m}")
        elif run.get("mode") != "smoke" and opts["min"]:
            warns.append(f"{name}: no NetAudit log at {bot.get('auditLog')}")
        for rel, digest in (bot.get("seed") or {}).items():
            if not bot.get("expectSeedUnchanged"):
                continue
            p = os.path.join(bot["dataRoot"], rel)
            if not os.path.exists(p):
                fails.append(f"{name}: seeded {rel} is gone")
            elif sha256(p) != digest:
                fails.append(f"{name}: seeded {rel} changed (guests must never write saves)")
        for pat in opts["expect"].get(name, []):
            texts = "".join(open(p["log"], encoding="utf-8", errors="replace").read() for p in bot.get("processes", [bot])
                            if p.get("log") and os.path.exists(p["log"]))
            if not re.search(pat, texts):
                fails.append(f"{name}: expected log text /{pat}/ not found")
    if total_errors > opts["max_errors"] > 0:
        fails.append(f"{total_errors} log errors in total (limit {opts['max_errors']})")
    for kind, n in opts["min"].items():
        if kinds.get(kind, 0) < n:
            fails.append(f"NetAudit '{kind}': {kinds.get(kind, 0)} line(s), expected at least {n}")
    for kind, n in opts["max"].items():
        if kinds.get(kind, 0) > n:
            fails.append(f"NetAudit '{kind}': {kinds.get(kind, 0)} line(s), at most {n} allowed")
    total_mismatch = sum(r["mismatched"] for r in hashes.values())
    if hashes:
        info.append("hash compares: " + ", ".join(f"{d} {r['matched']}/{r['compares']} matched" for d, r in sorted(hashes.items())))
    if total_mismatch > opts["max_mismatch"]:
        fails.append(f"{total_mismatch} hash mismatch(es) (limit {opts['max_mismatch']}): " + "; ".join(mismatches))
    elif total_mismatch:
        warns.append(f"{total_mismatch} hash mismatch(es), tolerated by --max-mismatch: " + "; ".join(mismatches))
    if kinds:
        info.append("NetAudit kinds: " + ", ".join(f"{k}={v}" for k, v in sorted(kinds.items())))
    unknown = set(opts["expect"]) - {b["name"] for b in run.get("bots", [])}
    for u in sorted(unknown):
        fails.append(f"--expect names an unknown bot {u!r}")
    return {"ok": not fails, "fails": fails, "warns": warns, "info": info, "netAuditKinds": kinds, "logErrors": total_errors,
            "hash": hashes}


CORE_SMOKE_OK = "BOTSMOKE OK"


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0 if argv else 2
    if argv[0] == "roots":
        print("\n".join(real_roots()))
        return 0
    if argv[0] == "snapshot":
        snap = snapshot()
        if "--out" in argv:
            out = argv[argv.index("--out") + 1]
            json.dump(snap, open(out, "w"), indent=1)
        else:
            print(json.dumps(snap, indent=1))
        return 0
    run_dir, rest = argv[0], argv[1:]
    opts = {"allow": [], "min": {}, "max": {}, "expect": {}, "max_errors": 0, "max_mismatch": 0, "allow_real_change": False}
    i = 0
    try:
        while i < len(rest):
            a = rest[i]
            if a == "--allow":
                opts["allow"].append(rest[i + 1]); i += 1
            elif a == "--allow-file":
                opts["allow"] += [l.strip() for l in open(rest[i + 1]) if l.strip() and not l.startswith("#")]; i += 1
            elif a == "--min":
                k, v = rest[i + 1].split("=", 1); opts["min"][k] = int(v); i += 1
            elif a == "--max":
                k, v = rest[i + 1].split("=", 1); opts["max"][k] = int(v); i += 1
            elif a == "--max-mismatch":
                opts["max_mismatch"] = int(rest[i + 1]); i += 1
            elif a == "--expect":
                b, pat = rest[i + 1].split(":", 1); opts["expect"].setdefault(b, []).append(pat); i += 1
            elif a == "--max-errors":
                opts["max_errors"] = int(rest[i + 1]); i += 1
            elif a == "--allow-real-change":
                opts["allow_real_change"] = True
            else:
                print(f"audit: unknown argument {a!r}", file=sys.stderr)
                return 2
            i += 1
    except (IndexError, ValueError) as e:
        print(f"audit: bad arguments ({e})", file=sys.stderr)
        return 2
    if not os.path.isdir(run_dir):
        print(f"audit: {run_dir} is not a folder", file=sys.stderr)
        return 2
    res = audit(run_dir, opts)
    json.dump(res, open(os.path.join(run_dir, "audit.json"), "w"), indent=1)
    for line in res["info"]:
        print("  ok    " + line)
    for line in res["warns"]:
        print("  warn  " + line)
    for line in res["fails"]:
        print("  FAIL  " + line)
    print(f"audit: {'PASS' if res['ok'] else 'FAIL'} ({run_dir})")
    return 0 if res["ok"] else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
