#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
source "../scripts/common.sh"
"$SDK_BIN" build NaturalMotion.csproj -c Release --nologo
"$SDK_BIN" run --project tests/NaturalMotion.Tests.csproj -c Release --nologo
python3 - <<'PY'
from pathlib import Path
import json,zipfile
root=Path.cwd(); version=json.loads((root/'modinfo.json').read_text())['version']
output=root/'dist'/f'naturalmotion-{version}.zip'; output.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as archive:
    archive.write(root/'modinfo.json','modinfo.json')
    archive.write(root/'bin/Release/net10.0/NaturalMotion.dll','NaturalMotion.dll')
print('Mod package:',output)
PY
