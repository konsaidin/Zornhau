#!/usr/bin/env python3
"""Package MIT Reanimated poses and joint-only fallback clips for the CO renderer."""
from copy import deepcopy
import json
from pathlib import Path

root = Path(__file__).resolve().parent.parent
assets = root / 'assets/warbandcombat'
poses = json.loads((root / 'third-party/reanimated/clips.json').read_text())
(assets / 'config').mkdir(exist_ok=True)
(assets / 'config/reanimated.json').write_text(json.dumps(poses, separators=(',', ':')) + '\n')
patches = json.loads((assets / 'patches/animations.json').read_text())
body = []
fallback = {}
for patch in patches:
    value = patch['value']
    code = value.get('code', '')
    if not code.startswith('warband-'):
        continue
    partial = deepcopy(patch)
    v = partial['value']
    v['code'] = code.replace('warband-', 'warband-body-', 1)
    if 'keyframes' in v:
        v['name'] = v['code']
        for frame in v['keyframes']:
            frame['elements'] = {k: pose for k, pose in frame['elements'].items()
                                 if k in ('UpperTorso', 'Neck', 'Head')}
        if not code.endswith('-fp') and patch['file'] == 'game:shapes/entity/humanoid/seraph.json':
            action = code.split('-')[1]
            frames = []
            length = max(1, value['quantityframes'] - 1)
            for frame in value['keyframes']:
                elements = {bone: [pose.get('offset' + axis, 0) for axis in 'XYZ']
                            + [pose.get('rotation' + axis, 0) for axis in 'XYZ']
                            for bone, pose in frame['elements'].items()
                            if bone not in ('UpperTorso', 'Neck', 'Head')}
                frames.append({'EasingTime': frame['frame'] / length * 1000,
                               'EasingFunction': 'Sin', 'PitchFollow': True, 'Elements': elements})
            fallback['warbandcombat:' + code] = {'Hold': True, 'PlayerKeyFrames': frames}
            if action == 'recovery':
                rest = deepcopy(frames[-1]); rest['EasingTime'] = 1000
                fallback['warbandcombat:' + code.replace('-recovery-', '-return-')] = {
                    'Hold': True, 'PlayerKeyFrames': [rest]}
    else:
        v['animation'] = v['code']
        for field in ('elementWeight', 'elementBlendMode'):
            v[field] = {k: pose for k, pose in v.get(field, {}).items() if k in ('UpperTorso', 'Neck', 'Head')}
    body.append(partial)
(assets / 'patches/overhaul-body.json').write_text(json.dumps(body, separators=(',', ':')) + '\n')
(assets / 'config/overhaul-fallback.json').write_text(json.dumps(fallback, separators=(',', ':')) + '\n')
print(f'Packaged {len(poses)} Reanimated clips, {len(fallback)} fallbacks, {len(body)} body patches')
