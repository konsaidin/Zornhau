#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE_DIR="${1:-/home/dead/.config/VintagestoryData-Warband}"
bash "$PROJECT_DIR/scripts/build.sh"
mkdir -p "$PROFILE_DIR/Mods"
MOD_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$PROJECT_DIR/modinfo.json")"
BACKUP_DIR="$PROFILE_DIR/Backups/WarbandCombat/$(date +%Y%m%d-%H%M%S)"
shopt -s nullglob
for OLD_PACKAGE in "$PROFILE_DIR"/Mods/warbandcombat-*.zip; do
    mkdir -p "$BACKUP_DIR"
    mv "$OLD_PACKAGE" "$BACKUP_DIR/"
done
cp "$PROJECT_DIR/dist/warbandcombat-$MOD_VERSION.zip" "$PROFILE_DIR/Mods/warbandcombat-$MOD_VERSION.zip"
printf 'Installed mod in %s\n' "$PROFILE_DIR/Mods"
