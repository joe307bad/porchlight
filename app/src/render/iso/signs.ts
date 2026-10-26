// Street name signs: a slim post on a corner of every junction where
// differently named streets meet, with a green blade per street, each
// running along its street and lettered on both sides. Every name is drawn
// once into one texture atlas (textures.ts), and all the blades are one mesh,
// so the signs cost two draw calls however many there are.
//
// `planSigns` is pure (unit-tested); `addStreetSigns` builds the meshes.

import { BufferGeometry, CylinderGeometry, Float32BufferAttribute, InstancedMesh, Matrix4, Mesh, MeshLambertMaterial, type Group } from 'three';
import { index, inBounds } from '../../core/grid';
import { dist, distToPolyline } from '../../core/geometry';
import { Terrain, type TownModel, type Vec2 } from '../../model/types';
import { SIDEWALK, Use, type IsoLayout } from './layout';
import { leaving, type StreetNames } from './names';
import { signAtlas } from './textures';

export interface SignBlade {
  /** Street index (into StreetNames.streets). */
  street: number;
  /** Unit direction the street runs from the junction (map space). */
  along: Vec2;
}

export interface Signpost {
  at: Vec2;
  blades: SignBlade[];
}

/** Blade size (tiles): small, about as long as a townsperson is tall. */
export const BLADE_LENGTH = 0.44;
export const BLADE_HEIGHT = 0.086;
const BLADE_THICK = 0.012;
/** Height of the lowest blade's middle, and the step between stacked blades. */
const BLADE_Y = 0.6;
const BLADE_STEP = 0.1;
const POST_RADIUS = 0.014;
/** No two signposts closer than this (tiles). */
const SIGN_SPACING = 1.6;

/** Where the signposts stand: one corner of each junction of two or more named streets. */
export function planSigns(model: TownModel, layout: IsoLayout, names: StreetNames): Signpost[] {
  const { roads } = layout;
  const ends = new Map<number, { road: number; dir: Vec2 }[]>();
  roads.forEach((r, i) => {
    for (const [node, pts] of [[r.from, r.points], [r.to, [...r.points].reverse()]] as const) {
      if (!ends.has(node)) ends.set(node, []);
      ends.get(node)!.push({ road: i, dir: leaving(pts) });
    }
  });
  const nodeAt = new Map(model.roads.nodes.map((n) => [n.id, n.at]));
  const out: Signpost[] = [];
  for (const [node, here] of ends) {
    const centre = nodeAt.get(node);
    if (!centre || here.length < 2) continue;
    // One blade per street meeting here (in a stable order).
    const blades: SignBlade[] = [];
    for (const e of here) {
      const street = names.ofRoad[e.road];
      if (!blades.some((b) => b.street === street)) blades.push({ street, along: e.dir });
    }
    if (blades.length < 2) continue;
    const at = cornerSpot(model, layout, centre, here);
    if (!at || out.some((s) => dist(s.at, at) < SIGN_SPACING)) continue;
    out.push({ at, blades: blades.slice(0, 3) });
  }
  return out;
}

/** A spot on the pavement at one of a junction's corners, clear of every road and building, or null. */
function cornerSpot(model: TownModel, layout: IsoLayout, centre: Vec2, here: { road: number; dir: Vec2 }[]): Vec2 | null {
  const angle = (d: Vec2) => Math.atan2(d.y, d.x);
  const sorted = [...here].sort((a, b) => angle(a.dir) - angle(b.dir));
  const width = Math.max(...here.map((e) => layout.roads[e.road].width));
  const reach = width / 2 + SIDEWALK * 0.75;
  const wedges = sorted.map((e, k) => {
    const next = sorted[(k + 1) % sorted.length];
    let gap = angle(next.dir) - angle(e.dir);
    if (gap <= 0) gap += Math.PI * 2;
    return { from: angle(e.dir), gap };
  });
  // The tightest corners first: they're the real street corners.
  wedges.sort((a, b) => a.gap - b.gap);
  for (const w of wedges) {
    const mid = w.from + w.gap / 2;
    const r = w.gap < Math.PI ? Math.min(3, reach / Math.sin(w.gap / 2)) : reach;
    const at = { x: centre.x + Math.cos(mid) * r, y: centre.y + Math.sin(mid) * r };
    if (clear(model, layout, at)) return at;
  }
  return null;
}

function clear(model: TownModel, layout: IsoLayout, at: Vec2): boolean {
  const tx = Math.floor(at.x);
  const ty = Math.floor(at.y);
  if (!inBounds(tx, ty, model.width, model.height)) return false;
  const t = index(tx, ty, model.width);
  const ter = model.tiles.terrain[t];
  if (ter === Terrain.WATER_SHALLOW || ter === Terrain.WATER_DEEP || layout.use[t] === Use.HOUSE || layout.use[t] === Use.SHED) return false;
  return layout.roads.every((r) => distToPolyline(at, r.points) > r.width / 2 + 0.08);
}

/** Build the signposts into `root`. Returns what to dispose with the town. */
export function addStreetSigns(root: Group, signs: readonly Signpost[], names: StreetNames): { dispose(): void }[] {
  if (!signs.length) return [];
  const atlas = signAtlas(names.streets.map((s) => s.name));
  const tallest = Math.max(...signs.map((s) => s.blades.length));
  const postHeight = BLADE_Y + (tallest - 1) * BLADE_STEP + BLADE_HEIGHT / 2 + 0.03;
  const postGeo = new CylinderGeometry(POST_RADIUS, POST_RADIUS * 1.2, postHeight, 6).translate(0, postHeight / 2, 0);
  const postMat = new MeshLambertMaterial({ color: 0x5b6067 });
  const posts = new InstancedMesh(postGeo, postMat, signs.length);
  signs.forEach((s, i) => posts.setMatrixAt(i, new Matrix4().makeTranslation(s.at.x, 0, s.at.y)));
  posts.castShadow = true;
  posts.computeBoundingSphere();

  const pos: number[] = [];
  const nor: number[] = [];
  const uv: number[] = [];
  for (const s of signs) {
    s.blades.forEach((b, k) => {
      const cell = atlas.cell(b.street);
      const y = BLADE_Y + k * BLADE_STEP;
      blade(pos, nor, uv, s.at, b.along, y, cell);
    });
  }
  const geo = new BufferGeometry();
  geo.setAttribute('position', new Float32BufferAttribute(pos, 3));
  geo.setAttribute('normal', new Float32BufferAttribute(nor, 3));
  geo.setAttribute('uv', new Float32BufferAttribute(uv, 2));
  geo.computeBoundingSphere();
  const mat = new MeshLambertMaterial({ map: atlas.texture });
  const blades = new Mesh(geo, mat);
  blades.castShadow = true;
  root.add(posts, blades);
  return [postGeo, postMat, geo, mat, atlas.texture, posts];
}

/**
 * One blade: a thin board centred on the post, its long side along `along`,
 * lettered on both faces so the name reads left to right from either side,
 * with plain green edges.
 */
function blade(pos: number[], nor: number[], uv: number[], at: Vec2, along: Vec2, y: number, cell: { u0: number; u1: number; v0: number; v1: number }): void {
  const d = along;
  const n = { x: -d.y, y: d.x }; // the face normal on one side (map space)
  const hl = BLADE_LENGTH / 2;
  const hh = BLADE_HEIGHT / 2;
  const ht = BLADE_THICK / 2;
  // Corner in three space: centre + d*a + n*b, height y + c.
  const P = (a: number, b: number, c: number) => [at.x + d.x * a + n.x * b, y + c, at.y + d.y * a + n.y * b];
  const quad = (p: number[][], normal: number[], uvs: number[][]) => {
    for (const k of [0, 1, 2, 0, 2, 3]) {
      pos.push(...p[k]);
      nor.push(...normal);
      uv.push(...uvs[k]);
    }
  };
  const { u0, u1, v0, v1 } = cell;
  const nn = [n.x, 0, n.y];
  const nb = [-n.x, 0, -n.y];
  // Seen from the +n side the text runs along +d; from the -n side along -d.
  quad([P(-hl, ht, -hh), P(hl, ht, -hh), P(hl, ht, hh), P(-hl, ht, hh)], nn, [[u0, v0], [u1, v0], [u1, v1], [u0, v1]]);
  quad([P(hl, -ht, -hh), P(-hl, -ht, -hh), P(-hl, -ht, hh), P(hl, -ht, hh)], nb, [[u0, v0], [u1, v0], [u1, v1], [u0, v1]]);
  // Edges and ends take the border colour from the cell's corner.
  const edge = [[u0 + 0.001, v1 - 0.001], [u0 + 0.001, v1 - 0.001], [u0 + 0.001, v1 - 0.001], [u0 + 0.001, v1 - 0.001]];
  quad([P(-hl, -ht, hh), P(-hl, ht, hh), P(hl, ht, hh), P(hl, -ht, hh)], [0, 1, 0], edge);
  quad([P(-hl, ht, -hh), P(-hl, -ht, -hh), P(hl, -ht, -hh), P(hl, ht, -hh)], [0, -1, 0], edge);
  quad([P(hl, ht, -hh), P(hl, -ht, -hh), P(hl, -ht, hh), P(hl, ht, hh)], [d.x, 0, d.y], edge);
  quad([P(-hl, -ht, -hh), P(-hl, ht, -hh), P(-hl, ht, hh), P(-hl, -ht, hh)], [-d.x, 0, -d.y], edge);
}
