#!/usr/bin/env python3
"""Generate additive JSON patches for the vanilla seraph skeleton (VS 1.22.7)."""
import copy
import json
from pathlib import Path
from animation_geometry import align_blade, align_forearm, wrist_path

ROOT = Path(__file__).resolve().parents[1]

def pose(upper, lower, anchor=(0, 0, 0)):
    def rotation(values):
        return dict(zip(('rotationX', 'rotationY', 'rotationZ'), values))
    arm = rotation(upper)
    # Shoulder translations break the joint with the torso in third person.
    arm.update(zip(('offsetX', 'offsetY', 'offsetZ'), (0, 0, 0)))
    return {'UpperArmR': arm, 'LowerArmR': rotation(lower), 'ItemAnchor': rotation(anchor)}

# Screen-relative directions. Leg animations remain free for locomotion.
windups = {
    'overhead': pose((-35, 0, -135), (-25, 0, -60)),
    'right': pose((0, -35, -75), (-10, -25, -70)),
    'left': pose((0, 35, -90), (0, 0, -85)),
    'thrust': pose((0, 60, 10), (0, 0, -125)),
}
ends = {
    'overhead': pose((10, 0, -40), (-20, 0, -15)),
    'right': pose((0, 35, -70), (0, 0, -25)),
    'left': pose((0, -35, -70), (0, 0, -25)),
    'thrust': pose((0, 15, -85), (0, 0, -5)),
}
contacts = {
    'right': pose((0, 15, -80), (0, 0, -10)),
    'left': pose((0, 15, -80), (0, 0, -10)),
    'thrust': ends['thrust'],
    'overhead': ends['overhead'],
}
approaches = {
    'right': pose((0, -15, -75), (0, 0, -35)),
    'left': pose((0, 30, -85), (0, 0, -35)),
    'thrust': pose((0, 25, -45), (0, 0, -55)),
}
guards = {
    'overhead': pose((-25, 0, -105), (-25, 0, -70), (0, 0, 70)),
    'right': pose((0, -45, -55), (-10, 10, -70), (0, 0, -30)),
    'left': pose((0, 50, -55), (-10, 40, -70), (0, 0, 35)),
    'thrust': pose((15, 10, -45), (-10, 0, -50), (-30, 0, 0)),
}

body_windups = {
    'overhead': (-8, -5, -2),
    'right': (2, -18, -4),
    'left': (2, 18, 4),
    'thrust': (3, -8, -2),
}
body_hits = {
    'overhead': (10, 6, 3),
    'right': (-5, 20, 5),
    'left': (-5, -20, -5),
    'thrust': (-6, 0, 0),
}

def with_body(arm_pose, body):
    result = copy.deepcopy(arm_pose)
    axes = ('rotationX', 'rotationY', 'rotationZ')
    result['UpperTorso'] = dict(zip(axes, body))
    # Counter-turn the neck and head so the fighter keeps looking at the target.
    counter = (-body[0] * .2, -body[1] * .35, -body[2] * .3)
    result['Neck'] = dict(zip(axes, counter))
    result['Head'] = dict(zip(axes, counter))
    return result

def camera_variant(elements):
    result = copy.deepcopy(elements)
    result['UpperArmR'].update(offsetX=1.3, offsetY=-2.7, offsetZ=-7.0)
    # Keep the impact visible in first person without a large camera sway.
    for name, strength in [('UpperTorso', .35), ('Neck', .25), ('Head', .25)]:
        for axis in ('rotationX', 'rotationY', 'rotationZ'):
            result[name][axis] *= strength
    return result

patches = []
contact_wrists = {}
for action in ('windup', 'swing', 'block', 'recovery'):
    for direction in windups:
        for fp in (False, True):
            code = f'warband-{action}-{direction}' + ('-fp' if fp else '')
            windup = with_body(windups[direction], body_windups[direction])
            end = with_body(ends[direction], body_hits[direction])
            contact_body = (-4, 0, 0) if direction in ('left', 'right') else body_hits[direction]
            contact = with_body(contacts[direction], contact_body)
            approach_body = {'right': (-1, -8, 0), 'left': (-1, 8, 0), 'thrust': (-1.5, -4, -1)}
            approach = with_body(approaches.get(direction, windups[direction]),
                                 approach_body.get(direction, body_windups[direction]))
            guard = with_body(guards[direction], tuple(v * .25 for v in body_windups[direction]))
            impact = with_body(ends[direction], tuple(v * 1.2 for v in body_hits[direction]))
            settled = with_body(pose((8, -2, -17), (-8, 0, -28)), (0, 0, 0))
            if fp:
                windup, end, contact, approach, guard, impact, settled = map(
                    camera_variant, (windup, end, contact, approach, guard, impact, settled))
            if direction in ('left', 'right', 'thrust'):
                # Solve the wrist from the complete arm and torso rotation, including
                # the vanilla grip. Rolling only one Euler axis made cuts strike flat.
                reverse_edge = direction == 'left'
                if direction in ('left', 'right'):
                    align_forearm(windup, left=reverse_edge)
                else:
                    align_blade(windup, 0)
                bank = -20 if reverse_edge else 0
                align_blade(approach, {'right': -35, 'left': 35, 'thrust': 0}[direction], reverse_edge, bank)
                align_blade(contact, 0, reverse_edge, bank)
                follow_yaw = {'right': 70, 'left': -70, 'thrust': 0}[direction]
                align_blade(end, follow_yaw, reverse_edge, bank)
                align_blade(impact, follow_yaw, reverse_edge, bank)
                if direction in ('left', 'right'):
                    # Choose one path for windup, swing AND recovery. Normalizing
                    # separate clips sent the recovery endpoint to -360 degrees.
                    wrist_path((windup, approach, contact, end, impact, settled))
                    if direction == 'right':
                        # Cock the held wrist after choosing the existing strike
                        # branches, preserving the working swing and recovery.
                        align_forearm(windup, wrist_back=20)
            begin = guard if action == 'block' else contact if action == 'recovery' else windup
            frames = [{'frame': 0, 'elements': begin}]
            if action == 'swing':
                if direction == 'right':
                    # Leave the raised chamber before starting the horizontal cut.
                    # Keep the blade outside the face while the elbow lowers.
                    chamber_exit = with_body(pose((0, -35, -75), (0, 0, -15)), (-1, -8, 0))
                    if fp:
                        chamber_exit = camera_variant(chamber_exit)
                    align_blade(chamber_exit, -75)
                    frames.append({'frame': 1, 'elements': chamber_exit})
                frames.append({'frame': 2, 'elements': approach})
                frames.append({'frame': 4, 'elements': contact})
                frames.append({'frame': 6, 'elements': end})
                frames.append({'frame': 8, 'elements': copy.deepcopy(end)})
            elif action == 'recovery':
                frames.append({'frame': 2, 'elements': impact})
                frames.append({'frame': 10, 'elements': settled})
                frames.append({'frame': 14, 'elements': copy.deepcopy(settled)})
            else: frames.append({'frame': 14, 'elements': copy.deepcopy(begin)})
            if action == 'recovery' and direction not in ('left', 'right'):
                # Preserve the same Euler winding across the animation transition,
                # not just within each clip. Equivalent +/-360 poses blend badly.
                reference = contact_wrists[(direction, fp)]
                first = frames[0]['elements']['ItemAnchor']
                for axis in ('rotationX', 'rotationY', 'rotationZ'):
                    while first[axis] - reference[axis] > 180: first[axis] -= 360
                    while first[axis] - reference[axis] < -180: first[axis] += 360
            # Keep wrist Euler angles continuous across +/-180 to prevent a full spin.
            for previous, current in ([] if direction in ('left', 'right') else zip(frames, frames[1:])):
                for axis in ('rotationX', 'rotationY', 'rotationZ'):
                    prior = previous['elements']['ItemAnchor'][axis]
                    value = current['elements']['ItemAnchor'][axis]
                    while value - prior > 180: value -= 360
                    while value - prior < -180: value += 360
                    current['elements']['ItemAnchor'][axis] = value
            if action == 'swing':
                contact_frame = next(frame for frame in frames if frame['frame'] == 4)
                contact_wrists[(direction, fp)] = copy.deepcopy(contact_frame['elements']['ItemAnchor'])
            animation = {
                'name': code, 'code': code, 'quantityframes': 15,
                'onActivityStopped': 'EaseOut',
                'onAnimationEnd': 'EaseOut' if action in ('swing', 'recovery') else 'Repeat',
                'keyframes': frames,
            }
            for shape in ('seraph', 'seraph-faceless', 'seraph-hairless'):
                patches.append({'op': 'add', 'file': f'game:shapes/entity/humanoid/{shape}.json',
                                'path': '/animations/-', 'value': animation})
            metadata = {
                'code': code, 'animation': code, 'blendMode': 'Average', 'weight': .001,
                'easeInSpeed': 20, 'easeOutSpeed': 12,
                'elementWeight': {'UpperArmR': 30, 'LowerArmR': 30, 'ItemAnchor': 30,
                                  'UpperTorso': 8, 'Neck': 3, 'Head': 3},
                'elementBlendMode': {name: 'Average' for name in
                                     ('UpperArmR', 'LowerArmR', 'ItemAnchor', 'UpperTorso', 'Neck', 'Head')},
            }
            patches.append({'op': 'add', 'file': 'game:entities/humanoid/player.json',
                            'path': '/client/animations/-', 'side': 'Server', 'value': metadata})
output = ROOT / 'assets/warbandcombat/patches/animations.json'
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(patches, indent=2) + '\n')
print(f'Generated {len(patches)} animation patches')
