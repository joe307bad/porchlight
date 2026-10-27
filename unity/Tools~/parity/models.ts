// Flattens every static model with Three (as the game does) and writes each
// one's bounds and part geometry, for the C# loader to compare against.
// Run: npx --prefix app vite-node "unity/Tools~/parity/models.ts" -- out.json

import { readFileSync, writeFileSync } from 'node:fs';
import { GLTFLoader } from '../../../app/node_modules/three/examples/jsm/loaders/GLTFLoader.js';
import type { Group } from 'three';
import { BUSHES, CARS, CROPS, FLOWERS, FURNISHINGS, HOUSES, PROPS, SHOPS, TOWERS, TREES, flatten } from '../../../app/src/render/iso/assets';

const out = process.argv.slice(2).filter((a) => a !== '--')[0] ?? 'models.json';

function parse(name: string) {
  const buf = new Uint8Array(readFileSync(`app/public/models/${name}.glb`));
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
  const o = new DataView(glb.buffer);
  o.setUint32(0, 0x46546c67, true);
  o.setUint32(4, 2, true);
  o.setUint32(8, glb.length, true);
  o.setUint32(12, raw.length + pad, true);
  o.setUint32(16, 0x4e4f534a, true);
  glb.set(raw, 20);
  glb.fill(0x20, 20 + raw.length, 20 + raw.length + pad);
  glb.set(rest, 20 + raw.length + pad);
  return new GLTFLoader().parseAsync(glb.buffer, '');
}

const result: unknown[] = [];
for (const name of [...HOUSES, ...SHOPS, ...TOWERS, ...CARS, ...TREES, ...BUSHES, ...FLOWERS, ...CROPS, ...PROPS, ...FURNISHINGS]) {
  const p = flatten(name, (await parse(name)).scene as Group);
  result.push({
    name,
    min: [p.min.x, p.min.y, p.min.z],
    size: [p.size.x, p.size.y, p.size.z],
    parts: p.parts.map((part) => ({
      count: part.geometry.attributes.position.count,
      index: part.geometry.index ? part.geometry.index.count : -1,
      pos: Array.from(part.geometry.attributes.position.array as Float32Array).slice(0, 30),
      normal: part.geometry.attributes.normal ? Array.from(part.geometry.attributes.normal.array as Float32Array).slice(0, 30) : null,
      color: (part.material as { color?: { r: number; g: number; b: number } }).color ? [(part.material as any).color.r, (part.material as any).color.g, (part.material as any).color.b] : null,
    })),
  });
}
writeFileSync(out, JSON.stringify(result));
console.log(`wrote ${result.length} models to ${out}`);
