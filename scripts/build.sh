#!/usr/bin/env bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_DIR"
SDK_BIN="${VS_DOTNET:-/home/dead/.local/share/vs-combat-dotnet/dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
python3 scripts/fetch-overhaul.py
python3 scripts/generate-animations.py
"$SDK_BIN" build WarbandCombat.csproj -c Release --nologo "$@"
"$SDK_BIN" run --project tests/CombatModel.Tests.csproj -c Release --nologo
"$SDK_BIN" run --project tests/overhaul/Overhaul.Tests.csproj -c Release --nologo
if [[ -n "${VS_EDITOR_MODEL:-}" ]]; then
    python3 scripts/import-editor-animations.py "$VS_EDITOR_MODEL"
fi
python3 scripts/generate-overhaul-animations.py
python3 - <<'PY'
from pathlib import Path
import json, zipfile
root = Path.cwd()
version = json.loads((root / 'modinfo.json').read_text())['version']
out = root / 'dist' / f'warbandcombat-{version}.zip'
out.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in [root / 'modinfo.json', root / 'third-party/reanimated/LICENSE', root / 'bin/Release/net10.0/WarbandCombat.dll',
                 *sorted((root / 'assets').rglob('*'))]:
        if path.is_file():
            name = path.name if path.suffix == '.dll' else str(path.relative_to(root))
            archive.write(path, name)
print('Mod package:', out)
PY
