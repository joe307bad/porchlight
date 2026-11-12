# Thanksgiving props for the town, exported to Models/props/ as glTF for the
# model library (Editor/Library.cs):
#
#   blender --background --python unity/Tools~/thanksgiving/turkeys.py
#
#   frozen-turkey      a whole frozen turkey in its shrink-wrap: plump body
#                      breast up, drumsticks tucked at the tail end, the bag
#                      gathered and clipped at the neck end, red netting
#                      round it and a label on top. The turkey mission piles
#                      them in the back of your pickup and carries them in.
#   roast-turkey       a roast turkey, golden brown, paper frills on its
#                      drumsticks, on an oval platter with parsley and orange
#   bowl-mash          mashed potatoes, a pat of butter melting on top
#   bowl-cranberry     cranberry sauce
#   bowl-beans         green beans
#   bowl-stuffing      stuffing
#   pumpkin-pie        a pumpkin pie in its dish, one slice's worth of cream
#
# The last six are the feast that the turkey mission sets on every house's
# dining table once it's done (Rendering/Feast.cs lays them out).
#
# Blender is z-up with the front facing -y; the glTF exporter turns that into
# y-up facing +z. Sizes are metres; the game scales each to the size it wants.

import math
import os
import random
import sys

import bpy
import bmesh
import mathutils

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Porchlight", "Models", "props"))


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


def mesh_object(name, bm, mat, smooth=True):
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


def join(name, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    objects[0].name = name
    objects[0].data.name = name
    return objects[0]


def solidify(ob, thickness):
    mod = ob.modifiers.new("solid", "SOLIDIFY")
    mod.thickness = thickness
    mod.offset = 1
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    ob.select_set(False)


def export(name):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, f"{name}.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_yup=True, export_apply=True, export_materials="EXPORT")
    names = sorted(o.name for o in bpy.context.scene.objects)
    print(f"thanksgiving: {path} parts={names}")


# A turkey's body: an ellipsoid, flat underneath, fuller over the breast.


class Bird:
    def __init__(self, a, b, c, base):
        # Half-width (x), half-length (y), half-height (z), and how high its middle stands.
        self.a, self.b, self.c, self.base = a, b, c, base
        self.floor = base - c * 0.62

    def shape(self, x, y, z):
        """Squash the bottom flat (it rests on it) and swell the breast."""
        if z < self.floor + 0.03:
            z = self.floor + (z - self.floor) * 0.25 if z > self.floor else self.floor - (self.floor - z) * 0.0
            z = max(z, self.floor)
        top = max(0.0, (z - self.base) / self.c)
        # The breast: fuller towards the front (-y), a ridge along the middle.
        z += self.c * 0.12 * top * math.exp(-(x / self.a) ** 2 * 3) * (1 - 0.4 * (y / self.b))
        return x, y, z

    def surface(self, y, theta, out=0.0):
        """The point on the body at length y and angle theta round it (0: the +x side, pi/2: the top)."""
        k = max(0.0, 1 - (y / self.b) ** 2)
        r = math.sqrt(k)
        x = self.a * r * math.cos(theta)
        z = self.base + self.c * r * math.sin(theta)
        n = mathutils.Vector((x / self.a ** 2, y / self.b ** 2, (z - self.base) / self.c ** 2)).normalized()
        x, y2, z = self.shape(x, y, z)
        return mathutils.Vector((x, y2, z)) + n * out

    def body(self, name, mat):
        bm = bmesh.new()
        bmesh.ops.create_uvsphere(bm, u_segments=28, v_segments=18, radius=1)
        for v in bm.verts:
            x, y, z = v.co.x * self.a, v.co.y * self.b, v.co.z * self.c + self.base
            v.co = self.shape(x, y, z)
        return mesh_object(name, bm, mat)

    def band(self, name, mat, y0, y1, t0, t1, out, steps=24, along=False):
        """A strip lying on the body: round it (from angle t0 to t1, y0..y1 wide), or along it (y0..y1 long, t0..t1 wide)."""
        bm = bmesh.new()
        rows = []
        n = steps
        for i in range(n + 1):
            f = i / n
            row = []
            for j in (0, 1):
                if along:
                    y, t = y0 + (y1 - y0) * f, t0 + (t1 - t0) * j
                else:
                    y, t = y0 + (y1 - y0) * j, t0 + (t1 - t0) * f
                row.append(bm.verts.new(self.surface(y, t, out)))
            rows.append(row)
        for i in range(n):
            a, b = rows[i], rows[i + 1]
            face = bm.faces.new((a[0], b[0], b[1], a[1]))
        # Face outwards, away from the body's middle.
        mid = mathutils.Vector((0, 0, self.base))
        for f in bm.faces:
            if f.normal.dot(f.calc_center_median() - mid) < 0:
                f.normal_flip()
        ob = mesh_object(name, bm, mat)
        solidify(ob, 0.003)
        return ob


def limb(bm, start, end, r0, r1, segments=10):
    """A tapered cylinder from start to end."""
    start, end = mathutils.Vector(start), mathutils.Vector(end)
    d = end - start
    res = bmesh.ops.create_cone(bm, cap_ends=True, segments=segments, radius1=r0, radius2=r1, depth=d.length)
    rot = mathutils.Vector((0, 0, 1)).rotation_difference(d.normalized()).to_matrix()
    for v in res["verts"]:
        v.co = rot @ v.co + (start + end) / 2


def ball(bm, at, r, scale=(1, 1, 1), segments=12, rings=8):
    res = bmesh.ops.create_uvsphere(bm, u_segments=segments, v_segments=rings, radius=r)
    for v in res["verts"]:
        v.co = mathutils.Vector((v.co.x * scale[0], v.co.y * scale[1], v.co.z * scale[2])) + mathutils.Vector(at)


def drumsticks(bird, name, mat, frills=None, lift=1.0):
    """Two drumsticks at the tail end (+y), pointing back and up, a knuckle on each end."""
    bm = bmesh.new()
    tips = []
    for side in (-1, 1):
        root = (side * bird.a * 0.42, bird.b * 0.55, bird.base + bird.c * 0.15)
        tip = (side * bird.a * 0.3, bird.b * 1.12, bird.base + bird.c * (0.55 + 0.35 * lift))
        limb(bm, root, tip, bird.a * 0.3, bird.a * 0.15)
        # The thigh, round where it joins.
        ball(bm, root, bird.a * 0.36, (1, 1.2, 0.9))
        if frills is None:
            ball(bm, tip, bird.a * 0.17)
        tips.append(tip)
    ob = mesh_object(name, bm, mat)
    if frills is not None:
        bm = bmesh.new()
        for tip in tips:
            t = mathutils.Vector(tip)
            limb(bm, t - mathutils.Vector((0, 0.004, 0.004)), t + mathutils.Vector((0, 0.03, 0.025)), bird.a * 0.16, bird.a * 0.24, 12)
        mesh_object(name + "-frills", bm, frills, smooth=False)
    return ob


def frozen_turkey():
    clear()
    # About 36 cm long, 28 cm across and 20 cm tall: a twelve-pounder.
    bird = Bird(0.14, 0.18, 0.105, 0.105)
    skin = material("frozen-skin", (0.95, 0.79, 0.66), 0.22)
    net = material("netting-red", (0.72, 0.06, 0.05), 0.55)
    label = material("label-gold", (0.96, 0.74, 0.18), 0.4)
    label_red = material("label-red", (0.80, 0.16, 0.08), 0.4)
    clip = material("clip-metal", (0.75, 0.76, 0.78), 0.3, 0.8)
    frost = material("frost", (0.97, 0.98, 1.0), 0.5)

    body = bird.body("body", skin)
    legs = drumsticks(bird, "drumsticks", skin, lift=0.4)
    # The bag gathered at the neck end (-y), twisted and clipped, the end fanned out.
    bm = bmesh.new()
    neck = bird.surface(-bird.b * 0.97, math.pi / 2, 0)
    neck.z = bird.base + bird.c * 0.1
    limb(bm, neck + mathutils.Vector((0, 0.02, 0)), neck + mathutils.Vector((0, -0.05, 0.004)), 0.045, 0.012, 12)
    limb(bm, neck + mathutils.Vector((0, -0.05, 0.004)), neck + mathutils.Vector((0, -0.085, 0.01)), 0.012, 0.035, 12)
    tail = mesh_object("wrap", bm, skin)
    tail.scale = (1, 1, 0.75)
    bm = bmesh.new()
    limb(bm, neck + mathutils.Vector((0, -0.04, 0.003)), neck + mathutils.Vector((0, -0.058, 0.005)), 0.017, 0.017, 12)
    mesh_object("clip", bm, clip)
    # Red netting: bands round it and two along it.
    bands = [bird.band("net", net, y, y + 0.012, -0.35, math.pi + 0.35, 0.004) for y in (-0.11, -0.04, 0.03, 0.1)]
    bands += [bird.band("net", net, -0.16, 0.15, t, t + 0.06, 0.004, along=True) for t in (math.pi * 0.32, math.pi * 0.62)]
    join("netting", bands)
    # The label on top, gold with a red stripe across it.
    bird.band("label", label, -0.075, 0.055, math.pi * 0.36, math.pi * 0.64, 0.006, steps=10)
    bird.band("label-stripe", label_red, -0.02, 0.005, math.pi * 0.37, math.pi * 0.63, 0.0085, steps=10)
    # A little frost in the folds.
    bm = bmesh.new()
    random.seed(7)
    for _ in range(26):
        y = random.uniform(-0.15, 0.14)
        t = random.uniform(-0.2, math.pi + 0.2)
        ball(bm, bird.surface(y, t, 0.002), random.uniform(0.006, 0.012), (1, 1, 0.4), 6, 4)
    mesh_object("frost", bm, frost)
    export("frozen-turkey")


def platter(rx, ry, mat):
    """An oval platter: a shallow dish with a raised rim."""
    bm = bmesh.new()
    res = bmesh.ops.create_cone(bm, cap_ends=True, segments=32, radius1=0.8, radius2=1.0, depth=1)
    for v in res["verts"]:
        v.co = mathutils.Vector((v.co.x * rx, v.co.y * ry, (v.co.z + 0.5) * 0.018))
    ob = mesh_object("platter", bm, mat)
    bm = bmesh.new()
    res = bmesh.ops.create_circle(bm, cap_ends=False, segments=40, radius=1)
    ring = []
    for v in res["verts"]:
        v.co = mathutils.Vector((v.co.x * rx, v.co.y * ry, 0.018))
    rim = mesh_object("rim", bm, mat)
    mod = rim.modifiers.new("skin", "SKIN")
    bpy.context.view_layer.objects.active = rim
    rim.select_set(True)
    for sv in rim.data.skin_vertices[0].data:
        sv.radius = (0.009, 0.006)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    rim.select_set(False)
    for p in rim.data.polygons:
        p.use_smooth = True
    return join("platter", [ob, rim])


def roast_turkey():
    clear()
    china = material("china-white", (0.95, 0.95, 0.92), 0.25)
    roast = material("roast-brown", (0.66, 0.31, 0.08), 0.38)
    paper = material("frill-paper", (0.98, 0.98, 0.96), 0.7)
    parsley = material("parsley", (0.16, 0.45, 0.12), 0.6)
    orange = material("orange", (0.95, 0.5, 0.08), 0.5)
    platter(0.2, 0.27, china)
    bird = Bird(0.12, 0.155, 0.085, 0.018 + 0.085 * 0.62)
    bird.body("roast", roast)
    drumsticks(bird, "drumsticks", roast, frills=paper, lift=0.5)
    # Parsley and orange slices round it.
    bm = bmesh.new()
    random.seed(3)
    for k in range(14):
        t = k / 14 * math.tau + random.uniform(-0.1, 0.1)
        ball(bm, (math.cos(t) * 0.155, math.sin(t) * 0.215, 0.026), random.uniform(0.012, 0.018), (1, 1, 0.6), 8, 5)
    mesh_object("parsley", bm, parsley)
    bm = bmesh.new()
    for k in range(6):
        t = (k + 0.5) / 6 * math.tau
        at = mathutils.Vector((math.cos(t) * 0.165, math.sin(t) * 0.225, 0.024))
        res = bmesh.ops.create_cone(bm, cap_ends=True, segments=12, radius1=0.022, radius2=0.022, depth=0.008)
        for v in res["verts"]:
            v.co += at
    mesh_object("orange", bm, orange)
    export("roast-turkey")


def bowl(radius, height, mat):
    """A round bowl: a solid hemisphere's shell, on a little foot."""
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=12, radius=1)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z > 0.001], context="VERTS")
    for v in bm.verts:
        v.co = mathutils.Vector((v.co.x * radius, v.co.y * radius, (v.co.z + 1) * height))
    # Flat underneath, where it sits.
    for v in bm.verts:
        if v.co.z < height * 0.12:
            v.co.z = height * 0.12
    for f in bm.faces:
        if f.normal.dot(f.calc_center_median() - mathutils.Vector((0, 0, height))) < 0:
            f.normal_flip()
    ob = mesh_object("bowl", bm, mat)
    solidify(ob, 0.006)
    bm = bmesh.new()
    res = bmesh.ops.create_cone(bm, cap_ends=True, segments=20, radius1=radius * 0.45, radius2=radius * 0.45, depth=height * 0.12)
    for v in res["verts"]:
        v.co.z += height * 0.06
    foot = mesh_object("foot", bm, mat)
    return join("bowl", [ob, foot])


def mound(name, mat, radius, z, rise, lumps=0, seed=1):
    """What's in a bowl: a disc filling it to z, heaped up by rise in the middle (lumpy if asked)."""
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=10, radius=1)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.co.z < -0.001], context="VERTS")
    random.seed(seed)
    for v in bm.verts:
        n = v.co.copy()
        wob = 1 + (random.uniform(-0.12, 0.12) if lumps and n.z > 0.05 else 0)
        v.co = mathutils.Vector((n.x * radius, n.y * radius, z + n.z * rise * wob))
    # Close it underneath.
    edge = [e for e in bm.edges if e.is_boundary]
    bmesh.ops.holes_fill(bm, edges=edge)
    return mesh_object(name, bm, mat)


def bowl_of(name, dish_rgb, fill, extra=None):
    clear()
    r, h = 0.075, 0.055
    dish = material("dish", dish_rgb, 0.3)
    bowl(r, h, dish)
    fill(r, h)
    if extra:
        extra(r, h)
    export(name)


def mash(r, h):
    mound("mash", material("mash", (0.96, 0.91, 0.76), 0.8), r * 0.93, h * 0.9, h * 0.65, lumps=1, seed=4)
    bm = bmesh.new()
    res = bmesh.ops.create_cube(bm, size=1)
    for v in res["verts"]:
        v.co = mathutils.Vector((v.co.x * 0.026, v.co.y * 0.022, v.co.z * 0.009 + h * 1.52))
    mesh_object("butter", bm, material("butter", (1.0, 0.86, 0.35), 0.3), smooth=False)


def cranberry(r, h):
    mound("cranberry", material("cranberry", (0.5, 0.02, 0.07), 0.15), r * 0.92, h * 0.85, h * 0.25)


def beans(r, h):
    green = material("beans", (0.2, 0.5, 0.14), 0.55)
    mound("beans-bed", green, r * 0.92, h * 0.85, h * 0.3)
    bm = bmesh.new()
    random.seed(11)
    for _ in range(22):
        a = random.uniform(0, math.tau)
        d = random.uniform(0, r * 0.65)
        at = mathutils.Vector((math.cos(a) * d, math.sin(a) * d, h * 0.85 + h * 0.3 * (1 - (d / r) ** 2) + 0.004))
        t = random.uniform(0, math.tau)
        dirv = mathutils.Vector((math.cos(t), math.sin(t), random.uniform(-0.15, 0.15))) * 0.022
        limb(bm, at - dirv, at + dirv, 0.0045, 0.0035, 6)
    mesh_object("beans", bm, green)
    bm = bmesh.new()
    for _ in range(10):
        a = random.uniform(0, math.tau)
        d = random.uniform(0, r * 0.6)
        ball(bm, (math.cos(a) * d, math.sin(a) * d, h * 0.85 + h * 0.3 * (1 - (d / r) ** 2) + 0.006), 0.006, (1, 1, 0.6), 6, 4)
    mesh_object("almonds", bm, material("almonds", (0.9, 0.78, 0.55), 0.6))


def stuffing(r, h):
    brown = material("stuffing", (0.6, 0.4, 0.2), 0.85)
    mound("stuffing-bed", brown, r * 0.92, h * 0.9, h * 0.45, lumps=1, seed=9)
    bm = bmesh.new()
    random.seed(5)
    for _ in range(18):
        a = random.uniform(0, math.tau)
        d = random.uniform(0, r * 0.7)
        res = bmesh.ops.create_cube(bm, size=random.uniform(0.012, 0.018))
        rot = mathutils.Euler((random.uniform(0, 3), random.uniform(0, 3), random.uniform(0, 3))).to_matrix()
        at = mathutils.Vector((math.cos(a) * d, math.sin(a) * d, h * 0.9 + h * 0.45 * (1 - (d / r) ** 2)))
        for v in res["verts"]:
            v.co = rot @ v.co + at
    mesh_object("croutons", bm, material("croutons", (0.78, 0.55, 0.28), 0.8), smooth=False)
    bm = bmesh.new()
    for _ in range(8):
        a = random.uniform(0, math.tau)
        d = random.uniform(0, r * 0.6)
        ball(bm, (math.cos(a) * d, math.sin(a) * d, h * 0.9 + h * 0.45 * (1 - (d / r) ** 2) + 0.003), 0.005, (1, 1, 0.5), 6, 4)
    mesh_object("herbs", bm, material("herbs", (0.2, 0.42, 0.15), 0.7))


def pumpkin_pie():
    clear()
    tin = material("pie-dish", (0.86, 0.55, 0.36), 0.4)
    crust = material("crust", (0.86, 0.64, 0.36), 0.8)
    filling = material("pumpkin", (0.8, 0.42, 0.1), 0.45)
    cream = material("cream", (0.99, 0.98, 0.94), 0.6)
    r = 0.11
    bm = bmesh.new()
    res = bmesh.ops.create_cone(bm, cap_ends=True, segments=32, radius1=r * 0.82, radius2=r, depth=0.03)
    for v in res["verts"]:
        v.co.z += 0.015
    mesh_object("dish", bm, tin)
    bm = bmesh.new()
    res = bmesh.ops.create_cone(bm, cap_ends=True, segments=32, radius1=r * 0.9, radius2=r * 0.9, depth=0.008)
    for v in res["verts"]:
        v.co.z += 0.031
    mesh_object("filling", bm, filling)
    # The crimped crust round the edge.
    bm = bmesh.new()
    for k in range(22):
        t = k / 22 * math.tau
        ball(bm, (math.cos(t) * r * 0.93, math.sin(t) * r * 0.93, 0.034), 0.012, (1, 1, 0.7), 8, 5)
    mesh_object("crust", bm, crust)
    bm = bmesh.new()
    for k in range(3):
        ball(bm, (0, 0, 0.038 + k * 0.009), 0.02 - k * 0.006, (1, 1, 0.8), 10, 6)
    mesh_object("cream", bm, cream)
    export("pumpkin-pie")


frozen_turkey()
roast_turkey()
bowl_of("bowl-mash", (0.95, 0.95, 0.92), mash)
bowl_of("bowl-cranberry", (0.95, 0.95, 0.92), cranberry)
bowl_of("bowl-beans", (0.55, 0.68, 0.82), beans)
bowl_of("bowl-stuffing", (0.82, 0.45, 0.3), stuffing)
pumpkin_pie()
sys.exit(0)
