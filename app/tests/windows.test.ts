// Window panes are found window by window, so a whole window lights (or not)
// together at night: on a synthetic quad, and on the real Kenney models with
// their real colourmaps.

import { BufferGeometry, Float32BufferAttribute, type Group } from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { describe, expect, it } from 'vitest';
import { HOUSES, SHOPS, TOWERS, flatten } from '../src/render/iso/assets';
import { isGlass, tagWindows, type Pixels } from '../src/render/iso/windows';

const pngs = import.meta.glob('../public/models/*/Textures/colormap.png', { query: '?inline', import: 'default', eager: true }) as Record<string, string>;
const glbs = import.meta.glob('../public/models/**/*.glb', { query: '?inline', import: 'default', eager: true }) as Record<string, string>;

function bytes(url: string): Uint8Array {
  const bin = atob(url.slice(url.indexOf(',') + 1));
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** A minimal PNG decoder: 8-bit RGB, RGBA or palette, not interlaced. */
async function decodePng(png: Uint8Array): Promise<Pixels> {
  const view = new DataView(png.buffer, png.byteOffset, png.byteLength);
  let o = 8;
  let width = 0;
  let height = 0;
  let type = 0;
  let palette: Uint8Array | null = null;
  let alpha: Uint8Array | null = null;
  const idat: Uint8Array[] = [];
  while (o < png.length) {
    const len = view.getUint32(o);
    const name = String.fromCharCode(...png.subarray(o + 4, o + 8));
    const body = png.subarray(o + 8, o + 8 + len);
    if (name === 'IHDR') {
      width = view.getUint32(o + 8);
      height = view.getUint32(o + 12);
      type = png[o + 17];
      expect(png[o + 16]).toBe(8);
    } else if (name === 'PLTE') palette = body;
    else if (name === 'tRNS') alpha = body;
    else if (name === 'IDAT') idat.push(body);
    o += 12 + len;
  }
  const joined = new Uint8Array(idat.reduce((s, c) => s + c.length, 0));
  idat.reduce((at, c) => (joined.set(c, at), at + c.length), 0);
  const raw = new Uint8Array(await new Response(new Blob([joined]).stream().pipeThrough(new DecompressionStream('deflate'))).arrayBuffer());
  const bpp = type === 6 ? 4 : type === 2 ? 3 : 1;
  const stride = width * bpp;
  const rows = new Uint8Array(height * stride);
  for (let y = 0; y < height; y++) {
    const f = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? rows[y * stride + x - bpp] : 0;
      const b = y ? rows[(y - 1) * stride + x] : 0;
      const c = x >= bpp && y ? rows[(y - 1) * stride + x - bpp] : 0;
      const p = a + b - c;
      const pr = Math.abs(p - a) <= Math.abs(p - b) && Math.abs(p - a) <= Math.abs(p - c) ? a : Math.abs(p - b) <= Math.abs(p - c) ? b : c;
      const add = [0, a, b, (a + b) >> 1, pr][f];
      rows[y * stride + x] = (line[x] + add) & 255;
    }
  }
  const data = new Uint8Array(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    if (type === 3) {
      const k = rows[i];
      data.set([palette![k * 3], palette![k * 3 + 1], palette![k * 3 + 2], alpha && k < alpha.length ? alpha[k] : 255], i * 4);
    } else {
      data.set([rows[i * bpp], rows[i * bpp + 1], rows[i * bpp + 2], bpp === 4 ? rows[i * bpp + 3] : 255], i * 4);
    }
  }
  return { data, width, height };
}

async function model(name: string): Promise<Group> {
  const buf = bytes(glbs[`../public/models/${name}.glb`]);
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const jsonLen = view.getUint32(12, true);
  const json = JSON.parse(new TextDecoder().decode(buf.subarray(20, 20 + jsonLen)));
  delete json.images;
  delete json.textures;
  delete json.samplers;
  for (const m of json.materials ?? []) delete m.pbrMetallicRoughness?.baseColorTexture;
  const rest = buf.subarray(20 + jsonLen);
  const raw = new TextEncoder().encode(JSON.stringify(json));
  const pad = (4 - (raw.length % 4)) % 4;
  const glb = new Uint8Array(20 + raw.length + pad + rest.length);
  const out = new DataView(glb.buffer);
  out.setUint32(0, 0x46546c67, true);
  out.setUint32(4, 2, true);
  out.setUint32(8, glb.length, true);
  out.setUint32(12, raw.length + pad, true);
  out.setUint32(16, 0x4e4f534a, true);
  glb.set(raw, 20);
  glb.fill(0x20, 20 + raw.length, 20 + raw.length + pad);
  glb.set(rest, 20 + raw.length + pad);
  return (await new GLTFLoader().parseAsync(glb.buffer, '')).scene as Group;
}

/** Seeds per triangle, and how many distinct panes. */
function panes(g: BufferGeometry): { panes: number; mixed: number; glassTris: number } {
  const seeds = g.attributes.windowSeed;
  const index = g.index;
  const tris = (index ? index.count : g.attributes.position.count) / 3;
  const set = new Set<number>();
  let mixed = 0;
  let glassTris = 0;
  for (let t = 0; t < tris; t++) {
    const s = [0, 1, 2].map((k) => seeds.getX(index ? index.getX(t * 3 + k) : t * 3 + k));
    if (s.some((x) => x > 0)) glassTris++;
    if (s.some((x) => x !== s[0])) mixed++;
    if (s[0] > 0) set.add(s[0]);
  }
  return { panes: set.size, mixed, glassTris };
}

/** How much of a model's surface (by area) is tagged glass. */
function glassShare(p: ReturnType<typeof flatten>): number {
  let glass = 0;
  let all = 0;
  for (const part of p.parts) {
    const g = part.geometry.attributes.windowSeed ? part.geometry : null;
    if (!g) continue;
    const pos = g.attributes.position;
    const seeds = g.attributes.windowSeed;
    for (let t = 0; t < pos.count / 3; t++) {
      const v = [0, 1, 2].map((k) => [pos.getX(t * 3 + k), pos.getY(t * 3 + k), pos.getZ(t * 3 + k)]);
      const ab = v[1].map((x, i) => x - v[0][i]);
      const ac = v[2].map((x, i) => x - v[0][i]);
      const area = Math.hypot(ab[1] * ac[2] - ab[2] * ac[1], ab[2] * ac[0] - ab[0] * ac[2], ab[0] * ac[1] - ab[1] * ac[0]) / 2;
      all += area;
      if (seeds.getX(t * 3) > 0) glass += area;
    }
  }
  return all ? glass / all : 0;
}

describe('window panes', () => {
  it('know glass from walls and roofs', () => {
    expect(isGlass(175, 206, 243)).toBe(true); // pale window blue
    expect(isGlass(124, 165, 225)).toBe(true); // deeper window blue
    expect(isGlass(214, 214, 229)).toBe(false); // the kits' lavender-white walls
    expect(isGlass(98, 148, 216)).toBe(false); // roof blue
    expect(isGlass(240, 240, 240)).toBe(false); // white wall
    expect(isGlass(96, 200, 140)).toBe(false); // green roof
    expect(isGlass(154, 168, 181)).toBe(false); // blue-grey render
  });

  it('give every triangle of one pane the same seed, and separate panes their own', () => {
    // Two separate quads of glass and one of wall, on a 2x1 texture: left texel glass, right texel wall.
    const pixels: Pixels = { data: new Uint8Array([175, 206, 243, 255, 240, 240, 240, 255]), width: 2, height: 1 };
    const g = new BufferGeometry();
    const quad = (x: number, u: number) => [
      [x, 0, 0, u], [x + 1, 0, 0, u], [x + 1, 1, 0, u],
      [x, 0, 0, u], [x + 1, 1, 0, u], [x, 1, 0, u],
    ];
    const verts = [...quad(0, 0.25), ...quad(3, 0.25), ...quad(6, 0.75)];
    g.setAttribute('position', new Float32BufferAttribute(verts.flatMap((p) => p.slice(0, 3)), 3));
    g.setAttribute('uv', new Float32BufferAttribute(verts.flatMap((p) => [p[3], 0.5]), 2));
    const s = tagWindows(g, pixels).attributes.windowSeed.array as Float32Array;
    // The first quad's six corners share a seed, the second its own, the wall none.
    expect(new Set(s.slice(0, 6)).size).toBe(1);
    expect(new Set(s.slice(6, 12)).size).toBe(1);
    expect(s[0]).not.toBe(s[6]);
    expect(s[0]).toBeGreaterThanOrEqual(1);
    // Each pane is a big share of this little test "building": curtain walls (3..4).
    expect(s[0]).toBeGreaterThanOrEqual(3);
    expect([...s.slice(12)]).toEqual([0, 0, 0, 0, 0, 0]);
  });

  it('find whole windows on the real Kenney buildings', async () => {
    const colourmaps = {
      suburban: await decodePng(bytes(pngs['../public/models/suburban/Textures/colormap.png'])),
      commercial: await decodePng(bytes(pngs['../public/models/commercial/Textures/colormap.png'])),
    };
    let withWindows = 0;
    const all = [...HOUSES, ...SHOPS, ...TOWERS];
    for (const name of all) {
      const p = flatten(name, await model(name));
      const kit = name.split('/')[0] as keyof typeof colourmaps;
      let count = 0;
      for (const part of p.parts) {
        part.geometry = tagWindows(part.geometry, colourmaps[kit]);
        const r = panes(part.geometry);
        // No triangle is split between two panes (or half lit).
        expect(r.mixed, name).toBe(0);
        count += r.panes;
      }
      if (count >= 2) withWindows++;
      expect(glassShare(p), name).toBeLessThan(0.35);
      // Not every face of the building: windows are a minority of it.
      expect(count, name).toBeLessThan(400);
    }
    expect(withWindows / all.length).toBeGreaterThan(0.8);
  });
});
