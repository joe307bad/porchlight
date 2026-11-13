# Christmas for the town: Santa, for the town's people (Models/people/santa.glb),
# Santa in his sleigh behind nine reindeer (Models/planes/santa-sleigh.glb),
# and a Christmas tree and a present (Models/props/):
#
#   blender --background --python unity/Tools~/christmas/christmas.py [-- previews-dir]
#
# (Blender's glTF exporter needs numpy: Blender for macOS bundles it; on Linux
# point PYTHONPATH at a numpy built for Blender's Python.)
#
#   people/santa        Santa as one of the people: red coat and trousers
#                       with white fur trim, a black belt with a gold
#                       buckle, black boots and mittens, a white beard and
#                       moustache, rosy nose, and his red hat with its
#                       bobble. He's skinned to the people's own armature
#                       (copied, bones and bind pose and all, from
#                       character-5-2-3.glb), each part to the bone it rides,
#                       so the people's clips (idle, walk, run, jump) play on
#                       him as on everyone: a mission can make you him
#                       (look("people/santa")).
#   planes/santa-sleigh Santa's sleigh, flying: a red sleigh on gold runners
#                       with Santa in the back and the sack of toys behind
#                       him, nine reindeer ahead in harness (Rudolph out in
#                       front, his nose aglow, then four pairs), reins and
#                       traces to the sleigh. Each reindeer is a node of its
#                       own ("reindeer-1" .. "reindeer-9"), its legs nodes
#                       under it pivoting at the hip ("leg-fl" and so on),
#                       and Santa ("santa") and the sack ("sack") too, so the
#                       game can bob them as it flies (Rendering/Vehicles.cs,
#                       Sleigh). Nose down +z once exported, as the planes'.
#   props/christmas-tree a fir in a red pot, three tiers of branches hung
#                       with baubles, and a gold star on top. The presents
#                       mission stands one in each house you call at.
#   props/present       a present wrapped in red with a gold ribbon and bow,
#                       that you put under the tree.
#
# The people's armature stands in a T-pose facing -x (in glTF's y-up terms),
# arms out along z, about 1.72 tall to the top of the head; Santa is built
# round it in Blender's z-up terms (glTF x, y, z is Blender x, -z, y) and
# written out by hand (write_skinned) with the armature copied in.

import json
import math
import os
import struct

import bpy
import bmesh
import mathutils

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Porchlight", "Models"))
ARMATURE_FROM = os.path.join(MODELS, "people", "character-5-2-3.glb")
V = mathutils.Vector


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


MATS = {}


def material(name, rgb, rough=0.7, emit=0.0):
    if name in MATS:
        return MATS[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    bsdf.inputs["Roughness"].default_value = rough
    if emit > 0:
        bsdf.inputs["Emission Color"].default_value = (*rgb, 1)
        bsdf.inputs["Emission Strength"].default_value = emit
    MATS[name] = m
    return m


def palette():
    return {
        "red": material("Santa Red", (0.62, 0.03, 0.03)),
        "fur": material("Fur White", (0.93, 0.93, 0.9), rough=0.95),
        "black": material("Boot Black", (0.04, 0.035, 0.035), rough=0.5),
        "gold": material("Buckle Gold", (0.85, 0.62, 0.12), rough=0.35),
        "skin": material("Skin", (0.93, 0.68, 0.55)),
        "rosy": material("Rosy", (0.9, 0.42, 0.38)),
        "eye": material("Eye", (0.03, 0.03, 0.05), rough=0.3),
        "sack": material("Sack Brown", (0.5, 0.33, 0.17), rough=0.95),
        "rope": material("Rope", (0.78, 0.6, 0.35), rough=0.95),
        "deer": material("Reindeer Brown", (0.42, 0.25, 0.13), rough=0.9),
        "deer_light": material("Reindeer Belly", (0.78, 0.64, 0.47), rough=0.9),
        "antler": material("Antler", (0.86, 0.78, 0.62), rough=0.8),
        "hoof": material("Hoof", (0.1, 0.07, 0.05), rough=0.6),
        "harness": material("Harness Red", (0.55, 0.05, 0.05), rough=0.5),
        "bell": material("Bell Gold", (0.95, 0.75, 0.2), rough=0.3),
        "rudolph": material("Rudolph Nose", (1.0, 0.08, 0.05), rough=0.3, emit=3.0),
        "wood": material("Sleigh Red", (0.72, 0.06, 0.05), rough=0.45),
        "trim": material("Sleigh Gold", (0.9, 0.68, 0.18), rough=0.3),
        "seat": material("Seat Green", (0.08, 0.35, 0.16), rough=0.8),
        "gift_a": material("Gift Blue", (0.12, 0.35, 0.8)),
        "gift_b": material("Gift Green", (0.1, 0.6, 0.25)),
        "gift_c": material("Gift Yellow", (0.95, 0.8, 0.15)),
        "ribbon": material("Ribbon", (0.95, 0.95, 0.95)),
    }


def obj(name, bm, mat, smooth=False, parent=None):
    me = bpy.data.meshes.new(name)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    for p in me.polygons:
        p.use_smooth = smooth
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    me.materials.append(mat)
    if parent is not None:
        ob.parent = parent
    return ob


def place(bm, scale=(1, 1, 1), rot=None, at=(0, 0, 0)):
    m = mathutils.Matrix.Translation(V(at))
    if rot is not None:
        m = m @ rot
    m = m @ mathutils.Matrix.Diagonal(V((*scale, 1)))
    bmesh.ops.transform(bm, matrix=m, verts=bm.verts)
    return bm


def ball(r=1.0, seg=10, rings=7, scale=(1, 1, 1), at=(0, 0, 0)):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=rings, radius=r)
    return place(bm, scale, None, at)


def box(size, at=(0, 0, 0), rot=None):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    return place(bm, size, rot, at)


def tube(a, b, r1, r2=None, seg=8, caps=True):
    """A cylinder (or cone) from point a to point b."""
    a, b = V(a), V(b)
    r2 = r1 if r2 is None else r2
    d = b - a
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=caps, segments=seg, radius1=r1, radius2=r2, depth=d.length)
    rot = d.to_track_quat("Z", "Y").to_matrix().to_4x4()
    return place(bm, (1, 1, 1), rot, (a + b) / 2)


def ring(r_major, r_minor, at=(0, 0, 0), seg=12, seg_minor=5, rot=None, scale=(1, 1, 1)):
    bm = bmesh.new()
    verts = []
    for i in range(seg):
        a = 2 * math.pi * i / seg
        row = []
        for j in range(seg_minor):
            b = 2 * math.pi * j / seg_minor
            x = (r_major + r_minor * math.cos(b)) * math.cos(a)
            y = (r_major + r_minor * math.cos(b)) * math.sin(a)
            z = r_minor * math.sin(b)
            row.append(bm.verts.new((x, y, z)))
        verts.append(row)
    for i in range(seg):
        for j in range(seg_minor):
            a, b = verts[i][j], verts[(i + 1) % seg][j]
            c, d = verts[(i + 1) % seg][(j + 1) % seg_minor], verts[i][(j + 1) % seg_minor]
            bm.faces.new((a, b, c, d))
    return place(bm, scale, rot, at)


def merge(*bms):
    out = bmesh.new()
    for bm in bms:
        me = bpy.data.meshes.new("tmp")
        bm.to_mesh(me)
        bm.free()
        out.from_mesh(me)
        bpy.data.meshes.remove(me)
    return out


# Blender z-up from the armature's glTF y-up: glTF (x, y, z) is Blender (x, -z, y).
def B(x, y, z):
    return (x, -z, y)


# --- Santa, skinned to the people's armature ------------------------------------------------

def build_santa(P, parts):
    """Santa's parts round the armature's T-pose, each (object, rule) where rule says which bone a vertex rides."""
    # Front is -x. Left is +z in glTF, so -y in Blender.
    def add(name, bm, mat, rule, smooth=False):
        parts.append((obj(name, bm, mat, smooth), rule))

    # Boots: a shaft over the ankle, the foot out front, a sole.
    for s, side in ((1, "L"), (-1, "R")):
        z = 0.176 * s
        boot = merge(
            tube(B(-0.02, 0.0, z), B(-0.02, 0.24, z), 0.1, 0.095, seg=10),
            ball(1, 10, 6, scale=(0.15, 0.1, 0.08), at=B(-0.12, 0.07, z)),
            box((0.36, 0.2, 0.04), at=B(-0.08, 0.02, z)),
        )
        add(f"boot.{side}", boot, P["black"], ("foot", side))
        # Trousers: red, tucked into the boots.
        add(f"leg.{side}", tube(B(0, 0.22, z), B(0, 0.56, z * 0.95), 0.095, 0.115, seg=10), P["red"], ("leg", side))
        # The boot's fur cuff.
        add(f"cuff.{side}", ring(0.1, 0.03, at=B(-0.02, 0.24, z), seg=12), P["fur"], ("foot", side))

    # The coat: a plump body, wider at the belly, out in front.
    body = ball(1, 14, 9, scale=(0.27, 0.3, 0.3), at=B(-0.03, 0.82, 0))
    add("coat", body, P["red"], ("body",), smooth=True)
    add("chest", ball(1, 12, 7, scale=(0.22, 0.25, 0.2), at=B(0.0, 1.02, 0)), P["red"], ("body",), smooth=True)
    # The hem's fur, round the bottom of the coat, and the strip down the front.
    add("hem", ring(0.26, 0.05, at=B(-0.03, 0.56, 0), seg=16, scale=(1.0, 1.08, 1)), P["fur"], ("body",))
    add("placket", box((0.04, 0.09, 0.3), at=B(-0.3, 0.84, 0)), P["fur"], ("body",))
    # The belt, its buckle, and the fur at the collar.
    add("belt", ring(0.285, 0.035, at=B(-0.03, 0.75, 0), seg=16, scale=(1.0, 1.05, 1)), P["black"], ("body",))
    add("buckle", box((0.03, 0.15, 0.12), at=B(-0.32, 0.75, 0)), P["gold"], ("body",))
    add("buckle.hole", box((0.02, 0.08, 0.06), at=B(-0.335, 0.75, 0)), P["black"], ("body",))
    add("collar", ring(0.17, 0.05, at=B(0.0, 1.17, 0), seg=14), P["fur"], ("body",))

    # Arms out along z: red sleeves, fur cuffs, black mittens.
    for s, side in ((1, "L"), (-1, "R")):
        add(f"sleeve.{side}", tube(B(0.0, 1.04, 0.15 * s), B(0.04, 1.034, 0.74 * s), 0.1, 0.08, seg=10), P["red"], ("arm", side))
        add(f"shoulder.{side}", ball(1, 10, 6, scale=(0.11, 0.11, 0.11), at=B(0.0, 1.04, 0.2 * s)), P["red"], ("arm", side), smooth=True)
        add(f"wristfur.{side}", tube(B(0.04, 1.034, 0.7 * s), B(0.045, 1.033, 0.79 * s), 0.095, 0.095, seg=10), P["fur"], ("arm", side))
        mitt = merge(
            ball(1, 8, 6, scale=(0.08, 0.075, 0.1), at=B(0.045, 1.03, 0.88 * s)),
            ball(1, 6, 4, scale=(0.035, 0.035, 0.05), at=B(-0.03, 1.045, 0.86 * s)),
        )
        add(f"mitten.{side}", mitt, P["black"], ("hand", side))

    # The head: face, nose, eyes, the beard and moustache, brows, and the hat.
    hx = 0.02
    add("head", ball(1, 14, 10, scale=(0.24, 0.25, 0.26), at=B(hx, 1.38, 0)), P["skin"], ("head",), smooth=True)
    add("nose", ball(1, 8, 6, scale=(0.055, 0.05, 0.05), at=B(hx - 0.25, 1.37, 0)), P["rosy"], ("head",), smooth=True)
    for s in (1, -1):
        add(f"eye.{s}", ball(1, 6, 4, scale=(0.02, 0.03, 0.025), at=B(hx - 0.225, 1.45, 0.08 * s)), P["eye"], ("head",))
        add(f"brow.{s}", box((0.04, 0.03, 0.09), at=B(hx - 0.22, 1.51, 0.085 * s),
                             rot=mathutils.Matrix.Rotation(-0.25 * s, 4, "X")), P["fur"], ("head",))
        add(f"cheek.{s}", ball(1, 6, 4, scale=(0.03, 0.05, 0.05), at=B(hx - 0.2, 1.38, 0.14 * s)), P["rosy"], ("head",), smooth=True)
        add(f"tache.{s}", ball(1, 8, 5, scale=(0.05, 0.11, 0.045), at=B(hx - 0.25, 1.32, 0.08 * s)), P["fur"], ("head",), smooth=True)
    # A big beard down over the chest: from the cheeks to a rounded point.
    beard = merge(
        ball(1, 12, 8, scale=(0.18, 0.25, 0.22), at=B(hx - 0.1, 1.23, 0)),
        ball(1, 10, 6, scale=(0.13, 0.18, 0.15), at=B(hx - 0.17, 1.1, 0)),
    )
    add("beard", beard, P["fur"], ("head",), smooth=True)
    # The hat: a fur brim round the head, a red cone flopping back, its bobble.
    add("brim", ring(0.24, 0.06, at=B(hx + 0.01, 1.58, 0), seg=16, scale=(1.02, 1.08, 1)), P["fur"], ("head",))
    hat = merge(
        tube(B(hx + 0.02, 1.58, 0), B(hx + 0.1, 1.82, 0), 0.25, 0.12, seg=12),
        tube(B(hx + 0.1, 1.82, 0), B(hx + 0.3, 1.86, 0), 0.12, 0.04, seg=10),
    )
    add("hat", hat, P["red"], ("head",))
    add("bobble", ball(1, 8, 6, scale=(0.07, 0.07, 0.07), at=B(hx + 0.33, 1.84, 0)), P["fur"], ("head",), smooth=True)


def bone_for(rule, x, y, z):
    """Which bone a vertex (glTF terms) rides, by the part's rule and where it is."""
    kind = rule[0]
    if kind == "head":
        return "Head"
    if kind == "body":
        return "Hips" if y < 0.7 else "Torso"
    side = rule[1]
    if kind == "foot":
        return f"Toes.{side}" if x < -0.17 and y < 0.11 else f"Foot.{side}"
    if kind == "leg":
        return f"UpperLeg.{side}" if y > 0.31 else f"Leg.{side}"
    if kind == "arm":
        if abs(z) < 0.2:
            return f"Shoulder.{side}"
        return f"Arm.{side}" if abs(z) < 0.52 else f"ForeArm.{side}"
    if kind == "hand":
        return f"Hand.{side}"
    raise ValueError(rule)


def read_glb(path):
    data = open(path, "rb").read()
    n = struct.unpack("<I", data[12:16])[0]
    gltf = json.loads(data[20:20 + n])
    rest = data[20 + n:]
    blen = struct.unpack("<I", rest[:4])[0]
    return gltf, rest[8:8 + blen]


def accessor_bytes(gltf, binary, i):
    a = gltf["accessors"][i]
    bv = gltf["bufferViews"][a["bufferView"]]
    comps = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}[a["type"]]
    size = {5126: 4, 5123: 2, 5121: 1, 5125: 4}[a["componentType"]]
    off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    return binary[off:off + a["count"] * comps * size]


def write_skinned(path, parts, materials):
    """Santa as a glTF: the armature (nodes, skin, bind pose) from ARMATURE_FROM, his mesh in place of its own."""
    src, src_bin = read_glb(ARMATURE_FROM)
    skin = src["skins"][0]
    joint_names = [src["nodes"][j]["name"] for j in skin["joints"]]
    # Triangles grouped by material, each vertex (glTF terms) with its flat normal and bone.
    by_mat = {}
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for ob, rule in parts:
        eval_ob = ob.evaluated_get(depsgraph)
        me = eval_ob.to_mesh()
        me.calc_loop_triangles()
        mw = ob.matrix_world
        mat = ob.material_slots[0].material.name
        tris = by_mat.setdefault(mat, [])
        for t in me.loop_triangles:
            vs = [mw @ me.vertices[i].co for i in t.vertices]
            n = (vs[1] - vs[0]).cross(vs[2] - vs[0])
            if n.length < 1e-12:
                continue
            n.normalize()
            if ob.data.polygons[t.polygon_index].use_smooth:
                normals = [(mw.to_3x3() @ me.vertices[i].normal).normalized() for i in t.vertices]
            else:
                normals = [n, n, n]
            for v, nn in zip(vs, normals):
                gx, gy, gz = v.x, v.z, -v.y
                tris.append(((gx, gy, gz), (nn.x, nn.z, -nn.y), joint_names.index(bone_for(rule, gx, gy, gz))))
        eval_ob.to_mesh_clear()

    blob = bytearray()
    views, accessors = [], []

    def add_view(data, target=None):
        while len(blob) % 4:
            blob.append(0)
        view = {"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)}
        if target:
            view["target"] = target
        blob.extend(data)
        views.append(view)
        return len(views) - 1

    def add_accessor(data, ctype, count, kind, **extra):
        accessors.append({"bufferView": add_view(data, extra.pop("target", None)), "componentType": ctype, "count": count, "type": kind, **extra})
        return len(accessors) - 1

    prims = []
    mat_list = []
    for mat_name, verts in by_mat.items():
        count = len(verts)
        pos = b"".join(struct.pack("<3f", *v[0]) for v in verts)
        nor = b"".join(struct.pack("<3f", *v[1]) for v in verts)
        jnt = b"".join(struct.pack("<4H", v[2], 0, 0, 0) for v in verts)
        wgt = b"".join(struct.pack("<4f", 1, 0, 0, 0) for v in verts)
        lo = [min(v[0][k] for v in verts) for k in range(3)]
        hi = [max(v[0][k] for v in verts) for k in range(3)]
        attrs = {
            "POSITION": add_accessor(pos, 5126, count, "VEC3", min=lo, max=hi, target=34962),
            "NORMAL": add_accessor(nor, 5126, count, "VEC3", target=34962),
            "JOINTS_0": add_accessor(jnt, 5123, count, "VEC4", target=34962),
            "WEIGHTS_0": add_accessor(wgt, 5126, count, "VEC4", target=34962),
        }
        prims.append({"attributes": attrs, "material": len(mat_list)})
        m = materials[mat_name]
        bsdf = m.node_tree.nodes["Principled BSDF"]
        rgb = list(bsdf.inputs["Base Color"].default_value)[:3]
        mat_list.append({"name": mat_name, "pbrMetallicRoughness": {"baseColorFactor": rgb + [1], "metallicFactor": 0,
                                                                      "roughnessFactor": bsdf.inputs["Roughness"].default_value}})
    ibm = add_accessor(accessor_bytes(src, src_bin, skin["inverseBindMatrices"]), 5126, len(skin["joints"]), "MAT4")

    nodes = json.loads(json.dumps(src["nodes"]))
    mesh_node = next(i for i, n in enumerate(nodes) if "skin" in n)
    nodes[mesh_node] = {"name": "Santa", "mesh": 0, "skin": 0}
    gltf = {
        "asset": {"version": "2.0", "generator": "porchlight christmas.py"},
        "scene": 0,
        "scenes": [{"name": "Scene", "nodes": src["scenes"][0]["nodes"]}],
        "nodes": nodes,
        "meshes": [{"name": "Santa", "primitives": prims}],
        "materials": mat_list,
        "skins": [{"name": skin.get("name", "CharacterArmature1"), "joints": skin["joints"], "inverseBindMatrices": ibm,
                   **({"skeleton": skin["skeleton"]} if "skeleton" in skin else {})}],
        "accessors": accessors,
        "bufferViews": views,
        "buffers": [{"byteLength": len(blob)}],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode()
    while len(js) % 4:
        js += b" "
    while len(blob) % 4:
        blob.append(0)
    out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + 8 + len(blob))
    out += struct.pack("<I4s", len(js), b"JSON") + js + struct.pack("<I4s", len(blob), b"BIN\x00") + bytes(blob)
    with open(path, "wb") as f:
        f.write(out)
    print(f"wrote {path}: {sum(len(v) for v in by_mat.values()) // 3} triangles, {len(mat_list)} materials")


# --- The sleigh and its reindeer -----------------------------------------------------------

def reindeer(P, name, at, rudolph=False):
    """A reindeer facing -x (Blender), its middle at `at`: body, neck, head, antlers, tail, harness and bell; four legs, each a node pivoting at its hip."""
    root = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(root)
    root.location = at
    parts = [
        (ball(1, 10, 7, scale=(0.34, 0.15, 0.16), at=(0, 0, 0.62)), P["deer"], True),
        (ball(1, 8, 6, scale=(0.26, 0.11, 0.08), at=(0, 0, 0.54)), P["deer_light"], True),
        (tube((-0.24, 0, 0.68), (-0.36, 0, 0.92), 0.08, 0.06, seg=8), P["deer"], True),
        (ball(1, 8, 6, scale=(0.13, 0.075, 0.075), at=(-0.43, 0, 0.96)), P["deer"], True),
        (ball(1, 6, 4, scale=(0.04, 0.03, 0.025), at=(-0.36, 0.06, 1.02)), P["deer"], True),
        (ball(1, 6, 4, scale=(0.04, 0.03, 0.025), at=(-0.36, -0.06, 1.02)), P["deer"], True),
        (ball(1, 6, 4, scale=(0.05, 0.04, 0.04), at=(0.34, 0, 0.7)), P["deer_light"], True),
        (ring(0.11, 0.025, at=(-0.27, 0, 0.75), seg=10, rot=mathutils.Matrix.Rotation(0.9, 4, "Y")), P["harness"], False),
        (ring(0.17, 0.02, at=(-0.05, 0, 0.62), seg=12, rot=mathutils.Matrix.Rotation(math.pi / 2, 4, "Y"), scale=(1, 0.95, 1)), P["harness"], False),
        (ball(1, 6, 5, scale=(0.035, 0.035, 0.035), at=(-0.32, 0, 0.66)), P["bell"], True),
    ]
    for s in (1, -1):
        parts.append((ball(1, 6, 4, scale=(0.02, 0.02, 0.02), at=(-0.5, 0.05 * s, 0.99)), P["eye"], False))
        # Antlers: a beam up and back, with tines.
        base = V((-0.4, 0.05 * s, 1.03))
        tip = V((-0.33, 0.17 * s, 1.27))
        parts.append((tube(base, tip, 0.016, 0.01, seg=5), P["antler"], False))
        for k, f in ((0.45, 0.6), (0.75, 0.5)):
            p = base.lerp(tip, k)
            parts.append((tube(p, p + V((-0.08, 0.03 * s, 0.07 * f)), 0.011, 0.006, seg=4), P["antler"], False))
    for bm, mat, smooth in parts:
        obj(f"{name}-part", bm, mat, smooth, parent=root)
    # The nose: Rudolph's big and red (the game makes it glow: "rudolph-nose"), the rest's dark.
    if rudolph:
        obj("rudolph-nose", ball(1, 8, 6, scale=(0.045, 0.045, 0.045), at=(-0.56, 0, 0.94)), P["rudolph"], True, parent=root)
    else:
        obj(f"{name}-nose", ball(1, 8, 6, scale=(0.025, 0.03, 0.025), at=(-0.56, 0, 0.94)), P["hoof"], True, parent=root)
    # Legs: thigh and shin down from the hip to the hoof, as their own nodes so they can gallop.
    for code, x, y in (("fl", -0.22, 0.08), ("fr", -0.22, -0.08), ("bl", 0.22, 0.08), ("br", 0.22, -0.08)):
        leg = bpy.data.objects.new(f"leg-{code}", None)
        bpy.context.scene.collection.objects.link(leg)
        leg.parent = root
        leg.location = (x, y, 0.58)
        bm = merge(
            tube((0, 0, 0.02), (0.02 if code[0] == "f" else -0.03, 0, -0.3), 0.045, 0.03, seg=6),
            tube((0.02 if code[0] == "f" else -0.03, 0, -0.3), (0, 0, -0.52), 0.028, 0.022, seg=6),
        )
        obj(f"{name}-{code}-leg", bm, P["deer"], False, parent=leg)
        obj(f"{name}-{code}-hoof", tube((0, 0, -0.52), (0, 0, -0.58), 0.03, 0.032, seg=6), P["hoof"], False, parent=leg)
    return root


def sleigh_santa(P):
    """Santa sat in the sleigh, a node of his own: coat, belly, beard, hat and mittens on the reins."""
    root = bpy.data.objects.new("santa", None)
    bpy.context.scene.collection.objects.link(root)
    root.location = (0.15, 0, 0.55)
    parts = [
        (ball(1, 12, 8, scale=(0.22, 0.24, 0.26), at=(0, 0, 0.22)), P["red"], True),
        (ring(0.21, 0.04, at=(0, 0, 0.12), seg=14, scale=(1, 1.08, 1)), P["fur"], False),
        (ring(0.225, 0.03, at=(0, 0, 0.22), seg=14, scale=(1, 1.05, 1)), P["black"], False),
        (box((0.03, 0.1, 0.08), at=(-0.22, 0, 0.22)), P["gold"], False),
        (ball(1, 12, 8, scale=(0.17, 0.18, 0.18), at=(0.0, 0, 0.6)), P["skin"], True),
        (ball(1, 10, 7, scale=(0.13, 0.16, 0.16), at=(-0.07, 0, 0.47)), P["fur"], True),
        (ball(1, 6, 4, scale=(0.04, 0.04, 0.035), at=(-0.17, 0, 0.6)), P["rosy"], True),
        (ring(0.17, 0.045, at=(0.01, 0, 0.74), seg=14), P["fur"], False),
        (tube((0.01, 0, 0.74), (0.08, 0, 0.92), 0.17, 0.08, seg=10), P["red"], False),
        (tube((0.08, 0, 0.92), (0.24, 0, 0.94), 0.08, 0.03, seg=8), P["red"], False),
        (ball(1, 6, 5, scale=(0.05, 0.05, 0.05), at=(0.27, 0, 0.93)), P["fur"], True),
    ]
    for s in (1, -1):
        parts.append((ball(1, 6, 4, scale=(0.015, 0.02, 0.02), at=(-0.155, 0.055 * s, 0.64)), P["eye"], False))
        parts.append((ball(1, 6, 4, scale=(0.04, 0.08, 0.03), at=(-0.17, 0.05 * s, 0.56)), P["fur"], True))
        # Arms forward to the reins, mittens on them.
        parts.append((tube((0, 0.2 * s, 0.38), (-0.24, 0.14 * s, 0.24), 0.07, 0.06, seg=8), P["red"], False))
        parts.append((ring(0.06, 0.02, at=(-0.23, 0.145 * s, 0.25), seg=10, rot=mathutils.Matrix.Rotation(1.2, 4, "Y")), P["fur"], False))
        parts.append((ball(1, 6, 5, scale=(0.055, 0.05, 0.05), at=(-0.29, 0.13 * s, 0.22)), P["black"], True))
    for bm, mat, smooth in parts:
        obj("santa-part", bm, mat, smooth, parent=root)
    return root


def sack(P):
    root = bpy.data.objects.new("sack", None)
    bpy.context.scene.collection.objects.link(root)
    root.location = (0.62, 0, 0.5)
    parts = [
        (ball(1, 12, 8, scale=(0.24, 0.26, 0.3), at=(0, 0, 0.26)), P["sack"], True),
        (tube((0, 0, 0.52), (0, 0, 0.6), 0.08, 0.05, seg=8), P["sack"], False),
        (ring(0.075, 0.02, at=(0, 0, 0.53), seg=10), P["rope"], False),
        (box((0.13, 0.13, 0.13), at=(-0.04, 0.08, 0.66), rot=mathutils.Matrix.Rotation(0.3, 4, "Z")), P["gift_a"], False),
        (box((0.14, 0.022, 0.135), at=(-0.04, 0.08, 0.66), rot=mathutils.Matrix.Rotation(0.3, 4, "Z")), P["ribbon"], False),
        (box((0.11, 0.11, 0.1), at=(0.06, -0.07, 0.64), rot=mathutils.Matrix.Rotation(-0.4, 4, "Z")), P["gift_b"], False),
        (box((0.02, 0.115, 0.105), at=(0.06, -0.07, 0.64), rot=mathutils.Matrix.Rotation(-0.4, 4, "Z")), P["gift_c"], False),
        (box((0.09, 0.09, 0.12), at=(0.02, 0.0, 0.74), rot=mathutils.Matrix.Rotation(0.7, 4, "Z")), P["gift_c"], False),
    ]
    for bm, mat, smooth in parts:
        obj("sack-part", bm, mat, smooth, parent=root)
    return root


def build_sleigh(P):
    """The sleigh and its team, nose to -x in Blender (+z once exported, turned round for the game's planes)."""
    root = bpy.data.objects.new("santa-sleigh", None)
    bpy.context.scene.collection.objects.link(root)
    # The sleigh's body: a curved tub, higher at the back, scrolled at the front.
    body = merge(
        box((1.1, 0.7, 0.32), at=(0.25, 0, 0.5)),
        box((0.45, 0.72, 0.42), at=(0.62, 0, 0.62)),
        tube((-0.3, 0, 0.42), (-0.42, 0, 0.62), 0.18, 0.12, seg=10),
    )
    obj("sleigh-body", body, P["wood"], False, parent=root)
    obj("sleigh-seat", box((0.36, 0.62, 0.08), at=(0.26, 0, 0.67)), P["seat"], False, parent=root)
    obj("sleigh-back", box((0.08, 0.66, 0.36), at=(0.47, 0, 0.82)), P["seat"], False, parent=root)
    # Gold trim along the top edges, and the runners: long rails curling up at the front.
    for s in (1, -1):
        obj("sleigh-trim", tube((-0.3, 0.36 * s, 0.66), (0.85, 0.36 * s, 0.84), 0.025, seg=6), P["trim"], False, parent=root)
        rail = merge(
            tube((-0.45, 0.3 * s, 0.12), (0.85, 0.3 * s, 0.12), 0.03, seg=6),
            tube((-0.45, 0.3 * s, 0.12), (-0.62, 0.3 * s, 0.26), 0.03, seg=6),
            tube((-0.62, 0.3 * s, 0.26), (-0.55, 0.3 * s, 0.4), 0.03, seg=6),
            tube((-0.1, 0.3 * s, 0.12), (-0.1, 0.3 * s, 0.38), 0.022, seg=6),
            tube((0.55, 0.3 * s, 0.12), (0.55, 0.3 * s, 0.38), 0.022, seg=6),
        )
        obj("sleigh-runner", rail, P["trim"], False, parent=root)
        obj("sleigh-scroll", ring(0.09, 0.025, at=(-0.48, 0.36 * s, 0.6), seg=10, rot=mathutils.Matrix.Rotation(math.pi / 2, 4, "X")), P["trim"], False, parent=root)
    for part in (sleigh_santa(P), sack(P)):
        part.parent = root
        join_children(part, part.name + "-mesh")
    # The team: Rudolph out in front, then four pairs; reins from Santa's mittens and traces to the sleigh.
    deer = []
    lead_x = -0.6 - 0.8 * 5
    deer.append(reindeer(P, "reindeer-1", (lead_x, 0, 0), rudolph=True))
    k = 2
    for pair in range(4):
        x = -0.6 - 0.8 * (pair + 1)
        for s in (1, -1):
            deer.append(reindeer(P, f"reindeer-{k}", (x, 0.24 * s, 0)))
            k += 1
    for d in deer:
        d.parent = root
        for leg in [c for c in d.children if c.type == "EMPTY"]:
            join_children(leg, leg.name + "-mesh")
        join_children(d, d.name + "-body", keep={"rudolph-nose"})
    for s in (1, -1):
        obj("trace", tube((-0.55, 0.22 * s, 0.5), (lead_x + 0.0, 0.0, 0.66), 0.012, seg=4), P["harness"], False, parent=root)
        obj("rein", tube((-0.14, 0.13 * s, 0.77), (lead_x - 0.3, 0.02 * s, 0.82), 0.008, seg=4), P["rope"], False, parent=root)
    join_children(root, "sleigh")
    return root


def join_children(parent, name, keep=()):
    """The meshes right under `parent` (but those named in `keep`) as one, called `name`: one node to draw, not dozens."""
    meshes = [c for c in parent.children if c.type == "MESH" and c.name not in keep]
    if not meshes:
        return None
    bpy.ops.object.select_all(action="DESELECT")
    for m in meshes:
        m.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.join()
    meshes[0].name = name
    meshes[0].data.name = name
    return meshes[0]


# --- Previews ---------------------------------------------------------------------------------

def preview(path, target, distance, height=0.0, yaw=-50, lens=50):
    """A render to look at the model by (not shipped)."""
    scene = bpy.context.scene
    cam_data = bpy.data.cameras.new("cam")
    cam_data.lens = lens
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    a = math.radians(yaw)
    t = V(target)
    cam.location = t + V((math.cos(a) * distance, math.sin(a) * distance, height))
    cam.rotation_euler = (t - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    sun = bpy.data.lights.new("sun", "SUN")
    sun.energy = 3
    sun_ob = bpy.data.objects.new("sun", sun)
    sun_ob.rotation_euler = (math.radians(40), math.radians(10), math.radians(-30))
    scene.collection.objects.link(sun_ob)
    world = bpy.data.worlds.new("w")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.65, 0.8, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.8
    scene.world = world
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 24
    scene.cycles.device = "CPU"
    scene.cycles.use_denoising = False
    scene.render.resolution_x = 900
    scene.render.resolution_y = 700
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)
    bpy.data.objects.remove(sun_ob)


def christmas_tree(P):
    green = material("Fir Green", (0.05, 0.32, 0.12), rough=0.9)
    parts = [
        (tube((0, 0, 0), (0, 0, 0.22), 0.2, 0.24, seg=10), material("Pot Red", (0.6, 0.08, 0.06)), False),
        (ring(0.24, 0.025, at=(0, 0, 0.21), seg=12), P["trim"], False),
        (tube((0, 0, 0.2), (0, 0, 0.42), 0.06, 0.05, seg=6), P["sack"], False),
        (tube((0, 0, 0.36), (0, 0, 0.98), 0.5, 0.0, seg=10), green, False),
        (tube((0, 0, 0.78), (0, 0, 1.32), 0.4, 0.0, seg=10), green, False),
        (tube((0, 0, 1.15), (0, 0, 1.62), 0.28, 0.0, seg=9), green, False),
    ]
    # Baubles round each tier, just proud of its slope.
    colours = [P["harness"], P["bell"], P["gift_a"], material("Bauble Silver", (0.8, 0.82, 0.86), rough=0.25)]
    k = 0
    for base, top, r, n in ((0.36, 0.98, 0.5, 7), (0.78, 1.32, 0.4, 6), (1.15, 1.62, 0.28, 4)):
        for i in range(n):
            a = 2 * math.pi * (i + 0.5 * (k % 2)) / n
            f = 0.18
            z = base + (top - base) * f
            rr = r * (1 - f) + 0.03
            parts.append((ball(1, 6, 4, scale=(0.045, 0.045, 0.045), at=(math.cos(a) * rr, math.sin(a) * rr, z)), colours[(i + k) % len(colours)], True))
        k += 1
    # The star: two flat five-pointed shapes crossed, gold.
    star = bmesh.new()
    pts = []
    for i in range(10):
        a = math.pi / 2 + i * math.pi / 5
        r = 0.13 if i % 2 == 0 else 0.055
        pts.append((math.cos(a) * r, math.sin(a) * r))
    front = [star.verts.new((x, -0.02, z)) for x, z in pts]
    back = [star.verts.new((x, 0.02, z)) for x, z in pts]
    star.faces.new(front)
    star.faces.new(list(reversed(back)))
    for i in range(10):
        j = (i + 1) % 10
        star.faces.new((front[i], front[j], back[j], back[i]))
    place(star, (1, 1, 1), None, (0, 0, 1.72))
    parts.append((star, P["trim"], False))
    root = bpy.data.objects.new("christmas-tree", None)
    bpy.context.scene.collection.objects.link(root)
    for bm, mat, smooth in parts:
        obj("tree-part", bm, mat, smooth, parent=root)
    join_children(root, "tree")
    return root


def present(P):
    root = bpy.data.objects.new("present", None)
    bpy.context.scene.collection.objects.link(root)
    parts = [
        (box((0.5, 0.42, 0.34), at=(0, 0, 0.17)), material("Wrapping Red", (0.75, 0.05, 0.08), rough=0.5), False),
        (box((0.52, 0.08, 0.36), at=(0, 0, 0.17)), P["trim"], False),
        (box((0.08, 0.44, 0.36), at=(0, 0, 0.17)), P["trim"], False),
    ]
    # The bow: two loops and their tails.
    for s in (1, -1):
        parts.append((ring(0.06, 0.018, at=(0.06 * s, 0, 0.38), seg=10, rot=mathutils.Matrix.Rotation(math.pi / 2, 4, "X") @ mathutils.Matrix.Rotation(0.5 * s, 4, "Y")), P["trim"], False))
    parts.append((ball(1, 6, 4, scale=(0.035, 0.035, 0.03), at=(0, 0, 0.36)), P["trim"], True))
    for bm, mat, smooth in parts:
        obj("present-part", bm, mat, smooth, parent=root)
    join_children(root, "present-mesh")
    return root


def export(root, path):
    bpy.context.view_layer.update()
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_apply=True, export_yup=True,
                              export_animations=False, export_lights=False, export_cameras=False)
    print(f"wrote {path}")


def main():
    import sys
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    previews = argv[0] if argv else None

    clear()
    P = palette()
    parts = []
    build_santa(P, parts)
    write_skinned(os.path.join(MODELS, "people", "santa.glb"), parts, MATS)
    if previews:
        preview(os.path.join(previews, "santa-front.png"), (0, 0, 1.0), 4.2, 0.6, yaw=200)
        preview(os.path.join(previews, "santa-side.png"), (0, 0, 1.0), 4.2, 0.4, yaw=120)

    clear()
    MATS.clear()
    P = palette()
    root = build_sleigh(P)
    # Nose down Blender's -y, which the exporter makes glTF's +z (the cars' front).
    root.rotation_euler = (0, 0, math.pi / 2)
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    out = os.path.join(MODELS, "planes", "santa-sleigh.glb")
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_apply=True, export_yup=True,
                              export_animations=False, export_lights=False, export_cameras=False)
    print(f"wrote {out}")
    root.rotation_euler = (0, 0, 0)
    bpy.context.view_layer.update()
    if previews:
        preview(os.path.join(previews, "sleigh.png"), (-1.8, 0, 0.6), 9.5, 3.5, yaw=-130, lens=40)
        preview(os.path.join(previews, "sleigh-close.png"), (0.2, 0, 0.7), 3.6, 1.6, yaw=-150)

    for name, build in (("christmas-tree", christmas_tree), ("present", present)):
        clear()
        MATS.clear()
        P = palette()
        build(P)
        export(None, os.path.join(MODELS, "props", f"{name}.glb"))
        if previews:
            preview(os.path.join(previews, f"{name}.png"), (0, 0, 0.8 if name == "christmas-tree" else 0.2), 4.0 if name == "christmas-tree" else 1.6, 1.0, yaw=-60)


main()
