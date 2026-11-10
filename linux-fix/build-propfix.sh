#!/usr/bin/env bash
# Builds and installs the procedural prop fix into FamilyTime_Data/Managed.
#   ./build-propfix.sh            build + install
#   ./build-propfix.sh --restore  put the pristine Assembly-CSharp.dll back
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
MANAGED="$ROOT/FamilyTime_Data/Managed"
PRISTINE="$ROOT/linux-fix/pristine/Assembly-CSharp.dll"
PRISTINE_MD5=a399271e0f0cdfad3df5c5526e526c45
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

stop_game() { pkill -x FamilyTime.exe 2>/dev/null || true; sleep 2; }

if [ "${1:-}" = "--restore" ]; then
  stop_game
  cp "$PRISTINE" "$MANAGED/Assembly-CSharp.dll"
  rm -f "$MANAGED/PropFix.dll"
  echo "restored: $(md5sum "$MANAGED/Assembly-CSharp.dll")"
  exit 0
fi

if [ ! -f "$PRISTINE" ]; then
  echo "missing $PRISTINE" >&2
  echo "copy the untouched Assembly-CSharp.dll from your own backup; md5 $PRISTINE_MD5" >&2
  exit 1
fi
echo "$PRISTINE_MD5  $PRISTINE" | md5sum -c --quiet - || { echo "pristine backup is not pristine" >&2; exit 1; }
dotnet build "$ROOT/linux-fix/props" -c Release -o "$ROOT/linux-fix/props/out" --nologo -v q
dotnet build "$ROOT/linux-fix/patchtool" -c Release -o "$ROOT/linux-fix/patchtool/out" --nologo -v q

stop_game
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
"$ROOT/linux-fix/patchtool/out/PatchTool" "$PRISTINE" "$ROOT/linux-fix/props/out/PropFix.dll" "$TMP/Assembly-CSharp.dll" "$MANAGED"
cp "$TMP/Assembly-CSharp.dll" "$MANAGED/Assembly-CSharp.dll"
cp "$ROOT/linux-fix/props/out/PropFix.dll" "$MANAGED/PropFix.dll"
echo "installed: $(md5sum "$MANAGED/Assembly-CSharp.dll")"
