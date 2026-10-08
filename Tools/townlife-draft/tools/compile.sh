#!/bin/bash
# Offline compile check for the TownLife drafts. No Editor contact; nothing is written under the Unity project.
#
#   compile.sh [--draft <draft root>] [--quiet] [extra .cs files ...]
#
# PASS 1 "editor": the whole Assembly-CSharp-Editor is compiled with Unity's bundled csc against a scratch copy of the
#   Editor response file (regenerated on every run from Library/Bee/artifacts/*.dag/Assembly-CSharp-Editor.rsp).
#   Every .cs under <draft root> is included: a file whose mirrored project path is already in the rsp is SUBSTITUTED
#   (the draft copy replaces the project file), any other file is appended. Extra files on the command line are appended.
#   This is exactly what the Editor will compile after integration.
#   Draft files under Assets/Tests/ are NOT part of this pass (they belong to the test assembly, see pass 3).
# PASS 2 "townlife": only <draft root>/Assets/Scripts/Editor/TownLife/*.cs (+ extra files) are compiled into a uniquely
#   named scratch DLL that REFERENCES the Editor's current Assembly-CSharp-Editor / Assembly-CSharp. This proves the
#   TownLife code uses nothing but PUBLIC API of the game assemblies, and it is the DLL tools/preview.sh loads.
#   (After integration the referenced Editor assembly holds the same types; the source wins, warning CS0436 is muted.)
# PASS 3 "tests" (only when the draft has files under Assets/Tests/EditMode/): Rubbish.Net.Tests is compiled from its
#   own response file with the draft test files substituted / appended, exactly as the Editor will compile it.
#
# Output: errors from anywhere (the project compiles clean without drafts, so every error is draft-caused) and warnings
# for draft/extra files only. Last lines:  "DLL: <path>"  "PDB: <path>"  then "COMPILE OK" or "COMPILE FAILED".
# Exit code 0 only when both passes are free of errors.
set -u
P=/Users/nick/Dev/Rubbish
HERE="$(cd "$(dirname "$0")" && pwd)"; SP="$(cd "$HERE/.." && pwd)"
DRAFT="$SP/draft"; QUIET=0; EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --draft) DRAFT="$(cd "$2" && pwd)"; shift 2;;
    --quiet) QUIET=1; shift;;
    -h|--help) sed -n 2,18p "$0"; exit 0;;
    *) case "$1" in /*) EXTRA+=("$1");; *) EXTRA+=("$PWD/$1");; esac; shift;;
  esac
done
RT=$(ls -d /Applications/Unity/Hub/Editor/6000.3.25f1/Unity.app/Contents/Resources/Scripting 2>/dev/null)
[ -n "$RT" ] || { echo "compile.sh: Unity 6000.3.25f1 scripting runtime not found" >&2; exit 2; }
SRC_RSP=$(ls -t "$P"/Library/Bee/artifacts/*.dag/Assembly-CSharp-Editor.rsp 2>/dev/null | head -1)
[ -n "$SRC_RSP" ] || { echo "compile.sh: no Assembly-CSharp-Editor.rsp under Library/Bee/artifacts" >&2; exit 2; }
OUTROOT="$SP/tools/out"; mkdir -p "$OUTROOT"
find "$OUTROOT" -mindepth 1 -maxdepth 1 -type d -mmin +90 -exec rm -rf {} + 2>/dev/null
STAMP="$(date +%H%M%S)_$$"; OUT="$OUTROOT/c_$STAMP"; mkdir -p "$OUT"
EXTRA_LIST="$OUT/extra.txt"; : > "$EXTRA_LIST"; for f in ${EXTRA[@]+"${EXTRA[@]}"}; do echo "$f" >> "$EXTRA_LIST"; done

python3 - "$SRC_RSP" "$DRAFT" "$OUT" "$EXTRA_LIST" "$P" "$STAMP" <<'PY' || exit 2
import os, sys
src, draft, out, extra_list, proj, stamp = sys.argv[1:7]
lines = open(src, encoding="utf-8-sig").read().splitlines()
extras = [l for l in open(extra_list).read().splitlines() if l.strip()]
drafts = []
for root, _, files in os.walk(draft):
    for f in sorted(files):
        if f.endswith(".cs"): drafts.append(os.path.join(root, f))
drafts.sort()
rel_all = {os.path.relpath(d, draft).replace(os.sep, "/"): d for d in drafts}
tests = {r: d for r, d in rel_all.items() if r.startswith("Assets/Tests/")}
rel = {r: d for r, d in rel_all.items() if r not in tests}
# ---- pass 1: full editor assembly with substitution ----
res, used = [], set()
for l in lines:
    s = l.strip()
    if s.startswith("-out:"): res.append('-out:"%s/Assembly-CSharp-Editor.dll"' % out); continue
    if s.startswith("-refout:"): res.append('-refout:"%s/Assembly-CSharp-Editor.ref.dll"' % out); continue
    if s.startswith('"') and s.endswith('.cs"'):
        r = s[1:-1]
        if r in rel: res.append('"%s"' % rel[r]); used.add(r); continue
        if not os.path.exists(os.path.join(proj, r)): continue      # deleted since the rsp was written
    res.append(l)
for r, d in sorted(rel.items()):
    if r not in used: res.append('"%s"' % d)
for e in extras: res.append('"%s"' % e)
open(os.path.join(out, "editor.rsp"), "w").write("\n".join(res) + "\n")
# ---- pass 2: TownLife only, referencing the compiled editor assembly ----
name = "TownLifeScratch_" + stamp
res2 = []
for l in lines:
    s = l.strip()
    if s.startswith("-out:"): res2.append('-out:"%s/%s.dll"' % (out, name)); continue
    if s.startswith("-refout:"): continue
    if s.startswith('"') and s.endswith('.cs"'): continue
    res2.append(l)
dag = os.path.dirname(src)
ref = os.path.join(dag, "Assembly-CSharp-Editor.ref.dll")
if not os.path.exists(ref): ref = os.path.join(proj, "Library/ScriptAssemblies/Assembly-CSharp-Editor.dll")
res2.append('-r:"%s"' % ref)
res2.append('-nowarn:0436')
tl = [d for r, d in sorted(rel.items()) if r.startswith("Assets/Scripts/Editor/TownLife/")]
for d in tl: res2.append('"%s"' % d)
for e in extras: res2.append('"%s"' % e)
open(os.path.join(out, "townlife.rsp"), "w").write("\n".join(res2) + "\n")
open(os.path.join(out, "name.txt"), "w").write(name)
# ---- pass 3: the test assembly, only when the draft carries test files ----
n_tests = 0
if tests:
    import glob
    cands = sorted(glob.glob(os.path.join(proj, "Library/Bee/artifacts/*.dag/Rubbish.Net.Tests.rsp")), key=os.path.getmtime, reverse=True)
    if cands:
        res3, used3 = [], set()
        for l in open(cands[0], encoding="utf-8-sig").read().splitlines():
            t = l.strip()
            if t.startswith("-out:"): res3.append('-out:"%s/Rubbish.Net.Tests.dll"' % out); continue
            if t.startswith("-refout:"): continue
            if t.startswith('"') and t.endswith('.cs"'):
                r = t[1:-1]
                if r in tests: res3.append('"%s"' % tests[r]); used3.add(r); continue
                if not os.path.exists(os.path.join(proj, r)): continue
            res3.append(l)
        for r, d in sorted(tests.items()):
            if r not in used3: res3.append('"%s"' % d)
        open(os.path.join(out, "tests.rsp"), "w").write("\n".join(res3) + "\n")
        n_tests = len(tests)
    else:
        sys.stderr.write("compile.sh: draft has test files but there is no Rubbish.Net.Tests.rsp; pass 3 skipped\n")
open(os.path.join(out, "summary.txt"), "w").write("drafts=%d substituted=%d townlife=%d tests=%d extras=%d rsp=%s\n" % (len(drafts), len(used), len(tl), n_tests, len(extras), src))
PY
NAME=$(cat "$OUT/name.txt")
[ $QUIET = 1 ] || echo "compile.sh: $(cat "$OUT/summary.txt")"
cd "$P" || exit 2
RC=0
run_pass() { # $1 label, $2 rsp
  local log="$OUT/$1.log"
  "$RT/NetCoreRuntime/dotnet" "$RT/DotNetSdkRoslyn/csc.dll" @"$2" > "$log" 2>&1; local rc=$?
  # errors from anywhere; warnings only for draft / extra files
  { grep -E "error [A-Z]+[0-9]+" "$log"
    grep -E "warning [A-Z]+[0-9]+" "$log" | grep -F -e "$DRAFT/" $(for f in ${EXTRA[@]+"${EXTRA[@]}"}; do printf -- '-e %q ' "$f"; done)
  } | sed -e "s#$DRAFT/##" | sort -u | sed -e "s#^#[$1] #"
  if [ $rc -ne 0 ] || grep -qE "error [A-Z]+[0-9]+" "$log"; then RC=1; [ -s "$log" ] || echo "[$1] csc exited $rc with no output"; fi
}
run_pass editor "$OUT/editor.rsp"
run_pass townlife "$OUT/townlife.rsp"
[ -f "$OUT/tests.rsp" ] && run_pass tests "$OUT/tests.rsp"
echo "DLL: $OUT/$NAME.dll"
echo "PDB: $OUT/$NAME.pdb"
if [ $RC = 0 ]; then echo "COMPILE OK"; else echo "COMPILE FAILED (full logs: $OUT/editor.log, $OUT/townlife.log$([ -f "$OUT/tests.log" ] && echo ", $OUT/tests.log"))"; fi
exit $RC
