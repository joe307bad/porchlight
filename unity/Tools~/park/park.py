# A park's furniture and a playground, exported to Models/props/ as glTF for
# the model library (Editor/Library.cs, Catalog.PARK):
#
#   blender --background --python unity/Tools~/park/park.py [-- previews-dir]
#
#   park-bench      a slatted wooden bench on cast-iron ends: four seat slats,
#                   three back slats leaning back, armrests. 1.8 m long, the
#                   seat 0.45 m up.
#   picnic-table    a wooden picnic table with its benches joined on, on two
#                   A-frames of crossed legs. 1.8 m long, the top 0.76 m up.
#   slide           a playground slide: a platform 1.5 m up on four blue posts
#                   under a little red roof, a ladder up the back, rails round
#                   it, and a yellow chute curving down to a run-out.
#   swings          an A-frame swing set, 2.3 m to the bar, with two seats.
#                   Each seat and its chains is a node of its own ("seat-1",
#                   "seat-2"), its origin on the bar where it hangs, so the
#                   game can swing it by turning it about its x.
#   climbing-frame  a jungle gym: a lattice of bars three cells by two and
#                   three high, 0.6 m a cell, with a deck in the middle.
#   seesaw          a seesaw on its stand: the beam, seats and handles are a
#                   node ("beam") with its origin on the pivot 0.5 m up, so
#                   the game can tip it about its z (glTF's terms).
#   spinner         a playground roundabout: a 2 m deck of red and yellow
#                   sectors on its hub, with grab rails. The deck and rails
#                   are a node ("deck") with its origin at the hub, so the game
#                   can turn it about its y (glTF's terms).
#   park-bin        a park's litter bin: a green slatted drum on a plinth
#                   under a domed lid, with the opening dark under the lid.
#
# Blender is z-up with the front facing -y; the glTF exporter turns that into
# y-up facing +z, as the library's cars face. Each stands on the ground at the
# origin, its footprint centred on it. Sizes are metres, as they are; the game
# is 4 m a tile and draws the street models at 0.2 tiles a metre. Each prints
# its size (wide along x, deep along y, high) as it's made.

import math
import os
import sys

import bmesh
import bpy
import mathutils

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "streets"))
from shapes import V, bezier, bounds, box, clear, export, footprint, material, node, plate, preview, root, sweep, tube  # noqa: E402


def palette():
    return {
        "wood": material("park wood", (0.42, 0.22, 0.09), rough=0.8),
        "wood_light": material("park wood light", (0.55, 0.33, 0.14), rough=0.8),
        "iron": material("park iron", (0.03, 0.08, 0.05), rough=0.5, metal=0.3),
        "red": material("play red", (0.72, 0.07, 0.05), rough=0.5),
        "yellow": material("play yellow", (0.92, 0.66, 0.04), rough=0.5),
        "blue": material("play blue", (0.05, 0.22, 0.62), rough=0.5),
        "green": material("play green", (0.08, 0.42, 0.14), rough=0.5),
        "steel": material("play steel", (0.6, 0.62, 0.63), rough=0.4, metal=0.7),
        "rubber": material("rubber", (0.04, 0.04, 0.04), rough=0.8),
        "bin": material("bin green", (0.06, 0.24, 0.12), rough=0.5),
        "dark": material("bin dark", (0.01, 0.01, 0.01), rough=0.9),
        "concrete": material("concrete", (0.42, 0.41, 0.38), rough=0.95),
    }


def turned(angle):
    """A turn about x (leaning a slat back)."""
    return mathutils.Matrix.Rotation(angle, 4, "X")


def channel(path, width, wall, height, thick):
    """A slide's chute: a U-section `width` wide inside, its walls `height` high, swept along a path in the y-z plane."""
    pts = [V(p) for p in path]
    a, b = width / 2, width / 2 + wall
    section = [(-b, height), (-b, -thick), (b, -thick), (b, height), (a, height), (a, 0), (-a, 0), (-a, height)]
    bm = bmesh.new()
    rings = []
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        side = V((1, 0, 0))
        up = side.cross(t).normalized()
        if up.z < 0:
            up = -up
        rings.append([bm.verts.new(p + side * u + up * v) for u, v in section])
    n = len(section)
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(n):
            j = (k + 1) % n
            bm.faces.new((r0[k], r0[j], r1[j], r1[k]))
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def park_bench(P):
    parts = []
    for s in (1, -1):
        x = 0.82 * s
        # A cast end: front leg, back leg running up into the back's support, seat rail, armrest and its upright.
        parts += [
            (box((0.06, 0.06, 0.45), at=(x, -0.22, 0.225)), P["iron"]),
            (sweep([(x, 0.24, 0), (x, 0.2, 0.44), (x, 0.26, 0.62), (x, 0.32, 0.86)], 0.03, seg=4), P["iron"]),
            (box((0.06, 0.52, 0.05), at=(x, 0.0, 0.42)), P["iron"]),
            (box((0.06, 0.06, 0.22), at=(x, -0.22, 0.55)), P["iron"]),
            (box((0.07, 0.48, 0.04), at=(x, 0.0, 0.66), bevel=0.01), P["iron"]),
            (box((0.12, 0.1, 0.02), at=(x, -0.22, 0.01)), P["iron"]),
            (box((0.12, 0.1, 0.02), at=(x, 0.24, 0.01)), P["iron"]),
        ]
    for k in range(4):
        parts.append((box((1.8, 0.1, 0.035), at=(0, -0.21 + k * 0.125, 0.462), bevel=0.008), P["wood"]))
    for k in range(3):
        f = 0.25 + k * 0.3
        parts.append((box((1.8, 0.1, 0.03), at=(0, 0.2 + 0.11 * f + 0.02, 0.48 + 0.38 * f), rot=turned(-0.3), bevel=0.008), P["wood"]))
    node("park-bench", parts)


def picnic_table(P):
    parts = []
    # The top: five planks along its length.
    for k in range(5):
        parts.append((box((1.8, 0.14, 0.04), at=(0, -0.3 + k * 0.15, 0.74), bevel=0.006), P["wood_light"]))
    # The benches: two planks each side.
    for s in (1, -1):
        for k in range(2):
            parts.append((box((1.8, 0.13, 0.04), at=(0, s * (0.55 + k * 0.14), 0.44), bevel=0.006), P["wood_light"]))
    for x in (0.62, -0.62):
        # The A-frame: two crossed legs, the bench bearer through them, and a bearer under the top.
        for s in (1, -1):
            parts.append((sweep([(x, s * 0.62, 0), (x, -s * 0.2, 0.72)], 0.035, seg=4), P["wood"]))
        parts.append((box((0.06, 1.5, 0.08), at=(x, 0, 0.38)), P["wood"]))
        parts.append((box((0.06, 0.74, 0.05), at=(x, 0, 0.695)), P["wood"]))
    # A brace under the top, from each bench bearer to the middle.
    for x in (0.62, -0.62):
        parts.append((sweep([(x * 0.95, 0, 0.38), (0, 0, 0.69)], 0.025, seg=4), P["wood"]))
    node("picnic-table", parts)


def slide(P):
    parts = []
    deck_z, back, front = 1.5, 1.25, 0.5
    # Four posts up to the roof, the platform between them, rails round its sides and a roof over it.
    for x in (0.38, -0.38):
        for y in (front, back):
            parts.append((tube((x, y, 0), (x, y, 2.4), 0.045, seg=6), P["blue"]))
            parts.append((tube((x, y, 2.4), (x, y, 2.46), 0.06, seg=6), P["yellow"]))
        parts.append((box((0.04, back - front, 0.04), at=(x, (back + front) / 2, deck_z + 0.55)), P["blue"]))
        parts.append((box((0.03, back - front, 0.03), at=(x, (back + front) / 2, deck_z + 0.3)), P["blue"]))
    parts.append((box((0.8, back - front + 0.06, 0.06), at=(0, (back + front) / 2, deck_z - 0.03)), P["red"]))
    roof = bmesh.new()
    bmesh.ops.create_cone(roof, cap_ends=True, segments=4, radius1=0.68, radius2=0.0, depth=0.4)
    bmesh.ops.rotate(roof, verts=roof.verts, cent=(0, 0, 0), matrix=mathutils.Matrix.Rotation(math.pi / 4, 3, "Z"))
    bmesh.ops.translate(roof, verts=roof.verts, vec=(0, (back + front) / 2, 2.6))
    parts.append((roof, P["red"]))
    # The ladder up the back, leaning in.
    foot, top = back + 0.55, back
    for x in (0.25, -0.25):
        parts.append((tube((x, foot, 0), (x, top, deck_z + 0.05), 0.035, seg=6), P["blue"]))
    for k in range(1, 6):
        f = k / 6
        parts.append((tube((-0.25, foot + (top - foot) * f, deck_z * f), (0.25, foot + (top - foot) * f, deck_z * f), 0.022, seg=6), P["steel"]))
    # The chute: down from the platform's front edge in a curve, and a flat run-out at the foot.
    path = bezier((0, front + 0.02, deck_z), (0, front - 0.7, deck_z - 0.15), (0, -1.3, 0.38), (0, -1.85, 0.32), 12)
    path.append(V((0, -2.15, 0.32)))
    parts.append((channel(path, 0.5, 0.04, 0.16, 0.03), P["yellow"]))
    # Its legs at the run-out.
    for x in (0.25, -0.25):
        parts.append((tube((x, -1.95, 0), (x, -1.95, 0.3), 0.03, seg=6), P["blue"]))
    node("slide", parts)


def swings(P):
    top = 2.3
    frame = root("swings")
    parts = [(tube((-1.55, 0, top), (1.55, 0, top), 0.05, seg=8), P["red"])]
    for x in (1.45, -1.45):
        for s in (1, -1):
            parts.append((tube((x, s * 0.9, 0), (x, 0, top), 0.045, seg=6), P["green"]))
        # A stay across the A-frame's legs, and the clamp on top.
        parts.append((tube((x, 0.62, 0.7), (x, -0.62, 0.7), 0.03, seg=6), P["green"]))
        parts.append((box((0.14, 0.14, 0.14), at=(x, 0, top), bevel=0.02), P["green"]))
    node("frame", parts, parent=frame)
    for n, x in ((1, 0.6), (2, -0.6)):
        seat = [
            (box((0.46, 0.18, 0.03), at=(x, 0, 0.45), bevel=0.01), P["rubber"]),
        ]
        for dx in (0.2, -0.2):
            seat.append((tube((x + dx, 0, 0.45), (x + dx, 0, top), 0.012, seg=4), P["steel"]))
            seat.append((tube((x + dx, 0, top - 0.06), (x + dx, 0, top + 0.04), 0.03, seg=6), P["steel"]))
        node(f"seat-{n}", seat, parent=frame, pivot=(x, 0, top))


def climbing_frame(P):
    cell, nx, ny, nz = 0.6, 3, 2, 3
    x0, y0 = -nx * cell / 2, -ny * cell / 2
    level = (P["yellow"], P["blue"], P["green"])
    parts = []
    for i in range(nx + 1):
        for j in range(ny + 1):
            parts.append((tube((x0 + i * cell, y0 + j * cell, 0), (x0 + i * cell, y0 + j * cell, nz * cell + 0.04), 0.03, seg=6), P["red"]))
            parts.append((tube((x0 + i * cell, y0 + j * cell, nz * cell + 0.04), (x0 + i * cell, y0 + j * cell, nz * cell + 0.08), 0.04, seg=6), P["yellow"]))
    for k in range(1, nz + 1):
        z = k * cell
        for j in range(ny + 1):
            parts.append((tube((x0, y0 + j * cell, z), (-x0, y0 + j * cell, z), 0.022, seg=6), level[k - 1]))
        for i in range(nx + 1):
            parts.append((tube((x0 + i * cell, y0, z), (x0 + i * cell, -y0, z), 0.022, seg=6), level[k - 1]))
    # A deck across the middle cells, a level up.
    parts.append((box((cell - 0.06, ny * cell - 0.06, 0.04), at=(0, 0, cell * 2 + 0.03)), P["wood_light"]))
    node("climbing-frame", parts)


def seesaw(P):
    pivot = 0.5
    frame = root("seesaw")
    stand = [
        (box((0.3, 0.5, 0.04), at=(0, 0, 0.02)), P["concrete"]),
        (tube((0, -0.17, pivot), (0, 0.17, pivot), 0.035, seg=6), P["steel"]),
    ]
    for s in (1, -1):
        for dx in (0.18, -0.18):
            stand.append((tube((dx, s * 0.15, 0.03), (0, s * 0.15, pivot), 0.035, seg=6), P["blue"]))
    node("stand", stand, parent=frame)
    beam = [
        (box((3.0, 0.18, 0.07), at=(0, 0, pivot + 0.07), bevel=0.01), P["red"]),
        (box((0.16, 0.22, 0.1), at=(0, 0, pivot + 0.03)), P["red"]),
    ]
    for s in (1, -1):
        # A seat pad at each end, a T-handle before it, and a rubber bumper under it.
        beam.append((box((0.32, 0.26, 0.05), at=(s * 1.32, 0, pivot + 0.13), bevel=0.015), P["rubber"]))
        beam.append((tube((s * 1.05, 0, pivot + 0.1), (s * 1.05, 0, pivot + 0.42), 0.025, seg=6), P["yellow"]))
        beam.append((tube((s * 1.05, -0.15, pivot + 0.42), (s * 1.05, 0.15, pivot + 0.42), 0.025, seg=6), P["yellow"]))
        beam.append((tube((s * 1.32, 0, pivot + 0.035), (s * 1.32, 0, pivot - 0.06), 0.08, seg=8), P["rubber"]))
    node("beam", beam, parent=frame, pivot=(0, 0, pivot))


def spinner(P):
    r, deck_z = 1.0, 0.24
    frame = root("spinner")
    base = [
        (tube((0, 0, 0), (0, 0, 0.03), 0.4, seg=12), P["concrete"]),
        (tube((0, 0, 0.03), (0, 0, deck_z - 0.04), 0.16, 0.12, seg=8), P["steel"]),
    ]
    node("base", base, parent=frame)
    deck = []
    # The deck: six sectors, red and yellow, and a steel rim.
    for k in range(6):
        a0 = k * math.pi / 3
        outline = [(0, 0)] + [(r * math.cos(a0 + i * math.pi / 6), r * math.sin(a0 + i * math.pi / 6)) for i in range(3)]
        deck.append((plate(outline, 0.06, "z", at=(0, 0, deck_z)), P["red"] if k % 2 == 0 else P["yellow"]))
    deck.append((tube((0, 0, deck_z - 0.04), (0, 0, deck_z + 0.04), r + 0.03, seg=12, caps=False), P["steel"]))
    # Grab rails: a post in the middle and six bent rails out from it to the deck's edge.
    deck.append((tube((0, 0, deck_z), (0, 0, deck_z + 0.75), 0.05, seg=8), P["blue"]))
    deck.append((tube((0, 0, deck_z + 0.75), (0, 0, deck_z + 0.8), 0.08, seg=8), P["blue"]))
    for k in range(6):
        a = k * math.pi / 3 + math.pi / 6
        c, s = math.cos(a), math.sin(a)
        rail = [(0.05 * c, 0.05 * s, deck_z + 0.7), (0.65 * c, 0.65 * s, deck_z + 0.7), (0.78 * c, 0.78 * s, deck_z + 0.55),
                (0.8 * c, 0.8 * s, deck_z + 0.02)]
        deck.append((sweep(rail, 0.022, seg=6), P["blue"]))
    node("deck", deck, parent=frame)


def park_bin(P):
    parts = [
        (tube((0, 0, 0), (0, 0, 0.06), 0.3, seg=10), P["concrete"]),
        (tube((0, 0, 0.06), (0, 0, 0.86), 0.25, seg=10), P["bin"]),
        (tube((0, 0, 0.12), (0, 0, 0.16), 0.265, seg=10), P["bin"]),
        (tube((0, 0, 0.8), (0, 0, 0.86), 0.27, seg=10), P["bin"]),
        # The opening, dark under the lid, and the lid on top of it.
        (tube((0, 0, 0.86), (0, 0, 0.98), 0.2, seg=10), P["dark"]),
        (tube((0, 0, 0.98), (0, 0, 1.01), 0.28, seg=10), P["bin"]),
        (tube((0, 0, 1.01), (0, 0, 1.12), 0.28, 0.12, seg=10), P["bin"]),
        (tube((0, 0, 1.12), (0, 0, 1.15), 0.08, seg=8), P["bin"]),
    ]
    # The opening's posts (three, holding the lid up) and the drum's slats.
    for k in range(3):
        a = k * 2 * math.pi / 3
        parts.append((box((0.04, 0.04, 0.13), at=(0.24 * math.cos(a), 0.24 * math.sin(a), 0.92)), P["bin"]))
    for k in range(10):
        a = (k + 0.5) * 2 * math.pi / 10
        parts.append((box((0.05, 0.03, 0.62), at=(0.25 * math.cos(a), 0.25 * math.sin(a), 0.48),
                          rot=mathutils.Matrix.Rotation(a, 4, "Z")), P["bin"]))
    node("park-bin", parts)


MODELS = [
    ("park-bench", park_bench, (0, 0, 0.5), 3.0),
    ("picnic-table", picnic_table, (0, 0, 0.5), 3.4),
    ("slide", slide, (0, -0.3, 1.2), 6.5),
    ("swings", swings, (0, 0, 1.2), 6.0),
    ("climbing-frame", climbing_frame, (0, 0, 0.9), 4.5),
    ("seesaw", seesaw, (0, 0, 0.5), 4.5),
    ("spinner", spinner, (0, 0, 0.5), 3.8),
    ("park-bin", park_bin, (0, 0, 0.55), 2.2),
]


def centre():
    """Moves the model so its footprint is centred on the origin (a root's children move with it)."""
    lo, hi = bounds()
    shift = V((-(lo.x + hi.x) / 2, -(lo.y + hi.y) / 2, 0))
    for ob in bpy.context.scene.objects:
        if ob.parent is not None:
            continue
        if ob.type == "MESH":
            ob.data.transform(mathutils.Matrix.Translation(shift))
        else:
            ob.location += shift


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    previews = argv[0] if argv else None
    for name, build, target, distance in MODELS:
        clear()
        build(palette())
        centre()
        footprint(name)
        export("props", name)
        if previews:
            preview(os.path.join(previews, f"{name}.png"), target, distance, distance * 0.45, yaw=-60, lens=40)


main()
