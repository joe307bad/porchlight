# Traffic signals, stop signs and street lights for the towns' streets,
# exported to Models/props/ as glTF for the model library (Editor/Library.cs):
#
#   blender --background --python unity/Tools~/streets/signals.py [-- previews-dir]
#
#   traffic-signal   a mast-arm signal, as American towns hang them over the
#                    lanes: a concrete footing and a cast base, a tapered pole
#                    with a handhole and a cap, an arm on a flange plate
#                    tapering out over the road with a brace under it, and a
#                    three-lamp head hanging from the arm over the lane, on a
#                    clamp, in a black backplate with a yellow border. A second
#                    head is on the pole for drivers stopped close to it. Each
#                    lamp has a tunnel visor over it. The lenses are dark: the
#                    game lights the one showing with a lamp of its own in
#                    front of it (Rendering/Signage.cs). A pedestrian head on
#                    the pole looks out across the road, and there's a push
#                    button under it. Those two are dark and never change.
#   stop-sign        a stop sign: a red octagon, white border and STOP in white
#                    block letters, two bolts through it, an aluminium back
#                    with two stiffeners clamped to a square post, and the
#                    post's anchor sleeve at the foot.
#   street-light     a street lamp: a concrete footing, a fluted cast base, a
#                    tapered pole with a collar and a finial, and a curved
#                    arm with a scroll under it, reaching 1.5 m out over the
#                    street to a lantern hung from its end: a roof with a
#                    finial, a six-post cage and a collar under it. The glass
#                    is the game's own glowing bulb, which sits in the cage
#                    (Rendering/WorldParts.cs, AddLamps).
#
# All three stand with the post's foot at the origin, the arm reaching along -y
# and the faces looking along +x. The glTF exporter turns Blender's z-up into
# y-up (the front, -y, becoming +z), so in the game the arm reaches forwards
# (+z) and the faces look to the model's left (-x): Signage.cs turns each so the
# arm reaches over its lane and the faces look back down it at the drivers
# coming. Sizes are metres (a 5.6 m pole and a 4.2 m arm, a 0.8 m sign at 2 m,
# a 4.75 m lamp reaching 1.5 m); the game draws them at 0.2 tiles a metre.
# Signage.cs keeps the lenses' places (LENSES) in step with the signal's, and
# AddLamps the bulb's (LAMP_HEIGHT, LAMP_REACH) with the street light's: move
# them together.

import math
import os
import sys

import bmesh

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from shapes import ball, bezier, box, clear, export, footprint, material, node, octagon, plate, preview, rounded_rect, sweep, tube  # noqa: E402

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

# The street light: its height and reach, and where the bulb sits in its lantern.
LAMP_H = 4.75
LAMP_REACH = 1.5
BULB_Z = 4.5


def visor(centre, r, length, seg=8, gap=math.radians(70)):
    """A tunnel visor over a lens facing +x: a tube round it, open in a slot underneath `gap` wide."""
    cx, cy, cz = centre
    a0 = -math.pi / 2 + gap / 2
    a1 = 3 * math.pi / 2 - gap / 2
    outer, inner = r * 1.18, r * 1.08
    bm = bmesh.new()
    rings = []
    for k in range(seg + 1):
        t = a0 + (a1 - a0) * k / seg
        y, z = math.cos(t), math.sin(t)
        rings.append([
            bm.verts.new((cx, cy + y * outer, cz + z * outer)),
            bm.verts.new((cx + length, cy + y * outer, cz + z * outer)),
            bm.verts.new((cx + length, cy + y * inner, cz + z * inner)),
            bm.verts.new((cx, cy + y * inner, cz + z * inner)),
        ])
    for a, b in zip(rings, rings[1:]):
        for i in range(4):
            j = (i + 1) % 4
            bm.faces.new((a[i], a[j], b[j], b[i]))
    for ring in (rings[0], rings[-1]):
        bm.faces.new(ring)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def head(parts, centre, M, plate_on=True):
    """A three-lamp head facing +x, its middle at `centre`: housing, backplate, lenses (red, amber, green) and visors."""
    x, y, z = centre
    front = x + HEAD_DEPTH / 2
    parts.append((box((HEAD_DEPTH, HEAD_WIDTH, HEAD_HEIGHT), at=(x, y, z), bevel=0.03), M["head"]))
    if plate_on:
        # The backplate (louvred plates are black; the yellow border is the retroreflective tape towns add).
        pw, ph = HEAD_WIDTH + 0.26, HEAD_HEIGHT + 0.12
        back = x - HEAD_DEPTH / 2
        parts.append((plate(rounded_rect(pw, ph, 0.05), 0.025, "x", at=(back - 0.0125, y, z)), M["plate"]))
        t, f = 0.045, back + 0.003
        for s in (1, -1):
            parts.append((box((0.006, t, ph - 0.1), at=(f, y + s * (pw / 2 - t / 2 - 0.01), z)), M["border"]))
            parts.append((box((0.006, pw - 0.1, t), at=(f, y, z + s * (ph / 2 - t / 2 - 0.01))), M["border"]))
    for k, lens in enumerate((M["red"], M["amber"], M["green"])):
        lz = z + LENS_STEP * (1 - k)
        # The lens door's rim, the lens, and the visor over it.
        parts.append((tube((front - 0.005, y, lz), (front + 0.005, y, lz), LENS_R + 0.025, seg=12), M["head"]))
        parts.append((tube((front - 0.005, y, lz), (front + 0.015, y, lz), LENS_R, seg=12), lens))
        parts.append((visor((front, y, lz), LENS_R, 0.24), M["head"]))


def pedestrian_head(parts, M, z):
    """A pedestrian signal on the pole, looking out over the road (-y): housing, hood, and its dark hand and walker."""
    yc = -0.27
    parts.append((box((0.06, 0.16, 0.26), at=(0, -0.13, z)), M["metal"]))
    parts.append((box((0.34, 0.24, 0.34), at=(0, yc, z), bevel=0.02), M["head"]))
    face = yc - 0.12
    parts.append((box((0.28, 0.01, 0.28), at=(0, face - 0.005, z)), M["plate"]))
    # The hood: a top and two sides, sticking out over the face.
    parts.append((box((0.34, 0.12, 0.015), at=(0, face - 0.06, z + 0.16)), M["head"]))
    for s in (1, -1):
        parts.append((box((0.015, 0.12, 0.33), at=(s * 0.162, face - 0.06, z)), M["head"]))
    parts.append((box((0.09, 0.006, 0.11), at=(-0.065, face - 0.012, z)), M["hand"]))
    parts.append((box((0.06, 0.006, 0.12), at=(0.065, face - 0.012, z)), M["walker"]))


def traffic_signal(previews):
    clear()
    M = {
        "metal": material("signal metal", (0.24, 0.26, 0.27), rough=0.45, metal=0.6),
        "base": material("signal base", (0.55, 0.55, 0.52), rough=0.9),
        "concrete": material("concrete", (0.42, 0.41, 0.38), rough=0.95),
        "head": material("signal head", (0.05, 0.05, 0.05), rough=0.5),
        "plate": material("signal plate", (0.03, 0.03, 0.03), rough=0.7),
        "border": material("plate border", (0.85, 0.65, 0.05), rough=0.4),
        "red": material("lens red", (0.28, 0.04, 0.03), rough=0.2),
        "amber": material("lens amber", (0.28, 0.18, 0.02), rough=0.2),
        "green": material("lens green", (0.02, 0.2, 0.09), rough=0.2),
        "hand": material("lens hand", (0.3, 0.12, 0.02), rough=0.3),
        "walker": material("lens walker", (0.3, 0.3, 0.28), rough=0.3),
        "button": material("button yellow", (0.8, 0.6, 0.05), rough=0.4),
        "sign": material("sign white", (0.9, 0.9, 0.88), rough=0.5),
    }
    parts = [
        # The footing, its anchor bolts, and the cast base the pole stands in.
        (box((0.6, 0.6, 0.08), at=(0, 0, 0.04)), M["concrete"]),
        (tube((0, 0, 0.08), (0, 0, 0.5), 0.2, 0.135, seg=8), M["base"]),
        (tube((0, 0, 0.5), (0, 0, 0.56), 0.15, seg=8), M["base"]),
        (tube((0, 0, 0.56), (0, 0, POLE_H), 0.115, 0.085, seg=8), M["metal"]),
        (box((0.02, 0.1, 0.2), at=(-0.115, 0, 0.85)), M["metal"]),
        (tube((0, 0, POLE_H), (0, 0, POLE_H + 0.06), 0.1, seg=8), M["metal"]),
        (tube((0, 0, POLE_H + 0.06), (0, 0, POLE_H + 0.16), 0.1, 0.02, seg=8), M["metal"]),
        # The arm on its flange plate, tapering out over the road to an end cap.
        (box((0.26, 0.05, 0.36), at=(0, -0.1, ARM_Z)), M["metal"]),
        (tube((0, -0.12, ARM_Z), (0, -ARM_LEN, ARM_Z), 0.1, 0.055, seg=8), M["metal"]),
        (tube((0, -ARM_LEN, ARM_Z), (0, -ARM_LEN - 0.04, ARM_Z), 0.065, 0.045, seg=8), M["metal"]),
        # The brace under it, clamped to the pole.
        (tube((0, 0, ARM_Z - 0.78), (0, 0, ARM_Z - 0.68), 0.11, seg=8), M["metal"]),
        (tube((0, -0.08, ARM_Z - 0.74), (0, -1.4, ARM_Z - 0.03), 0.035, seg=6), M["metal"]),
        # The head over the lane: a clamp on the arm and a short pipe down to the head's top.
        (box((0.16, 0.16, 0.16), at=(0, ARM_HEAD_Y, ARM_Z), bevel=0.02), M["metal"]),
        (tube((0, ARM_HEAD_Y, ARM_HEAD_Z + HEAD_HEIGHT / 2 - 0.02), (0, ARM_HEAD_Y, ARM_Z), 0.03, seg=6), M["metal"]),
    ]
    for c in ((1, 1), (1, -1), (-1, 1), (-1, -1)):
        parts.append((tube((0.2 * c[0], 0.2 * c[1], 0.08), (0.2 * c[0], 0.2 * c[1], 0.13), 0.025, seg=6), M["metal"]))
    head(parts, (0, ARM_HEAD_Y, ARM_HEAD_Z), M)
    # The head on the pole, on brackets top and bottom.
    for dz in (0.42, -0.42):
        parts.append((box((POLE_HEAD_X - 0.05, 0.07, 0.06), at=((POLE_HEAD_X - 0.05) / 2 + 0.06, 0, POLE_HEAD_Z + dz)), M["metal"]))
    head(parts, (POLE_HEAD_X + 0.02, 0, POLE_HEAD_Z), M, plate_on=False)
    pedestrian_head(parts, M, 2.55)
    # The push button and its little sign, at hand height.
    parts.append((box((0.1, 0.06, 0.14), at=(0, -0.13, 1.1), bevel=0.01), M["button"]))
    parts.append((tube((0, -0.16, 1.08), (0, -0.175, 1.08), 0.025, seg=8), M["head"]))
    parts.append((box((0.11, 0.01, 0.14), at=(0, -0.165, 1.28)), M["sign"]))
    node("traffic-signal", parts)
    footprint("traffic-signal")
    export("props", "traffic-signal")
    if previews:
        preview(os.path.join(previews, "traffic-signal.png"), (0.0, -1.8, 3.2), 9.5, 1.5, yaw=-20, lens=40)
        preview(os.path.join(previews, "traffic-signal-head.png"), (0.1, -3.6, 4.7), 3.2, 0.5, yaw=30, lens=50)
        preview(os.path.join(previews, "traffic-signal-pole.png"), (0.1, 0, 1.8), 6.0, 0.8, yaw=-60, lens=50)


def letters(parts, x, centre_y, centre_z, mat):
    """STOP in block letters on a plane facing +x: read left to right along +y (the right of someone facing -x)."""
    h, w, s, gap = 0.2, 0.12, 0.038, 0.028
    total = 4 * w + 3 * gap
    y0 = centre_y - total / 2
    z0 = centre_z - h / 2
    count = [0]

    def letter(k, strokes):
        base = y0 + k * (w + gap)
        for y, z, dy, dz in strokes:
            # A stroke from (y, z), dy wide and dz high, in the letter's own frame (y from its left, z from its
            # foot); each a hair proud of the last, so where two cross only one face shows.
            count[0] += 1
            parts.append((box((0.006, dy, dz), at=(x + 0.0006 * count[0], base + y + dy / 2, z0 + z + dz / 2)), mat))

    top, mid, bot = h - s, (h - s) / 2, 0
    letter(0, [(0, top, w, s), (0, mid, w, s), (0, bot, w, s), (0, mid, s, h / 2), (w - s, bot, s, h / 2)])  # S
    letter(1, [(0, top, w, s), ((w - s) / 2, 0, s, h)])  # T
    letter(2, [(0, top, w, s), (0, bot, w, s), (0, 0, s, h), (w - s, 0, s, h)])  # O
    letter(3, [(0, top, w, s), (0, mid, w, s), (0, 0, s, h), (w - s, mid, s, h / 2)])  # P


def stop_sign(previews):
    clear()
    M = {
        "post": material("sign post", (0.6, 0.62, 0.63), rough=0.4, metal=0.7),
        "red": material("stop red", (0.62, 0.04, 0.04), rough=0.5),
        "white": material("stop white", (0.92, 0.92, 0.9), rough=0.5),
        "back": material("sign back", (0.55, 0.56, 0.56), rough=0.6, metal=0.4),
        "bolt": material("bolt", (0.7, 0.7, 0.68), rough=0.35, metal=0.8),
        "concrete": material("concrete", (0.42, 0.41, 0.38), rough=0.95),
    }
    z = 2.0
    parts = [
        # The anchor sleeve the post drops into, its bolt, and the post with a cap.
        (box((0.09, 0.09, 0.04), at=(0, 0, 0.02)), M["concrete"]),
        (box((0.07, 0.07, 0.36), at=(0, 0, 0.18)), M["post"]),
        (box((0.02, 0.09, 0.02), at=(0, 0, 0.28)), M["bolt"]),
        (box((0.055, 0.055, 2.45), at=(0, 0, 1.225)), M["post"]),
        (box((0.065, 0.065, 0.02), at=(0, 0, 2.45)), M["post"]),
        # The sign's back, with two stiffeners across it clamped to the post.
        (plate(octagon(0.4), 0.02, "x", at=(0.075, 0, z)), M["back"]),
        (plate(octagon(0.4), 0.01, "x", at=(0.09, 0, z)), M["white"]),
        (plate(octagon(0.36), 0.006, "x", at=(0.097, 0, z)), M["red"]),
    ]
    for dz in (0.18, -0.18):
        parts.append((box((0.025, 0.56, 0.04), at=(0.0525, 0, z + dz)), M["back"]))
        parts.append((box((0.08, 0.075, 0.05), at=(0.01, 0, z + dz)), M["bolt"]))
    # The bolts through the face, above and below the word.
    for dz in (0.29, -0.29):
        parts.append((tube((0.098, 0, z + dz), (0.108, 0, z + dz), 0.016, seg=6), M["bolt"]))
    letters(parts, 0.103, 0, z, M["white"])
    node("stop-sign", parts)
    footprint("stop-sign")
    export("props", "stop-sign")
    if previews:
        preview(os.path.join(previews, "stop-sign.png"), (0.05, 0, 1.6), 4.0, 0.4, yaw=15, lens=50)
        preview(os.path.join(previews, "stop-sign-back.png"), (0.05, 0, 1.6), 4.0, 0.4, yaw=200, lens=50)


def street_light(previews):
    clear()
    M = {
        # The old lamps' post colour (World.cs C.lampPost, 0x3b3f47), in linear terms.
        "iron": material("lamp iron", (0.044, 0.05, 0.063), rough=0.5, metal=0.3),
        "concrete": material("concrete", (0.42, 0.41, 0.38), rough=0.95),
        "brass": material("lamp brass", (0.55, 0.42, 0.15), rough=0.35, metal=0.6),
    }
    ly = -LAMP_REACH
    parts = [
        # The footing, the fluted base (an octagon going in at a collar), and the tapered pole.
        (tube((0, 0, 0), (0, 0, 0.08), 0.28, seg=8), M["concrete"]),
        (tube((0, 0, 0.08), (0, 0, 0.55), 0.21, 0.15, seg=8), M["iron"]),
        (tube((0, 0, 0.55), (0, 0, 0.62), 0.17, seg=8), M["iron"]),
        (tube((0, 0, 0.62), (0, 0, 0.95), 0.13, 0.095, seg=8), M["iron"]),
        (tube((0, 0, 0.95), (0, 0, 4.6), 0.09, 0.06, seg=8), M["iron"]),
        (tube((0, 0, 3.0), (0, 0, 3.07), 0.085, seg=8), M["iron"]),
        # The finial on top.
        (tube((0, 0, 4.6), (0, 0, 4.65), 0.075, seg=8), M["iron"]),
        (ball(0.06, 8, 5, at=(0, 0, 4.7)), M["brass"]),
        (tube((0, 0, 4.75), (0, 0, 4.85), 0.025, 0.0, seg=6), M["brass"]),
        # The arm: up out of the pole's side and over in a curve to the lantern, a scroll under it, and a ball at its end.
        (sweep(bezier((0, -0.04, 4.25), (0, -0.1, 4.75), (0, -0.7, 4.86), (0, ly - 0.08, 4.84), 10), 0.04, seg=6), M["iron"]),
        (sweep(bezier((0, -0.05, 3.85), (0, -0.25, 3.9), (0, -0.25, 4.5), (0, -0.62, 4.78), 8), 0.022, seg=6), M["iron"]),
        (ball(0.05, 8, 5, at=(0, ly - 0.1, 4.84)), M["iron"]),
        # The lantern: a hook, a roof with a finial and eaves, a six-post cage, and a collar and drop under it.
        (tube((0, ly, 4.72), (0, ly, 4.84), 0.02, seg=6), M["iron"]),
        (tube((0, ly, 4.6), (0, ly, 4.74), 0.25, 0.04, seg=6), M["iron"]),
        (tube((0, ly, 4.57), (0, ly, 4.6), 0.25, seg=6), M["iron"]),
        (tube((0, ly, 4.3), (0, ly, 4.35), 0.13, seg=6), M["iron"]),
        (tube((0, ly, 4.3), (0, ly, 4.2), 0.07, 0.0, seg=6), M["brass"]),
    ]
    for k in range(6):
        a = math.pi / 6 + k * math.pi / 3
        top = (0.205 * math.cos(a), ly + 0.205 * math.sin(a), 4.575)
        bottom = (0.125 * math.cos(a), ly + 0.125 * math.sin(a), 4.34)
        parts.append((tube(bottom, top, 0.012, seg=4), M["iron"]))
    node("street-light", parts)
    footprint("street-light")
    export("props", "street-light")
    if previews:
        # Where the game's bulb sits, to see it in its cage (not exported).
        node("bulb", [(ball(1, 12, 8, scale=(0.15, 0.15, 0.175), at=(0, ly, BULB_Z)), material("bulb", (1.0, 0.95, 0.8)))])
        preview(os.path.join(previews, "street-light.png"), (0, -0.7, 2.5), 8.0, 1.0, yaw=-30, lens=45)
        preview(os.path.join(previews, "street-light-head.png"), (0, -1.0, 4.45), 2.6, 0.3, yaw=-20, lens=50)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    previews = argv[0] if argv else None
    traffic_signal(previews)
    stop_sign(previews)
    street_light(previews)


main()
