#!/usr/bin/env bash
set -euo pipefail
GAME_DIR="${VS_GAME_DIR:-${VSPath:-$HOME/Games/vintagestory}}"
if [[ -z "${VS_GAME_DIR:-}" && -z "${VSPath:-}" && ! -f "$GAME_DIR/run.sh" ]]; then
    GAME_DIR="$HOME/Documents/vintagestory"
fi
PROFILE_DIR="${VS_COMBAT_PROFILE:-${XDG_CONFIG_HOME:-$HOME/.config}/VintagestoryData-Zornhau}"
if [[ -n "${VS_DOTNET_ROOT:-}" ]]; then
    export DOTNET_ROOT="$VS_DOTNET_ROOT"
elif [[ -x "${XDG_DATA_HOME:-$HOME/.local/share}/vs-combat-dotnet/dotnet" ]]; then
    export DOTNET_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/vs-combat-dotnet"
fi
exec bash "$GAME_DIR/run.sh" --dataPath "$PROFILE_DIR" --addModPath "$PROFILE_DIR/Mods" "$@"
