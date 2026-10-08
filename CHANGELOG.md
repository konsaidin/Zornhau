# Changelog

## 0.8.3

- Limit actual character and body turning during released attacks, including movement
  in the free F5 camera. Keep camera orbit and jumping available.
- Validate rotation through movement and contact packets on the server; reject stale,
  duplicate and implausible reports without removing attack recovery.
- Extend the contact protocol with character angles; update clients and server together.
- Import saved authored animation poses in the default build and check SDK prerequisites.

## 0.8.2

- Apply saved immersive first-person settings on world entry and rebuild the model
  on the first camera transition. Remove the need to toggle the setting manually.

## 0.8.1

- Fit vanilla and Armory shields to the back using their model orientation and dimensions,
  keeping the top edge below the neck and matching shadow placement.

## 0.8.0

- Rename the mod, assembly, namespace, assets, settings and network domain to Zornhau.
- Tune weapon penetration against existing CO/Armory armor, preserving raw damage and
  attack cadence against unarmored targets.
- Interrupt healing-item use and bow drawing on damaging physical hits; preserve
  healing already applied and avoid consuming interrupted bandages/arrows.
- Automatically stow shields when a weapon requires both hands, disabling their protection.
- Fix Armory dagger slash damage while retaining piercing thrusts.

## 0.7.9

- Preserve authored one-handed weapon rest grips in third person.
- Coordinate held-weapon poses with Natural Motion to prevent locomotion arm swings
  from disturbing one- and two-handed grips.

Earlier development is recorded in Git history.

## Natural Motion 0.1.11

- Recognize the renamed Zornhau combat states and asset domains.
- Retain the held-weapon stability and step/arm timing fixes from 0.1.8–0.1.10.
