// The Kenney models load, flatten and rig as the explore view expects. Runs on
// the real GLB files in public/models (with their textures stripped, since
// Node has no image decoding).

import type { Group } from 'three';
import { GLTFLoader, type GLTF } from 'three/addons/loaders/GLTFLoader.js';
import { describe, expect, it } from 'vitest';
import { Walker } from '../src/render/iso/actors';
import { BUSHES, CARS, CHARACTERS, CROPS, FLOWERS, HOUSES, PROPS, SHOPS, TOWERS, TREES, flatten } from '../src/render/iso/assets';
import { Avatar } from '../src/render/iso/characters';

/** Every model file, as a base64 data URL. */
const files = import.meta.glob('../public/models/**/*.glb', { query: '?inline', import: 'default', eager: true }) as Record<string, string>;

function bytes(name: string): Uint8Array {
  const url = files[`../public/models/${name}.glb`];
  if (!url) throw new Error(`no model file for ${name}`);
  const bin = atob(url.slice(url.indexOf(',') + 1));
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** Parse a GLB without its images (materials keep their colours). */
function parse(name: string): Promise<GLTF> {
  const buf = bytes(name);
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const jsonLen = view.getUint32(12, true);
  const json = JSON.parse(new TextDecoder().decode(buf.subarray(20, 20 + jsonLen)));
  delete json.images;
  delete json.textures;
  delete json.samplers;
  for (const m of json.materials ?? []) {
    delete m.pbrMetallicRoughness?.baseColorTexture;
    delete m.normalTexture;
    delete m.emissiveTexture;
  }
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
  return new GLTFLoader().parseAsync(glb.buffer, '');
}

describe('Kenney models', () => {
  it('flatten every static model into parts that stand on the ground', async () => {
    for (const name of [...HOUSES, ...SHOPS, ...TOWERS, ...CARS, ...TREES, ...BUSHES, ...FLOWERS, ...CROPS, ...PROPS]) {
      const p = flatten(name, (await parse(name)).scene as Group);
      expect(p.parts.length, name).toBeGreaterThan(0);
      for (const v of [p.size.x, p.size.y, p.size.z]) expect(v, name).toBeGreaterThan(0.02);
      // Wheels hang a little below a car's origin; everything else starts at y = 0.
      expect(Math.abs(p.min.y), name).toBeLessThan(name.startsWith('cars/') ? 0.35 : 0.06);
      for (const part of p.parts) expect(part.geometry.attributes.position.count, name).toBeGreaterThan(0);
    }
  });

  it('cars are longest front to back (+Z), as the traffic assumes', async () => {
    for (const name of CARS) {
      const p = flatten(name, (await parse(name)).scene as Group);
      expect(p.size.z, name).toBeGreaterThan(p.size.x);
    }
  });

  it('rig every character with walk and idle clips, scaled to height, with an x-ray copy', async () => {
    for (const name of CHARACTERS) {
      const gltf = await parse(`characters/${name}`);
      const names = gltf.animations.map((a) => a.name);
      expect(names, name).toContain('walk');
      expect(names, name).toContain('idle');
      const a = new Avatar(gltf, 0.62, 0.3);
      const w = new Walker({ model: name }, 5, 7);
      w.moved(0.1, 0, 0.05);
      a.update(w, 0.05);
      expect(a.root.position.x).toBeCloseTo(5);
      expect(a.root.position.z).toBeCloseTo(7);
      let meshes = 0;
      a.root.traverse((o) => {
        if ((o as { isMesh?: boolean }).isMesh) meshes++;
      });
      a.addXray();
      let after = 0;
      a.root.traverse((o) => {
        if ((o as { isMesh?: boolean }).isMesh) after++;
      });
      expect(after).toBe(meshes * 2);
    }
  });
});

describe('Kenney model orientation', () => {
  /** Triangles facing +Z and -Z (doors, porches and shopfronts add detail on the front). */
  async function detail(name: string): Promise<{ front: number; back: number }> {
    const { Vector3 } = await import('three');
    const p = flatten(name, (await parse(name)).scene as Group);
    const out = { front: 0, back: 0 };
    const [a, b, c, n] = [new Vector3(), new Vector3(), new Vector3(), new Vector3()];
    for (const part of p.parts) {
      const g = part.geometry.index ? part.geometry.toNonIndexed() : part.geometry;
      const pos = g.attributes.position;
      for (let i = 0; i < pos.count; i += 3) {
        a.fromBufferAttribute(pos, i);
        b.fromBufferAttribute(pos, i + 1).sub(a);
        c.fromBufferAttribute(pos, i + 2).sub(a);
        n.crossVectors(b, c).normalize();
        if (n.z < -0.7) out.front++;
        else if (n.z > 0.7) out.back++;
      }
    }
    return out;
  }

  it('city-kit buildings have their detailed front on -Z (the world turns them half round)', async () => {
    let frontish = 0;
    const all = [...HOUSES, ...SHOPS, ...TOWERS];
    for (const name of all) {
      const d = await detail(name);
      if (d.front > d.back) frontish++;
    }
    expect(frontish / all.length).toBeGreaterThan(0.85);
  });
});

describe('Kenney materials', () => {
  it('are all matte once flattened (the nature kit ships them fully metallic)', async () => {
    for (const name of [...TREES, ...BUSHES, ...FLOWERS, ...CROPS, ...HOUSES.slice(0, 2)]) {
      const p = flatten(name, (await parse(name)).scene as Group);
      for (const part of p.parts) expect((part.material as unknown as { metalness: number }).metalness, name).toBe(0);
    }
  });
});
