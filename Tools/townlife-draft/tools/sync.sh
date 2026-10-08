#!/bin/bash
# sync.sh <agent-name> <own-file-basename> [more own basenames...]
# Creates/refreshes the agent's PRIVATE draft tree from the shared one, never touching the agent's own files.
# Then compile-checks the private tree. If someone else's freshly synced file breaks the compile, the previous
# private copy of the tree is restored for the files that changed (you keep working on the last good base).
set -u
SP="$(cd "$(dirname "$0")/.." && pwd)"; ME="$1"; shift
PRIV="$SP/work/$ME/draft"; SHARED="$SP/draft"
mkdir -p "$SP/work/$ME"
if [ ! -d "$PRIV" ]; then cp -R "$SHARED" "$PRIV"; echo "sync: created $PRIV"; fi
EX=(); for f in "$@"; do EX+=(--exclude "$f"); done
rm -rf "$SP/work/$ME/.prev"; cp -R "$PRIV" "$SP/work/$ME/.prev"
rsync -a "${EX[@]}" "$SHARED/" "$PRIV/"
if "$SP/tools/compile.sh" --draft "$PRIV" --quiet >"$SP/work/$ME/sync-compile.txt" 2>&1; then
  echo "sync: OK (private tree compiles) -> $PRIV"
else
  echo "sync: shared files do not compile with your files; restoring previous base. See $SP/work/$ME/sync-compile.txt"
  tail -15 "$SP/work/$ME/sync-compile.txt"
  rm -rf "$PRIV"; mv "$SP/work/$ME/.prev" "$PRIV"; exit 1
fi
