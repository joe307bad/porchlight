# Traffic signals and stop signs for the towns' junctions, exported to
# Models/props/ as glTF for the model library (Editor/Library.cs):
#
#   blender --background --python unity/Tools~/streets/signals.py
#
#   traffic-signal   a mast-arm signal, as American towns hang them over the
#                    lanes: a pole on the kerb, an arm reaching out over the
#                    road with a brace under it, a three-lamp head hanging from
#                    the arm over the lane, and a second head on the pole for
#                    drivers stopped close to it. The lenses are dark: the game
#                    lights the one showing (Rendering/Signage.cs).
#   stop-sign        a stop sign on its post: a red octagon, white border and
#                    STOP in white block letters.
#
# Both stand with the post's foot at the origin, the arm reaching along -y and
# the faces looking along +x. The glTF exporter turns Blender's z-up into y-up
# (the front, -y, becoming +z), so in the game the arm reaches forwards (+z)
# and the faces look to the model's left (-x): Signage.cs turns each so the arm
# reaches over its lane and the faces look back down it at the drivers coming.
# Sizes are metres (a 5.6 m pole, a 4.2 m arm, a 0.8 m sign at 2 m); the game
# scales them. Signage.cs keeps the lenses' places (LENSES) in step with these.

import math
import os

import bmesh
import bpy
import mathutils

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Porchlight", "Models", "props"))

# The signal: where the heads hang, and their lenses (top to bottom: red, amber, green).
POLE_H = 5.6
ARM_Z = 5.3
ARM_LEN = 4.2
ARM_HEAD_Y = -3.6
ARM_HEAD_Z = 4.65
POLE_HEAD_X = 0.25
POLE_HEAD_Z = 3.2
HEAD_DEPTH, HEAD_WIDTH, HEAD_HEIGHT = 0.3, 0.38, 1.05
LENS_STEP = 0.33
LENS_R = 0.12


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def material(name, rgb, rough=0.6, metal=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    return m


def finish(name, bm, mat, smooth=False):
    me = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    for p in me.polygons:
        p.use_smooth = smooth
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    me.materials.append(mat)
    return ob


def box(name, centre, size, mat):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=size, verts=bm.verts)
    bmesh.ops.translate(bm, vec=centre, verts=bm.verts)
    return finish(name, bm, mat)


def rod(name, a, b, r1, mat, r2=None, segments=16):
    """A cylinder (or a cone, tapering to r2) from point a to point b."""
    a, b = mathutils.Vector(a), mathutils.Vector(b)
    axis = b - a
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=segments, radius1=r1, radius2=r1 if r2 is None else r2, depth=axis.length)
    # create_cone stands it on z, centred: turn z onto the axis and move its middle to the middle.
    rot = mathutils.Vector((0, 0, 1)).rotation_difference(axis.normalized()).to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=rot, verts=bm.verts)
    bmesh.ops.translate(bm, vec=(a + b) / 2, verts=bm.verts)
    return finish(name, bm, mat, smooth=True)


def disc_x(name, centre, r, depth, mat, segments=24):
    """A short cylinder facing +x (a lens)."""
    c = mathutils.Vector(centre)
    return rod(name, c - mathutils.Vector((depth / 2, 0, 0)), c + mathutils.Vector((depth / 2, 0, 0)), r, mat, segments=segments)


def visor(name, centre, r, length, mat, segments=12):
    """The hood over a lens: the top half of a thin tube, sticking out along +x."""
    bm = bmesh.new()
    cx, cy, cz = centre
    outer, inner = r * 1.15, r * 1.05
    rings = []
    for k in range(segments + 1):
        t = math.pi * k / segments  # 0..pi: over the top
        y, z = math.cos(t), math.sin(t)
        rings.append([
            bm.verts.new((cx, cy + y * outer, cz + z * outer)),
            bm.verts.new((cx + length, cy + y * outer, cz + z * outer)),
            bm.verts.new((cx + length, cy + y * inner, cz + z * inner)),
            bm.verts.new((cx, cy + y * inner, cz + z * inner)),
        ])
    for k in range(segments):
        a, b = rings[k], rings[k + 1]
        for i in range(4):
            j = (i + 1) % 4
            bm.faces.new((a[i], a[j], b[j], b[i]))
    for ring in (rings[0], rings[-1]):
        bm.faces.new(ring)
    return finish(name, bm, mat)


def octagon_x(name, centre, r, depth, mat):
    """An octagonal plate facing +x, flat along its top and bottom (a stop sign's shape)."""
    bm = bmesh.new()
    cx, cy, cz = centre
    front, back = [], []
    for k in range(8):
        t = math.pi / 8 + k * math.pi / 4
        y, z = cy + r * math.cos(t), cz + r * math.sin(t)
        front.append(bm.verts.new((cx + depth / 2, y, z)))
        back.append(bm.verts.new((cx - depth / 2, y, z)))
    bm.faces.new(front)
    bm.faces.new(list(reversed(back)))
    for k in range(8):
        j = (k + 1) % 8
        bm.faces.new((front[k], back[k], back[j], front[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return finish(name, bm, mat)


def join(name, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    objects[0].name = name
    objects[0].data.name = name
    return objects[0]


def export(name):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, f"{name}.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_yup=True, export_apply=True, export_materials="EXPORT")
    print(f"streets: {path}")


def head(parts, centre, mats):
    """A three-lamp head facing +x, its middle at `centre`: body, back plate, lenses (red, amber, green) and visors."""
    body, plate, lenses = mats["head"], mats["plate"], (mats["red"], mats["amber"], mats["green"])
    x, y, z = centre
    parts.append(box("head", (x, y, z), (HEAD_DEPTH, HEAD_WIDTH, HEAD_HEIGHT), body))
    parts.append(box("plate", (x - HEAD_DEPTH / 2 - 0.01, y, z), (0.02, HEAD_WIDTH + 0.24, HEAD_HEIGHT + 0.22), plate))
    for k, lens in enumerate(lenses):
        lz = z + LENS_STEP * (1 - k)
        parts.append(disc_x("lens", (x + HEAD_DEPTH / 2 + 0.005, y, lz), LENS_R, 0.02, lens))
        parts.append(visor("visor", (x + HEAD_DEPTH / 2, y, lz), LENS_R, 0.2, body))


def traffic_signal():
    clear()
    mats = {
        "metal": material("signal metal", (0.24, 0.26, 0.27), rough=0.45, metal=0.6),
        "base": material("signal base", (0.55, 0.55, 0.52), rough=0.9),
        "head": material("signal head", (0.05, 0.05, 0.05), rough=0.5),
        "plate": material("signal plate", (0.03, 0.03, 0.03), rough=0.7),
        "red": material("lens red", (0.28, 0.04, 0.03), rough=0.2),
        "amber": material("lens amber", (0.28, 0.18, 0.02), rough=0.2),
        "green": material("lens green", (0.02, 0.2, 0.09), rough=0.2),
    }
    parts = [
        rod("base", (0, 0, 0), (0, 0, 0.3), 0.18, mats["base"], segments=12),
        rod("pole", (0, 0, 0), (0, 0, POLE_H), 0.1, mats["metal"], r2=0.08),
        rod("cap", (0, 0, POLE_H), (0, 0, POLE_H + 0.05), 0.11, mats["metal"]),
        # The arm, tapering out over the road, and the brace under it.
        rod("arm", (0, 0, ARM_Z), (0, -ARM_LEN, ARM_Z), 0.09, mats["metal"], r2=0.05),
        rod("brace", (0, 0, ARM_Z - 0.75), (0, -1.4, ARM_Z), 0.035, mats["metal"]),
        # The head over the lane, hung from the arm.
        rod("hanger", (0, ARM_HEAD_Y, ARM_Z), (0, ARM_HEAD_Y, ARM_HEAD_Z + HEAD_HEIGHT / 2), 0.03, mats["metal"]),
        # The head on the pole, on a short bracket.
        box("bracket", (POLE_HEAD_X / 2, 0, POLE_HEAD_Z), (POLE_HEAD_X, 0.08, 0.08), mats["metal"]),
    ]
    head(parts, (0, ARM_HEAD_Y, ARM_HEAD_Z), mats)
    head(parts, (POLE_HEAD_X + 0.02, 0, POLE_HEAD_Z), mats)
    join("traffic-signal", parts)
    export("traffic-signal")


def letters(parts, x, centre_y, centre_z, mat):
    """STOP in block letters on a plane facing +x: read left to right along +y (the right of someone facing -x)."""
    h, w, s, gap = 0.17, 0.115, 0.036, 0.032
    total = 4 * w + 3 * gap
    y0 = centre_y - total / 2
    z0 = centre_z - h / 2

    strokes = [0]

    def bar(y, z, dy, dz):
        # A stroke from (y, z), dy wide and dz high, in the letter's own frame (y from its left, z from its foot);
        # each a hair proud of the last, so where two cross only one face shows.
        strokes[0] += 1
        parts.append(box("letter", (x + 0.0008 * strokes[0], y0 + y + dy / 2, z0 + z + dz / 2), (0.006, dy, dz), mat))

    def letter(k, strokes):
        nonlocal y0
        base = y0
        y0 = base + k * (w + gap)
        for st in strokes:
            bar(*st)
        y0 = base

    top, mid, bot = h - s, (h - s) / 2, 0
    letter(0, [(0, top, w, s), (0, mid, w, s), (0, bot, w, s), (0, mid, s, h / 2), (w - s, bot, s, h / 2)])  # S
    letter(1, [(0, top, w, s), ((w - s) / 2, 0, s, h)])  # T
    letter(2, [(0, top, w, s), (0, bot, w, s), (0, 0, s, h), (w - s, 0, s, h)])  # O
    letter(3, [(0, top, w, s), (0, mid, w, s), (0, 0, s, h), (w - s, mid, s, h / 2)])  # P


def stop_sign():
    clear()
    post = material("sign post", (0.6, 0.62, 0.63), rough=0.4, metal=0.7)
    red = material("stop red", (0.62, 0.04, 0.04), rough=0.5)
    white = material("stop white", (0.92, 0.92, 0.9), rough=0.5)
    back = material("sign back", (0.55, 0.56, 0.56), rough=0.6, metal=0.4)
    z = 2.0
    parts = [
        box("post", (0, 0, 1.0), (0.06, 0.06, 2.3), post),
        octagon_x("back", (0.045, 0, z), 0.4, 0.02, back),
        octagon_x("border", (0.06, 0, z), 0.4, 0.01, white),
        octagon_x("face", (0.067, 0, z), 0.36, 0.006, red),
    ]
    letters(parts, 0.073, 0, z, white)
    join("stop-sign", parts)
    export("stop-sign")


traffic_signal()
stop_sign()
