// Kenney's CC0 3D kits (www.kenney.nl), served from public/models: City Kit
// Suburban (houses, fences), City Kit Commercial (shops, offices, towers), Car
// Kit, Mini Characters (rigged, with walk and idle clips) and Nature Kit (trees,
// bushes, crops) and Furniture Kit (room furniture and counters). Static models are flattened into one geometry per material so
// the world can draw every copy of a model as a single InstancedMesh.

import { Box3, Material, Plane, Mesh, MeshStandardMaterial, SRGBColorSpace, Texture, TextureLoader, Vector3, type BufferGeometry, type Object3D } from 'three';
import { GLTFLoader, type GLTF } from 'three/addons/loaders/GLTFLoader.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import type { BuildingKind } from '../../model/types';

const BASE = `${import.meta.env.BASE_URL}models`;

/** Keeps what's above the modelled ground floor (y > WALL_HEIGHT). */
export const ABOVE_GROUND_FLOOR = new Plane(new Vector3(0, 1, 0), -WALL_HEIGHT);

/** A static model baked down to world-space geometry, one part per material. */
export interface Prefab {
  name: string;
  parts: { geometry: BufferGeometry; material: Material }[];
  /** Bounding box of the whole model (model units; every kit stands on y = 0 and faces +Z). */
  min: Vector3;
  size: Vector3;
}

const range = (letters: string, f: (c: string) => string) => [...letters].map(f);

export const HOUSES = range('abcdefghijklmnopqrstu', (c) => `suburban/building-type-${c}`);
export const SHOPS = range('abcdefghijklmn', (c) => `commercial/building-${c}`);
export const TOWERS = range('abcde', (c) => `commercial/building-skyscraper-${c}`);
export const CARS = ['sedan', 'hatchback-sports', 'suv', 'van', 'taxi', 'delivery', 'truck', 'police'].map((c) => `cars/${c}`);
/** How often each car model turns up (police and trucks are rare). */
export const CAR_WEIGHTS = [5, 3, 4, 2, 1, 1, 1, 0.4];
export const CHARACTERS = [...range('abcdef', (c) => `character-female-${c}`), ...range('abcdef', (c) => `character-male-${c}`)];
export const TREES = ['tree_default', 'tree_oak', 'tree_detailed', 'tree_fat', 'tree_pineRoundA', 'tree_pineRoundC', 'tree_cone', 'tree_simple', 'tree_small'].map(
  (n) => `nature/${n}`,
);
export const BUSHES = ['plant_bush', 'plant_bushLarge', 'plant_bushSmall', 'grass'].map((n) => `nature/${n}`);
export const FLOWERS = ['flower_redA', 'flower_purpleA', 'flower_yellowA'].map((n) => `nature/${n}`);
export const CROPS = ['crop_carrot', 'crop_pumpkin', 'crop_turnip', 'crops_cornStageC', 'crops_leafsStageB', 'crops_wheatStageB'].map((n) => `nature/${n}`);
import { FURNITURE, WALL_HEIGHT } from './interiors';
import { lightWindows, readPixels, tagWindows, type Pixels } from './windows';

export const FURNISHINGS = FURNITURE.map((n) => `furniture/${n}`);
export const PROPS = ['nature/rock_smallA', 'nature/log_stack', 'nature/pot_large', 'suburban/planter', 'suburban/fence', 'commercial/detail-awning'];

/** Which model families suit each kind of building. */
export const KIND_MODELS: Record<BuildingKind | 'office', readonly string[]> = {
  house: HOUSES,
  workshop: SHOPS.slice(0, 6),
  shop: SHOPS,
  office: [...SHOPS.slice(6), ...TOWERS],
  hall: SHOPS.slice(8),
  church: SHOPS.slice(10),
};

/** Alternative colourmaps each kit ships (roof and facade colours). */
const VARIATIONS: Record<string, string[]> = {
  suburban: ['variation-a', 'variation-b', 'variation-c'],
  commercial: ['variation-a', 'variation-b'],
};

export interface TownAssets {
  prefab(name: string): Prefab;
  /** The same model in one of its kit's colour variations (0 = the default colourmap). */
  variant(name: string, variation: number): Prefab;
  variationCount(name: string): number;
  /** A rigged character (scene and clips) to clone per walker. */
  character(name: string): GLTF;
}

export async function loadTownAssets(): Promise<TownAssets> {
  const loader = new GLTFLoader();
  const statics = [...HOUSES, ...SHOPS, ...TOWERS, ...CARS, ...TREES, ...BUSHES, ...FLOWERS, ...CROPS, ...PROPS, ...FURNISHINGS];
  const [prefabs, characters, variations] = await Promise.all([
    Promise.all(statics.map(async (name) => flatten(name, (await loader.loadAsync(`${BASE}/${name}.glb`)).scene))),
    Promise.all(CHARACTERS.map((name) => loader.loadAsync(`${BASE}/characters/${name}.glb`))),
    loadVariations(),
  ]);
  const byName = new Map(prefabs.map((p) => [p.name, p]));
  // Buildings stand on the town's own modelled ground floors (world.ts): draw
  // each Kenney building only above it, and light its windows at night.
  for (const p of prefabs) {
    if (!p.name.startsWith('suburban/building') && !p.name.startsWith('commercial/building')) continue;
    for (const part of p.parts) {
      part.geometry = tagWindows(part.geometry, colourmapPixels(part.material));
      part.material.clippingPlanes = [ABOVE_GROUND_FLOOR];
      part.material.clipShadows = true;
      lightWindows(part.material);
    }
  }
  const charByName = new Map(CHARACTERS.map((n, i) => [n, characters[i]]));
  const variants = new Map<string, Prefab>();
  const recoloured = new Map<string, Material>();

  const prefab = (name: string): Prefab => {
    const p = byName.get(name);
    if (!p) throw new Error(`unknown model ${name}`);
    return p;
  };
  return {
    prefab,
    variationCount: (name) => 1 + (VARIATIONS[name.split('/')[0]]?.length ?? 0),
    variant(name, variation) {
      const base = prefab(name);
      const maps = variations.get(name.split('/')[0]);
      if (!variation || !maps?.[variation - 1]) return base;
      const key = `${name}#${variation}`;
      let p = variants.get(key);
      if (!p) {
        p = {
          ...base,
          parts: base.parts.map((part) => {
            const mkey = `${part.material.uuid}#${variation}`;
            let m = recoloured.get(mkey);
            if (!m) {
              m = recolour(part.material, maps[variation - 1]);
              if (part.material.userData.windows) {
                m.userData.windows = false;
                lightWindows(m);
              }
              recoloured.set(mkey, m);
            }
            return { geometry: part.geometry, material: m };
          }),
        };
        variants.set(key, p);
      }
      return p;
    },
    character(name) {
      const c = charByName.get(name);
      if (!c) throw new Error(`unknown character ${name}`);
      return c;
    },
  };
}

const pixelCache = new Map<unknown, Pixels | null>();

/** The pixels of a material's colourmap (cached per image), to find its window panes. */
function colourmapPixels(material: Material): Pixels | null {
  const image = (material as MeshStandardMaterial).map?.image as (CanvasImageSource & { width: number; height: number }) | undefined;
  if (!image) return null;
  if (!pixelCache.has(image)) pixelCache.set(image, readPixels(image));
  return pixelCache.get(image) ?? null;
}

async function loadVariations(): Promise<Map<string, Texture[]>> {
  const loader = new TextureLoader();
  const out = new Map<string, Texture[]>();
  await Promise.all(
    Object.entries(VARIATIONS).map(async ([kit, files]) => {
      out.set(kit, await Promise.all(files.map((f) => loader.loadAsync(`${BASE}/${kit}/Textures/${f}.png`))));
    }),
  );
  return out;
}

/** A copy of a kit material using another colourmap, sampled the same way. */
function recolour(material: Material, map: Texture): Material {
  const m = (material as MeshStandardMaterial).clone();
  const old = m.map;
  const tex = map.clone();
  tex.flipY = false; // glTF texture convention
  tex.colorSpace = SRGBColorSpace;
  if (old) {
    tex.magFilter = old.magFilter;
    tex.minFilter = old.minFilter;
    tex.wrapS = old.wrapS;
    tex.wrapT = old.wrapT;
  }
  tex.needsUpdate = true;
  m.map = tex;
  return m;
}

/** Bake a loaded scene's meshes into world space, merged per material. */
export function flatten(name: string, scene: Object3D): Prefab {
  scene.updateMatrixWorld(true);
  const groups = new Map<Material, BufferGeometry[]>();
  scene.traverse((o) => {
    const mesh = o as Mesh;
    if (!mesh.isMesh) return;
    const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    // The nature kit's materials say fully metallic, which renders near-black without an environment map: keep everything matte.
    for (const m of materials) if ((m as MeshStandardMaterial).isMeshStandardMaterial) (m as MeshStandardMaterial).metalness = 0;
    const g = mesh.geometry.clone().applyMatrix4(mesh.matrixWorld);
    if (materials.length === 1) {
      push(groups, materials[0], g);
      return;
    }
    // Multi-material meshes: split by their draw groups.
    for (const grp of g.groups) {
      const part = g.clone();
      part.clearGroups();
      part.setDrawRange(0, Infinity);
      if (part.index) {
        const idx = part.index.array.slice(grp.start, grp.start + grp.count);
        part.setIndex(Array.from(idx));
      }
      push(groups, materials[grp.materialIndex ?? 0], part);
    }
  });
  const parts = [...groups].flatMap(([material, geoms]) => {
    const merged = geoms.length > 1 ? mergeGeometries(geoms.map(normalise)) : geoms[0];
    // Meshes that can't merge (mismatched attributes) stay separate parts.
    return (merged ? [merged] : geoms).map((geometry) => ({ geometry, material }));
  });
  const box = new Box3();
  for (const p of parts) {
    p.geometry.computeBoundingBox();
    box.union(p.geometry.boundingBox!);
  }
  return { name, parts, min: box.min.clone(), size: box.getSize(new Vector3()) };
}

function push(groups: Map<Material, BufferGeometry[]>, m: Material, g: BufferGeometry): void {
  const list = groups.get(m);
  if (list) list.push(g);
  else groups.set(m, [g]);
}

/** Make geometries mergeable: indexed, with only the attributes every kit mesh has. */
function normalise(g: BufferGeometry): BufferGeometry {
  const out = g.index ? g : g.setIndex([...Array(g.attributes.position.count).keys()]);
  for (const key of Object.keys(out.attributes)) if (!['position', 'normal', 'uv'].includes(key)) out.deleteAttribute(key);
  return out;
}
