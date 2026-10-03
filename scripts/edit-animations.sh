#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
EDITOR_DIR="/home/dead/.local/share/vs-model-creator"
MODEL="${VS_EDITOR_MODEL:-$PROJECT_DIR/editor/seraph-combat.json}"
if [[ -z "${VS_EDITOR_MODEL:-}" && -f "$PROJECT_DIR/editor/seraph-combat-completed.json" ]]; then
    MODEL="$PROJECT_DIR/editor/seraph-combat-completed.json"
fi
cd "$EDITOR_DIR"
exec java -Djava.library.path="$EDITOR_DIR/natives/linux" -jar vsmodelcreator.jar \
    -t "$PROJECT_DIR/editor/textures" \
    -s /home/dead/Documents/vintagestory/assets/game/shapes \
    -f "$MODEL"
