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
    img = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    i = int(inset * n)
    ImageDraw.Draw(img).rounded_rectangle([i, i, n - i - 1, n - i - 1], radius=int(radius * n), fill=BLUE + (255,))
    lm = fit(int((n - 2 * i) * 0.66))
    img.alpha_composite(coloured(lm, (255, 255, 255)), ((n - lm.size[0]) // 2, (n - lm.size[1]) // 2))
    return img.resize((size, size), Image.LANCZOS)


# The app icon: macOS's grid (824 of 1024, corners 185), which Unity makes every size of.
icon(1024, 100 / 1024, 185 / 1024).save(os.path.join(UNITY, 'Branding', 'AppIcon.png'))
# Windows: the whole square, a little rounded, every size in one .ico.
icon(256, 0, 0.12).save(os.path.join(ROOT, 'desktop', 'AppIcon.ico'),
                        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print('lantern made; blue is #%02x%02x%02x' % BLUE)
