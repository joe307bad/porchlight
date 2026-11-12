"""The lantern, from one small picture (lantern.png: white on blue) to every
size the game needs. Needs Pillow and numpy:

    python3 -m venv .venv && .venv/bin/pip install pillow numpy
    .venv/bin/python desktop/branding/make.py

The white is lifted out as a soft mask, enlarged 16x and its edge sharpened
again, so the icon is crisp at 1024 though the picture is under 200 across.
"""
import os
from PIL import Image, ImageDraw, ImageFilter
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
UNITY = os.path.join(ROOT, 'unity', 'Assets', 'Porchlight')

src = np.asarray(Image.open(os.path.join(HERE, 'lantern.png')).convert('RGB')).astype(float)
border = np.concatenate([src[0], src[-1], src[:, 0], src[:, -1]])
blue = np.median(border, axis=0)
white = np.clip((src[:, :, 0] - blue[0]) / (255 - blue[0]), 0, 1)
ys, xs = np.where(white > 0.5)
pad, S = 3, 16
crop = white[ys.min() - pad:ys.max() + pad + 1, xs.min() - pad:xs.max() + pad + 1]
m = Image.fromarray((crop * 255).astype(np.uint8)).resize((crop.shape[1] * S, crop.shape[0] * S), Image.LANCZOS)
m = m.filter(ImageFilter.GaussianBlur(S * 0.45))
a = np.clip((np.asarray(m).astype(float) / 255 - 0.5) / 0.06 + 0.5, 0, 1)
mask = Image.fromarray((a * 255).astype(np.uint8))
mask = mask.crop(mask.getbbox())
BLUE = tuple(int(round(x)) for x in blue)


def fit(height):
    k = height / mask.size[1]
    return mask.resize((max(1, round(mask.size[0] * k)), height), Image.LANCZOS)


def coloured(m, rgb):
    out = Image.new('RGBA', m.size, rgb + (255,))
    out.putalpha(m)
    return out


def scaled(rgba, size):
    """Smaller, the colour and the cover scaled apart: scaled together (straight alpha), what's under
    the clear pixels bleeds into the edge, a light or dark rim round the shape."""
    rgb = rgba.convert('RGB').resize(size, Image.LANCZOS)
    out = rgb.convert('RGBA')
    out.putalpha(rgba.getchannel('A').resize(size, Image.LANCZOS))
    return out


# The logo: white on nothing.
coloured(fit(512), (255, 255, 255)).save(os.path.join(UNITY, 'Branding', 'Logo.png'))

# The sign-in card's: shown about 50 px tall, with the title's shadow (4 px down and right, solid black).
H = 256
m = fit(H)
off = round(4 * H / 50)
card = Image.new('RGBA', (m.size[0] + off, H + off), (0, 0, 0, 0))
card.alpha_composite(coloured(m, (0, 0, 0)), (off, off))
card.alpha_composite(coloured(m, (255, 255, 255)), (0, 0))
card.save(os.path.join(UNITY, 'Resources', 'Porchlight', 'UI', 'LogoShadow.png'))


def icon(size, inset, radius):
    k = 4
    n = size * k
    # Blue all over, even where it's clear (so the clear corners are blue underneath, and scaling the
    # icon, here or by the system, can't draw a dark or white rim round it), the lantern on it, and
    # the rounded square as the cover.
    img = Image.new('RGBA', (n, n), BLUE + (255,))
    i = int(inset * n)
    lm = fit(int((n - 2 * i) * 0.66))
    img.alpha_composite(coloured(lm, (255, 255, 255)), ((n - lm.size[0]) // 2, (n - lm.size[1]) // 2))
    cover = Image.new('L', (n, n), 0)
    ImageDraw.Draw(cover).rounded_rectangle([i, i, n - i - 1, n - i - 1], radius=int(radius * n), fill=255)
    img.putalpha(cover)
    return scaled(img, (size, size))


# The app icon: macOS's grid (824 of 1024, corners 185), which Unity makes every size of.
icon(1024, 100 / 1024, 185 / 1024).save(os.path.join(UNITY, 'Branding', 'AppIcon.png'))
# Windows: the whole square, a little rounded, every size in one .ico.
# Each size made on its own (left to the .ico writer, the big one was shrunk with a rim round it).
ICO = [16, 24, 32, 48, 64, 128, 256]
frames = [icon(s, 0, 0.12) for s in ICO]
frames[-1].save(os.path.join(ROOT, 'desktop', 'AppIcon.ico'), sizes=[(s, s) for s in ICO], append_images=frames[:-1])
print('lantern made; blue is #%02x%02x%02x' % BLUE)
