// Procedural sprites in Kenney's flat style: the plaza paving block (same
// 132x99 block shape and palette as the pack), plus soft glow, water-streak
// and fountain-droplet sprites. Roads are vectors (see world.ts).

import { Texture } from 'pixi.js';

/** Block sprite geometry, matching Kenney's 132x99 tiles. */
export const BLOCK_W = 132;
export const BLOCK_H = 99;
const CX = 66;
const TOP = 1;
const HW = 65;
const HH = 32;
const THICK = 32;

const PALETTE = {
  asphalt: '#696969',
  asphaltLeft: '#5e5e5e',
  asphaltRight: '#474747',
  sidewalk: '#d8d3bf',
  sidewalkLeft: '#c2beac',
  sidewalkRight: '#979385',
  curb: '#a9a491',
  dirtLeft: '#a77d53',
  dirtRight: '#81603e',
  lane: '#e9e4cf',
  path: '#c9a46e',
  pathEdge: '#b08a57',
  grassEdge: '#8ab549',
  plazaLine: 'rgba(120, 112, 90, 0.35)',
  deck: '#a8764a',
  deckLine: '#7f5534',
  rail: '#6b4528',
  water: '#9cd4e2',
} as const;

/** Tile-local (u along +x, v along +y) to texture pixels. */
const P = (u: number, v: number): [number, number] => [CX + (u - v) * HW, TOP + (u + v) * HH];

function canvas(w = BLOCK_W, h = BLOCK_H): [HTMLCanvasElement, CanvasRenderingContext2D] {
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  return [c, c.getContext('2d')!];
}

function poly(ctx: CanvasRenderingContext2D, pts: [number, number][], fill: string): void {
  ctx.beginPath();
  ctx.moveTo(pts[0][0], pts[0][1]);
  for (const p of pts.slice(1)) ctx.lineTo(p[0], p[1]);
  ctx.closePath();
  ctx.fillStyle = fill;
  ctx.fill();
}

/** Quad covering tile-local u0..u1 x v0..v1 on the top face, lifted by `lift` pixels. */
function quad(u0: number, v0: number, u1: number, v1: number, lift = 0): [number, number][] {
  return [P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1)].map(([x, y]) => [x, y - lift]);
}

/** The block's two visible side faces: a surface layer over dirt. */
function sides(ctx: CanvasRenderingContext2D, layer: number, left: string, right: string): void {
  const [lx, ly] = P(0, 1);
  const [bx, by] = P(1, 1);
  const [rx, ry] = P(1, 0);
  poly(ctx, [[lx, ly], [bx, by], [bx, by + THICK], [lx, ly + THICK]], PALETTE.dirtLeft);
  poly(ctx, [[bx, by], [rx, ry], [rx, ry + THICK], [bx, by + THICK]], PALETTE.dirtRight);
  if (layer > 0) {
    poly(ctx, [[lx, ly], [bx, by], [bx, by + layer], [lx, ly + layer]], left);
    poly(ctx, [[bx, by], [rx, ry], [rx, ry + layer], [bx, by + layer]], right);
  }
}

const cache = new Map<string, Texture>();
function cached(key: string, draw: () => HTMLCanvasElement): Texture {
  let t = cache.get(key);
  if (!t) {
    t = Texture.from(draw());
    cache.set(key, t);
  }
  return t;
}

/** Paved plaza block with a faint flagstone grid. */
export function plazaTexture(): Texture {
  return cached('plaza', () => {
    const [c, ctx] = canvas();
    sides(ctx, 10, PALETTE.sidewalkLeft, PALETTE.sidewalkRight);
    poly(ctx, quad(0, 0, 1, 1), PALETTE.sidewalk);
    ctx.strokeStyle = PALETTE.plazaLine;
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let k = 1; k < 4; k++) {
      const t = k / 4;
      for (const [a, b] of [[P(t, 0), P(t, 1)], [P(0, t), P(1, t)]]) {
        ctx.moveTo(a[0], a[1]);
        ctx.lineTo(b[0], b[1]);
      }
    }
    ctx.stroke();
    return c;
  });
}

/** Soft radial glow for lamps and lit windows (white; tint it). */
export function glowTexture(): Texture {
  return cached('glow', () => {
    const size = 128;
    const [c, ctx] = canvas(size, size);
    const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    g.addColorStop(0, 'rgba(255,255,255,1)');
    g.addColorStop(0.25, 'rgba(255,255,255,0.55)');
    g.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, size, size);
    return c;
  });
}

/** A short pale streak for running water. */
export function streakTexture(): Texture {
  return cached('streak', () => {
    const [c, ctx] = canvas(24, 8);
    const g = ctx.createRadialGradient(12, 4, 0, 12, 4, 12);
    g.addColorStop(0, 'rgba(255,255,255,0.9)');
    g.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.fillStyle = g;
    ctx.beginPath();
    ctx.ellipse(12, 4, 12, 3, 0, 0, Math.PI * 2);
    ctx.fill();
    return c;
  });
}

/** A tiny round droplet (fountain spray). */
export function dropTexture(): Texture {
  return cached('drop', () => {
    const [c, ctx] = canvas(6, 6);
    ctx.fillStyle = '#e8f6ff';
    ctx.beginPath();
    ctx.arc(3, 3, 2.4, 0, Math.PI * 2);
    ctx.fill();
    return c;
  });
}

/** A little wooden garden shed in the pack's flat style (about half a tile). */
export function shedTexture(): Texture {
  return cached('shed', () => {
    const [c, ctx] = canvas(80, 80);
    // Footprint corners (half-size 0.3 tile) around the centre at (40, 58).
    const hw = 65 * 0.3;
    const hh = 32 * 0.3;
    const cx = 40;
    const cy = 58;
    const wall = 20;
    const top = { x: cx, y: cy - hh };
    const right = { x: cx + hw, y: cy };
    const bottom = { x: cx, y: cy + hh };
    const left = { x: cx - hw, y: cy };
    const f = (pts: { x: number; y: number }[], fill: string) => {
      ctx.beginPath();
      ctx.moveTo(pts[0].x, pts[0].y);
      for (const p of pts.slice(1)) ctx.lineTo(p.x, p.y);
      ctx.closePath();
      ctx.fillStyle = fill;
      ctx.fill();
    };
    const up = (p: { x: number; y: number }, d: number) => ({ x: p.x, y: p.y - d });
    // Walls: left (south) and right (east) faces.
    f([left, bottom, up(bottom, wall), up(left, wall)], '#9b6b43');
    f([bottom, right, up(right, wall), up(bottom, wall)], '#7d5433');
    // Door on the south face.
    const d0 = { x: left.x + (bottom.x - left.x) * 0.38, y: left.y + (bottom.y - left.y) * 0.38 };
    const d1 = { x: left.x + (bottom.x - left.x) * 0.66, y: left.y + (bottom.y - left.y) * 0.66 };
    f([d0, d1, up(d1, 13), up(d0, 13)], '#5a3a22');
    // Pitched roof, ridge running left-right, with a little overhang.
    const o = 3;
    const ridgeL = { x: (left.x + top.x) / 2 - o, y: (left.y + top.y) / 2 - wall - 12 };
    const ridgeR = { x: (bottom.x + right.x) / 2 + o, y: (bottom.y + right.y) / 2 - wall - 12 };
    f([up({ x: left.x - o, y: left.y + 1 }, wall), up({ x: bottom.x, y: bottom.y + o }, wall), ridgeR, ridgeL], '#6e5a48');
    f([up({ x: bottom.x, y: bottom.y + o }, wall), up({ x: right.x + o, y: right.y + 1 }, wall), ridgeR], '#5a4838');
    return c;
  });
}

/** Body colours for cars. */
export const CAR_COLOURS = ['#c94a3f', '#3f6fb5', '#e0b23a', '#4f9a55', '#e8e4da', '#3b3f47', '#8a5bb0', '#d9783a'];
const CAR_HEADINGS = 32;
/** Canvas size and where the car's centre (on the ground) sits in it. */
export const CAR_TEX = { w: 96, h: 72, cx: 48, cy: 46 };

/**
 * A little car in the pack's flat style, drawn from 3D boxes at any heading
 * (radians in world space, 0 = +x), so it lines up with any road and faces
 * the way it drives. Headings snap to 32 steps and are cached.
 */
export function carTexture(colour: string, heading: number): Texture {
  const step = ((Math.round((heading / (Math.PI * 2)) * CAR_HEADINGS) % CAR_HEADINGS) + CAR_HEADINGS) % CAR_HEADINGS;
  return cached(`car:${colour}:${step}`, () => {
    const a = (step / CAR_HEADINGS) * Math.PI * 2;
    const [c, ctx] = canvas(CAR_TEX.w, CAR_TEX.h);
    const fx = Math.cos(a);
    const fy = Math.sin(a);
    // World (along, across, up px) to screen; 1 tile = 65/32 px per axis as the iso projection.
    const P = (along: number, across: number, up: number): [number, number] => {
      const x = fx * along - fy * across;
      const y = fy * along + fx * across;
      return [CAR_TEX.cx + (x - y) * 65, CAR_TEX.cy + (x + y) * 32 - up];
    };
    const shade = (hex: string, k: number) => {
      const n = parseInt(hex.slice(1), 16);
      const f = (v: number) => Math.round(Math.min(255, Math.max(0, v * k)));
      return `rgb(${f((n >> 16) & 255)},${f((n >> 8) & 255)},${f(n & 255)})`;
    };
    const face = (pts: [number, number][], fill: string) => {
      ctx.beginPath();
      ctx.moveTo(pts[0][0], pts[0][1]);
      for (const p of pts.slice(1)) ctx.lineTo(p[0], p[1]);
      ctx.closePath();
      ctx.fillStyle = fill;
      ctx.fill();
    };
    /** A box from (a0..a1 along, -half..half across, z0..z1 px up); draws the faces the camera sees. */
    const box = (a0: number, a1: number, half: number, z0: number, z1: number, colour: string, glass?: string) => {
      const sides: { n: [number, number]; pts: [number, number][]; glassy: boolean }[] = [
        { n: [fx, fy], pts: [P(a1, -half, z0), P(a1, half, z0), P(a1, half, z1), P(a1, -half, z1)], glassy: true },
        { n: [-fx, -fy], pts: [P(a0, -half, z0), P(a0, half, z0), P(a0, half, z1), P(a0, -half, z1)], glassy: true },
        { n: [-fy, fx], pts: [P(a0, half, z0), P(a1, half, z0), P(a1, half, z1), P(a0, half, z1)], glassy: true },
        { n: [fy, -fx], pts: [P(a0, -half, z0), P(a1, -half, z0), P(a1, -half, z1), P(a0, -half, z1)], glassy: true },
      ];
      for (const s of sides) {
        // The camera looks from +x+y; faces turned that way are visible.
        const facing = s.n[0] + s.n[1];
        if (facing <= 0.001) continue;
        const light = s.n[0] > s.n[1] ? 0.72 : 0.86;
        face(s.pts, glass && s.glassy ? glass : shade(colour, light));
      }
      face([P(a0, -half, z1), P(a1, -half, z1), P(a1, half, z1), P(a0, half, z1)], shade(colour, 1));
    };
    // Shadow.
    face([P(-0.5, -0.27, 0), P(0.5, -0.27, 0), P(0.5, 0.27, 0), P(-0.5, 0.27, 0)], 'rgba(0,0,0,0.25)');
    // Wheels.
    for (const [al, ac] of [[0.28, 0.21], [0.28, -0.21], [-0.28, 0.21], [-0.28, -0.21]]) {
      const [x, y] = P(al, ac, 4);
      ctx.beginPath();
      ctx.ellipse(x, y, 4.5, 4, 0, 0, Math.PI * 2);
      ctx.fillStyle = '#2b2b2b';
      ctx.fill();
    }
    box(-0.45, 0.45, 0.22, 4, 13, colour);
    // Cabin, set back from the bonnet, with glass all round.
    box(-0.3, 0.12, 0.19, 13, 21, colour, '#9fd3ea');
    // Headlights and tail lights on the visible ends.
    const lamp = (al: number, colour2: string) => {
      for (const ac of [0.14, -0.14]) {
        const [x, y] = P(al, ac, 9);
        ctx.fillStyle = colour2;
        ctx.fillRect(x - 1.5, y - 1.5, 3, 3);
      }
    };
    if (fx + fy > 0) lamp(0.455, '#fff3b0');
    else lamp(-0.455, '#ff5a4a');
    return c;
  });
}
