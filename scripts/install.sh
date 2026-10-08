#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE_DIR="${1:-${XDG_CONFIG_HOME:-$HOME/.config}/VintagestoryData-Zornhau}"
VS_EDITOR_MODEL="${VS_EDITOR_MODEL:-$PROJECT_DIR/editor/seraph-combat-completed.json}" bash "$PROJECT_DIR/scripts/build.sh"
mkdir -p "$PROFILE_DIR/Mods"
MOD_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$PROJECT_DIR/modinfo.json")"
BACKUP_DIR="$PROFILE_DIR/Backups/Zornhau/$(date +%Y%m%d-%H%M%S)"
shopt -s nullglob
for OLD_PACKAGE in "$PROFILE_DIR"/Mods/zornhau-*.zip "$PROFILE_DIR"/Mods/warbandcombat-*.zip; do
    mkdir -p "$BACKUP_DIR"
    mv "$OLD_PACKAGE" "$BACKUP_DIR/"
done
cp "$PROJECT_DIR/dist/zornhau-$MOD_VERSION.zip" "$PROFILE_DIR/Mods/zornhau-$MOD_VERSION.zip"
printf 'Installed mod in %s\n' "$PROFILE_DIR/Mods"
