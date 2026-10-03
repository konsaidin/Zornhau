#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
export VS_EDITOR_MODEL="${VS_EDITOR_MODEL:-$PROJECT_DIR/editor/seraph-combat.json}"
if [[ "$VS_EDITOR_MODEL" == "$PROJECT_DIR/editor/seraph-combat.json" && -f "$PROJECT_DIR/editor/seraph-combat-completed.json" ]]; then
    export VS_EDITOR_MODEL="$PROJECT_DIR/editor/seraph-combat-completed.json"
fi
exec bash "$PROJECT_DIR/scripts/install.sh" "$@"
