# Wheelie bins for the town: a trash bin and a recycling bin, each five parts,
# exported to Models/props/ as glTF for the model library (Editor/Library.cs).
#
#   blender --background --python unity/Tools~/bins/wheelie_bins.py
#
# Parts (glTF nodes, found by name at run time):
#   body    the tub, wider at the top, open under the lid
#   lid     its origin is the hinge along the back top edge, so turning it
#           about its own x opens it (MissionPlay flips it open as a bin is tipped)
#   wheels  the pair at the back, on their axle
#   handle  the bar across the back, under the lid's hinge
#   emblem  on the front: a recycling triangle, or a white house-number plate
#
# Blender is z-up with the front facing -y; the glTF exporter turns that into
# y-up facing +z, as the library's cars face. Sizes are metres (a real bin is
# about 1.07 m tall); missions scale them to whatever size they ask for.

import math
import os
import sys

import bpy
import bmesh
import mathutils

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Porchlight", "Models", "props"))

W_BOTTOM, D_BOTTOM = 0.50, 0.60  # the tub's foot
W_TOP, D_TOP = 0.58, 0.72  # and its rim
HEIGHT = 1.0  # the rim's height
WHEEL_R = 0.10
WHEEL_W = 0.06


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def material(name, rgb, rough=0.6):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    bsdf.inputs["Roughness"].default_value = rough
    return m


def mesh_object(name, bm, mat):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    me.materials.append(mat)
    return ob


def bevel(ob, width, segments=2):
    mod = ob.modifiers.new("bevel", "BEVEL")
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    ob.select_set(False)
    for p in ob.data.polygons:
        p.use_smooth = False


def body(mat):
    """A tapered tub: an open-topped box with walls, wider at the rim."""
    bm = bmesh.new()
    zb, zt = 0.04, HEIGHT
    # The back stays upright (the wheels and handle hang on it); the front leans out.
    y_back = D_BOTTOM / 2 + 0.02
    bottom = [(-W_BOTTOM / 2, -D_BOTTOM / 2, zb), (W_BOTTOM / 2, -D_BOTTOM / 2, zb), (W_BOTTOM / 2, y_back, zb), (-W_BOTTOM / 2, y_back, zb)]
    top = [(-W_TOP / 2, -D_TOP / 2 - 0.04, zt), (W_TOP / 2, -D_TOP / 2 - 0.04, zt), (W_TOP / 2, y_back + 0.06, zt), (-W_TOP / 2, y_back + 0.06, zt)]
    vb = [bm.verts.new(v) for v in bottom]
    vt = [bm.verts.new(v) for v in top]
    bm.faces.new(list(reversed(vb)))
    for i in range(4):
        j = (i + 1) % 4
        bm.faces.new((vb[i], vb[j], vt[j], vt[i]))
    # An open tub has no inside and outside to work normals out from, so face each one away
    # from the tub's middle (the game draws only the side a face points to).
    centre = mathutils.Vector((0, 0, HEIGHT / 2))
    for f in bm.faces:
        if f.normal.dot(f.calc_center_median() - centre) < 0:
            f.normal_flip()
    ob = mesh_object("body", bm, mat)
    # Walls with some thickness (the rim shows as a band round the top), so the tub's inside
    # shows when the lid's open; then its edges softened.
    mod = ob.modifiers.new("walls", "SOLIDIFY")
    mod.thickness = 0.03
    mod.offset = -1
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    ob.select_set(False)
    bevel(ob, 0.01)
    return ob


def lid(mat):
    """A slab over the rim, a little proud of it, with a grip lip at the front; hinged at the back."""
    hinge_y = D_BOTTOM / 2 + 0.08 + 0.02
    hinge_z = HEIGHT + 0.01
    bm = bmesh.new()
    depth = D_TOP + 0.12
    bmesh.ops.create_cube(bm, size=1)
    for v in bm.verts:
        v.co.x *= W_TOP * 1.1
        v.co.y = v.co.y * depth - depth / 2  # back edge at 0, reaching forward (-y)
        v.co.z = (v.co.z + 0.5) * 0.045
    # The grip: a lip down over the front.
    front = bmesh.ops.create_cube(bm, size=1)["verts"]
    for v in front:
        v.co.x *= W_TOP * 1.1
        v.co.y = v.co.y * 0.03 - depth + 0.015
        v.co.z = v.co.z * 0.07 + 0.0
    ob = mesh_object("lid", bm, mat)
    bevel(ob, 0.01)
    ob.location = (0, hinge_y, hinge_z)
    return ob


def wheels(tyre, hub):
    """Two wheels on an axle across the back, at the bottom."""
    bm = bmesh.new()
    y, z = D_BOTTOM / 2 + 0.04, WHEEL_R
    for side in (-1, 1):
        r = bmesh.ops.create_cone(bm, cap_ends=True, segments=16, radius1=WHEEL_R, radius2=WHEEL_R, depth=WHEEL_W)
        for v in r["verts"]:
            x, yy, zz = v.co
            v.co = (zz + side * (W_BOTTOM / 2 + WHEEL_W / 2), yy + y, x + z)
    tyres = mesh_object("wheels", bm, tyre)
    # The axle and hubs, in the hub colour, as part of the same object.
    bm = bmesh.new()
    r = bmesh.ops.create_cone(bm, cap_ends=True, segments=8, radius1=0.022, radius2=0.022, depth=W_BOTTOM + WHEEL_W * 2 + 0.02)
    for v in r["verts"]:
        x, yy, zz = v.co
        v.co = (zz, yy + y, x + z)
    for side in (-1, 1):
        r = bmesh.ops.create_cone(bm, cap_ends=True, segments=10, radius1=WHEEL_R * 0.5, radius2=WHEEL_R * 0.5, depth=WHEEL_W + 0.01)
        for v in r["verts"]:
            x, yy, zz = v.co
            v.co = (zz + side * (W_BOTTOM / 2 + WHEEL_W / 2), yy + y, x + z)
    axle = mesh_object("axle", bm, hub)
    join(tyres, [axle])
    return tyres


def handle(mat):
    """A bar across the back just under the lid's hinge, on two brackets."""
    bm = bmesh.new()
    y, z = D_BOTTOM / 2 + 0.14, HEIGHT - 0.06
    r = bmesh.ops.create_cone(bm, cap_ends=True, segments=10, radius1=0.022, radius2=0.022, depth=W_TOP * 0.9)
    for v in r["verts"]:
        x, yy, zz = v.co
        v.co = (zz, yy + y, x + z)
    for side in (-1, 1):
        b = bmesh.ops.create_cube(bm, size=1)["verts"]
        for v in b:
            v.co = (v.co.x * 0.03 + side * W_TOP * 0.4, v.co.y * 0.1 + y - 0.05, v.co.z * 0.04 + z)
    ob = mesh_object("handle", bm, mat)
    return ob


def emblem(kind, mat):
    """On the front: a recycling triangle (three-sided ring), or a white plate for the house number."""
    y = -(D_BOTTOM / 2 + (D_TOP - D_BOTTOM) / 2 * 0.6) - 0.045
    z = HEIGHT * 0.6
    lean = math.atan2((D_TOP + 0.08 - D_BOTTOM) / 2, HEIGHT)
    bm = bmesh.new()
    if kind == "recycle":
        bmesh.ops.create_circle(bm, cap_ends=False, segments=3, radius=0.15)
        outer = list(bm.verts)
        ring = bmesh.ops.extrude_edge_only(bm, edges=list(bm.edges))
        for v in ring["geom"]:
            if isinstance(v, bmesh.types.BMVert):
                v.co *= 0.62
        faces = bmesh.ops.extrude_face_region(bm, geom=list(bm.faces))
        for v in faces["geom"]:
            if isinstance(v, bmesh.types.BMVert):
                v.co.z += 0.012
        # The circle's first corner is on +y, which points it up once it's stood on the front (below).
    else:
        bmesh.ops.create_cube(bm, size=1)
        for v in bm.verts:
            v.co = (v.co.x * 0.22, v.co.y * 0.12, v.co.z * 0.012 + 0.006)
    # Stand it on the front face: its flat side facing -y, leaning back with the tub.
    m = mathutils.Matrix.Rotation(math.pi / 2 + lean, 3, "X")
    bmesh.ops.rotate(bm, verts=list(bm.verts), cent=(0, 0, 0), matrix=m)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    ob = mesh_object("emblem", bm, mat)
    ob.location = (0, y, z)
    return ob


def join(target, others):
    bpy.ops.object.select_all(action="DESELECT")
    for o in others:
        o.select_set(True)
    target.select_set(True)
    bpy.context.view_layer.objects.active = target
    bpy.ops.object.join()


def build(kind):
    clear()
    if kind == "recycle":
        body_mat = material("bin-blue", (0.05, 0.22, 0.62))
        lid_mat = material("lid-yellow", (0.95, 0.70, 0.08))
    else:
        body_mat = material("bin-green", (0.08, 0.26, 0.12))
        lid_mat = material("lid-dark", (0.04, 0.10, 0.05))
    tyre = material("tyre", (0.03, 0.03, 0.03), 0.9)
    hub = material("hub", (0.35, 0.35, 0.37), 0.4)
    white = material("emblem-white", (0.92, 0.92, 0.9), 0.5)
    body(body_mat)
    lid(lid_mat)
    wheels(tyre, hub)
    handle(lid_mat)
    emblem(kind, white)
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, f"wheelie-bin-{kind}.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_yup=True, export_apply=True, export_materials="EXPORT")
    names = sorted(o.name for o in bpy.context.scene.objects)
    print(f"wheelie bin: {path} parts={names}")


for kind in ("trash", "recycle"):
    build(kind)
sys.exit(0)
