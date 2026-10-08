# Documentation

Start with the [project README](../README.md) for installation and controls.
The detailed combat and balance references below are currently written in Russian;
setup and development guides are in English.

| Document | Contents |
| --- | --- |
| [Development](DEVELOPMENT.md) | Prerequisites, builds, tests, isolated profiles and repository layout |
| [Combat and PvP](PVP-BALANCE.md) | Combat rules, timing, protection, interruption and planned checks |
| [Weapon balance](WEAPON-BALANCE.md) | Generated weapon timing, damage and manufacturing tables |
| [Armor matchups](ARMOR-BALANCE.md) | Generated armor comparisons, penetration policy and calculation limits |
| [Balance research](BALANCE-RESEARCH.md) | Design rationale and sources behind weapon roles |
| [Netcode](NETCODE.md) | Prediction, reconciliation, server validation and current limits |
| [Fatalities](FATALITIES.md) | Triggers, effects, settings and training commands |
| [Release checklist](RELEASE-CHECKLIST.md) | Automated results and remaining live-game verification |
| [CO/Armory integration](../compat/README.md) | Optional pinned modpack and adapter behavior |
| [Animation editing](../editor/README.md) | Authored models and importing saved poses |
| [Natural Motion](../natural-motion/README.md) | Separate client mod, settings and compatibility |
| [Balance export tool](../tools/BalanceAudit/README.md) | Refreshing resolved engine catalogs |
| [Changelog](../CHANGELOG.md) | Recent release changes |

Paths in command examples are relative to the repository root unless stated otherwise.
`WEAPON-BALANCE.md` and `ARMOR-BALANCE.md` are generated: change
[`balance/policy.json`](../balance/policy.json), then rebuild. Do not edit table values by hand.
