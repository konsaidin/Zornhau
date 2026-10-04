# Combat Overhaul / Armory integration (0.3.1)

Target: Vintage Story 1.22.7. The original CO release targets older game versions;
this pack uses [CO Fork](https://mods.vintagestory.at/show/mod/48376) 0.18.8,
Overhaullib Legacy Compat 1.2.14, JSONPatchesLib 1.5.9, VSImgui 1.2.7 and
[Armory Fork](https://mods.vintagestory.at/armoryfork) 1.11.3.
`overhaul-modpack.json` pins downloads and SHA-256 checksums. Fetch with
`python3 scripts/fetch-overhaul.py`. Install the five packages and the matching
Warband Combat archive on clients and server. Do not also install original CO,
original Armory or the old Reanimated mod's patches.

Warband owns four directions, held windups, feints, directional blocks, cooldowns
and the 50 ms contact window. Native CO melee attack/block input and native block
packets are suppressed while Warband owns the weapon. Changing weapon, grip mode
or offhand invalidates the attack without removing recovery. CO supplies weapon
statistics, armor and animated blade collider sweeps against entity colliders;
ranged weapons retain their native controls. Vanilla axes/knives retain tool and
corpse interactions. Armory weapons are combat items, not replacements for every
vanilla tool action.

Reanimated 1.0.0's 23 MIT-licensed clips are embedded with attribution. They are
split into held windup, swing and recovery and retimed to our server timings;
native attack callbacks and camera effects are removed. Families/directions not
covered by those clips use native CO clips or authored Warband fallback poses.
Body-only vanilla clips accompany CO arm/item animation. Third-person phase codes
are registered on every client for the native CO animation network.

Physical contacts currently use CO's client-generated collider reports. The
server checks the authorized window, held weapon/mode/offhand, permitted damage
profile, reach, contact proximity, terrain and PvP permissions, and consumes only
one contact. It overwrites client damage/armor-piercing/knockback values. It does
not independently replay the entire animated mesh; this is not complete anti-cheat
or lag compensation. Vanilla weapons without CO melee stats still use the
existing server ray contact check. Successful-hit attack interruption remains a
separate requested task in PVP-BALANCE.md.

Build: `VS_EDITOR_MODEL=editor/seraph-combat-completed.json bash scripts/build.sh`.
The optional bridge is loaded only when `combatoverhaulfork` exists. Standalone
Warband installs do not need the five external runtime packages.
