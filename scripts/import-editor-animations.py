#!/usr/bin/env python3
"""Import saved VSMC clips; the preview model and sword stay editor-only."""
import argparse
import copy
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('model', type=Path)
parser.add_argument('--output', type=Path, default=ROOT / 'assets/warbandcombat/patches/animations.json')
args = parser.parse_args()
model = json.loads(args.model.read_text())
patches = json.loads((ROOT / 'assets/warbandcombat/patches/animations.json').read_text())
known = {p['value']['code'] for p in patches if p['file'].endswith('/seraph.json')}
supported = {'UpperArmR', 'LowerArmR', 'ItemAnchor', 'UpperTorso', 'Neck', 'Head'}
clips = {}
for source in model.get('animations', []):
    code = source.get('code', source.get('name', '')).lower()
    if code not in known:
        continue
    if code in clips:
        raise SystemExit(f'Duplicate animation code: {code}')
    clip = copy.deepcopy(source)
    clip['code'] = code
    clip['version'] = int(clip.get('version', 0))
    length = clip['quantityframes'] = int(clip['quantityframes'])
    if length < 1 or clip['version'] != 0:
        raise SystemExit(f'{code}: keep animation version 0 and a positive frame count')
    seen = set()
    for frame in clip.get('keyframes', []):
        number = frame['frame'] = int(frame['frame'])
        if not 0 <= number < length or number in seen:
            raise SystemExit(f'{code}: duplicate or out-of-range keyframe {number}')
        seen.add(number)
        for name in list(frame['elements']):
            if name.startswith('PreviewSword'):
                del frame['elements'][name]
                continue
            if name not in supported:
                raise SystemExit(f'{code}: {name} has no combat blend weights; edit the arm, torso, neck or head')
            for key, value in frame['elements'][name].items():
                if not isinstance(value, (int, float)) or not math.isfinite(value):
                    raise SystemExit(f'{code}: invalid {name}.{key}')
    if not seen:
        raise SystemExit(f'{code}: animation has no keyframes')
    clip['keyframes'].sort(key=lambda frame: frame['frame'])
    clips[code] = clip
for patch in patches:
    if 'shapes/entity/humanoid/' in patch['file'] and patch['value']['code'] in clips:
        patch['value'] = copy.deepcopy(clips[patch['value']['code']])
args.output.write_text(json.dumps(patches, indent=2) + '\n')
print(f'Imported {len(clips)} editor clips; preview geometry excluded')
