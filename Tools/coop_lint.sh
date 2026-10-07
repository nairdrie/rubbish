#!/bin/bash
# coop_lint.sh — enforce the "rules for new code" in Docs/Multiplayer.md §9 against a baseline of today's hits.
#
#   Tools/coop_lint.sh                     compare against Tools/coop_lint.baseline.json; list new hits; exit 1 if any
#   Tools/coop_lint.sh --files <glob>...   only files matching a glob (repo-relative; ** ok; a folder = everything in it)
#   Tools/coop_lint.sh --json              the same result as JSON on stdout (same exit code)
#   Tools/coop_lint.sh --allows            also list every `// coop:allow(<rule>) <reason>` in scope
#   Tools/coop_lint.sh --baseline          record the current counts as the new baseline (LEAD ONLY, at a wave merge, §8.7.8)
#   Tools/coop_lint.sh --seed-rule <rule>  add today's counts for a rule the baseline has never seen (new rules only)
#   Tools/coop_lint.sh --selftest          run Tools/coop_lint_fixtures (every rule fires on its fixture; exit 0 = pass)
#   (testing) --root <dir> scans <dir>/Assets/Scripts;  --baseline-file <path>;  --no-baseline (every hit is new)
#
# Counts matches per (file, rule) in Assets/Scripts (excluding Editor/ and the chess engine). A file whose count for a
# rule went up is a violation: fix it, or mark the line `// coop:allow(<rule>) <reason>` (same line, or a comment line
# directly above) and list it in your report. Allowed hits don't count. An allow with no reason or an unknown rule is
# an error. `cash`, `guid`, `timescale` and `scene` allows always need the lead's explicit approval.
# Exit codes: 0 OK, 1 new hits / bad allows / selftest failure, 2 usage error.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - "$@" <<'PY'
import fnmatch, json, os, re, subprocess, sys, tempfile

ARGV = sys.argv[1:]
BASELINE_DEFAULT = "Tools/coop_lint.baseline.json"
FIXTURES = "Tools/coop_lint_fixtures"
SCRIPTS = "Assets/Scripts"
EXCLUDE = ("/Editor/", "/Core/Board/", "/Core/Search/", "/Core/Move Generation/", "/Core/Evaluation/",
           "/Core/Helpers/", "/Core/Opening Book/", "/Core/Game Result/")

# rule -> (regex, message[, scope]) ; scope(path) -> bool says whether the rule applies to that file at all.
# Paths are repo-relative with forward slashes, e.g. "Assets/Scripts/Net/Core/Save/SafeFile.cs".
SAVE_OK = re.compile(r"^Assets/Scripts/(Net/(Core/)?)?Save/")
SAVE_OK_UNTIL_M1 = re.compile(r"^Assets/Scripts/(Core/GameSession[^/]*\.cs|Ebae/EbaeMarket\.cs)$")   # legacy writers; drop at M1
def save_scope(p):        return not (SAVE_OK.search(p) or SAVE_OK_UNTIL_M1.search(p))
def netsingleton_scope(p): return p.startswith("Assets/Scripts/Net/") and not p.endswith("/NetBoot.cs")
def steamapi_scope(p):     return not p.startswith("Assets/Scripts/Net/Steam/")

RULES = {
    "cash":      (r"GameSession\.AddCash\(", "money goes through Ledger.Credit/Charge/Spend/Rep, never GameSession.AddCash (rule 1, D1)"),
    "random":    (r"\bRandom\.(Range|value|insideUnitSphere|insideUnitCircle|onUnitSphere|rotation|ColorHSV|InitState)\b|new System\.Random\(",
                  "gameplay rolls go through the host RNG service; cosmetic-only randomness is OK if justified (rule 2)"),
    "guid":      (r"Guid\.NewGuid|NewUid\(", "ids from the host id/RNG service (rule 2)"),
    "find":      (r"\bFind(First|Any)ObjectByType\b|\bFindObjectOfType\b", "pass references / company ids (rule 3)"),
    "current":   (r"\.Current\b(?!\s*;)", "no static Current singletons for truck/depot/station/route/laptop (rule 3)"),
    "timescale": (r"Time\.timeScale\s*=", "no global slow-mo for juice (rule 4)"),
    "scene":     (r"GetActiveScene\(|\.scene\.name|GetSceneByName|\"(ShopScene|Neighborhood|MainMenuScene|Bootstrap|GameScene)\"",
                  "use zones / ZoneId, not scene names (rule 6)"),
    "local":     (r"PlayerController\.Local\b", "Local is for camera/HUD/input only (rule 8)"),
    # --- added by T1 (§8.7.8); counts seeded at the day they landed ---
    "save":      (r"File\.(Write|Append|Replace|Delete|Move|Copy)|PlayerPrefs\.Set|persistentDataPath",
                  "saved data goes through TownStore/CompanyStore/ProfileStore (Save/**), never File.*/PlayerPrefs/persistentDataPath (rule 9)",
                  save_scope),
    "netsingleton": (r"NetworkManager\.Singleton", "inside Net/** only NetBoot.cs touches NetworkManager.Singleton; pass the manager in (rule 3)",
                  netsingleton_scope),
    "steamapi":  (r"using\s+Steamworks|Steamworks\.", "Steamworks lives only under Net/Steam/** behind #if !DISABLESTEAMWORKS (D10)",
                  steamapi_scope),
    "secrets":   (r"Debug\.Log\w*\s*\(.*(?i:ticket|token|apikey|secret|password)",
                  "never log Steam tickets, UGS tokens, keys or secrets; players paste Player.log on Discord (rule 17)"),
}
NEEDS_APPROVAL = {"cash", "guid", "timescale", "scene"}
compiled = {k: re.compile(v[0]) for k, v in RULES.items()}
scopes = {k: (v[2] if len(v) > 2 else None) for k, v in RULES.items()}
ALLOW_RX = re.compile(r"//\s*coop:allow\(([A-Za-z_,\s]*)\)\s*(.*)$")   # rule names only, so docs quoting `coop:allow(<rule>)` aren't allows

def die(msg):
    print(f"coop_lint: {msg}", file=sys.stderr); sys.exit(2)

# ---------------------------------------------------------------- args
opts = {"files": None, "json": False, "allows": False, "baseline": False, "seed": [], "selftest": False,
        "root": ".", "baseline_file": BASELINE_DEFAULT, "no_baseline": False}
i = 0
while i < len(ARGV):
    a = ARGV[i]
    if a == "--files":
        opts["files"] = opts["files"] or []
        i += 1
        while i < len(ARGV) and not ARGV[i].startswith("--"):
            opts["files"].append(ARGV[i]); i += 1
        if not opts["files"]: die("--files needs at least one glob")
        continue
    elif a == "--json": opts["json"] = True
    elif a == "--allows": opts["allows"] = True
    elif a == "--baseline": opts["baseline"] = True
    elif a == "--seed-rule":
        i += 1
        if i >= len(ARGV): die("--seed-rule needs a rule name")
        opts["seed"] += [r for r in ARGV[i].split(",") if r]
    elif a == "--selftest": opts["selftest"] = True
    elif a == "--root":
        i += 1
        if i >= len(ARGV): die("--root needs a folder")
        opts["root"] = ARGV[i]
    elif a == "--baseline-file":
        i += 1
        if i >= len(ARGV): die("--baseline-file needs a path")
        opts["baseline_file"] = ARGV[i]
    elif a == "--no-baseline": opts["no_baseline"] = True
    elif a in ("-h", "--help"):
        print(open("Tools/coop_lint.sh").read().split("set -euo")[0].replace("# ", "").replace("#", "")); sys.exit(0)
    else: die(f"unknown argument {a!r} (see --help)")
    i += 1
if (opts["baseline"] or opts["seed"]) and opts["files"]: die("--baseline/--seed-rule record the whole tree; don't combine them with --files")

# ---------------------------------------------------------------- globs
def glob_rx(g):
    g = g.replace("\\", "/")
    proj = os.getcwd().replace("\\", "/") + "/"
    if g.startswith(proj): g = g[len(proj):]
    while g.startswith("./"): g = g[2:]
    if not any(c in g for c in "*?[") and (g.endswith("/") or os.path.isdir(os.path.join(opts["root"], g))):
        g = g.rstrip("/") + "/**"
    out, j = "", 0
    while j < len(g):
        if g.startswith("**/", j): out += "(?:.*/)?"; j += 3
        elif g.startswith("**", j): out += ".*"; j += 2
        elif g[j] == "*": out += "[^/]*"; j += 1
        elif g[j] == "?": out += "[^/]"; j += 1
        else: out += re.escape(g[j]); j += 1
    return re.compile("^" + out + "$")

file_rx = [glob_rx(g) for g in opts["files"]] if opts["files"] else None
def in_scope(rel): return file_rx is None or any(r.match(rel) for r in file_rx)

# ---------------------------------------------------------------- scan
def scan(root, want=None):
    """-> counts{path|rule:n}, hits{path|rule:[(line,text)]}, allows[...], bad_allows[...], scanned[paths]"""
    counts, hits, allows, bad, scanned = {}, {}, [], [], []
    base = os.path.join(root, SCRIPTS)
    for dp, dns, fns in os.walk(base):
        dns.sort()
        for fn in sorted(fns):
            if not fn.endswith(".cs"): continue
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, root).replace("\\", "/")
            if any(x in "/" + rel + "/" for x in EXCLUDE): continue
            if want is not None and not want(rel): continue
            try: lines = open(full, encoding="utf-8", errors="replace").read().splitlines()
            except OSError: continue
            scanned.append(rel)
            pending = []          # allows from comment-only lines, applied to the next code line
            for n, line in enumerate(lines, 1):
                code, _, comment = line.partition("//")
                m = ALLOW_RX.search(line)
                line_allow = None
                if m:
                    rules = [r.strip() for r in m.group(1).split(",") if r.strip()]
                    reason = m.group(2).strip()
                    entry = {"file": rel, "line": n, "rules": rules, "reason": reason, "used": []}
                    unknown = [r for r in rules if r not in RULES]
                    if not rules or unknown or len(reason) < 3:
                        why = ("unknown rule(s) " + ", ".join(unknown)) if unknown else ("no rule" if not rules else "no reason given")
                        bad.append(dict(entry, why=why))
                    else:
                        allows.append(entry)
                        line_allow = entry
                if not code.strip():
                    if line_allow: pending.append(line_allow)
                    continue
                active = ([line_allow] if line_allow else []) + pending
                pending = []
                for k, rx in compiled.items():
                    sc = scopes[k]
                    if sc is not None and not sc(rel): continue
                    if not rx.search(code): continue
                    hit_allow = next((a for a in active if k in a["rules"]), None)
                    if hit_allow is not None:
                        hit_allow["used"].append(k); continue
                    key = f"{rel}|{k}"
                    counts[key] = counts.get(key, 0) + 1
                    hits.setdefault(key, []).append((n, line.strip()[:140]))
    return counts, hits, allows, bad, scanned

def load_baseline(path):
    if opts["no_baseline"] or not os.path.exists(path): return {}
    return json.load(open(path))

# ---------------------------------------------------------------- selftest
def selftest():
    exp_path = os.path.join(FIXTURES, "expected.json")
    if not os.path.exists(exp_path): die(f"{exp_path} missing")
    exp = json.load(open(exp_path))
    fails = []
    counts, hits, allows, bad, scanned = scan(FIXTURES)
    want = {k: v for k, v in exp["counts"].items()}
    for k in sorted(set(want) | set(counts)):
        if want.get(k, 0) != counts.get(k, 0):
            fails.append(f"count {k}: expected {want.get(k, 0)}, got {counts.get(k, 0)}  {hits.get(k, '')}")
    fired = {k.split("|")[1] for k in counts}
    for r in RULES:
        if r not in fired: fails.append(f"rule {r!r} never fired on any fixture")
    used = sorted(f"{a['file']}:{a['line']}" for a in allows if a["used"])
    if used != sorted(exp["allowsUsed"]): fails.append(f"allows used: expected {sorted(exp['allowsUsed'])}, got {used}")
    badl = sorted(f"{b['file']}:{b['line']}" for b in bad)
    if badl != sorted(exp["badAllows"]): fails.append(f"bad allows: expected {sorted(exp['badAllows'])}, got {badl}")
    # --files filter, through the real command line (also checks exit codes and the JSON shape)
    me = ["bash", "Tools/coop_lint.sh", "--root", FIXTURES]
    def run(extra):
        p = subprocess.run(me + extra, capture_output=True, text=True)
        return p.returncode, p.stdout, p.stderr
    rc, out, err = run(["--no-baseline", "--json", "--files", "Assets/Scripts/Net/**"])
    try: j = json.loads(out)
    except Exception: j = None; fails.append(f"--json output isn't JSON: {out[:200]!r} {err[:200]!r}")
    if j is not None:
        files = {n["file"] for n in j["new"]}
        if not files or any(not f.startswith("Assets/Scripts/Net/") for f in files):
            fails.append(f"--files Net/** leaked or matched nothing: {sorted(files)}")
        if rc != 1: fails.append(f"--files Net/** with new hits should exit 1, got {rc}")
    rc, out, err = run(["--no-baseline", "--files", "Assets/Scripts/Save/SaveOk.cs"])
    if rc != 0 or "OK" not in out: fails.append(f"--files on a clean exempt file should exit 0 with OK, got {rc}: {out[:200]!r}")
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as tf:
        json.dump(want, tf); tmpb = tf.name
    try:
        rc, out, err = run(["--baseline-file", tmpb])
        # a baseline equal to today's counts is OK except for the bad allows, which must still fail the run
        if rc != 1 or "bad coop:allow" not in out: fails.append(f"bad allows must fail even at baseline, got {rc}: {out[-300:]!r}")
        rc, out, err = run(["--baseline-file", tmpb, "--files", "Assets/Scripts/Gameplay/LegacyRules.cs", "Assets/Scripts/Net/**"])
        if rc != 0 or not out.startswith("coop_lint: OK"): fails.append(f"at-baseline run should print OK and exit 0, got {rc}: {out!r}")
    finally:
        os.unlink(tmpb)
    rc, out, err = run(["--files"])
    if rc != 2: fails.append(f"usage error should exit 2, got {rc}")
    if fails:
        print("coop_lint selftest: FAIL"); [print("  - " + f) for f in fails]; sys.exit(1)
    print(f"coop_lint selftest: OK ({len(RULES)} rules fired on {len(scanned)} fixture files; "
          f"{len(used)} allows honoured, {len(badl)} bad allows caught; --files, --json and exit codes checked)")
    sys.exit(0)

if opts["selftest"]: selftest()

# ---------------------------------------------------------------- main
counts, hits, allows, bad, scanned = scan(opts["root"], in_scope)

if opts["seed"]:
    base = load_baseline(opts["baseline_file"])
    have = {k.split("|")[1] for k in base}
    added = {}
    for r in opts["seed"]:
        if r not in RULES: die(f"unknown rule {r!r}")
        if r in have: die(f"the baseline already has {r!r} entries; only the lead re-baselines (--baseline)")
        for k, v in counts.items():
            if k.endswith("|" + r): added[k] = v
    base.update(added)
    json.dump(dict(sorted(base.items())), open(opts["baseline_file"], "w"), indent=1)
    print(f"coop_lint: seeded {', '.join(opts['seed'])}: {sum(added.values())} hit(s) in {len(added)} file/rule pair(s)")
    for k, v in sorted(added.items()): print(f"    {k}: {v}")
    sys.exit(0)

if opts["baseline"]:
    os.makedirs("Tools", exist_ok=True)
    json.dump(dict(sorted(counts.items())), open(opts["baseline_file"], "w"), indent=1)
    print(f"coop_lint: baseline written ({sum(counts.values())} hits in {len({k.split('|')[0] for k in counts})} files)")
    sys.exit(0)

base = load_baseline(opts["baseline_file"])
if file_rx is not None: base = {k: v for k, v in base.items() if in_scope(k.split("|")[0])}
new = []
for key, n in sorted(counts.items()):
    was = base.get(key, 0)
    if n > was:
        path, rule = key.split("|")
        new.append({"file": path, "rule": rule, "was": was, "now": n, "message": RULES[rule][1],
                    "hits": [{"line": ln, "text": t} for ln, t in hits[key]]})
bad_count = sum(x["now"] - x["was"] for x in new)
fixed = sum(max(0, v - counts.get(k, 0)) for k, v in base.items())
for a in allows: a["needsApproval"] = bool(NEEDS_APPROVAL & set(a["rules"]))
unused = [a for a in allows if not a["used"]]
ok = bad_count == 0 and not bad

if opts["json"]:
    print(json.dumps({"ok": ok, "newHits": bad_count, "new": new, "removedSinceBaseline": fixed,
                      "allows": allows, "badAllows": bad, "unusedAllows": [f"{a['file']}:{a['line']}" for a in unused],
                      "scope": {"files": opts["files"], "scanned": len(scanned)}}, indent=1))
    sys.exit(0 if ok else 1)

if opts["files"] is not None and not scanned:
    print(f"coop_lint: --files matched no scanned file (Editor/ and the chess engine are never scanned): {opts['files']}", file=sys.stderr)
for x in new:
    print(f"\n{x['file']}  [{x['rule']}] {x['was']} -> {x['now']}: {x['message']}")
    for h in x["hits"]: print(f"    {h['line']}: {h['text']}")
for b in bad:
    print(f"\n{b['file']}:{b['line']}  bad coop:allow ({b['why']}): use `// coop:allow(<rule>) <reason>`")
if opts["allows"] and allows:
    print("\ncoop:allow in scope:")
    for a in allows:
        flag = "  NEEDS LEAD APPROVAL" if a["needsApproval"] else ""
        flag += "  (unused)" if not a["used"] else ""
        print(f"    {a['file']}:{a['line']}  [{','.join(a['rules'])}] {a['reason']}{flag}")
if bad_count or bad:
    msg = []
    if bad_count: msg.append(f"{bad_count} new hit(s). Fix them, or mark them `// coop:allow(<rule>) <reason>` and list them in your report")
    if bad: msg.append(f"{len(bad)} bad coop:allow comment(s)")
    print(f"\ncoop_lint: {'; '.join(msg)}. Only the lead re-baselines (Tools/coop_lint.sh --baseline).")
    sys.exit(1)
tail = f" {len(allows)} coop:allow(s) in scope{' (see --allows)' if not opts['allows'] else ''};" if allows else ""
print(f"coop_lint: OK (no new hits;{tail} {fixed} baseline hit(s) removed since the baseline)")
PY
