# Zornhau

Directional melee combat for **Vintage Story 1.22.7**, inspired by Mount & Blade: Warband.
Read an opponent's attack, choose your guard, control your reach and commit to a swing.

**Current version: 0.8.3 — experimental.** Multiplayer balance and compatibility testing
are ongoing; see the [release checklist](docs/RELEASE-CHECKLIST.md) for outstanding checks.

## Features

- Four attack and weapon-block directions: overhead, thrust, left and right.
- Held windups, manual parries and feints, with weapon timing by family, material and grip.
- Animated blade sweeps; server checks for attack state, timing, equipment, reach,
  obstacles, turn limits and PvP permissions.
- Damaging physical hits interrupt attacks, healing-item use and bow drawing.
  Healing already applied continues.
- Shields defend against frontal melee attacks. Weapons requiring both hands
  automatically stow the shield on the back, where it provides no protection.
- Sprint restrictions during attacks and limits on actual character turning during
  released swings. Jumping remains available; the free F5 camera can orbit independently.
- First-person, immersive first-person and third-person support, with directional
  crosshair feedback for combat phases, hits, parries and shield breakage.
- Configurable fatality effects with decapitation, crushing head hits, blood and sound.

With the supported CO/Armory setup, weapon penetration is tuned against existing armor
so heavy protection prolongs fights. Armor statistics themselves remain unchanged.

## Installation

1. Get the release archive `zornhau-0.8.3.zip`, or
   [build it from source](docs/DEVELOPMENT.md).
2. Put the archive in the `Mods` folder of your Vintage Story data profile.
   Multiplayer requires the **same Zornhau version on client and server**.
3. Remove older Zornhau archives and the former `warbandcombat` archive.
   Keep only one version, then fully restart the game or server.

Zornhau works **standalone** and does not require Combat Overhaul or Armory at runtime.
For the optional item, armor and ranged-weapon setup, use these pinned versions:

| Package | Version |
| --- | --- |
| CO Fork | 0.18.8 |
| Armory Fork | 1.11.3 |
| Overhaullib Legacy Compat | 1.2.14 |
| JSONPatchesLib | 1.5.9 |
| VSImgui | 1.2.7 |

See [integration instructions](compat/README.md) and the
[pinned download manifest](compat/overhaul-modpack.json). Install the matching setup on
both sides. Do not combine it with original CO/Armory releases or old Reanimated patches.

[Natural Motion](natural-motion/README.md) is a separate optional **client-only** mod.
It improves locomotion, step timing and held-weapon stability. It is built as its own
archive and is not bundled with Zornhau.

## Controls

| Input | Action |
| --- | --- |
| Mouse movement before pressing a button | Select direction: up = overhead, down = thrust, sideways = lateral attack or guard |
| Hold LMB, then release | Prepare and release an attack; a short click still completes the minimum windup |
| Hold RMB | Directional weapon block, or frontal shield guard |
| RMB during a windup | Feint |
| Shift | Normal tool interactions where applicable |
| Shift + RMB with a knife | Harvest a corpse |
| K | Toggle standalone combat where permitted |
| F8 | Toggle hit diagnostics and collider display |

Direction is locked when the attack or block button is pressed. To parry a lateral
attack, select the opposite side relative to the attacker's screen.
Knives and axes retain normal block and corpse interactions.

Directional melee is mandatory against players in PvP-enabled worlds; K and Shift
cannot bypass it. With CO installed, it also applies to PvE. Walking and jumping stay
available in combat. Sprint is disabled during windup/swing and for 1.5 seconds after
contact or the end of a miss.

## Status and limitations

- Mobs do not have directional combat AI; their attacks use the overhead direction
  for weapon parries.
- Weapons do not parry projectiles. Projectile shield defense and mounted combat
  are not supported Zornhau features in this release.
- The server validates client contact reports but does not independently replay the
  full animated blade trajectory. See [netcode details](docs/NETCODE.md).
- Automated checks passed; live multiplayer duels, armor matchups and load testing
  still need the recorded checks in the [release checklist](docs/RELEASE-CHECKLIST.md).

## Documentation and development

- [Documentation index](docs/README.md): combat rules, balance tables, netcode and fatalities.
- [Build, test and development guide](docs/DEVELOPMENT.md).
- [Animation editor guide](editor/README.md).
- [Changelog](CHANGELOG.md).

Report bugs in [GitHub Issues](https://github.com/konsaidin/Zornhau/issues). Include game
and mod versions, weapon/grip, camera mode, reproduction steps and relevant logs. For
contact problems, F8 diagnostics help identify the attack and server response. Remove
account credentials and private server information before attaching logs.

## Credits and licensing

No project-wide license has been selected for original Zornhau code.
The embedded Combat Overhaul and Reanimated components retain their MIT licenses
and attribution: [Overhaul core](third-party/overhaul-core/README.md),
[Reanimated](third-party/reanimated/README.md).
Vintage Story and the optional external modpack are not included in this repository's
release archive.
