#!/usr/bin/env bash
set -euo pipefail
GAME_DIR="${VS_GAME_DIR:-${VSPath:-/home/dead/Games/vintagestory}}"
PROFILE_DIR="${VS_COMBAT_PROFILE:-/home/dead/.config/VintagestoryData-Warband}"
export DOTNET_ROOT="${VS_DOTNET_ROOT:-/home/dead/.local/share/vs-combat-dotnet}"
exec bash "$GAME_DIR/run.sh" --dataPath "$PROFILE_DIR" --addModPath "$PROFILE_DIR/Mods" "$@"
