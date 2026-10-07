#!/bin/bash
# Refresh the build/test copy (~/Dev/Rubbish-build) from this project. Used for background player builds and
# multiplayer bot tests so they never touch the Editor you're working in. Library is kept so the copy opens fast.
# Don't run while a Unity Editor or batch build has the copy open.
set -euo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)/"
DST="${1:-$HOME/Dev/Rubbish-build}/"
rsync -a --delete \
  --exclude='.plastic/' --exclude='.git/' --exclude='Temp/' --exclude='Logs/' --exclude='Library/VP/' \
  --exclude='Builds/' --exclude='Build/' --exclude='Build.zip' --exclude='obj/' --exclude='*.csproj' --exclude='*.sln' \
  "$SRC" "$DST"
echo "synced $SRC -> $DST"
