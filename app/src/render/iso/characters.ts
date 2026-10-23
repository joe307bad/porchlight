// Little townsfolk drawn in the same flat style as the Kenney tiles. Each look
// (skin, hair, clothes, maybe a hat) is baked once into front and back walk
// cycles; facing left reuses the right-facing frames mirrored.

import { Texture } from 'pixi.js';
import type { Rng } from '../../core/rng';

export interface Look {
  skin: string;
  hair: string;
  hairStyle: 'short' | 'long' | 'bald';
  shirt: string;
  pants: string;
  shoes: string;
  hat: string | null;
  /** Dress instead of trousers. */
  dress: boolean;
}

const SKINS = ['#f6d7b8', '#e9b98f', '#d39a6a', '#a8714a', '#7a4e33', '#5a3825'];
const HAIRS = ['#2b2018', '#4a3020', '#7a4a24', '#c48a3a', '#e3c27a', '#9a9a9a', '#b5452f'];
const SHIRTS = ['#d9534f', '#4a7fc1', '#5cae5c', '#e8b13c', '#8e5bb5', '#f0efe8', '#2f3b4c', '#e07a3a', '#3fa7a0', '#c2457a'];
const PANTS = ['#2f3b4c', '#4b5a73', '#5a4632', '#3b3b3b', '#6b7f99', '#7d6a4f'];
const SHOES = ['#2b2b2b', '#5a3a22', '#7a7a7a'];
const HATS = ['#3b3b3b', '#b5452f', '#e8d9a8', '#4a7fc1'];

export function randomLook(rng: Rng): Look {
  return {
    skin: rng.pick(SKINS),
    hair: rng.pick(HAIRS),
    hairStyle: rng.pick(['short', 'short', 'long', 'long', 'bald'] as const),
    shirt: rng.pick(SHIRTS),
    pants: rng.pick(PANTS),
    shoes: rng.pick(SHOES),
    hat: rng.chance(0.2) ? rng.pick(HATS) : null,
    dress: rng.chance(0.2),
  };
}

export interface WalkFrames {
  /** Facing the camera (moving down-screen), walking right; frame 0 is standing. */
  front: Texture[];
  /** Facing away (moving up-screen), walking right. */
  back: Texture[];
  /** The same frames as solid white silhouettes (tint them for outlines). */
  frontMask: Texture[];
  backMask: Texture[];
}

/** Drawn at 2x and displayed at half size, so edges stay crisp when zoomed in. */
export const CHAR_SCALE = 0.5;
const W = 44;
const H = 84;
/** Feet sit here in the frame; sprites anchor at this point. */
export const CHAR_ANCHOR = { x: 0.5, y: 78 / H };

const WALK_FRAMES = 4;

function frame(look: Look, back: boolean, step: number): HTMLCanvasElement {
  const c = document.createElement('canvas');
  c.width = W;
  c.height = H;
  const ctx = c.getContext('2d')!;
  const cx = W / 2;
  const feet = 78;
  // step 0 = standing; 1..4 = walk cycle.
  const phase = step === 0 ? 0 : Math.sin(((step - 1) / WALK_FRAMES) * Math.PI * 2);
  const bob = step === 0 ? 0 : Math.abs(phase) * -2;
  const stride = phase * 5;

  const rect = (x: number, y: number, w: number, h: number, r: number, fill: string) => {
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, r);
    ctx.fillStyle = fill;
    ctx.fill();
  };

  // Shadow.
  ctx.fillStyle = 'rgba(0,0,0,0.22)';
  ctx.beginPath();
  ctx.ellipse(cx, feet, 13, 5, 0, 0, Math.PI * 2);
  ctx.fill();

  const hip = feet - 22 + bob;
  // Legs (the far leg first, slightly darker).
  const legs: [number, number, string][] = [
    [cx - 6 - stride, -stride, shade(look.pants, -0.12)],
    [cx + 1 + stride, stride, look.pants],
  ];
  for (const [x, lift, color] of legs) {
    rect(x, hip, 6, feet - hip - 3 - Math.max(0, lift) * 0.4, 3, color);
    rect(x - 1, feet - 5 - Math.max(0, lift) * 0.4, 8, 5, 2, look.shoes);
  }

  // Body.
  const shoulder = hip - 20;
  if (look.dress) {
    ctx.beginPath();
    ctx.moveTo(cx - 8, shoulder + 4);
    ctx.lineTo(cx + 8, shoulder + 4);
    ctx.lineTo(cx + 12, hip + 8);
    ctx.lineTo(cx - 12, hip + 8);
    ctx.closePath();
    ctx.fillStyle = look.shirt;
    ctx.fill();
  }
  rect(cx - 9, shoulder, 18, hip - shoulder + 3, 6, look.shirt);
  // Arms swing opposite to the legs.
  const arm = -stride * 0.8;
  rect(cx - 13, shoulder + 2 + arm * 0.3, 5, 16, 3, shade(look.shirt, -0.15));
  rect(cx + 8, shoulder + 2 - arm * 0.3, 5, 16, 3, shade(look.shirt, -0.15));
  rect(cx - 13, shoulder + 16 + arm * 0.3, 5, 5, 3, look.skin);
  rect(cx + 8, shoulder + 16 - arm * 0.3, 5, 5, 3, look.skin);

  // Head.
  const headY = shoulder - 11;
  ctx.beginPath();
  ctx.arc(cx, headY, 10, 0, Math.PI * 2);
  ctx.fillStyle = look.skin;
  ctx.fill();
  if (look.hairStyle !== 'bald' || back) {
    ctx.fillStyle = look.hairStyle === 'bald' ? shade(look.skin, -0.08) : look.hair;
    ctx.beginPath();
    if (back) {
      ctx.arc(cx, headY, 10.5, 0, Math.PI * 2);
      ctx.fill();
      if (look.hairStyle === 'long') rect(cx - 10, headY, 20, 14, 5, look.hair);
    } else {
      ctx.arc(cx, headY - 1, 10.5, Math.PI * 1.05, Math.PI * 1.95);
      ctx.fill();
      if (look.hairStyle === 'long') {
        rect(cx - 11, headY - 4, 5, 16, 3, look.hair);
        rect(cx + 6, headY - 4, 5, 16, 3, look.hair);
      }
    }
  }
  if (!back) {
    // Eyes looking slightly right (frames are mirrored for left).
    ctx.fillStyle = '#2b2018';
    ctx.fillRect(cx - 1, headY, 2.5, 3);
    ctx.fillRect(cx + 5, headY, 2.5, 3);
    ctx.fillStyle = shade(look.skin, -0.2);
    ctx.fillRect(cx + 2, headY + 5, 4, 1.5);
  }
  if (look.hat) {
    rect(cx - 13, headY - 7, 26, 4, 2, shade(look.hat, -0.1));
    rect(cx - 8, headY - 15, 16, 10, 3, look.hat);
  }
  return c;
}

export function walkFrames(look: Look): WalkFrames {
  const canvases = (back: boolean) => Array.from({ length: WALK_FRAMES + 1 }, (_, step) => frame(look, back, step));
  const front = canvases(false);
  const back = canvases(true);
  return {
    front: front.map((c) => Texture.from(c)),
    back: back.map((c) => Texture.from(c)),
    frontMask: front.map((c) => Texture.from(silhouette(c))),
    backMask: back.map((c) => Texture.from(silhouette(c))),
  };
}

/** A copy of a frame with every visible pixel turned white (minus the ground shadow). */
function silhouette(src: HTMLCanvasElement): HTMLCanvasElement {
  const c = document.createElement('canvas');
  c.width = src.width;
  c.height = src.height;
  const ctx = c.getContext('2d')!;
  ctx.drawImage(src, 0, 0);
  // Drop the soft ground shadow so only the figure is outlined.
  const img = ctx.getImageData(0, 0, c.width, c.height);
  for (let i = 0; i < img.data.length; i += 4) img.data[i + 3] = img.data[i + 3] > 120 ? 255 : 0;
  ctx.putImageData(img, 0, 0);
  ctx.globalCompositeOperation = 'source-in';
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(0, 0, c.width, c.height);
  return c;
}

export const WALK_FRAME_COUNT = WALK_FRAMES;

/** Lighten (amount > 0) or darken (amount < 0) a #rrggbb colour. */
function shade(hex: string, amount: number): string {
  const n = parseInt(hex.slice(1), 16);
  const f = (v: number) => Math.round(Math.min(255, Math.max(0, amount < 0 ? v * (1 + amount) : v + (255 - v) * amount)));
  const r = f((n >> 16) & 255);
  const g = f((n >> 8) & 255);
  const b = f(n & 255);
  return `#${((r << 16) | (g << 8) | b).toString(16).padStart(6, '0')}`;
}
