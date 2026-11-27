# Low-poly shapes for the street and park models (signals.py, park/park.py):
# boxes, tubes, swept curves and flat plates, each built as a bmesh in metres,
# Blender's z-up, then made into objects, joined into a node or two a model and
# exported to glTF. Flat-shaded and few-sided, as the rest of the town is.

import math
import os

import bmesh
import bpy
import mathutils

V = mathutils.Vector
HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Porchlight", "Models"))


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    MATS.clear()


MATS = {}


def material(name, rgb, rough=0.6, metal=0.0):
    """A plain colour (the library makes each a matte URP Lit one, keyed by colour)."""
    if name in MATS:
        return MATS[name]
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    m.diffuse_color = (*rgb, 1)
    MATS[name] = m
    return m


def place(bm, scale=(1, 1, 1), rot=None, at=(0, 0, 0)):
    m = mathutils.Matrix.Translation(V(at))
    if rot is not None:
        m = m @ rot
    m = m @ mathutils.Matrix.Diagonal(V((*scale, 1)))
    bmesh.ops.transform(bm, matrix=m, verts=bm.verts)
    return bm


def box(size, at=(0, 0, 0), rot=None, bevel=0.0):
    """A box `size` big, its middle at `at`; `bevel` chamfers its edges (once: still low-poly)."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    place(bm, size)
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=1, affect="EDGES")
    return place(bm, (1, 1, 1), rot, at)


def tube(a, b, r1, r2=None, seg=8, caps=True):
    """A cylinder (or a cone, tapering to r2) from point a to point b."""
    a, b = V(a), V(b)
    r2 = r1 if r2 is None else r2
    d = b - a
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=caps, segments=seg, radius1=r1, radius2=r2, depth=d.length)
    rot = d.to_track_quat("Z", "Y").to_matrix().to_4x4()
    return place(bm, (1, 1, 1), rot, (a + b) / 2)


def ball(r=1.0, seg=8, rings=5, scale=(1, 1, 1), at=(0, 0, 0)):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=rings, radius=r)
    return place(bm, scale, None, at)


def sweep(points, radii, seg=8, caps=True):
    """A tube along a path of points, `radii` at each (or one radius for all): bent arms and rails."""
    pts = [V(p) for p in points]
    if not isinstance(radii, (list, tuple)):
        radii = [radii] * len(pts)
    bm = bmesh.new()
    rings = []
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        ref = V((1, 0, 0)) if abs(t.x) < 0.9 else V((0, 1, 0))
        u = t.cross(ref).normalized()
        w = t.cross(u).normalized()
        rings.append([bm.verts.new(p + (u * math.cos(a) + w * math.sin(a)) * radii[i])
                      for a in (2 * math.pi * k / seg for k in range(seg))])
    for a, b in zip(rings, rings[1:]):
        for k in range(seg):
            j = (k + 1) % seg
            bm.faces.new((a[k], a[j], b[j], b[k]))
    if caps:
        bm.faces.new(list(reversed(rings[0])))
        bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def bezier(p0, p1, p2, p3, n):
    """n+1 points along a cubic Bezier curve."""
    p0, p1, p2, p3 = V(p0), V(p1), V(p2), V(p3)
    out = []
    for i in range(n + 1):
        t = i / n
        s = 1 - t
        out.append(s * s * s * p0 + 3 * s * s * t * p1 + 3 * s * t * t * p2 + t * t * t * p3)
    return out


def plate(outline, depth, axis="x", at=(0, 0, 0)):
    """A flat plate: a 2D outline (its own (u, v)) given `depth`, facing +axis, its middle at `at`.
    Facing x, u runs along y and v along z; facing y, u runs along x and v along z; facing z, u is x and v is y."""
    bm = bmesh.new()
    front, back = [], []
    for u, v in outline:
        for d, side in ((depth / 2, front), (-depth / 2, back)):
            if axis == "x":
                co = (d, u, v)
            elif axis == "y":
                co = (u, d, v)
            else:
                co = (u, v, d)
            side.append(bm.verts.new(co))
    bm.faces.new(front)
    bm.faces.new(list(reversed(back)))
    n = len(outline)
    for k in range(n):
        j = (k + 1) % n
        bm.faces.new((front[k], back[k], back[j], front[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return place(bm, (1, 1, 1), None, at)


def octagon(r):
    """An octagon's outline, flat along its top and bottom (a stop sign's)."""
    return [(r * math.cos(math.pi / 8 + k * math.pi / 4), r * math.sin(math.pi / 8 + k * math.pi / 4)) for k in range(8)]


def rounded_rect(w, h, cut):
    """A rectangle's outline with its corners cut off."""
    a, b = w / 2, h / 2
    return [(a, b - cut), (a - cut, b), (-a + cut, b), (-a, b - cut), (-a, -b + cut), (-a + cut, -b), (a - cut, -b), (a, -b + cut)]


def merge(*bms):
    out = bmesh.new()
    for bm in bms:
        me = bpy.data.meshes.new("tmp")
        bm.to_mesh(me)
        bm.free()
        out.from_mesh(me)
        bpy.data.meshes.remove(me)
    return out


def node(name, parts, parent=None, pivot=(0, 0, 0)):
    """One glTF node from parts [(bmesh, material)]: a mesh with a material slot a colour, its origin at `pivot`
    (built in the model's own terms, so the vertices are moved to sit round it)."""
    me = bpy.data.meshes.new(name)
    whole = bmesh.new()
    slots = []
    for bm, mat in parts:
        if mat not in slots:
            slots.append(mat)
        k = slots.index(mat)
        for f in bm.faces:
            f.material_index = k
        tmp = bpy.data.meshes.new("tmp")
        bm.to_mesh(tmp)
        bm.free()
        whole.from_mesh(tmp)
        bpy.data.meshes.remove(tmp)
    bmesh.ops.translate(whole, vec=-V(pivot), verts=whole.verts)
    whole.normal_update()
    whole.to_mesh(me)
    whole.free()
    for p in me.polygons:
        p.use_smooth = False
    for mat in slots:
        me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    ob.location = V(pivot)
    if parent is not None:
        ob.parent = parent
        ob.location = V(pivot) - parent.location
    return ob


def root(name):
    ob = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def bounds():
    """The scene's meshes' box, world terms: (lo, hi)."""
    bpy.context.view_layer.update()
    lo, hi = V((1e9, 1e9, 1e9)), V((-1e9, -1e9, -1e9))
    for ob in bpy.context.scene.objects:
        if ob.type != "MESH":
            continue
        for v in ob.data.vertices:
            w = ob.matrix_world @ v.co
            lo = V((min(lo.x, w.x), min(lo.y, w.y), min(lo.z, w.z)))
            hi = V((max(hi.x, w.x), max(hi.y, w.y), max(hi.z, w.z)))
    return lo, hi


def footprint(name):
    lo, hi = bounds()
    print(f"{name}: {hi.x - lo.x:.2f} wide x {hi.y - lo.y:.2f} deep x {hi.z - lo.z:.2f} high (m)")
    return lo, hi


def export(folder, name):
    out = os.path.join(MODELS, folder)
    os.makedirs(out, exist_ok=True)
    path = os.path.join(out, f"{name}.glb")
    bpy.context.view_layer.update()
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_yup=True, export_apply=True,
                              export_materials="EXPORT", export_animations=False, export_lights=False, export_cameras=False)
    print(f"wrote {path}")


def preview(path, target, distance, height=0.0, yaw=-50, lens=50):
    """A quick render to look at a model by (not shipped): Workbench, each part its material's colour."""
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
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.display.shading.show_shadows = True
    scene.display.shading.show_cavity = True
    scene.display.shading.background_type = "VIEWPORT"
    scene.display.shading.background_color = (0.6, 0.7, 0.82)
    scene.render.resolution_x = 700
    scene.render.resolution_y = 700
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)
