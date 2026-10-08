#!/usr/bin/env bash
# Shared build setup; source this file from a script using set -euo pipefail.
export VSPath="${VSPath:-${VS_GAME_DIR:-$HOME/Documents/vintagestory}}"
SDK_CANDIDATES=("${VS_DOTNET:-}")
if [[ -z "${VS_DOTNET:-}" ]]; then
    SDK_CANDIDATES=("$(command -v dotnet || true)" "${XDG_DATA_HOME:-$HOME/.local/share}/vs-combat-dotnet/dotnet")
fi
SDK_BIN=""
for SDK_CANDIDATE in "${SDK_CANDIDATES[@]}"; do
    [[ -n "$SDK_CANDIDATE" ]] || continue
    SDK_VERSIONS="$("$SDK_CANDIDATE" --list-sdks 2>/dev/null || true)"
    if [[ "$SDK_VERSIONS" =~ (^|$'\n')10\. ]]; then
        SDK_BIN="$SDK_CANDIDATE"
        break
    fi
done
if [[ ! -f "$VSPath/VintagestoryAPI.dll" ]]; then
    printf 'Set VSPath to your Vintage Story 1.22.7 installation.\n' >&2
    exit 1
fi
if [[ -z "$SDK_BIN" ]]; then
    printf 'Install .NET SDK 10 or set VS_DOTNET to its dotnet executable.\n' >&2
    exit 1
fi
export VS_DOTNET="$SDK_BIN"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
