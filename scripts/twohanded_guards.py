"""Fit the free left arm to the saved right-hand guards on the actual seraph rig."""
from copy import deepcopy
import math
from animation_geometry import rotation, multiply, transpose, euler, GRIP


def add(a, b):
    return [x + y for x, y in zip(a, b)]


def sub(a, b):
    return [x - y for x, y in zip(a, b)]


def scale(a, amount):
    return [x * amount for x in a]


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0]]


def length(a):
    return math.sqrt(dot(a, a))


def unit(a):
    return scale(a, 1 / length(a))


def transform(matrix, value):
    return [dot(row, value) for row in matrix]


def aim(before, after):
    """Shortest rotation between two directions; all four guards are nondegenerate."""
    a, b = unit(before), unit(after)
    v = cross(a, b)
    cosine = max(-1, min(1, dot(a, b)))
    if cosine < -.999999:
        axis = unit(cross(a, [1, 0, 0] if abs(a[0]) < .9 else [0, 1, 0]))
        return [[2 * axis[i] * axis[j] - int(i == j) for j in range(3)] for i in range(3)]
    skew = [[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]]
    square = multiply(skew, skew)
    return [[int(i == j) + skew[i][j] + square[i][j] / (1 + cosine)
             for j in range(3)] for i in range(3)]


def twohanded_guard(clip, shape):
    bones = {}

    def read(elements):
        for bone in elements:
            bones[bone['name']] = bone
            read(bone.get('children', []))
    read(shape['elements'])

    def bind(name):
        return [bones[name].get('rotation' + axis, 0) for axis in 'XYZ']

    def local(name, values):
        bone = bones[name]
        r = rotation(add(bind(name), values[3:6]))
        # GetLocalTransformMatrix: T(offset + pivot) * R * T(from - pivot).
        t = add(add(values[:3], bone['rotationOrigin']),
                transform(r, sub(bone['from'], bone['rotationOrigin'])))
        return r, t

    result = deepcopy(clip)
    # One target blends from the actual current grip over the guard raise time,
    # including a cancelled attack. Never snap to an explicit zero-time frame.
    frame = deepcopy(clip['PlayerKeyFrames'][-1])
    frame['EasingTime'] = 1000
    elements = frame['Elements']
    ru, tu = local('UpperArmR', elements['UpperArmR'])
    rl, tl = local('LowerArmR', elements['LowerArmR'])
    ra, ta = local('ItemAnchor', elements['ItemAnchor'])
    right_lower = multiply(ru, rl)
    right_hand = add(tu, transform(ru, add(tl, transform(rl, ta))))
    weapon = multiply(multiply(right_lower, ra), GRIP)
    # A second fist just behind the right grip, toward the sword's pommel.
    target = sub(right_hand, scale([weapon[i][0] for i in range(3)], 2))
    shoulder = bones['UpperArmL']['rotationOrigin']
    upper_vector = sub(add(bones['UpperArmL']['from'], bones['LowerArmL']['rotationOrigin']), shoulder)
    lower_vector = sub(add(bones['LowerArmL']['from'], bones['ItemAnchorL']['from']),
                       bones['LowerArmL']['rotationOrigin'])
    upper_length, lower_length = length(upper_vector), length(lower_vector)
    toward_hand = sub(target, shoulder)
    distance = min(length(toward_hand), upper_length + lower_length - .35)
    direction = unit(toward_hand)
    target = add(shoulder, scale(direction, distance))
    along = (upper_length ** 2 + distance ** 2 - lower_length ** 2) / (2 * distance)
    height = math.sqrt(max(0, upper_length ** 2 - along ** 2))
    # The elbow stays forward and to the player's left, away from the torso.
    pole = [-1, -.6, .8]
    outward = unit(sub(pole, scale(direction, dot(pole, direction))))
    elbow = add(add(shoulder, scale(direction, along)), scale(outward, height))
    upper_bind = rotation(bind('UpperArmL'))
    upper = multiply(aim(transform(upper_bind, upper_vector), sub(elbow, shoulder)), upper_bind)
    lower_world = multiply(aim(transform(right_lower, lower_vector), sub(target, elbow)), right_lower)
    lower = multiply(transpose(upper), lower_world)

    def pose(matrix, name):
        angles = euler(matrix)
        # Equivalent Euler branch nearest the bind pose, avoiding a wrist/arm spin.
        branches = [angles, (angles[0] + 180, 180 - angles[1], angles[2] + 180)]
        offsets = [[round((v - base + 180) % 360 - 180, 3)
                    for v, base in zip(branch, bind(name))] for branch in branches]
        return [0, 0, 0] + min(offsets, key=lambda values: dot(values, values))

    elements['UpperArmL'] = pose(upper, 'UpperArmL')
    elements['LowerArmL'] = pose(lower, 'LowerArmL')
    elements['ItemAnchorL'] = [0, 0, 0, 0, 0, 0]
    result['PlayerKeyFrames'] = [frame]
    return result
