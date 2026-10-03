"""Wrist alignment for the VS 1.22 seraph skeleton and vanilla blade grip."""
import math

def multiply(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

def transpose(matrix):
    return [list(row) for row in zip(*matrix)]

def rotation(angles):
    # Same Rx * Ry * Rz order as ShapeElement.GetLocalTransformMatrix(version=0).
    x, y, z = map(math.radians, angles)
    sx, cx = math.sin(x), math.cos(x)
    sy, cy = math.sin(y), math.cos(y)
    sz, cz = math.sin(z), math.cos(z)
    return [[cy * cz, -cy * sz, sy],
            [sx * sy * cz + cx * sz, cx * cz - sx * sy * sz, -sx * cy],
            [-cx * sy * cz + sx * sz, sx * cz + cx * sy * sz, cx * cy]]

def euler(matrix):
    y = math.asin(max(-1, min(1, matrix[0][2])))
    if abs(math.cos(y)) > .00001:
        x = math.atan2(-matrix[1][2], matrix[2][2])
        z = math.atan2(-matrix[0][1], matrix[0][0])
    else:
        x = math.atan2(matrix[2][1], matrix[1][1])
        z = 0
    return tuple(round(math.degrees(value), 3) for value in (x, y, z))

def arm_rotation(elements):
    parent = rotation((0, 0, 3))  # LowerTorso bind pose.
    for name, bind in [('UpperTorso', (0, 0, -6)),
                       ('UpperArmR', (10, 0, 7)), ('LowerArmR', (-8, 0, -6))]:
        values = elements.get(name, {})
        angles = [base + values.get('rotation' + axis, 0) for axis, base in zip('XYZ', bind)]
        parent = multiply(parent, rotation(angles))
    return parent

GRIP = rotation((85, -180, 2))

def align_blade(elements, yaw, reverse_edge=False, bank=0):
    # Blade length is local +X; its broad face has a local +Y normal.
    # Keep the tip in the horizontal cut plane and the face normal vertical.
    # The seraph faces -X (the Back attachment is on +X). In RenderItem,
    # attachment and held-item Euler angles are ADDED before RotateXYZ.
    # +Z is the fighter's left; positive yaw points toward that side.
    angle = math.radians(yaw)
    target = [[-math.cos(angle), 0, -math.sin(angle)],
              [0, 1, 0], [math.sin(angle), 0, -math.cos(angle)]]
    # Tilt the complete cut plane, so the edge follows a diagonal cut too.
    target = multiply(rotation((bank, 0, 0)), target)
    # The falx has one cutting edge. Reversing the cut also reverses its roll.
    if reverse_edge:
        target = multiply(target, rotation((180, 0, 0)))
    wrist = multiply(multiply(transpose(arm_rotation(elements)), target), transpose(GRIP))
    elements['ItemAnchor'] = dict(zip(('rotationX', 'rotationY', 'rotationZ'), euler(wrist)))
    return elements

def align_forearm(elements, left=False, wrist_back=0):
    """Align the held blade with the forearm, allowing a small wrist cock."""
    parent = arm_rotation(elements)
    # LowerArm origin -> ItemAnchor in the unrotated lower-arm coordinates.
    local = (.3, -7.5, 0)
    def unit(vector):
        length = math.sqrt(sum(value * value for value in vector))
        return [value / length for value in vector]
    tip = unit([sum(parent[i][j] * local[j] for j in range(3)) for i in range(3)])
    if wrist_back:
        # Model +X is behind the fighter. Cock only the blade in the wrist;
        # leave the successful right shoulder and elbow positions unchanged.
        backward = unit([float(i == 0) - tip[0] * tip[i] for i in range(3)])
        angle = math.radians(wrist_back)
        tip = [math.cos(angle) * tip[i] + math.sin(angle) * backward[i] for i in range(3)]
    cut = (-1, -.3, -1 if left else 1)
    along = sum(tip[i] * cut[i] for i in range(3))
    edge = unit([cut[i] - along * tip[i] for i in range(3)])
    face = [tip[1] * edge[2] - tip[2] * edge[1],
            tip[2] * edge[0] - tip[0] * edge[2],
            tip[0] * edge[1] - tip[1] * edge[0]]
    target = [[tip[i], face[i], -edge[i]] for i in range(3)]
    wrist = multiply(multiply(transpose(parent), target), transpose(GRIP))
    elements['ItemAnchor'] = dict(zip(('rotationX', 'rotationY', 'rotationZ'), euler(wrist)))
    return elements

def wrist_path(elements, end_at_zero=False):
    """Select equivalent XYZ branches for the complete attack and return to zero.

    The engine blends Euler values with idle. An endpoint of -360 is physically
    identical to zero but spins once during that blend. Keep all angles within
    +/-180, choosing both valid XYZ branches across the entire motion.
    """
    axes = ('rotationX', 'rotationY', 'rotationZ')
    candidates = []
    for element in elements:
        x, y, z = (element['ItemAnchor'][axis] for axis in axes)
        branches = [(x, y, z), (x + 180, 180 - y, z + 180)]
        candidates.append([tuple((v + 180) % 360 - 180 for v in branch) for branch in branches])
    if end_at_zero:
        if any(abs(value) > .001 for value in candidates[-1][0]):
            raise ValueError('The idle endpoint must have a zero wrist rotation')
        candidates[-1] = [(0, 0, 0)]
    def distance(a, b):
        return sum((x - y) ** 2 for x, y in zip(a, b))
    states = [(distance((0, 0, 0), branch), [branch]) for branch in candidates[0]]
    for branches in candidates[1:]:
        next_states = []
        for branch in branches:
            cost, path = min(states, key=lambda state: state[0] + distance(state[1][-1], branch))
            next_states.append((cost + distance(path[-1], branch), path + [branch]))
        states = next_states
    _, path = min(states, key=lambda state: state[0] + distance(state[1][-1], (0, 0, 0)))
    for element, angles in zip(elements, path):
        element['ItemAnchor'] = dict(zip(axes, angles))
