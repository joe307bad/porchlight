// Lit windows at night, decided window by window. Each window pane in a
// building's geometry carries a `windowSeed` vertex attribute: 0 for anything
// that isn't glass, and for glass a random value in 1..2 shared by every
// vertex of that pane, so a whole window is lit or dark together. (Curtain
// walls, panes spanning much of a building, get 3..4 and light floor by
// floor instead.) For the
// Kenney buildings the panes are found on load (`tagWindows`): triangles whose
// colourmap texel is window blue, grouped into connected panes. The town's own
// ground-floor windows are tagged as they're built.
//
// A small patch to the building materials' shader then glows a random share
// of the panes warm yellow after dark: more often the higher up they are, so
// towers twinkle more than cottages, varied per building, and about one lit
// window in eight flickers like a failing bulb or a television.

import { BufferAttribute, type BufferGeometry, type Material, type MeshStandardMaterial } from 'three';

/** Shared by every patched material: 0..1 night, and seconds for the flicker. */
export const windowUniforms = { uNight: { value: 0 }, uTime: { value: 0 } };

/** Patch a building material so its windows light up at night (once per material). */
export function lightWindows(material: Material): void {
  const m = material as MeshStandardMaterial;
  if (!m.isMeshStandardMaterial || m.userData.windows) return;
  m.userData.windows = true;
  m.onBeforeCompile = (shader) => {
    shader.uniforms.uNight = windowUniforms.uNight;
    shader.uniforms.uTime = windowUniforms.uTime;
    shader.vertexShader = shader.vertexShader
      .replace(
        '#include <common>',
        '#include <common>\nattribute float windowSeed;\nvarying float vSeed;\nvarying vec3 vLitPos;\nvarying vec3 vBuilding;\nvarying float vLitUp;',
      )
      .replace(
        '#include <project_vertex>',
        `#include <project_vertex>
        vSeed = windowSeed;
        vec4 litWorld = vec4(transformed, 1.0);
        vec3 litNormal = objectNormal;
        vBuilding = modelMatrix[3].xyz;
        #ifdef USE_INSTANCING
          litWorld = instanceMatrix * litWorld;
          litNormal = mat3(instanceMatrix) * litNormal;
          vBuilding += instanceMatrix[3].xyz;
        #endif
        vLitPos = (modelMatrix * litWorld).xyz;
        vLitUp = abs(normalize(mat3(modelMatrix) * litNormal).y);`,
      );
    shader.fragmentShader = shader.fragmentShader
      .replace(
        '#include <common>',
        `#include <common>
        varying float vSeed;
        varying vec3 vLitPos;
        varying vec3 vBuilding;
        varying float vLitUp;
        uniform float uNight;
        uniform float uTime;
        float litHash(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }`,
      )
      .replace(
        '#include <emissivemap_fragment>',
        `#include <emissivemap_fragment>
        // Window panes on walls (skylights on roofs stay dark).
        if (uNight > 0.01 && vSeed > 0.5 && vLitUp < 0.35) {
          // One pane, one decision: its seed, varied by which building it's on.
          // Curtain walls (seeds 3..4) decide floor by floor and bay by bay instead.
          vec3 key = vec3(vSeed * 97.0, floor(vBuilding.x * 10.0), floor(vBuilding.z * 10.0));
          if (vSeed > 2.5) key += vec3(floor(vLitPos.y / 0.42) * 13.0, floor((vLitPos.x + vLitPos.z) / 0.55) * 7.0, 0.0);
          float h = litHash(key);
          float chance = mix(0.15, 0.36, clamp(vLitPos.y / 3.5, 0.0, 1.0));
          float lit = step(1.0 - chance, h);
          // About one lit window in eight flickers.
          float f = litHash(key + 17.0);
          if (f < 0.12) {
            float t = uTime * (6.0 + f * 40.0) + h * 50.0;
            float n = fract(sin(floor(t) * 91.7 + h * 13.0) * 4375.5);
            lit *= mix(0.25, 1.0, step(0.3, n)) * (0.85 + 0.15 * sin(t * 3.1));
          }
          vec3 warm = mix(vec3(1.0, 0.72, 0.38), vec3(1.0, 0.86, 0.6), litHash(key + 3.0));
          totalEmissiveRadiance += warm * lit * uNight * 0.7;
        }`,
      );
  };
  m.customProgramCacheKey = () => 'lit-windows';
  m.needsUpdate = true;
}

/**
 * True if an sRGB colour (0..255) is window glass: the kits' pale sky-blue
 * swatches, e.g. (175, 206, 243) to (124, 165, 225). Blue well above red,
 * with green part way between. Not the lavender-white wall paint
 * (214, 214, 229), which is only faintly blue, nor the stronger roof blues,
 * whose green sits further from red.
 */
export function isGlass(r: number, g: number, b: number): boolean {
  return b >= 200 && b - r >= 40 && g - r >= 20 && g - r <= 45 && b - g >= 15;
}

/** An image's pixels (RGBA rows, top row first), for sampling a colourmap on the CPU. */
export interface Pixels {
  data: Uint8ClampedArray | Uint8Array;
  width: number;
  height: number;
}

/**
 * A copy of a geometry with its `windowSeed` attribute: find triangles whose
 * texel (at their UV centre; glTF UVs, v down the image) is window glass,
 * group those sharing a corner into panes, and give each pane its own seed in
 * 1..2. Everything else gets 0. The copy is un-indexed, so no wall triangle
 * shares a corner (and with it, a glow) with a pane.
 */
export function tagWindows(source: BufferGeometry, pixels: Pixels | null): BufferGeometry {
  const geometry = source.index ? source.toNonIndexed() : source;
  const pos = geometry.attributes.position;
  const seeds = new Float32Array(pos.count);
  const uv = geometry.attributes.uv;
  if (pixels && uv) {
    const index = geometry.index;
    const tris = (index ? index.count : pos.count) / 3;
    const corner = (t: number, k: number) => (index ? index.getX(t * 3 + k) : t * 3 + k);
    // Union-find over vertices, merged by position so split vertices still join.
    const parent = Int32Array.from({ length: pos.count }, (_, i) => i);
    const find = (i: number): number => {
      while (parent[i] !== i) i = parent[i] = parent[parent[i]];
      return i;
    };
    const union = (a: number, b: number) => {
      const ra = find(a);
      const rb = find(b);
      if (ra !== rb) parent[ra] = rb;
    };
    const byPosition = new Map<string, number>();
    const key = (i: number) => `${Math.round(pos.getX(i) * 1e4)},${Math.round(pos.getY(i) * 1e4)},${Math.round(pos.getZ(i) * 1e4)}`;
    const glass: number[] = [];
    for (let t = 0; t < tris; t++) {
      const [a, b, c] = [corner(t, 0), corner(t, 1), corner(t, 2)];
      const u = (uv.getX(a) + uv.getX(b) + uv.getX(c)) / 3;
      const v = (uv.getY(a) + uv.getY(b) + uv.getY(c)) / 3;
      const px = Math.min(pixels.width - 1, Math.max(0, Math.floor((u - Math.floor(u)) * pixels.width)));
      const py = Math.min(pixels.height - 1, Math.max(0, Math.floor((v - Math.floor(v)) * pixels.height)));
      const o = (py * pixels.width + px) * 4;
      if (!isGlass(pixels.data[o], pixels.data[o + 1], pixels.data[o + 2])) continue;
      glass.push(t);
      for (const i of [a, b, c]) {
        const k = key(i);
        const same = byPosition.get(k);
        if (same === undefined) byPosition.set(k, i);
        else union(i, same);
      }
      union(a, b);
      union(b, c);
    }
    // Each pane's extent, to tell windows from curtain walls.
    const extent = new Map<number, { lo: number[]; hi: number[] }>();
    for (const t of glass) {
      for (let k = 0; k < 3; k++) {
        const i = corner(t, k);
        const root = find(i);
        const p = [pos.getX(i), pos.getY(i), pos.getZ(i)];
        const e = extent.get(root);
        if (!e) extent.set(root, { lo: [...p], hi: [...p] });
        else for (let d = 0; d < 3; d++) {
          e.lo[d] = Math.min(e.lo[d], p[d]);
          e.hi[d] = Math.max(e.hi[d], p[d]);
        }
      }
    }
    geometry.computeBoundingBox();
    const size = geometry.boundingBox!.max.clone().sub(geometry.boundingBox!.min);
    const seedOf = new Map<number, number>();
    for (const t of glass) {
      for (let k = 0; k < 3; k++) {
        const i = corner(t, k);
        const root = find(i);
        let s = seedOf.get(root);
        if (s === undefined) {
          // A pane more than a fifth of the building's height or width is a
          // curtain wall: seeds 3..4 tell the shader to light it in floor bays.
          const e = extent.get(root)!;
          const big = e.hi[1] - e.lo[1] > size.y * 0.2 || Math.max(e.hi[0] - e.lo[0], e.hi[2] - e.lo[2]) > Math.max(size.x, size.z) * 0.2;
          s = (big ? 3 : 1) + paneHash(seedOf.size + 1);
          seedOf.set(root, s);
        }
        seeds[i] = s;
      }
    }
  }
  geometry.setAttribute('windowSeed', new BufferAttribute(seeds, 1));
  geometry.computeBoundingSphere();
  geometry.computeBoundingBox();
  return geometry;
}

/** A stable pseudo-random 0..1 per pane number. */
export function paneHash(n: number): number {
  let x = Math.imul(n ^ 0x9e3779b9, 0x85ebca6b);
  x ^= x >>> 13;
  x = Math.imul(x, 0xc2b2ae35);
  x ^= x >>> 16;
  return (x >>> 0) / 4294967296;
}

/** Read an image's pixels through a canvas (browser only), or null if it can't be read. */
export function readPixels(image: CanvasImageSource & { width: number; height: number }): Pixels | null {
  try {
    const c = document.createElement('canvas');
    c.width = image.width;
    c.height = image.height;
    const ctx = c.getContext('2d', { willReadFrequently: true });
    if (!ctx) return null;
    ctx.drawImage(image, 0, 0);
    return { data: ctx.getImageData(0, 0, c.width, c.height).data, width: c.width, height: c.height };
  } catch {
    return null;
  }
}
