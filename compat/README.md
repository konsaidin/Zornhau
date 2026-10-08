# Combat Overhaul / Armory integration

Optional integration for Zornhau 0.8.3. Zornhau also works standalone.

Target: Vintage Story 1.22.7. The original CO release targets older game versions;
this pack uses [CO Fork](https://mods.vintagestory.at/show/mod/48376) 0.18.8,
Overhaullib Legacy Compat 1.2.14, JSONPatchesLib 1.5.9, VSImgui 1.2.7 and
[Armory Fork](https://mods.vintagestory.at/armoryfork) 1.11.3.
`overhaul-modpack.json` pins downloads and SHA-256 checksums. Fetch with
`python3 scripts/fetch-overhaul.py`. Install the five packages and the matching
Zornhau archive on clients and server. Do not also install original CO,
original Armory or the old Reanimated mod's patches.

Zornhau owns four directions, held windups, feints, directional blocks, cooldowns
and the 50 ms contact window. Native CO melee attack/block input and native block
packets are suppressed while Zornhau owns the weapon. Changing weapon, grip mode
or offhand invalidates the attack without removing recovery. CO supplies weapon
statistics, armor and animated blade collider sweeps against entity colliders;
ranged weapons retain their native controls. Vanilla axes/knives retain tool and
corpse interactions. Armory weapons are combat items, not replacements for every
vanilla tool action.

Reanimated 1.0.0's 23 MIT-licensed clips are embedded with attribution. They are
split into held windup, swing and recovery and retimed to our server timings;
native attack callbacks and camera effects are removed. Families/directions not
covered by those clips use native CO clips or authored Zornhau fallback poses.
Two-handed stances use only two-handed attack/guard clips: exact native direction,
matching family, then a polearm/sword donor. Missing pole directions are adapted
from poleaxe clips; the embedded Reanimated greatsword provides coverage when
Armory is absent. Donor grips still need visual checking on different models.
Body-only vanilla clips accompany CO arm/item animation. Third-person phase codes
are registered on every client for the native CO animation network.

Physical contacts currently use CO's client-generated collider reports. The
server checks the authorized window, held weapon/mode/offhand, permitted damage
profile, reach, contact proximity, terrain and PvP permissions, and consumes only
one contact. It overwrites client damage/armor-piercing/knockback values. It does
not independently replay the entire animated mesh; this is not complete anti-cheat
or lag compensation. Vanilla weapons without CO melee stats now use Zornhau's
embedded MIT-ported CO composer, hand/line transforms and animated body
colliders, including terrain checks. These work without the five external runtime packages. Standalone
contact packets carry an exact attack ID and blade geometry; damage, radius and
timing come from the server's held item and Fighter. The server validates geometry,
reach, target model envelope/recent positions, terrain and permissions, but does
not independently replay the client's animated pose. Actual physical damage
interrupts the victim's melee attack, item use and bow draw. Existing healing
ticks continue. Since 0.8.3, both contact protocols also carry character/model
rotation; server movement packets and proofs share a per-strike angular budget.
Clients and server must use the updated protocol together.

Build: `bash scripts/build.sh` (saved editor poses are imported by default).
The optional bridge is loaded only when `combatoverhaulfork` exists. Standalone
Zornhau installs do not need the five external runtime packages.

Embedded source provenance and adaptations: [overhaul-core](../third-party/overhaul-core/README.md).
