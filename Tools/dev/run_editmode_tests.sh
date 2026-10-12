#!/bin/bash
# Run the EditMode tests on a PRIVATE COPY of the project in a batch-mode Unity: never in the live Editor Nick plays in.
#
#   Tools/dev/run_editmode_tests.sh [testFilter]     (default: the whole EditMode suite, ~3 min; filter = a class or test name)
#
# What it does: rsyncs this project to ~/Dev/Rubbish-build (override: RUBBISH_TEST_COPY) WITHOUT its Library, UserSettings or
# scratchpad, so the copy keeps its own Library and the live Editor's identity files (Library/Pipeline's port file,
# EditorInstance.json, ilpp.pid) never reach it (with them `unity command --project-path <copy>` would talk to the LIVE Editor,
# Docs/Multiplayer.md §8.7.6 and the editor-play-session notes). Then runs `Unity -batchmode -runTests` on the copy and prints
# the totals and every failing test. Results: $RUBBISH_TEST_OUT (default /tmp/rubbish_editmode)/editmode_results.xml + editmode.log.
# Refuses to run while any Unity process has the copy open. The first run after a long gap re-imports what changed (minutes).
# Tests that fail only here: CleanupBoardMaterialTests (a URP shader tag the batch-mode Editor doesn't report) is known.
set -euo pipefail
SRC="$(cd "$(dirname "$0")/../.." && pwd)/"
DST="${RUBBISH_TEST_COPY:-$HOME/Dev/Rubbish-build}/"
OUTDIR="${RUBBISH_TEST_OUT:-/tmp/rubbish_editmode}"
VERSION="$(sed -n 's/^m_EditorVersion: //p' "$SRC/ProjectSettings/ProjectVersion.txt")"
UNITY="/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity"
FILTER="${1:-}"
[ -x "$UNITY" ] || { echo "no Unity $VERSION at $UNITY" >&2; exit 2; }

if ps aux | grep -i '[U]nity.app/Contents/MacOS/Unity' | grep -F -q -- "${DST%/}"; then
  echo "a Unity process already has $DST open; not touching it" >&2; exit 2
fi

mkdir -p "$DST" "$OUTDIR"
rsync -a --delete \
  --exclude='/.git/' --exclude='/.plastic/' --exclude='/Temp/' --exclude='/Logs/' --exclude='/Builds/' --exclude='/Build/' \
  --exclude='/Build.zip' --exclude='/obj/' --exclude='*.csproj' --exclude='*.sln' --exclude='*.slnx' \
  --exclude='/Library/' --exclude='/UserSettings/' --exclude='/scratchpad/' \
  "$SRC" "$DST"
echo "synced $SRC -> $DST (Library, UserSettings kept as the copy's own)"

OUT="$OUTDIR/editmode_results.xml"
LOG="$OUTDIR/editmode.log"
rm -f "$OUT" "$LOG"
ARGS=(-batchmode -projectPath "$DST" -runTests -testPlatform EditMode -testResults "$OUT" -logFile "$LOG")
if [ -n "$FILTER" ]; then ARGS+=(-testFilter "$FILTER"); fi
set +e
"$UNITY" "${ARGS[@]}"
code=$?
set -e
echo "unity exit code: $code"
if [ -f "$OUT" ]; then
  python3 - "$OUT" <<'PY'
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
print({k: r.get(k) for k in ('total', 'passed', 'failed', 'skipped', 'inconclusive', 'result', 'duration')})
for tc in r.iter('test-case'):
    if tc.get('result') in ('Failed',):
        msg = tc.find('failure/message')
        print('FAILED', tc.get('fullname'), '::', (msg.text or '').strip().replace('\n', ' ')[:300] if msg is not None else '')
PY
else
  echo "no results file; see $LOG" >&2
  tail -40 "$LOG" >&2 || true
fi
exit $code
