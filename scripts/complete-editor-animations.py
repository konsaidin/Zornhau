#!/usr/bin/env python3
"""Complete held poses and blade paths from the user's saved starting frames."""
import copy
import argparse
import json
import math
import time
from pathlib import Path
from animation_geometry import arm_rotation, euler, multiply, rotation, transpose, wrist_path, GRIP

ROOT = Path(__file__).resolve().parents[1]
AXES = ('rotationX', 'rotationY', 'rotationZ')

def blade(elements):
    return multiply(multiply(arm_rotation(elements), rotation([elements['ItemAnchor'][a] for a in AXES])), GRIP)

def quaternion(matrix):
    trace = sum(matrix[i][i] for i in range(3))
    if trace > 0:
        s = math.sqrt(trace + 1) * 2
        return [s / 4, (matrix[2][1] - matrix[1][2]) / s,
                (matrix[0][2] - matrix[2][0]) / s, (matrix[1][0] - matrix[0][1]) / s]
    i = max(range(3), key=lambda index: matrix[index][index])
    j, k = (i + 1) % 3, (i + 2) % 3
    s = math.sqrt(1 + matrix[i][i] - matrix[j][j] - matrix[k][k]) * 2
    q = [0, 0, 0, 0]
    q[0] = (matrix[k][j] - matrix[j][k]) / s
    q[i + 1] = s / 4
    q[j + 1] = (matrix[j][i] + matrix[i][j]) / s
    q[k + 1] = (matrix[k][i] + matrix[i][k]) / s
    return q

def slerp(first, last, t):
    dot = sum(a * b for a, b in zip(first, last))
    if dot < 0:
        last = [-v for v in last]
        dot = -dot
    if dot > .9995:
        q = [a + (b - a) * t for a, b in zip(first, last)]
    else:
        angle = math.acos(max(-1, min(1, dot)))
        a, b = math.sin((1 - t) * angle) / math.sin(angle), math.sin(t * angle) / math.sin(angle)
        q = [a * x + b * y for x, y in zip(first, last)]
    norm = math.sqrt(sum(v * v for v in q))
    w, x, y, z = [v / norm for v in q]
    return [[1 - 2*y*y - 2*z*z, 2*x*y - 2*z*w, 2*x*z + 2*y*w],
            [2*x*y + 2*z*w, 1 - 2*x*x - 2*z*z, 2*y*z - 2*x*w],
            [2*x*z - 2*y*w, 2*y*z + 2*x*w, 1 - 2*x*x - 2*y*y]]

def set_blade(elements, target):
    wrist = multiply(multiply(transpose(arm_rotation(elements)), target), transpose(GRIP))
    elements['ItemAnchor'] = dict(zip(AXES, euler(wrist)))

def dense_frames(frames):
    """Sample blade orientation in model space, avoiding coupled Euler tumbles."""
    result = []
    for first, last in zip(frames, frames[1:]):
        start, end = first['frame'], last['frame']
        a, b = first['elements'], last['elements']
        qa, qb = quaternion(blade(a)), quaternion(blade(b))
        for frame in range(start, end):
            t = (frame - start) / (end - start)
            elements = {}
            for name in a.keys() | b.keys():
                av, bv = a.get(name, {}), b.get(name, {})
                elements[name] = {key: av.get(key, 0) + (bv.get(key, 0) - av.get(key, 0)) * t
                                  for key in av.keys() | bv.keys()}
            if frame == start:
                elements = copy.deepcopy(a)
            else:
                set_blade(elements, slerp(qa, qb, t))
            result.append({'frame': frame, 'elements': elements})
    result.append(copy.deepcopy(frames[-1]))
    return result

def first_person(elements):
    result = copy.deepcopy(elements)
    result['UpperArmR'].update(offsetX=1.3, offsetY=-2.7, offsetZ=-7)
    for name, strength in [('UpperTorso', .35), ('Neck', .25), ('Head', .25)]:
        for axis in AXES:
            result[name][axis] *= strength
    set_blade(result, blade(elements))
    return result

def complete(model):
    clips = {a['code']: a for a in model['animations']}
    for direction in ('overhead', 'right', 'thrust', 'left'):
        guard = clips[f'zornhau-block-{direction}']
        pose = copy.deepcopy(guard['keyframes'][0]['elements'])
        guard['keyframes'] = [{'frame': f, 'elements': copy.deepcopy(pose)} for f in (0, 14)]
        guard_fp = clips[guard['code'] + '-fp']
        guard_fp['keyframes'] = [{'frame': f, 'elements': first_person(pose)} for f in (0, 14)]
    for direction in ('left', 'right'):
        swing = clips[f'zornhau-swing-{direction}']
        keys = copy.deepcopy(swing['keyframes'])
        if direction == 'right':
            # Give the authored shoulder release two frames instead of one.
            for key in keys:
                if key['frame'] in (1, 2):
                    key['frame'] += 1
        swing['keyframes'] = dense_frames(keys)
        recovery = clips[f'zornhau-recovery-{direction}']
        contact = next(f for f in swing['keyframes'] if f['frame'] == 4)
        recovery['keyframes'][0]['elements'] = copy.deepcopy(contact['elements'])
        recovery['keyframes'] = dense_frames(recovery['keyframes'])
        path = [f['elements'] for f in swing['keyframes']]
        path += [f['elements'] for f in recovery['keyframes'][1:]]
        wrist_path(path, end_at_zero=True)
        recovery['keyframes'][0]['elements'] = copy.deepcopy(contact['elements'])
        windup = clips[f'zornhau-windup-{direction}']
        windup['keyframes'] = [{'frame': f, 'elements': copy.deepcopy(swing['keyframes'][0]['elements'])}
                              for f in (0, 14)]
        for clip in (windup, swing, recovery):
            fp = clips[clip['code'] + '-fp']
            fp['keyframes'] = [{'frame': f['frame'], 'elements': first_person(f['elements'])}
                              for f in clip['keyframes']]
        fp_swing, fp_recovery = clips[swing['code'] + '-fp'], clips[recovery['code'] + '-fp']
        fp_path = [f['elements'] for f in fp_swing['keyframes']]
        fp_path += [f['elements'] for f in fp_recovery['keyframes'][1:]]
        wrist_path(fp_path, end_at_zero=True)
        fp_contact = next(f for f in fp_swing['keyframes'] if f['frame'] == 4)
        fp_recovery['keyframes'][0]['elements'] = copy.deepcopy(fp_contact['elements'])
        fp_windup = clips[windup['code'] + '-fp']
        fp_windup['keyframes'] = [{'frame': f, 'elements': copy.deepcopy(fp_swing['keyframes'][0]['elements'])}
                                 for f in (0, 14)]
    return model

def complete_thrust(model):
    """Connect the authored chamber to its existing extended contact pose."""
    clips = {a['code']: a for a in model['animations']}
    swing = clips['zornhau-swing-thrust']
    first = copy.deepcopy(swing['keyframes'][0])
    last = copy.deepcopy(swing['keyframes'][-1])
    if first['frame'] != 0 or last['frame'] < 4:
        raise ValueError('Thrust needs a starting frame 0 and an endpoint at/after contact frame 4')
    # Keep contact at frame 4 (the server's strike time). Frames after contact
    # hold the authored endpoint while the late-contact window remains open.
    contact = copy.deepcopy(last)
    contact['frame'] = 4
    anchors = [first, contact] + ([last] if last['frame'] > 4 else [])
    swing['keyframes'] = dense_frames(anchors)
    assert swing['keyframes'][0] == first
    assert swing['keyframes'][-1] == last

    windup = clips['zornhau-windup-thrust']
    windup['keyframes'] = [{'frame': f, 'elements': copy.deepcopy(first['elements'])}
                          for f in (0, windup['quantityframes'] - 1)]
    recovery = clips['zornhau-recovery-thrust']
    recovery['keyframes'][0]['elements'] = copy.deepcopy(contact['elements'])

    fp_swing = clips['zornhau-swing-thrust-fp']
    fp_swing['keyframes'] = [{'frame': f['frame'], 'elements': first_person(f['elements'])}
                            for f in swing['keyframes']]
    fp_recovery = clips['zornhau-recovery-thrust-fp']
    fp_recovery['keyframes'] = dense_frames([
        {'frame': f['frame'], 'elements': first_person(f['elements'])}
        for f in recovery['keyframes']])
    # Choose one wrist winding through contact and return, ending at literal
    # zero rather than the old FP endpoint of 360 degrees.
    path = [f['elements'] for f in fp_swing['keyframes']]
    path += [f['elements'] for f in fp_recovery['keyframes'][1:]]
    wrist_path(path, end_at_zero=True)
    fp_contact = next(f for f in fp_swing['keyframes'] if f['frame'] == 4)
    fp_recovery['keyframes'][0]['elements'] = copy.deepcopy(fp_contact['elements'])
    fp_windup = clips['zornhau-windup-thrust-fp']
    fp_windup['keyframes'] = [
        {'frame': f, 'elements': copy.deepcopy(fp_swing['keyframes'][0]['elements'])}
        for f in (0, fp_windup['quantityframes'] - 1)]
    return model

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--thrust-only', action='store_true',
                        help='Complete the saved thrust in place, preserving both endpoints and backing up the model')
    args = parser.parse_args()
    source = ROOT / 'editor/seraph-combat.json'
    output = ROOT / 'editor/seraph-combat-completed.json'
    if args.thrust_only:
        saved = output.read_text()
        model = complete_thrust(json.loads(saved))
        backup = ROOT / '.artifacts/editor-backups' / f'seraph-combat-before-thrust-{time.time_ns()}.json'
        backup.parent.mkdir(parents=True, exist_ok=True)
        backup.write_text(saved)
        output.write_text(json.dumps(model, indent=2) + '\n')
        print(f'Completed thrust swing, held chamber and FP transitions: {output}; backup: {backup}')
    elif output.exists():
        raise SystemExit(f'Preserving existing completed edits: {output}')
    else:
        model = complete(json.loads(source.read_text()))
        output.write_text(json.dumps(model, indent=2) + '\n')
        print(f'Completed four held guards and both side swings: {output}')
