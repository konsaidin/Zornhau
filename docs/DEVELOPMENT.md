# Development

## Prerequisites

- Your own installation of Vintage Story **1.22.7**, including game DLLs and assets.
- **.NET SDK 10**; a runtime-only installation cannot compile the project.
- Python 3 and Bash. The helper scripts target Linux; set installation paths explicitly
  for other environments.
- Network access on the first build to fetch the pinned compatibility references.

From the repository root:

```bash
export VSPath="$HOME/Documents/vintagestory"
# Only needed if SDK 10 is not found through PATH:
export VS_DOTNET="$HOME/.local/share/vs-combat-dotnet/dotnet"
bash scripts/build.sh
```

Set `VSPath` to your actual game directory. `VS_GAME_DIR` is accepted as an alternative.
Without either variable, builds use `~/Documents/vintagestory`.
`VS_DOTNET` selects the SDK executable; otherwise the scripts try PATH, then
`${XDG_DATA_HOME:-$HOME/.local/share}/vs-combat-dotnet/dotnet`.

The build fetches the pinned CO/Armory references, generates balance and animation data,
compiles Zornhau, runs model/server/standalone/integration checks, imports the saved
`editor/seraph-combat-completed.json` poses and creates `dist/zornhau-0.8.3.zip`.
Downloaded references and build output stay outside Git. They are not runtime
requirements for standalone play. `VS_EDITOR_MODEL` selects another saved editor model.

## Natural Motion

After the main build has fetched compatibility fixtures:

```bash
bash natural-motion/build.sh
```

This runs its checks and writes `natural-motion/dist/naturalmotion-0.1.11.zip`.
Install it only on clients. See [its README](../natural-motion/README.md).

## Isolated checks

```bash
python3 scripts/audit-balance.py --smoke --training-smoke
```

The script launches temporary headless worlds with and without the pinned CO/Armory
stack. They use `.artifacts/` and localhost ports 25568/25569, reach RunGame, check a
training dummy's spawn/death/revival and stop gracefully. Use `--game`, `--dotnet` and
`--port` to override paths or ports. These checks do not use your normal player saves.

To refresh loaded item, armor and recipe catalogs after changing dependency versions:

```bash
python3 scripts/audit-balance.py --refresh
```

This replaces the committed balance catalogs and generated tables. Normal builds use
those catalogs without exporting again. See the [export tool](../tools/BalanceAudit/README.md).
Automated checks do not replace the live-game [release checklist](RELEASE-CHECKLIST.md).

## Local install and launch

```bash
bash scripts/install.sh
bash scripts/play.sh
```

Installation rebuilds the mod, backs up older Zornhau/WarbandCombat archives in the target
profile and installs the new ZIP. The default profile is
`${XDG_CONFIG_HOME:-$HOME/.config}/VintagestoryData-Zornhau`.
Use `bash scripts/install.sh /path/to/profile` and set `VS_COMBAT_PROFILE` when launching
that profile. `VS_DOTNET_ROOT` optionally selects the game runtime directory.
The launch script checks `~/Games/vintagestory`, then `~/Documents/vintagestory`, unless
`VS_GAME_DIR` or `VSPath` is set.

## Repository layout

| Directory | Purpose |
| --- | --- |
| `src/` | Combat, rendering, integration and embedded animation/collider code |
| `assets/zornhau/` | Runtime assets and generated configuration |
| `balance/` | Resolved catalogs, policy and CSV tables |
| `tests/` | Model, real-engine, standalone and optional integration checks |
| `compat/` | Pinned external package manifest and adapter notes |
| `editor/` | Authored animation models and baseline data |
| `natural-motion/` | Separate client locomotion mod |
| `scripts/` | Build, install, generation and audit helpers |
| `tools/` | Developer-only engine export and training smoke mods |
| `third-party/` | Embedded component attribution, licenses and source data |
| `docs/` | Design, balance, protocol and release documentation |

## Licensing

Original project code has no project-wide license yet. Keep the embedded components'
MIT license files and attribution when changing or packaging them. Game files and
external mod archives are fetched or read from local installations and are not committed.
