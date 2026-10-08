#!/bin/bash
# =====================================================================================================================
# TownLife INTEGRATION RUNBOOK (core agent). Moves the drafted TownLife layer from scratchpad/draft into the real Unity
# project and publishes it through the project's own transactional pipeline.
#
#   integrate.sh <step> [args]      run ONE step (each step is guarded and prints what it did)
#   integrate.sh help               list the steps
#
# Steps, in order. Read the output of each one before running the next. Nothing runs when this file is sourced, and
# calling it with no argument only prints this help.
#
#   1 preflight   read-only: draft tree compiles, Editor idle (not playing / compiling), Neighborhood + DepotRow loaded
#                 and clean, blueprint stamp matches, published signature current, no transaction open
#   2 backup      copies the two scenes, baked assets, Assets/Resources/City, the generated folders and every project
#                 file the draft will replace to scratchpad/backup/integrate_<timestamp>/ with a sha256 manifest
#   3 copy        copies draft/Assets/** into the project (new TownLife folder, edited existing files, the test file);
#                 Unity creates the .meta files itself
#   4 recompile   `unity recompile`, waits, prints compile errors, checks that the new types and hooks are in
#   5 rebuild     TownLifeBuilder.RebuildStandalone() through one long eval; report or exception is captured in a file
#   6 postcheck   read-only: scenes clean, signature current, city + yard validation, object counts per group
#   7 tests       the affected EditMode test classes (TownLifeTests first)
#   8 photos      re-shoots the standard photo set from the REAL scene (nothing built in memory, nothing hidden)
#     rollback    restores the last backup (files only, never git), removes what `copy` added, recompiles and reloads
#                 the scenes from the restored files
#     status      prints where the run is (last backup, which steps have completed)
#
# Hard rules this script keeps: never touches Assets/Scenes/Bootstrap.unity, never uses git to undo anything, never
# stops Play Mode (it waits or refuses), never saves a scene itself (only the project's publish pipeline saves),
# serialises all Editor access with the scratchpad's editor.lock (mkdir protocol, owner name INTEGRATE_OWNER).
#
# If step 5 fails the publish transaction has ALREADY rolled the scenes and baked assets back to their files; the new
# scripts stay in the project (they are harmless until a publish runs). Unity may leave Neighborhood flagged dirty after
# the Undo revert: `integrate.sh reload-scenes` reloads the two town scenes from disk without saving. Fix the draft,
# then `copy` + `recompile` + `rebuild` again, or `rollback` to remove everything.
# =====================================================================================================================
INTEGRATE_SELF="${BASH_SOURCE[0]:-$0}"
P=/Users/nick/Dev/Rubbish
HERE="$(cd "$(dirname "$INTEGRATE_SELF")" && pwd)"; SP="$(cd "$HERE/.." && pwd)"
DRAFT="${INTEGRATE_DRAFT:-$SP/draft}"                 # the SHARED draft tree (never a private one, unless you mean it)
LOCK="$SP/editor.lock"; ME="${INTEGRATE_OWNER:-integrator}"
STATE="$SP/integrate"
UNITY="unity"
SCENES=("Assets/Scenes/Neighborhood.unity" "Assets/Scenes/DepotRow.unity")          # Bootstrap.unity is deliberately absent
BAKED=("Assets/Resources/StreetGraph.asset" "Assets/Resources/HouseDirectory.asset" "Assets/Resources/TownMap.asset" "Assets/UI/map/town_map.png")
FOLDERS=("Assets/Resources/City" "Assets/Materials/Generated/CoastalCity" "Assets/Materials/Generated/Neighborhood" "Assets/Materials/Generated/TownLife")
# the three project files this job edits (plus anything else the draft replaces is added automatically):
EDITED=("Assets/Scripts/Editor/NeighborhoodBuilder.Coastal.cs" "Assets/Scripts/Editor/CoastalYardAuthoring.cs" "Assets/Scripts/Editor/NeighborhoodBuilder.cs")
TEST_CLASSES=(TownLifeTests CoastalYardBoundaryTests CoastalParkAuthoringTests CoastalBackdropTests CoastalCityDensityTests CoastalCityGraphMapTests CoastalInfillTests CoastalResidentialIntegrationTests CoastalYardTransactionTests CityEditTransactionTests)

say()  { printf '%s\n' "integrate: $*"; }
fail() { printf '%s\n' "integrate: FAILED: $*" >&2; exit 1; }
mark() { date +%Y-%m-%dT%H:%M:%S > "$STATE/done_$1"; }
need() { [ -f "$STATE/done_$1" ] || fail "step '$1' has not completed yet (run: integrate.sh $1)"; }

# ---------------------------------------------------------------------------------------------- editor plumbing
lock_take() {
  local waited=0
  until mkdir "$LOCK" 2>/dev/null; do
    [ "$(cat "$LOCK/owner" 2>/dev/null)" = "$ME" ] && return 0            # already ours (nested step)
    sleep 5; waited=$((waited+5))
    [ $((waited % 60)) -eq 0 ] && say "waiting for editor.lock held by '$(cat "$LOCK/owner" 2>/dev/null)' (${waited}s)"
    [ $waited -ge 1800 ] && fail "editor.lock still held by '$(cat "$LOCK/owner" 2>/dev/null)' after 30 min (not removed; check whether that agent is alive)"
  done
  echo "$ME" > "$LOCK/owner"
}
lock_drop() { if [ "$(cat "$LOCK/owner" 2>/dev/null)" = "$ME" ]; then rm -rf "$LOCK"; fi; }

editor_state() {   # prints "playMode compiling reloading"; empty when the Editor does not answer
  $UNITY command editor_status --project-path "$P" --json 2>/dev/null | python3 -c '
import json, sys
try: r = json.load(sys.stdin)["data"]["result"]
except Exception: sys.exit(0)
print(r.get("playMode"), r.get("compiling"), r.get("domainReloadInProgress"))'
}
editor_idle() { [ "$(editor_state)" = "stopped False False" ]; }
wait_idle() {      # waits (never interrupts) until the Editor is in Edit Mode and not compiling; $1 = max seconds
  local max="${1:-900}" waited=0
  until editor_idle; do
    [ $waited -ge "$max" ] && return 1
    [ $((waited % 60)) -eq 0 ] && say "Editor busy (state: '$(editor_state)'; the owner may be playing). Waiting, not interrupting."
    sleep 10; waited=$((waited+10))
  done
}

# run_eval <name> <seconds>: evaluates $RUN/<name>.cs. The eval itself must write $RUN/<name>.out (first line = verdict).
# The CLI can report a bogus main-thread timeout while the eval keeps running, so only the .out file is trusted.
run_eval() {
  local name="$1" secs="${2:-120}" out="$RUN/$1.out" i=0
  rm -f "$out"
  lock_take; trap lock_drop EXIT
  wait_idle 900 || { lock_drop; fail "Editor stayed busy for 15 min; '$name' was not run"; }
  $UNITY command eval_file --project-path "$P" --file "$RUN/$name.cs" --timeout "$secs" --json > "$RUN/$name.cli.json" 2>&1
  while [ ! -s "$out" ] && [ $i -lt "$secs" ]; do sleep 2; i=$((i+2)); done
  lock_drop; trap - EXIT
  [ -s "$out" ] || { head -c 2000 "$RUN/$name.cli.json" >&2; fail "'$name' produced no result within ${secs}s (CLI reply above)"; }
}

new_run() { RUN="$STATE/run_$(date +%Y%m%d_%H%M%S)_$1"; mkdir -p "$RUN"; }
draft_files() { (cd "$DRAFT" && find Assets -type f -name '*.cs' | LC_ALL=C sort); }
cs_string() { python3 -c 'import sys; print("@\"" + sys.argv[1].replace("\"", "\"\"") + "\"")' "$1"; }

# ---------------------------------------------------------------------------------------------- 1. preflight
step_preflight() {
  new_run preflight
  say "draft tree: $DRAFT"
  [ -d "$DRAFT/Assets/Scripts/Editor/TownLife" ] || fail "no TownLife folder in the draft tree"
  [ -d "$SP/publish.lock" ] && fail "an agent is publishing into the shared draft right now (publish.lock); wait for it"
  local stray; stray=$(draft_files | grep -E 'TownLife(Core[a-z]*|Example|Proto[A-Za-z]*)\.cs$' || true)
  [ -z "$stray" ] || fail "debug / example files in the draft tree must not be integrated: $stray"
  say "offline compile of the draft against the project (no Editor contact) ..."
  "$HERE/compile.sh" --draft "$DRAFT" > "$RUN/compile.txt" 2>&1 || { tail -30 "$RUN/compile.txt"; fail "the draft tree does not compile"; }
  tail -1 "$RUN/compile.txt"
  local todo; todo=$(grep -l 'NotImplementedException\|stub: ' $(draft_files | sed "s#^#$DRAFT/#") 2>/dev/null | sed "s#$DRAFT/##" || true)
  [ -z "$todo" ] || say "WARNING: placeholder code still present in: $(echo $todo)"
  for f in "${EDITED[@]}"; do [ -f "$P/$f" ] || fail "expected project file is missing: $f"; done
  grep -q 'TownLifeBuilder.PipelineClear' "$DRAFT/Assets/Scripts/Editor/NeighborhoodBuilder.Coastal.cs" 2>/dev/null || fail "draft NeighborhoodBuilder.Coastal.cs has no TownLife hooks"
  # the draft copies of existing files must be based on the project's CURRENT versions: show the diff size for a human look
  for f in $(draft_files); do
    if [ -f "$P/$f" ]; then say "replaces $f ($(diff "$P/$f" "$DRAFT/$f" | grep -c '^[<>]') changed lines)"; fi
  done
  local free; free=$(df -m "$SP" | awk 'NR==2 {print $4}'); [ "${free:-0}" -ge 500 ] || fail "less than 500 MB free for the backup"
  cat > "$RUN/preflight.cs" <<EOF
// generated by tools/integrate.sh (read-only)
var sb = new System.Text.StringBuilder(); bool ok = true;
System.Action<bool, string> check = (good, what) => { sb.Append(good ? "  ok    " : "  FAIL  ").Append(what).Append('\n'); if (!good) ok = false; };
check(!UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode, "Edit Mode");
check(!UnityEditor.EditorApplication.isCompiling && !UnityEditor.EditorApplication.isUpdating, "not compiling / importing");
check(!UnityEditor.EditorUtility.scriptCompilationFailed, "no compile errors in the project");
check(CityEditTransaction.Current == null, "no city transaction open");
for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
{ var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i); sb.Append("  scene " + s.path + " loaded=" + s.isLoaded + " dirty=" + s.isDirty + "\n"); }
var town = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Neighborhood.unity");
var depot = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/DepotRow.unity");
check(town.IsValid() && town.isLoaded, "Neighborhood.unity loaded"); check(depot.IsValid() && depot.isLoaded, "DepotRow.unity loaded");
check(!(town.IsValid() && town.isDirty), "Neighborhood.unity clean"); check(!(depot.IsValid() && depot.isDirty), "DepotRow.unity clean");
UnityEngine.Transform root = null;
if (town.IsValid() && town.isLoaded) foreach (var go in town.GetRootGameObjects()) if (go.name == "NeighborhoodGen") root = go.transform;
check(root != null && CoastalCityLayout.IsCoastal(root), "coastal NeighborhoodGen root");
if (root != null)
{
    var stamp = root.GetComponent<CityBlueprintStamp>();
    check(stamp != null && stamp.Matches(CoastalCityLayout.Data), "blueprint stamp matches (an incremental publish will be accepted)");
    check(root.Find("Park") != null, "Park group present");
    sb.Append("  info  NeighborhoodGen GameObjects: " + root.GetComponentsInChildren<UnityEngine.Transform>(true).Length + "; TownLife group " + (root.Find("TownLife") != null ? "EXISTS (re-integration)" : "absent (first integration)") + "\n");
    var yardErrors = CoastalYardBoundary.Validate(root); check(yardErrors.Count == 0, "yard markers validate (" + yardErrors.Count + " problems)");
}
string stale; bool current = TownWorldSignatureBuilder.IsCurrent(out stale);
check(current, "published town signature is current" + (current ? "" : " (" + stale + ")"));
sb.Append("  info  TownLife scripts in project: " + UnityEditor.AssetDatabase.IsValidFolder("Assets/Scripts/Editor/TownLife") + "; TownLife assets folder: " + UnityEditor.AssetDatabase.IsValidFolder("Assets/Materials/Generated/TownLife") + "\n");
System.IO.File.WriteAllText($(cs_string "$RUN/preflight.out"), (ok ? "PREFLIGHT-OK" : "PREFLIGHT-FAILED") + "\n" + sb);
return ok;
EOF
  run_eval preflight 120
  cat "$RUN/preflight.out"
  [ "$(head -1 "$RUN/preflight.out")" = "PREFLIGHT-OK" ] || fail "preflight found problems (nothing was changed)"
  mark preflight; rm -f "$STATE"/done_{backup,copy,recompile,rebuild,postcheck,tests,photos}
  say "preflight OK. Next: integrate.sh backup"
}

# ---------------------------------------------------------------------------------------------- 2. backup
step_backup() {
  need preflight
  wait_idle 600 || fail "Editor busy; a backup must not straddle a publish"
  local B="$SP/backup/integrate_$(date +%Y%m%d_%H%M%S)"; mkdir -p "$B/project"
  cd "$P" || fail "no project"
  local list="$B/paths.txt"; : > "$list"
  for f in "${SCENES[@]}" "${BAKED[@]}"; do [ -f "$f" ] && { echo "$f" >> "$list"; [ -f "$f.meta" ] && echo "$f.meta" >> "$list"; }; done
  for d in "${FOLDERS[@]}"; do
    if [ -d "$d" ]; then find "$d" -type f >> "$list"; [ -f "$d.meta" ] && echo "$d.meta" >> "$list"; else echo "$d" >> "$B/folders_absent.txt"; fi
  done
  : > "$B/files_new.txt"; : > "$B/files_replaced.txt"
  for f in "${EDITED[@]}"; do [ -f "$f" ] && echo "$f" >> "$list"; done
  for f in $(draft_files); do
    if [ -f "$f" ]; then echo "$f" >> "$B/files_replaced.txt"; echo "$f" >> "$list"; else echo "$f" >> "$B/files_new.txt"; fi
  done
  [ -d "Assets/Scripts/Editor/TownLife" ] || echo "Assets/Scripts/Editor/TownLife" >> "$B/folders_absent.txt"
  LC_ALL=C sort -u "$list" -o "$list"
  grep -q 'Bootstrap.unity' "$list" && fail "Bootstrap.unity must never be part of this job"
  rsync -a --files-from="$list" "$P/" "$B/project/" || fail "backup copy failed"
  (cd "$B/project" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256 > "$B/MANIFEST.sha256") || fail "checksum manifest failed"
  # verify: the backup equals the project byte for byte right now
  (cd "$P" && shasum -a 256 -c "$B/MANIFEST.sha256" > "$B/verify.txt" 2>&1) || { grep -v ': OK$' "$B/verify.txt" | head; fail "backup verification failed"; }
  echo "$B" > "$STATE/current_backup"
  say "backup: $B ($(wc -l < "$list" | tr -d ' ') files, $(du -sh "$B" | cut -f1)); $(wc -l < "$B/files_new.txt" | tr -d ' ') new files and $(wc -l < "$B/files_replaced.txt" | tr -d ' ') replaced files will come from the draft"
  mark backup; say "Next: integrate.sh copy"
}

# ---------------------------------------------------------------------------------------------- 3. copy
step_copy() {
  need backup
  local B; B=$(cat "$STATE/current_backup"); [ -d "$B/project" ] || fail "backup folder is gone: $B"
  "$HERE/compile.sh" --draft "$DRAFT" --quiet > "$STATE/copy_compile.txt" 2>&1 || { tail -20 "$STATE/copy_compile.txt"; fail "the draft tree no longer compiles; not copying"; }
  lock_take; trap lock_drop EXIT
  # Writing a .cs under Assets is an Editor event (auto refresh recompiles at the next refresh, or when Play stops).
  wait_idle 900 || { lock_drop; fail "Editor busy (the owner is probably playing); nothing was copied"; }
  local n=0
  for f in $(draft_files); do
    grep -qx "$f" "$B/files_new.txt" "$B/files_replaced.txt" || { lock_drop; fail "$f appeared in the draft after the backup was taken; run backup again"; }
    mkdir -p "$P/$(dirname "$f")"; cp "$DRAFT/$f" "$P/$f" || { lock_drop; fail "copy failed for $f"; }
    n=$((n+1))
  done
  lock_drop; trap - EXIT
  say "copied $n files into the project (Unity writes the .meta files on import)"
  mark copy; say "Next: integrate.sh recompile"
}

# ---------------------------------------------------------------------------------------------- 4. recompile
step_recompile() {
  need copy; new_run recompile
  lock_take; trap lock_drop EXIT
  wait_idle 900 || { lock_drop; fail "Editor busy; not recompiling during a play session"; }
  say "recompiling (blocking, up to 300 s) ..."
  $UNITY recompile --project-path "$P" --timeout 300 --json > "$RUN/recompile.json" 2>&1; local rc=$?
  $UNITY status --until-ready --timeout 300 --project-path "$P" --json > "$RUN/ready.json" 2>&1
  sleep 3
  $UNITY command console --level error --tail 60 --project-path "$P" --json > "$RUN/console_errors.json" 2>&1
  $UNITY command console_status --project-path "$P" --json > "$RUN/console_status.json" 2>&1
  lock_drop; trap - EXIT
  python3 - "$RUN/recompile.json" "$RUN/console_status.json" "$RUN/console_errors.json" <<'PY'
import json, sys
def load(p):
    try: return json.load(open(p))
    except Exception as e: return {"_unreadable": str(e)}
rec, status, errs = load(sys.argv[1]), load(sys.argv[2]), load(sys.argv[3])
d = rec.get("data", rec) or {}
r = d.get("result", d) if isinstance(d, dict) else {}
failed = bool(r.get("failed")) or bool(r.get("compilationFailed"))
gt = (((status.get("data") or {}).get("result") or {}).get("groundTruth") or {})
failed = failed or bool(gt.get("compilationFailed"))
print("recompile: status=%s failed=%s compilationFailed=%s console.compilationFailed=%s" % (r.get("status"), r.get("failed"), r.get("compilationFailed"), gt.get("compilationFailed")))
for e in (r.get("errors") or [])[:40]: print("  ", e if isinstance(e, str) else json.dumps(e))
if failed:
    entries = (((errs.get("data") or {}).get("result") or {}).get("entries") or [])
    for e in entries[-40:]: print("  console:", (e.get("message") or e.get("text") or json.dumps(e))[:400])
    sys.exit(1)
PY
  [ $? -eq 0 ] || fail "compile errors (see $RUN). Fix the draft and run copy + recompile again, or rollback"
  [ $rc -eq 0 ] || say "note: 'unity recompile' exited $rc although no compile error was reported; continuing to the type check"
  cat > "$RUN/types.cs" <<EOF
// generated by tools/integrate.sh (read-only)
var sb = new System.Text.StringBuilder(); bool ok = true;
foreach (var name in new[] { "TownLifeBuilder", "TownLifeScene", "TownLifeOccupancy", "TownLifePlan", "TownLifeFences", "TownLifeKit", "TownLifePark", "TownLifeYards", "TownLifeStreets", "TownLifeMeadows", "TownLifeEdges", "TownLifePaint", "TownLifePreview" })
{ bool has = System.Type.GetType(name + ", Assembly-CSharp-Editor") != null; sb.Append(has ? "  ok    " : "  FAIL  ").Append(name).Append('\n'); ok &= has; }
bool tests = System.Type.GetType("TownLifeTests, Rubbish.Net.Tests") != null; sb.Append(tests ? "  ok    " : "  FAIL  ").Append("TownLifeTests (Rubbish.Net.Tests)\n"); ok &= tests;
string blocker = ok ? TownLifeBuilder.StandaloneBlocker() : "types missing";
sb.Append(blocker == null ? "  ok    standalone rebuild may start\n" : "  FAIL  standalone rebuild blocked: " + blocker + "\n"); ok &= blocker == null;
System.IO.File.WriteAllText($(cs_string "$RUN/types.out"), (ok ? "TYPES-OK" : "TYPES-FAILED") + "\n" + sb);
return ok;
EOF
  run_eval types 120; cat "$RUN/types.out"
  [ "$(head -1 "$RUN/types.out")" = "TYPES-OK" ] || fail "the compiled project does not expose TownLife as expected"
  grep -q 'TownLifeBuilder.PipelineBuild' "$P/Assets/Scripts/Editor/NeighborhoodBuilder.Coastal.cs" || fail "the project's NeighborhoodBuilder.Coastal.cs has no TownLife hooks"
  mark recompile; say "Next: integrate.sh rebuild   (the real publish; takes a few minutes)"
}

# ---------------------------------------------------------------------------------------------- 5. rebuild
step_rebuild() {
  need recompile; new_run rebuild
  cat > "$RUN/rebuild.cs" <<EOF
// generated by tools/integrate.sh: the one mutating call of the whole runbook. Transactional: on any failure the
// publish pipeline restores both scenes and every baked / generated asset before the exception reaches us.
string outPath = $(cs_string "$RUN/rebuild.out");
try
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    string report = TownLifeBuilder.RebuildStandalone();
    System.IO.File.WriteAllText(outPath, "REBUILD-OK " + clock.ElapsedMilliseconds + " ms\n" + report);
    return "REBUILD-OK";
}
catch (System.Exception e)
{
    System.IO.File.WriteAllText(outPath, "REBUILD-FAILED\n" + e);
    return "REBUILD-FAILED";
}
EOF
  say "running TownLifeBuilder.RebuildStandalone() (waiting up to 30 min for the result file) ..."
  run_eval rebuild 1800
  head -60 "$RUN/rebuild.out"; echo "... (full text: $RUN/rebuild.out)"
  if [ "$(head -1 "$RUN/rebuild.out" | cut -d' ' -f1)" != "REBUILD-OK" ]; then
    say "The publish was rolled back by its transaction. If Neighborhood is now flagged dirty: integrate.sh reload-scenes"
    fail "rebuild failed; scripts are still integrated. Fix the draft and repeat copy/recompile/rebuild, or rollback"
  fi
  cp "$RUN/rebuild.out" "$STATE/last_rebuild_report.txt"
  mark rebuild; say "Next: integrate.sh postcheck"
}

# ---------------------------------------------------------------------------------------------- 6. postcheck
step_postcheck() {
  need rebuild; new_run postcheck
  cat > "$RUN/postcheck.cs" <<EOF
// generated by tools/integrate.sh (read-only)
var sb = new System.Text.StringBuilder(); bool ok = true;
System.Action<bool, string> check = (good, what) => { sb.Append(good ? "  ok    " : "  FAIL  ").Append(what).Append('\n'); if (!good) ok = false; };
var town = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Neighborhood.unity");
var depot = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/DepotRow.unity");
check(town.isLoaded && !town.isDirty, "Neighborhood.unity loaded and clean"); check(depot.isLoaded && !depot.isDirty, "DepotRow.unity loaded and clean");
string stale; bool current = TownWorldSignatureBuilder.IsCurrent(out stale); check(current, "published signature current" + (current ? "" : " (" + stale + ")"));
var root = TownLifeScene.FindTown(); check(root != null, "NeighborhoodGen found");
if (root != null)
{
    foreach (UnityEngine.Transform g in root) sb.Append("  count " + g.name + ": " + g.GetComponentsInChildren<UnityEngine.Transform>(true).Length + " GameObjects, " + g.GetComponentsInChildren<UnityEngine.Renderer>(true).Length + " renderers, " + g.GetComponentsInChildren<UnityEngine.Collider>(true).Length + " colliders\n");
    sb.Append("  count NeighborhoodGen total: " + root.GetComponentsInChildren<UnityEngine.Transform>(true).Length + "\n");
    var life = root.Find(TownLifeBuilder.GroupName); var park = root.Find("Park"); var parkLife = park != null ? park.Find(TownLifeBuilder.ParkGroupName) : null;
    check(life != null, "NeighborhoodGen/TownLife exists"); check(parkLife != null, "NeighborhoodGen/Park/TL_Park exists");
    int objects = (life != null ? life.GetComponentsInChildren<UnityEngine.Transform>(true).Length - 1 : 0) + (parkLife != null ? parkLife.GetComponentsInChildren<UnityEngine.Transform>(true).Length : 0);
    check(objects > 0 && objects <= TownLifeBuilder.ObjectBudget, "TownLife objects " + objects + " within the budget of " + TownLifeBuilder.ObjectBudget);
    if (life != null) foreach (UnityEngine.Transform g in life) sb.Append("  count TownLife/" + g.name + ": " + g.GetComponentsInChildren<UnityEngine.Transform>(true).Length + "\n");
    var scene = TownLifeScene.Capture(root);
    int legacy = 0; if (park != null) foreach (UnityEngine.Transform c in park) if (TownLifeBuilder.IsRetiredParkLegacy(c, scene)) legacy++;
    check(legacy == 0, "legacy park retired (" + legacy + " left)");
    var yard = CoastalYardBoundary.Validate(root); check(yard.Count == 0, "yard markers validate (" + yard.Count + " problems)"); foreach (var e in yard) sb.Append("        " + e + "\n");
    int markers = root.GetComponentsInChildren<CoastalYardBoundary>(true).Length, newStyle = 0;
    foreach (var m in root.GetComponentsInChildren<CoastalYardBoundary>(true)) foreach (var t in m.GetComponentsInChildren<UnityEngine.Transform>(true)) if (t != m.transform && t.name.StartsWith("TL_")) { newStyle++; break; }
    sb.Append("  count yard markers: " + markers + " (" + newStyle + " built from the TownLife network)\n");
    check(newStyle > 0, "the connected boundary network is in the scene");
    var anchor = root.GetComponentInChildren<WorldLifeAnchor>(true);
    var city = anchor != null && anchor.graph != null ? CoastalCityValidation.Validate(root, anchor.graph) : new System.Collections.Generic.List<string> { "no WorldLifeAnchor graph" };
    city.AddRange(TownGameplayAuthoring.ValidateAuthored(root)); city.AddRange(CoastalInfillSite.Validate(root));
    check(city.Count == 0, "city validation (" + city.Count + " problems)"); foreach (var e in city) sb.Append("        " + e + "\n");
    int dirtyAssets = 0; foreach (var guid in UnityEditor.AssetDatabase.FindAssets("", new[] { TownLifeKit.AssetDir })) { var a = UnityEditor.AssetDatabase.LoadMainAssetAtPath(UnityEditor.AssetDatabase.GUIDToAssetPath(guid)); if (a != null && UnityEditor.EditorUtility.IsDirty(a)) dirtyAssets++; }
    check(dirtyAssets == 0, "no unsaved TownLife assets (" + dirtyAssets + ")");
    int missing = 0; foreach (var r in root.GetComponentsInChildren<UnityEngine.MeshRenderer>(true)) { if (!r.name.StartsWith("TL_")) continue; foreach (var m in r.sharedMaterials) if (m == null) missing++; var f = r.GetComponent<UnityEngine.MeshFilter>(); if (f != null && f.sharedMesh == null) missing++; }
    check(missing == 0, "no TownLife object with a missing mesh or material (" + missing + ")");
}
System.IO.File.WriteAllText($(cs_string "$RUN/postcheck.out"), (ok ? "POSTCHECK-OK" : "POSTCHECK-FAILED") + "\n" + sb);
return ok;
EOF
  run_eval postcheck 300; cat "$RUN/postcheck.out"
  cd "$P" && say "files on disk: TownLife assets $(find Assets/Materials/Generated/TownLife -type f ! -name '*.meta' 2>/dev/null | wc -l | tr -d ' '), Neighborhood.unity $(du -h Assets/Scenes/Neighborhood.unity | cut -f1)"
  [ "$(head -1 "$RUN/postcheck.out")" = "POSTCHECK-OK" ] || fail "post-checks found problems (the publish itself succeeded; decide: fix forward or rollback)"
  mark postcheck; say "Next: integrate.sh tests"
}

# ---------------------------------------------------------------------------------------------- 7. tests
# Preconditions (tests-tools.md 2c): Edit Mode, compile clean, NO dirty saved scene. The transaction / yard fixtures
# replace the open scenes while they run and restore the scene setup afterwards; TownLifeTests therefore runs first.
step_tests() {
  need rebuild; new_run tests
  local bad=0 classes=("$@")
  [ ${#classes[@]} -gt 0 ] || classes=("${TEST_CLASSES[@]}")
  for class in "${classes[@]}"; do
    lock_take; trap lock_drop EXIT
    wait_idle 900 || { lock_drop; fail "Editor busy; tests not run"; }
    $UNITY command run_tests --mode editor --filter "$class" --async_tests true --project-path "$P" --json > "$RUN/$class.start.json" 2>&1
    local i=0
    while [ $i -lt 300 ]; do
      sleep 3; i=$((i+3))
      $UNITY command test_status --result-only --project-path "$P" > "$RUN/$class.json" 2>/dev/null
      grep -q '"status"[[:space:]]*:[[:space:]]*"completed"' "$RUN/$class.json" && break
    done
    lock_drop; trap - EXIT
    python3 - "$RUN/$class.json" "$class" <<'PY' || bad=$((bad+1))
import json, sys
try: r = json.load(open(sys.argv[1]))
except Exception as e: print("  %s: unreadable result (%s)" % (sys.argv[2], e)); sys.exit(1)
s = r.get("summary") or {}
print("  %-40s %s total=%s passed=%s failed=%s skipped=%s" % (sys.argv[2], r.get("status"), s.get("total"), s.get("passed"), s.get("failed"), s.get("skipped")))
notok = [t for t in (r.get("results") or []) if t.get("Status") not in ("Passed",)]
for t in notok: print("     %s: %s %s" % (t.get("Status"), t.get("FullName"), (t.get("Message") or "").strip().replace("\n", " | ")[:300]))
sys.exit(1 if r.get("status") != "completed" or (s.get("failed") or 0) > 0 or (s.get("total") or 0) == 0 else 0)
PY
  done
  [ $bad -eq 0 ] || fail "$bad test class(es) failed or did not run (results in $RUN)"
  mark tests; say "Next: integrate.sh photos"
}

# ---------------------------------------------------------------------------------------------- 8. photos
# Shoots the fixed 34-pose set from the REAL, published scene: module "none" builds nothing and hides nothing, so the
# pictures show exactly what the game will. Compare with photos/before2/.
step_photos() {
  need rebuild
  local out="${1:-$SP/photos/after_$(date +%Y%m%d_%H%M%S)}"
  PREVIEW_OWNER="$ME" PREVIEW_DRAFT="$DRAFT" "$HERE/preview.sh" none "$out" all "${2:-}" || fail "photo run failed (see $out/result.txt)"
  mark photos; say "photos: $out  (baseline: $SP/photos/before2)"
}

# ---------------------------------------------------------------------------------------------- helpers
# Closes (WITHOUT saving) and re-opens the two town scenes, e.g. after a rolled-back publish left a dirty flag, or
# around a file restore (closing first means Unity never shows its "scene modified externally" dialog).
# Bootstrap.unity stays open and untouched throughout; it also keeps the Editor from running out of scenes.
scenes_close() {
  new_run scenes_close
  cat > "$RUN/close.cs" <<EOF
// generated by tools/integrate.sh: discard the in-memory state of the two town scenes (no save). Bootstrap.unity is not touched.
var sb = new System.Text.StringBuilder(); bool ok = true;
if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) { System.IO.File.WriteAllText($(cs_string "$RUN/close.out"), "CLOSE-FAILED\nPlay Mode"); return false; }
sb.Append("active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path + "\n");
foreach (var path in new[] { "Assets/Scenes/DepotRow.unity", "Assets/Scenes/Neighborhood.unity" })
{
    var s = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
    if (!s.IsValid() || !s.isLoaded) { sb.Append("  not loaded: " + path + "\n"); continue; }
    bool wasDirty = s.isDirty;
    if (UnityEngine.SceneManagement.SceneManager.sceneCount < 2) { sb.Append("  cannot close the only open scene: " + path + "\n"); ok = false; continue; }
    bool closed = UnityEditor.SceneManagement.EditorSceneManager.CloseScene(s, true);
    sb.Append("  " + (closed ? "closed " : "COULD NOT close ") + path + " (was dirty: " + wasDirty + ")\n"); ok &= closed;
}
System.IO.File.WriteAllText($(cs_string "$RUN/close.out"), (ok ? "CLOSE-OK" : "CLOSE-FAILED") + "\n" + sb);
return ok;
EOF
  run_eval close 300; cat "$RUN/close.out"
  [ "$(head -1 "$RUN/close.out")" = "CLOSE-OK" ] || fail "could not close the town scenes"
  sed -n 's/^active=//p' "$RUN/close.out" > "$STATE/active_scene"
}
scenes_open() {
  new_run scenes_open
  local active; active=$(cat "$STATE/active_scene" 2>/dev/null)
  cat > "$RUN/open.cs" <<EOF
// generated by tools/integrate.sh: open the two town scenes additively from their files.
var sb = new System.Text.StringBuilder(); bool ok = true;
if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) { System.IO.File.WriteAllText($(cs_string "$RUN/open.out"), "OPEN-FAILED\nPlay Mode"); return false; }
foreach (var path in new[] { "Assets/Scenes/Neighborhood.unity", "Assets/Scenes/DepotRow.unity" })
{
    var s = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
    if (!s.IsValid() || !s.isLoaded) s = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Additive);
    sb.Append("  " + path + " loaded=" + s.isLoaded + " dirty=" + s.isDirty + "\n"); ok &= s.isLoaded && !s.isDirty;
}
var back = UnityEngine.SceneManagement.SceneManager.GetSceneByPath($(cs_string "$active")); if (back.IsValid() && back.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(back);
sb.Append("  active=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path + "\n");
System.IO.File.WriteAllText($(cs_string "$RUN/open.out"), (ok ? "OPEN-OK" : "OPEN-FAILED") + "\n" + sb);
return ok;
EOF
  run_eval open 300; cat "$RUN/open.out"
  [ "$(head -1 "$RUN/open.out")" = "OPEN-OK" ] || fail "the town scenes did not come back loaded and clean"
}
step_reload_scenes() { scenes_close; scenes_open; }

# ---------------------------------------------------------------------------------------------- rollback
# Restores the backup byte for byte and removes what `copy` and the publish added. Files only; git is never used.
step_rollback() {
  [ -f "$STATE/current_backup" ] || fail "no backup recorded; nothing to roll back to"
  local B; B=$(cat "$STATE/current_backup"); [ -d "$B/project" ] && [ -f "$B/MANIFEST.sha256" ] || fail "backup is incomplete: $B"
  [ "${1:-}" = "--yes" ] || fail "rollback restores scenes, baked assets and scripts from $B and deletes the integrated TownLife files. Re-run as: integrate.sh rollback --yes"
  (cd "$B/project" && shasum -a 256 -c "$B/MANIFEST.sha256" > "$B/verify_before_restore.txt" 2>&1) || fail "the backup itself no longer matches its manifest; not restoring from it"
  wait_idle 1800 || fail "Editor busy; rollback needs Edit Mode"
  scenes_close                                   # so the file restore never meets an open (or dirty) copy of a scene
  lock_take; trap lock_drop EXIT
  wait_idle 600 || { lock_drop; fail "Editor became busy; scenes are closed, files untouched. Run rollback --yes again"; }
  cd "$P" || fail "no project"
  say "restoring $(wc -l < "$B/paths.txt" | tr -d ' ') files from $B"
  rsync -a --files-from="$B/paths.txt" "$B/project/" "$P/" || { lock_drop; fail "restore copy failed"; }
  # files the draft ADDED (and their Unity-made metas)
  while IFS= read -r f; do [ -n "$f" ] && rm -f "$P/$f" "$P/$f.meta"; done < "$B/files_new.txt"
  # folders that did not exist before: remove them entirely; folders that did exist: remove files that are not in the backup
  if [ -f "$B/folders_absent.txt" ]; then
    while IFS= read -r d; do
      case "$d" in Assets/Scripts/Editor/TownLife|Assets/Materials/Generated/TownLife) rm -rf "$P/$d" "$P/$d.meta";; esac
    done < "$B/folders_absent.txt"
  fi
  # TownLife's own asset folder existed before (re-integration): drop the assets this run added, keep the backed-up ones
  local tl="Assets/Materials/Generated/TownLife"
  if [ -d "$P/$tl" ]; then find "$tl" -type f | while IFS= read -r f; do grep -qxF "$f" "$B/paths.txt" || rm -f "$P/$f"; done; find "$P/$tl" -type d -empty -delete 2>/dev/null; fi
  (cd "$P" && shasum -a 256 -c "$B/MANIFEST.sha256" > "$STATE/rollback_verify.txt" 2>&1) || { grep -v ': OK$' "$STATE/rollback_verify.txt" | head; lock_drop; fail "restored files do not match the backup manifest"; }
  say "files restored and verified. Recompiling and re-opening the town scenes from the restored files ..."
  $UNITY recompile --project-path "$P" --timeout 300 --json > "$STATE/rollback_recompile.json" 2>&1
  $UNITY status --until-ready --timeout 300 --project-path "$P" --json > /dev/null 2>&1
  lock_drop; trap - EXIT
  scenes_open
  rm -f "$STATE"/done_*
  say "rollback complete. Check: integrate.sh preflight (signature must be current again)"
}

step_status() {
  say "state dir: $STATE"; say "backup: $(cat "$STATE/current_backup" 2>/dev/null || echo none)"
  for s in preflight backup copy recompile rebuild postcheck tests photos; do
    if [ -f "$STATE/done_$s" ]; then say "  $s: done $(cat "$STATE/done_$s")"; else say "  $s: -"; fi
  done
  say "editor: '$(editor_state)' (want 'stopped False False'); editor.lock owner: '$(cat "$LOCK/owner" 2>/dev/null || echo free)'"
}

main() {
  set -u; mkdir -p "$STATE"
  case "${1:-help}" in
    preflight) step_preflight;; backup) step_backup;; copy) step_copy;; recompile) step_recompile;; rebuild) step_rebuild;;
    postcheck) step_postcheck;; tests) shift; step_tests "$@";; photos) shift; step_photos "$@";;
    reload-scenes) step_reload_scenes;; rollback) shift; step_rollback "$@";; status) step_status;;
    help|-h|--help|*) sed -n 2,31p "$INTEGRATE_SELF";;
  esac
}

# Never run anything when sourced; run exactly one named step when executed.
if [ -n "${BASH_SOURCE[0]:-}" ] && [ "${BASH_SOURCE[0]}" = "$0" ]; then main "$@"; fi
