#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"
source "$PROJECT_DIR/scripts/common.sh"
EDITOR_MODEL="${VS_EDITOR_MODEL:-$PROJECT_DIR/editor/seraph-combat-completed.json}"
if [[ ! -f "$EDITOR_MODEL" ]]; then
    printf 'Editor animation model not found: %s\n' "$EDITOR_MODEL" >&2
    exit 1
fi
python3 scripts/fetch-overhaul.py
python3 scripts/generate-animations.py
python3 scripts/generate-balance.py
"$SDK_BIN" build Zornhau.csproj -c Release --nologo "$@"
"$SDK_BIN" run --project tests/CombatModel.Tests.csproj -c Release --nologo
"$SDK_BIN" run --project tests/server/Server.Tests.csproj -c Release --nologo
python3 scripts/import-editor-animations.py "$EDITOR_MODEL"
python3 scripts/generate-overhaul-animations.py
"$SDK_BIN" run --project tests/standalone/Standalone.Tests.csproj -c Release --nologo
"$SDK_BIN" run --project tests/overhaul/Overhaul.Tests.csproj -c Release --nologo
python3 - <<'PY'
from pathlib import Path
import json, zipfile
root = Path.cwd()
version = json.loads((root / 'modinfo.json').read_text())['version']
out = root / 'dist' / f'zornhau-{version}.zip'
out.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in [root / 'modinfo.json', root / 'third-party/reanimated/LICENSE', root / 'third-party/overhaul-core/LICENSE', root / 'third-party/overhaul-core/README.md', root / 'bin/Release/net10.0/Zornhau.dll',
                 *sorted((root / 'assets').rglob('*'))]:
        if path.is_file():
            name = path.name if path.suffix == '.dll' else str(path.relative_to(root))
            archive.write(path, name)
print('Mod package:', out)
PY
