#!/bin/bash
# publish.sh <agent-name> <path-relative-to-draft-root> [more...]
# Copies YOUR files from your private draft tree into the shared tree, but only if the shared tree still compiles
# with them. Serialised by a lock so two publishers cannot interleave.
set -u
SP="$(cd "$(dirname "$0")/.." && pwd)"; ME="$1"; shift
PRIV="$SP/work/$ME/draft"; SHARED="$SP/draft"; LOCK="$SP/publish.lock"
until mkdir "$LOCK" 2>/dev/null; do sleep 2; done; trap 'rm -rf "$LOCK"' EXIT
TMP="$SP/work/$ME/.publish"; rm -rf "$TMP"; cp -R "$SHARED" "$TMP"
for rel in "$@"; do
  [ -f "$PRIV/$rel" ] || { echo "publish: missing $PRIV/$rel"; exit 2; }
  mkdir -p "$(dirname "$TMP/$rel")"; cp -p "$PRIV/$rel" "$TMP/$rel"
done
if "$SP/tools/compile.sh" --draft "$TMP" --quiet >"$SP/work/$ME/publish-compile.txt" 2>&1; then
  for rel in "$@"; do mkdir -p "$(dirname "$SHARED/$rel")"; cp -p "$PRIV/$rel" "$SHARED/$rel"; done
  echo "publish: OK ($*)"; echo "$(date +%H:%M:%S) $ME $*" >> "$SP/publish.log"
else
  echo "publish: REFUSED, shared tree would not compile. See $SP/work/$ME/publish-compile.txt"; tail -20 "$SP/work/$ME/publish-compile.txt"; exit 1
fi
