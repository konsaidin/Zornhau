# Embedded Combat Overhaul core

Source: https://github.com/Alodh1/Overhaullib-Legacy
Revision: `9bb564978967914c2d07fb13a60e5cc1749bb4ee` (engine of the pinned
Overhaullib Legacy Compat 1.2.14 package). Copyright (c) 2024 Maltiez, MIT;
`LICENSE` is copied verbatim and included in the distributed archive.

The following source files are adapted into `src/Native` under the separate
`Zornhau.Native` namespace:

- `source/Animations/AnimationSystem/{Animation,Frames,Animator,Composer,EasingFunctions}.cs`
- `source/Colliders/{EntityCollider,WeaponCollider,CollidersTools}.cs`

The interpolation, pose application, composition, line transformation and
oriented body collider calculations run in Zornhau.dll. There is no
runtime dependency on Combat Overhaul, Overhaullib, ImGui or JSONPatchesLib for
this path. Native calls are driven by Zornhau's authorized combat phases.
Editor/ImGui code and optimized ExtendedElementPose overloads are omitted.
Native sound/particle services are removed: our existing server-confirmed
feedback owns sound, particles and damage, without running native attack callbacks.

`NativePhaseAnimations` adapts our existing phase splitter to the embedded
animation types. `StandaloneAnimations` drives its composer from Zornhau state,
uses the already licensed Reanimated clips and saved author clips, keeps
continuous recovery/ready poses and applies only arm/item-anchor bones before
joint matrices are calculated. Saved body impact, camera and eye anchors remain
under the game's existing animation system. Remote clients receive phase
snapshots in watched attributes instead of replaying vanilla arm attacks.

`NativeBodyColliders` adapts the temporal subdivision algorithm from
`source/Colliders/CollidersEntityBehavior.cs`, including both sweep endpoints.
`assets/zornhau/config/native-colliders.json` consolidates its MIT-licensed
38 entity collider configurations from `resources/assets/overhaulliblegacycompat/patches/colliders/`.

Adaptations for VS 1.22.7 include camera-relative non-shadow render snapshots,
dimension-aware origins, engine rest matrices for parent pivots/scale, finite
zero-length contacts, and exact current endpoint sampling. Engine rest matrices
replace the upstream mix of pixel parent offsets and block-space vertices.
Native body collision math is used for animated parts; custom/unrendered targets
have a collision-box fallback. Vanilla weapon damage segments are generated
from their own item meshes, excluding grips and fixtures, and use the native
line/hand transform pipeline and the CO default minimum contact radius.

This is a selective source port, not a redistributed copy of the complete CO
modpack. CO weapon assets, armor/inventory/ranged systems and Armory items are
provided by the optional unchanged external packages. Their optional adapter
continues to use their own runtime engine without installing this embedded
animation path on those weapons. Reanimated attribution is separate in
`third-party/reanimated`.
