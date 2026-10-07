#!/bin/bash
# Tools/bots/sync_build.sh: refresh the build copy and run tests or bot builds THERE, never in the live project
# (Docs/Multiplayer.md §8.7.6). One build or test at a time, enforced with a mkdir lock next to the copy.
#
#   Tools/bots/sync_build.sh                         sync only
#   Tools/bots/sync_build.sh --tests [--filter F] [--pkg NAME]
#                                                    EditMode tests in the copy -> <copy>/Logs/ut-NAME.xml (exit 0 = all green)
#   Tools/bots/sync_build.sh --build                 Setup_Coop.Run (once it exists) + RubbishBuild.DevHeadless in the copy
#                                                    -> <copy>/Builds/DevHeadless/RubbishBots.app
#   Tools/bots/sync_build.sh --build --smoke         ... then run it with -batchmode -nographics -botsmoke (exit 0 within 60 s)
#   Tools/bots/sync_build.sh --smoke                 smoke-test the existing RubbishBots.app only
# Options:  --no-sync          use the copy as it is
#           --copy DIR         build copy (default $RUBBISH_BUILD_COPY or ~/Dev/Rubbish-build)
#           --wait-lock MIN    wait this long for another build/test to finish (default 30)
#           --timeout SEC      kill Unity after this long (default 3600)
# Env:      UNITY_EDITOR_PATH  the Unity binary (default: the Hub install matching ProjectVersion.txt)
#           RUBBISH_BUILD_LOCK the lock folder (default <copy>.lock)
#
# The sync never copies Library, Temp, Logs, Builds, obj, UserSettings, .plastic, .git or Build.zip: the copy keeps its
# own Library (imports stay warm), never sees the live project's test data root (UserSettings/RubbishDev.json), and its
# Builds/ and Logs/ survive the --delete. The one exception is Library/PackageCache, mirrored exactly (see below).
# Excludes are anchored at the project root, so folders named Build/Temp/obj inside Assets or packages still sync.
# The copy's product name is RubbishBuildCopy, so its Editor never uses the real game's save folder or PlayerPrefs.
set -euo pipefail

SRC="$(cd "$(dirname "$0")/../.." && pwd)"
DST="${RUBBISH_BUILD_COPY:-$HOME/Dev/Rubbish-build}"
ORIG_ARGS="$*"
DO_SYNC=1 DO_TESTS=0 DO_BUILD=0 DO_SMOKE=0 FILTER="" PKG="" WAIT_MIN=30 TIMEOUT=3600

usage() { awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"; exit "${1:-2}"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --tests) DO_TESTS=1 ;;
    --build) DO_BUILD=1 ;;
    --smoke) DO_SMOKE=1 ;;
    --no-sync) DO_SYNC=0 ;;
    --filter) FILTER="${2:?--filter needs a value}"; shift ;;
    --pkg) PKG="${2:?--pkg needs a value}"; shift ;;
    --copy) DST="${2:?--copy needs a folder}"; shift ;;
    --wait-lock) WAIT_MIN="${2:?}"; shift ;;
    --timeout) TIMEOUT="${2:?}"; shift ;;
    -h|--help) usage 0 ;;
    *) echo "sync_build: unknown argument $1" >&2; usage 2 ;;
  esac
  shift
done
DST="${DST%/}"
[ -z "$PKG" ] && PKG="${FILTER:-all}"
PKG="$(printf '%s' "$PKG" | tr -c 'A-Za-z0-9._-' '_')"

# ---- safety: the copy is never the live project
mkdir -p "$DST"
DST="$(cd "$DST" && pwd)"
case "$DST/" in "$SRC/"*) echo "sync_build: the build copy ($DST) can't be the live project or inside it" >&2; exit 2 ;; esac
case "$SRC/" in "$DST/"*) echo "sync_build: the live project can't be inside the build copy" >&2; exit 2 ;; esac

# ---- one build or test at a time
LOCK="${RUBBISH_BUILD_LOCK:-$DST.lock}"
take_lock() {
  local waited=0
  while ! mkdir "$LOCK" 2>/dev/null; do
    local pid age
    pid="$(sed -n 's/^pid=//p' "$LOCK/owner" 2>/dev/null || true)"
    age=$(( $(date +%s) - $(stat -f %m "$LOCK" 2>/dev/null || date +%s) ))
    if [ -n "$pid" ] && ! kill -0 "$pid" 2>/dev/null; then
      echo "sync_build: removing a stale lock (owner pid $pid is gone)"; rm -rf "$LOCK"; continue
    fi
    if [ "$age" -gt 10800 ]; then echo "sync_build: removing a lock older than 3 h"; rm -rf "$LOCK"; continue; fi
    if [ "$waited" -ge $((WAIT_MIN * 60)) ]; then
      echo "sync_build: $LOCK is held: $(tr '\n' ' ' < "$LOCK/owner" 2>/dev/null)" >&2; exit 3
    fi
    [ "$waited" -eq 0 ] && echo "sync_build: waiting for $LOCK ($(tr '\n' ' ' < "$LOCK/owner" 2>/dev/null))"
    sleep 10; waited=$((waited + 10))
  done
  printf 'pid=%s\nwho=%s\nwhat=%s\nsince=%s\n' "$$" "${USER:-?}" "sync_build $ORIG_ARGS" "$(date '+%F %T')" > "$LOCK/owner"
  trap 'rm -rf "$LOCK"' EXIT
}

unity_on_copy() { ps -axo pid=,command= | grep -i -E -- "-projectpath[ =]\"?$DST/?(\"| |\$)" | grep -v -e "grep -i" || true; }

UNITY_VER="$(sed -n 's/^m_EditorVersion: //p' "$SRC/ProjectSettings/ProjectVersion.txt")"
UNITY="${UNITY_EDITOR_PATH:-/Applications/Unity/Hub/Editor/$UNITY_VER/Unity.app/Contents/MacOS/Unity}"

# Runs Unity in batchmode on the copy with a wall-clock timeout. Usage: run_unity <log> <args...>
run_unity() {
  local log="$1"; shift
  [ -x "$UNITY" ] || { echo "sync_build: Unity $UNITY_VER not found at $UNITY (set UNITY_EDITOR_PATH)" >&2; return 4; }
  grep -q "^  productName: $COPY_PRODUCT\$" "$DST/ProjectSettings/ProjectSettings.asset" ||
    { echo "sync_build: the copy still has the real product name; refusing to start Unity on it" >&2; return 5; }
  local busy; busy="$(unity_on_copy)"
  [ -z "$busy" ] || { echo "sync_build: a Unity process already has the copy open:" >&2; echo "$busy" >&2; return 3; }
  mkdir -p "$(dirname "$log")"
  echo "sync_build: Unity $UNITY_VER $* (log: $log)"
  "$UNITY" -batchmode -projectPath "$DST" -logFile "$log" "$@" &
  local pid=$! t=0
  while kill -0 "$pid" 2>/dev/null; do
    sleep 2; t=$((t + 2))
    if [ "$t" -ge "$TIMEOUT" ]; then echo "sync_build: Unity ran past ${TIMEOUT}s; killing it" >&2; kill -9 "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; return 124; fi
  done
  wait "$pid"
}

summarize_tests() {  # NUnit XML -> one line per failure + a total
  python3 - "$1" <<'PY'
import sys, xml.etree.ElementTree as ET
try: root = ET.parse(sys.argv[1]).getroot()
except Exception as e: print(f"sync_build: no readable results ({e})"); sys.exit(1)
a = root.attrib
total, passed, failed, skipped = (int(a.get(k, 0)) for k in ("total", "passed", "failed", "skipped"))
for tc in root.iter("test-case"):
    if tc.attrib.get("result") == "Failed":
        msg = (tc.findtext("./failure/message") or "").strip().splitlines()
        print(f"  FAIL {tc.attrib.get('fullname')}: {msg[0] if msg else ''}")
print(f"sync_build: tests {a.get('result','?')}: {passed}/{total} passed, {failed} failed, {skipped} skipped")
sys.exit(0 if failed == 0 and total > 0 and a.get("result", "").startswith("Passed") else 1)
PY
}

if [ "$DO_SYNC" = 1 ] || [ "$DO_TESTS" = 1 ] || [ "$DO_BUILD" = 1 ] || [ "$DO_SMOKE" = 1 ]; then take_lock; fi

if [ "$DO_SYNC" = 1 ]; then
  busy="$(unity_on_copy)"
  [ -z "$busy" ] || { echo "sync_build: a Unity process has the copy open; not syncing under it:" >&2; echo "$busy" >&2; exit 3; }
  rsync -a --delete \
    --exclude='/Library/' --exclude='/Temp/' --exclude='/Logs/' --exclude='/Builds/' --exclude='/Build/' --exclude='/obj/' \
    --exclude='/UserSettings/' --exclude='/.plastic/' --exclude='/.git/' --exclude='/Build.zip' \
    --exclude='*.csproj' --exclude='*.sln' --exclude='.DS_Store' \
    "$SRC/" "$DST/"
  # Package caches are content-addressed and immutable, so mirror them exactly (no excludes: packages contain folders
  # named Build, Temp, obj …). This keeps the copy's packages identical to the live project's without re-fetching git
  # packages, and repairs a cache damaged by an older unanchored --exclude='Build/'.
  if [ -d "$SRC/Library/PackageCache" ]; then
    mkdir -p "$DST/Library/PackageCache"
    rsync -a --delete "$SRC/Library/PackageCache/" "$DST/Library/PackageCache/"
  fi
  echo "sync_build: synced $SRC -> $DST"
fi

# The copy's Editor must never share the real game's persistentDataPath (Nick's saves) or its Editor PlayerPrefs: the
# Unity Test Framework writes TestResults.xml to persistentDataPath, and edit-mode code may read or write saves there.
# So the copy always has its own product name, re-applied after every sync, and Unity never starts on the copy without it.
COPY_PRODUCT="RubbishBuildCopy"
isolate_copy() {
  local ps="$DST/ProjectSettings/ProjectSettings.asset"
  [ -f "$ps" ] || { echo "sync_build: $ps missing; sync first" >&2; return 1; }
  sed -i '' -E "s/^  productName: .*/  productName: $COPY_PRODUCT/" "$ps"
  grep -q "^  productName: $COPY_PRODUCT\$" "$ps" || { echo "sync_build: couldn't give the copy its own product name" >&2; return 1; }
}
if [ "$DO_SYNC" = 1 ] || [ "$DO_TESTS" = 1 ] || [ "$DO_BUILD" = 1 ]; then isolate_copy; fi

STAMP="$(date +%Y%m%d-%H%M%S)"
if [ "$DO_TESTS" = 1 ]; then
  RESULTS="$DST/Logs/ut-$PKG.xml"
  rm -f "$RESULTS"
  args=(-runTests -testPlatform EditMode -testResults "$RESULTS")
  [ -n "$FILTER" ] && args+=(-testFilter "$FILTER")
  set +e; run_unity "$DST/Logs/ut-$PKG.log" "${args[@]}"; rc=$?; set -e
  [ -f "$RESULTS" ] || { echo "sync_build: Unity exited $rc without results; see $DST/Logs/ut-$PKG.log" >&2
                         grep -E "error CS|Compilation failed|Scripts have compiler errors" "$DST/Logs/ut-$PKG.log" | head -20 >&2 || true; exit 1; }
  set +e; summarize_tests "$RESULTS"; trc=$?; set -e
  echo "sync_build: results $RESULTS"
  [ "$trc" = 0 ] && [ "$rc" = 0 ] || exit 1
fi

APP="$DST/Builds/DevHeadless/RubbishBots.app"
if [ "$DO_BUILD" = 1 ]; then
  LOG="$DST/Logs/build-bots-$STAMP.log"
  set +e
  run_unity "$LOG" -quit -buildTarget OSXUniversal -executeMethod RubbishBuild.BotBuild -rubbishBuildCopy -buildOutput "$APP"
  rc=$?; set -e
  if [ "$rc" != 0 ] || [ ! -x "$APP/Contents/MacOS/RubbishBots" ]; then
    echo "sync_build: bot build failed (exit $rc); see $LOG" >&2
    grep -E "error CS|\[RubbishBuild\]|Build Failed|Error building" "$LOG" | head -20 >&2 || true
    exit 1
  fi
  grep -F "[RubbishBuild]" "$LOG" | tail -3 || true
  echo "sync_build: built $APP"
fi

if [ "$DO_SMOKE" = 1 ]; then
  rm -rf "$LOCK"; trap - EXIT     # run_bots.sh doesn't need the build lock; release it for the next build
  exec "$SRC/Tools/bots/run_bots.sh" --smoke --app "$APP"
fi
