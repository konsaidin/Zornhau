# Resolved balance audit

Developer-only export mod; it is not included in Warband Combat or installed in player profiles.
It asks the engine for its loaded item variants and crafting recipes after asset patches run.

Build Warband Combat, then run from the repository:

```sh
python3 scripts/audit-balance.py --refresh
```

This runs two isolated headless creative worlds under `.artifacts`, bound to localhost ports
25568/25569. The CO world uses only the five pinned dependencies in `compat/overhaul-modpack.json`.
The export includes original attributes, resolved ingredient quantities and recipe patterns.
The generator records a manufacturing path for each part, not repair/decoration recipes.
Grid symbols repeated across several slots consume several ingredient quantities.
Smithing voxels describe the shape per output, not an ingot, fuel or labor-time estimate.

Change goals/timings in `balance/policy.json`; the normal build regenerates runtime data and tables
from the committed `balance/catalog.json` without starting a game. Repeat the export when game
or dependency assets change. To validate both packaged configurations without exporting:

```sh
python3 scripts/audit-balance.py --smoke
```

Use `--game`, `--dotnet`, and `--port` for other installation paths or occupied ports.
