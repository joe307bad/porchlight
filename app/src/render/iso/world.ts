// Builds the 3D town for a TownModel from its iso layout (layout.ts): tiled
// ground with sunken rivers, flat vector roads with sidewalks and kerbs, stone
// bridges, Kenney houses, shops and towers fitted to every blueprint footprint
// (long street fronts split into rows), fenced yards with sheds and vegetable
// beds, trees, street lamps, street name signs, parked cars and the plaza fountain.
//
// Every copy of a Kenney model is drawn through one InstancedMesh per model
// part, so the whole town is a few hundred draw calls.
//
// Map (x, y) is Three's (x, z); 1 unit = 1 tile; Three's y is up.

import {
  AdditiveBlending,
  BufferAttribute,
  BufferGeometry,
  Color,
  CylinderGeometry,
  DynamicDrawUsage,
  Float32BufferAttribute,
  Group,
  InstancedMesh,
  Matrix4,
  Mesh,
  MeshBasicMaterial,
  MeshLambertMaterial,
  MeshStandardMaterial,
  PlaneGeometry,
  Points,
  PointsMaterial,
  Quaternion,
  ShaderMaterial,
  SphereGeometry,
  Vector3,
  BoxGeometry,
  type Material,
} from 'three';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { DIRS4, index, inBounds } from '../../core/grid';
import { dist, polylineLength, samplePolyline } from '../../core/geometry';
import { createRng, type Rng } from '../../core/rng';
import { Overlay, Terrain, type Facing, type TownModel, type Vec2 } from '../../model/types';
import { BUSHES, CARS, CROPS, FLOWERS, KIND_MODELS, TOWERS, TREES, type Prefab, type TownAssets } from './assets';
import { SIDEWALK, Use, buildLayout, type House, type IsoLayout, type SidewalkNetwork } from './layout';
import { MeshBuilder, v } from './mesh';
import type { Box } from './orbit';
import { Solids } from './collide';
import type { CarPose } from './driving';
import { DOOR_HEIGHT, DOOR_WIDTH, FURNITURE_SCALE, WALL, WALL_HEIGHT, doorOf, furnishRoom, innerRect, roomWalls, wallSide, type Rect, type Room, type RoomPlan } from './interiors';
import { nameStreets, type StreetNames } from './names';
import { addStreetSigns, planSigns, type Signpost } from './signs';
import { glowTexture } from './textures';
import { lightWindows, paneHash } from './windows';

/** Height of the water surface and the river bed below the land. */
export const WATER_Y = -0.14;
const BED_Y = -0.45;
/** How deep the map's slab runs below the ground at its edges. */
const SLAB = 1.6;
/** Distance between street lamps along a sidewalk (tiles). */
const LAMP_SPACING = 7;
/** Height of one storey (tiles). */
const FLOOR_H = 0.95;
/** Parked and moving cars are this long (tiles). */
export const CAR_LENGTH = 1.05;

const COLORS = {
  grass: 0x86bd5b,
  lawn: 0x95c964,
  sand: 0xe6d49a,
  rock: 0x9c968c,
  soil: 0x7a5638,
  paving: 0xbdb6a4,
  plazaA: 0xddd2b6,
  plazaB: 0xcdc1a2,
  bed: 0x6f8a86,
  bank: 0x8f7656,
  slab: 0x6b5238,
  asphalt: 0x6b6b6b,
  sidewalk: 0xd8d3bf,
  curb: 0xb3ad98,
  lane: 0xece7d2,
  path: 0xc9a46e,
  stone: 0xa39d8a,
  stoneShade: 0x7d7768,
  shedWall: 0xb07a4a,
  shedRoof: 0x5b4636,
  lampPost: 0x3b3f47,
};

export interface Lamp {
  /** Where the bulb hangs (map x, y and height). */
  x: number;
  y: number;
  h: number;
  phase: number;
  /** Some lamps are faulty and flicker hard. */
  faulty: boolean;
}

export interface Streak {
  path: Vec2[];
  /** Cumulative length at each path point. */
  at: number[];
  s: number;
  speed: number;
}

export interface Droplet {
  t: number;
  vx: number;
  vy: number;
}

export interface TownWorld {
  root: Group;
  model: TownModel;
  layout: IsoLayout;
  sidewalks: SidewalkNetwork;
  /** Every street's name, and which street each road belongs to. */
  names: StreetNames;
  /** Street name signs at the junctions. */
  signs: Signpost[];
  /** Every building's box, for working out what hides the player from the camera. */
  boxes: Box[];
  lamps: Lamp[];
  /** Bulbs, halos and light pools, one entry per lamp, recoloured each frame. */
  lampFx: { bulbs: InstancedMesh; halos: Points<BufferGeometry, PointsMaterial>; pools: InstancedMesh };
  water: ShaderMaterial;
  streaks: { mesh: InstancedMesh; items: Streak[] };
  fountain: { at: Vec2; drops: InstancedMesh; items: Droplet[] } | null;
  /** One ground-floor room per building piece, its furnishing, and its door. */
  rooms: Room[];
  /** Which blueprint building (index into layout.houses) each room belongs to. */
  roomBuilding: number[];
  plans: RoomPlan[];
  doors: Door[];
  /** The door panels, one instance per room, swung by `swingDoor`. */
  doorPanels: InstancedMesh;
  /** Walls and furniture the player can't walk through. */
  solids: Solids;
  /** The cars parked along the side streets, where each stands. */
  parked: CarPose[];
  /** Show or hide a parked car (hidden once the player drives it away). */
  setParkedVisible(car: number, visible: boolean): void;
  /** Show or hide a room's building (hidden while you're inside it, so you see in). */
  setShellVisible(room: number, visible: boolean): void;
  /** The furnished ground floor of a room, built on first use and kept. */
  interior(room: number): Group;
  dispose(): void;
}

export interface Door {
  /** Hinge on the map, and the panel's turn when shut. */
  hinge: Vec2;
  shut: number;
  /** Which way round opens it inward (+1 or -1). */
  swing: number;
  /** 0 = shut, 1 = wide open; eased by the view. */
  open: number;
}

/** How far a door swings open (radians). */
const DOOR_SWING = 1.75;

export function buildWorld(model: TownModel, assets: TownAssets): TownWorld {
  const layout = buildLayout(model);
  const rng = createRng(`${model.seed}::iso`);
  const root = new Group();
  const owned: { dispose(): void }[] = [];
  const instancer = new Instancer();
  const interiors = new Map<number, Group>();
  const shells: { prefab: Prefab; index: number }[] = [];

  const world: TownWorld = {
    root,
    model,
    layout,
    sidewalks: layout.sidewalks,
    names: nameStreets(model, layout.roads),
    signs: [],
    boxes: [],
    lamps: [],
    lampFx: null!,
    water: null!,
    streaks: null!,
    fountain: null,
    rooms: [],
    roomBuilding: [],
    plans: [],
    doors: [],
    doorPanels: null!,
    solids: null!,
    parked: [],
    setParkedVisible: () => {},
    setShellVisible: () => {},
    interior: () => {
      throw new Error('no rooms yet');
    },
    dispose: () => {
      root.removeFromParent();
      for (const g of interiors.values()) g.traverse((o) => {
        if ((o as Mesh).isMesh && o.userData.owned) (o as Mesh).geometry.dispose();
      });
      root.traverse((o) => {
        if ((o as InstancedMesh).isInstancedMesh) (o as InstancedMesh).dispose();
      });
      for (const d of owned) d.dispose();
    },
  };

  const lambert = new MeshLambertMaterial({ vertexColors: true });
  owned.push(lambert);
  const addStatic = (geometry: BufferGeometry, material: Material, shadows: { cast: boolean; receive: boolean }) => {
    const mesh = new Mesh(geometry, material);
    mesh.castShadow = shadows.cast;
    mesh.receiveShadow = shadows.receive;
    root.add(mesh);
    owned.push(geometry);
    return mesh;
  };

  addStatic(buildGround(model, layout, rng), lambert, { cast: false, receive: true });
  world.water = addWater(world, owned);
  addStatic(buildRoads(model, layout), lambert, { cast: true, receive: true });

  const sheds = new MeshBuilder();
  addHouses(world, assets, instancer, rng, shells);
  furnish(world, assets, rng);
  const facades = addFacades(world, rng, owned);
  addDoors(world, rng, owned);
  addYards(world, assets, instancer, sheds, rng);
  addTrees(world, assets, instancer, rng);
  addPlazaDecor(world, assets, instancer, rng);
  const parked = addParkedCars(world, assets, instancer);
  if (!sheds.empty) addStatic(sheds.build(), lambert, { cast: true, receive: true });
  addLamps(world, rng, owned);
  world.signs = planSigns(model, layout, world.names);
  owned.push(...addStreetSigns(root, world.signs, world.names));
  addStreaks(world, owned);
  addFountain(world, owned);
  instancer.build(root);
  world.setShellVisible = (room, visible) => {
    instancer.setVisible(shells[room].prefab, shells[room].index, visible);
    facades[room].visible = visible;
  };
  world.setParkedVisible = (car, visible) => instancer.setVisible(parked[car].prefab, parked[car].index, visible);
  const interiorMaterial = new MeshLambertMaterial({ vertexColors: true });
  owned.push(interiorMaterial);
  world.interior = (room) => {
    let g = interiors.get(room);
    if (!g) {
      g = buildInterior(world, assets, room, interiorMaterial);
      interiors.set(room, g);
      root.add(g);
    }
    return g;
  };
  return world;
}

/** The modelled ground floor is this tall; every Kenney building is clipped to stand on it. */
export const GROUND_FLOOR = WALL_HEIGHT;

const FACADES = [0xb5563f, 0xe9dcc0, 0xf1efe9, 0xa9b99a, 0x9aa8b5, 0xd8bf8f, 0xc9c4b8, 0xc98a5a, 0x8fa6a0];
const GLASS = 0x8fb8e8;
const TRIM = 0xf4f1ea;

/**
 * Every piece's ground floor, modelled: facade walls in the building's
 * colour round its room, with the doorway (where the door hangs), windows
 * with frames (wide shop windows on shops), a plinth, a cornice band and a
 * slab on top for the Kenney upper floors to stand on. One mesh per piece, so
 * the view can hide it while you're inside.
 */
function addFacades(world: TownWorld, rng: Rng, owned: { dispose(): void }[]): Mesh[] {
  const material = new MeshStandardMaterial({ vertexColors: true, roughness: 0.9, metalness: 0 });
  lightWindows(material);
  owned.push(material);
  let lastBuilding = -1;
  let colour = FACADES[0];
  return world.rooms.map((room, i) => {
    // One colour per building, so a row of pieces reads as a terrace.
    if (world.roomBuilding[i] !== lastBuilding) {
      lastBuilding = world.roomBuilding[i];
      colour = FACADES[Math.floor(rng.next() * FACADES.length)];
    }
    const b = new MeshBuilder();
    const H = GROUND_FLOOR;
    const shade = darker(colour, 0.78);
    const door = doorOf(room);
    for (const w of roomWalls(room)) b.box(w.x0, w.y0, w.x1, w.y1, 0, H, colour, shade);
    // Over the doorway: the wall carries on above the door.
    const half = DOOR_WIDTH / 2;
    const corners = [-1, 1].flatMap((side) =>
      [0, WALL].map((inward) => ({ x: door.at.x + door.along.x * side * half - door.outward.x * inward, y: door.at.y + door.along.y * side * half - door.outward.y * inward })),
    );
    const xs = corners.map((q) => q.x);
    const ys = corners.map((q) => q.y);
    b.box(Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys), DOOR_HEIGHT + 0.06, H, colour, shade);
    // Plinth and cornice, a little proud of the walls; the slab caps the top.
    const p = 0.015;
    // (a ring round the walls, not a slab, so it doesn't cover the floor seen through the door)
    const base = darker(colour, 0.6);
    const r = { x0: room.x0 - p, y0: room.y0 - p, x1: room.x1 + p, y1: room.y1 + p };
    const t = WALL + p;
    b.box(r.x0, r.y0, r.x1, r.y0 + t, 0, 0.05, base);
    b.box(r.x0, r.y1 - t, r.x1, r.y1, 0, 0.05, base);
    b.box(r.x0, r.y0 + t, r.x0 + t, r.y1 - t, 0, 0.05, base);
    b.box(r.x1 - t, r.y0 + t, r.x1, r.y1 - t, 0, 0.05, base);
    const c = 0.035;
    b.box(room.x0 - c, room.y0 - c, room.x1 + c, room.y1 + c, H - 0.05, H + 0.02, TRIM, 0x8c8a84);
    // Windows on every wall, skipping the doorway.
    const shop = room.style === 'shop' || room.style === 'office';
    for (const side of ['N', 'E', 'S', 'W'] as const) {
      const front = side === room.facing;
      const n = SIDE_NORMAL[side];
      const along = { x: Math.abs(n.y), y: Math.abs(n.x) };
      const len = along.x ? room.x1 - room.x0 : room.y1 - room.y0;
      const origin = { x: n.x > 0 ? room.x1 : room.x0, y: n.y > 0 ? room.y1 : room.y0 };
      const wide = front && shop;
      const width = wide ? 0.62 : 0.28;
      const spacing = wide ? 0.82 : 0.6;
      const sill = wide ? 0.12 : 0.26;
      const top = wide ? 0.58 : 0.58;
      const count = Math.floor((len - 0.3) / spacing);
      if (count < 1) continue;
      const start = (len - count * spacing) / 2 + spacing / 2;
      for (let k = 0; k < count; k++) {
        const t = start + k * spacing;
        const cx = origin.x + along.x * t;
        const cy = origin.y + along.y * t;
        if (front && Math.hypot(cx - door.at.x, cy - door.at.y) < half + width / 2 + 0.08) continue;
        windowPane(b, { x: cx, y: cy }, along, n, width, sill, top);
      }
    }
    const mesh = new Mesh(b.build(), material);
    mesh.castShadow = mesh.receiveShadow = true;
    owned.push(mesh.geometry);
    world.root.add(mesh);
    return mesh;
  });
}

const SIDE_NORMAL: Record<Facing, Vec2> = { N: { x: 0, y: -1 }, E: { x: 1, y: 0 }, S: { x: 0, y: 1 }, W: { x: -1, y: 0 } };

/** A framed pane of glass standing just proud of a wall whose outward normal is n. */
let paneCount = 0;

function windowPane(b: MeshBuilder, c: Vec2, along: Vec2, n: Vec2, width: number, y0: number, y1: number): void {
  const face = new Vector3(n.x, 0, n.y);
  // Its own seed, so the whole pane lights (or not) together at night.
  const seed = 1 + paneHash(++paneCount);
  const quad = (half: number, lo: number, hi: number, out: number, color: number, glass = false) => {
    const ox = c.x + n.x * out;
    const oy = c.y + n.y * out;
    b.quad(
      v(ox - along.x * half, lo, oy - along.y * half),
      v(ox + along.x * half, lo, oy + along.y * half),
      v(ox + along.x * half, hi, oy + along.y * half),
      v(ox - along.x * half, hi, oy - along.y * half),
      color,
      face,
      glass ? seed : 0,
    );
  };
  quad(width / 2 + 0.03, y0 - 0.03, y1 + 0.03, 0.004, TRIM);
  quad(width / 2, y0, y1, 0.008, GLASS, true);
  // A sill.
  quad(width / 2 + 0.05, y0 - 0.05, y0 - 0.02, 0.012, 0xdcd8cf);
}

function darker(c: number, k: number): number {
  return (Math.round(((c >> 16) & 255) * k) << 16) | (Math.round(((c >> 8) & 255) * k) << 8) | Math.round((c & 255) * k);
}

const FLOORS: Record<string, number> = { house: 0xb98b5e, shop: 0xd9d2c5, office: 0xcfd3d6, workshop: 0x9a958c, hall: 0xa8794f, church: 0xa8794f };

/**
 * A room's ground floor: a floor, the walls (one mesh per side, so the view
 * can cut down the ones facing the camera), and its furniture.
 */
function buildInterior(world: TownWorld, assets: TownAssets, id: number, material: Material): Group {
  const room = world.rooms[id];
  const g = new Group();
  const floor = new MeshBuilder();
  const inner = innerRect(room);
  floor.rect(inner.x0, inner.y0, inner.x1, inner.y1, 0.012, FLOORS[room.style] ?? FLOORS.house);
  const floorMesh = new Mesh(floor.build(), material);
  floorMesh.receiveShadow = true;
  floorMesh.userData.owned = true;
  g.add(floorMesh);
  // One mesh per wall side, standing on y = 0, so the view can cut down
  // whichever walls face the camera (scale.y) to see in over them.
  const sides = new Map<Facing, MeshBuilder>();
  for (const w of roomWalls(room)) {
    const side = wallSide(room, w);
    let b = sides.get(side);
    if (!b) sides.set(side, (b = new MeshBuilder()));
    // A hair inside the facade's faces, so the two never fight when both show (door open).
    const e = 0.006;
    b.box(w.x0 + e, w.y0 + e, w.x1 - e, w.y1 - e, 0, WALL_HEIGHT - e, 0xefe6d6, 0xb8ad9a);
  }
  for (const [side, b] of sides) {
    const wall = new Mesh(b.build(), material);
    wall.receiveShadow = true;
    wall.castShadow = true;
    wall.userData.owned = true;
    wall.userData.side = side;
    g.add(wall);
  }
  for (const p of world.plans[id].furniture) {
    const prefab = assets.prefab(`furniture/${p.name}`);
    const m = placement(prefab, p, p.rot, FURNITURE_SCALE, FURNITURE_SCALE, FURNITURE_SCALE, p.lift + 0.012);
    for (const part of prefab.parts) {
      const mesh = new Mesh(part.geometry, part.material);
      mesh.matrixAutoUpdate = false;
      mesh.matrix.copy(m);
      mesh.castShadow = !p.flat;
      mesh.receiveShadow = true;
      g.add(mesh);
    }
  }
  return g;
}

/** Plan every room's furniture, and make walls and furniture solid. */
function furnish(world: TownWorld, assets: TownAssets, rng: Rng): void {
  const size = (name: string) => {
    const p = assets.prefab(`furniture/${name}`);
    return { w: p.size.x * FURNITURE_SCALE, d: p.size.z * FURNITURE_SCALE, h: p.size.y * FURNITURE_SCALE };
  };
  const solid: Rect[] = [];
  for (const room of world.rooms) {
    const plan = furnishRoom(room, size, rng);
    world.plans.push(plan);
    solid.push(...roomWalls(room));
    for (const f of plan.furniture) if (!f.flat && f.lift === 0) solid.push(f.box);
  }
  world.solids = new Solids(solid, world.model.width);
}

const DOOR_COLOURS = [0x8a3b2e, 0x2f5d50, 0x2e4a7a, 0x6b4a2f, 0xb08a3e, 0x5b3b5e, 0xe8e2d0];

/** A door in every room's front wall: a frame (fixed) and a panel that swings in. */
function addDoors(world: TownWorld, rng: Rng, owned: { dispose(): void }[]): void {
  const frames = new MeshBuilder();
  const count = Math.max(1, world.rooms.length);
  const panelGeo = new BoxGeometry(DOOR_WIDTH - 0.03, DOOR_HEIGHT - 0.02, 0.035).translate((DOOR_WIDTH - 0.03) / 2, (DOOR_HEIGHT - 0.02) / 2, 0);
  const panelMat = new MeshLambertMaterial({ color: 0xffffff });
  const panels = new InstancedMesh(panelGeo, panelMat, count);
  panels.count = world.rooms.length;
  panels.instanceMatrix.setUsage(DynamicDrawUsage);
  panels.castShadow = true;
  panels.frustumCulled = false;
  world.rooms.forEach((room, i) => {
    const { at, outward, along } = doorOf(room);
    // Frame: two posts and a lintel standing a hair proud of the front wall.
    const post = 0.045;
    const depth = WALL + 0.05;
    for (const side of [-1, 1]) {
      const c = { x: at.x + along.x * side * (DOOR_WIDTH / 2 + post / 2) - outward.x * (WALL / 2), y: at.y + along.y * side * (DOOR_WIDTH / 2 + post / 2) - outward.y * (WALL / 2) };
      const hx = Math.abs(along.x) * post / 2 + Math.abs(outward.x) * depth / 2;
      const hy = Math.abs(along.y) * post / 2 + Math.abs(outward.y) * depth / 2;
      frames.box(c.x - hx, c.y - hy, c.x + hx, c.y + hy, 0, DOOR_HEIGHT + 0.04, 0xf2eee4);
    }
    const lc = { x: at.x - outward.x * (WALL / 2), y: at.y - outward.y * (WALL / 2) };
    const lx = Math.abs(along.x) * (DOOR_WIDTH / 2 + post) + Math.abs(outward.x) * depth / 2;
    const ly = Math.abs(along.y) * (DOOR_WIDTH / 2 + post) + Math.abs(outward.y) * depth / 2;
    frames.box(lc.x - lx, lc.y - ly, lc.x + lx, lc.y + ly, DOOR_HEIGHT - 0.01, DOOR_HEIGHT + 0.06, 0xf2eee4);
    // The panel hangs from a hinge at one side of the gap; local +X runs along it.
    const hinge = { x: at.x - along.x * (DOOR_WIDTH / 2 - 0.01) - outward.x * (WALL / 2), y: at.y - along.y * (DOOR_WIDTH / 2 - 0.01) - outward.y * (WALL / 2) };
    const shut = Math.atan2(-along.y, along.x);
    // Turning by +φ moves local +X toward (-sin, -cos) of the shut angle; open inward (away from outward).
    const swing = -Math.sin(shut) * -outward.x + -Math.cos(shut) * -outward.y > 0 ? 1 : -1;
    const door: Door = { hinge, shut, swing, open: 0 };
    world.doors.push(door);
    panels.setColorAt(i, new Color(DOOR_COLOURS[Math.floor(rng.next() * DOOR_COLOURS.length)]));
    swingDoor(world, i, panels);
  });
  world.doorPanels = panels;
  world.root.add(panels);
  const frameGeo = frames.build();
  const frameMat = new MeshLambertMaterial({ vertexColors: true });
  const frameMesh = new Mesh(frameGeo, frameMat);
  frameMesh.castShadow = frameMesh.receiveShadow = true;
  world.root.add(frameMesh);
  owned.push(panelGeo, panelMat, frameGeo, frameMat);
}

const panelMatrix = new Matrix4();
const panelQuat = new Quaternion();
const panelPos = new Vector3();
const ONE = new Vector3(1, 1, 1);

/** Set door i's panel to its current opening. */
export function swingDoor(world: TownWorld, i: number, panels = world.doorPanels): void {
  const d = world.doors[i];
  const eased = d.open * d.open * (3 - 2 * d.open);
  panelQuat.setFromAxisAngle(Y_AXIS, d.shut + d.swing * eased * DOOR_SWING);
  panelPos.set(d.hinge.x, 0.01, d.hinge.y);
  panels.setMatrixAt(i, panelMatrix.compose(panelPos, panelQuat, ONE));
  panels.instanceMatrix.needsUpdate = true;
}

const isWaterTerrain = (t: number) => t === Terrain.WATER_SHALLOW || t === Terrain.WATER_DEEP;

/** Ground tiles as flat coloured squares; water tiles sink to a river bed with earth banks. */
function buildGround(model: TownModel, layout: IsoLayout, rng: Rng): BufferGeometry {
  const { tiles, width: w, height: h } = model;
  const b = new MeshBuilder();
  const wet = (x: number, y: number) => inBounds(x, y, w, h) && isWaterTerrain(tiles.terrain[index(x, y, w)]);
  const jitter = (c: number, amount: number) => {
    const k = 1 - rng.next() * amount;
    const r = Math.round(((c >> 16) & 255) * k);
    const g = Math.round(((c >> 8) & 255) * (1 - (1 - k) * 0.5));
    const bl = Math.round((c & 255) * k);
    return (r << 16) | (g << 8) | bl;
  };
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = index(x, y, w);
      const terrain = tiles.terrain[i];
      const overlay = tiles.overlay[i];
      const use = layout.use[i];
      if (isWaterTerrain(terrain)) {
        b.rect(x, y, x + 1, y + 1, BED_Y, COLORS.bed);
        continue;
      }
      let color: number;
      if (use === Use.HOUSE || overlay === Overlay.ROAD_MAIN || overlay === Overlay.ROAD_STREET || overlay === Overlay.BRIDGE) color = COLORS.paving;
      else if (overlay === Overlay.PLAZA) color = (x + y) % 2 ? COLORS.plazaA : COLORS.plazaB;
      else if (use === Use.GARDEN || use === Use.COMMUNITY_GARDEN) color = jitter(COLORS.soil, 0.08);
      else if (terrain === Terrain.SAND) color = jitter(COLORS.sand, 0.05);
      else if (terrain === Terrain.ROCK) color = jitter(COLORS.rock, 0.08);
      else if (use === Use.YARD || use === Use.SHED) color = jitter(COLORS.lawn, 0.05);
      else color = jitter(COLORS.grass, 0.08);
      b.rect(x, y, x + 1, y + 1, 0, color);
      // Banks down to the river bed on every side that meets water.
      const bank = terrain === Terrain.SAND ? COLORS.sand : COLORS.bank;
      if (wet(x, y - 1)) b.quad(v(x, 0, y), v(x + 1, 0, y), v(x + 1, BED_Y, y), v(x, BED_Y, y), bank, new Vector3(0, 0, -1));
      if (wet(x, y + 1)) b.quad(v(x, 0, y + 1), v(x + 1, 0, y + 1), v(x + 1, BED_Y, y + 1), v(x, BED_Y, y + 1), bank, new Vector3(0, 0, 1));
      if (wet(x - 1, y)) b.quad(v(x, 0, y), v(x, 0, y + 1), v(x, BED_Y, y + 1), v(x, BED_Y, y), bank, new Vector3(-1, 0, 0));
      if (wet(x + 1, y)) b.quad(v(x + 1, 0, y), v(x + 1, 0, y + 1), v(x + 1, BED_Y, y + 1), v(x + 1, BED_Y, y), bank, new Vector3(1, 0, 0));
    }
  }
  // The town sits on a slab of earth, so its edges read as a diorama.
  b.quad(v(0, 0, 0), v(w, 0, 0), v(w, -SLAB, 0), v(0, -SLAB, 0), COLORS.slab, new Vector3(0, 0, -1));
  b.quad(v(0, 0, h), v(w, 0, h), v(w, -SLAB, h), v(0, -SLAB, h), COLORS.slab, new Vector3(0, 0, 1));
  b.quad(v(0, 0, 0), v(0, 0, h), v(0, -SLAB, h), v(0, -SLAB, 0), COLORS.slab, new Vector3(-1, 0, 0));
  b.quad(v(w, 0, 0), v(w, 0, h), v(w, -SLAB, h), v(w, -SLAB, 0), COLORS.slab, new Vector3(1, 0, 0));
  return b.build();
}

/** The water surface over every water tile, shimmering in a small shader. */
function addWater(world: TownWorld, owned: { dispose(): void }[]): ShaderMaterial {
  const { tiles, width: w, height: h } = world.model;
  const pos: number[] = [];
  const deep: number[] = [];
  const idx: number[] = [];
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const t = tiles.terrain[index(x, y, w)];
      if (!isWaterTerrain(t)) continue;
      const base = pos.length / 3;
      pos.push(x, WATER_Y, y, x, WATER_Y, y + 1, x + 1, WATER_Y, y + 1, x + 1, WATER_Y, y);
      const d = t === Terrain.WATER_DEEP ? 1 : 0;
      deep.push(d, d, d, d);
      idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
    }
  }
  const material = new ShaderMaterial({
    uniforms: { time: { value: 0 }, night: { value: 0 } },
    vertexShader: /* glsl */ `
      attribute float deep;
      varying float vDeep;
      varying vec2 vXZ;
      void main() {
        vDeep = deep;
        vec4 wp = modelMatrix * vec4(position, 1.0);
        vXZ = wp.xz;
        gl_Position = projectionMatrix * viewMatrix * wp;
      }`,
    fragmentShader: /* glsl */ `
      uniform float time;
      uniform float night;
      varying float vDeep;
      varying vec2 vXZ;
      void main() {
        float wave = 0.5 + 0.5 * sin(time * 1.6 + vXZ.x * 0.55 + vXZ.y * 0.35);
        float ripple = 0.5 + 0.5 * sin(time * 2.3 - vXZ.x * 1.7 + vXZ.y * 1.1);
        vec3 c = mix(vec3(0.42, 0.70, 0.88), vec3(0.25, 0.50, 0.76), vDeep);
        c += 0.05 * wave + 0.03 * ripple;
        c = mix(c, c * vec3(0.28, 0.34, 0.55), night);
        gl_FragColor = vec4(c, 1.0);
      }`,
  });
  owned.push(material);
  if (!idx.length) return material;
  const g = new BufferGeometry();
  g.setAttribute('position', new Float32BufferAttribute(pos, 3));
  g.setAttribute('deep', new Float32BufferAttribute(deep, 1));
  g.setIndex(idx);
  g.computeBoundingSphere();
  owned.push(g);
  world.root.add(new Mesh(g, material));
  return material;
}

/**
 * Roads as flat strips along each smoothed centreline, stacked a hair apart
 * so the layers read in order: bridge decks, outer kerbs, sidewalks, inner
 * kerbs, asphalt (so junctions blend), then dashed centre lines.
 */
function buildRoads(model: TownModel, layout: IsoLayout): BufferGeometry {
  const b = new MeshBuilder();
  const streets = layout.roads.filter((r) => r.kind !== 'path');
  const inMap = (p: Vec2) => p.x > 0.01 && p.y > 0.01 && p.x < model.width - 0.01 && p.y < model.height - 0.01;
  const layer = (y: number, extra: number, color: number) => {
    for (const r of streets) {
      b.ribbon(r.points, r.width + extra, y, color);
      // Round caps fill junctions, but would spill past the map edge at the gates.
      for (const p of [r.points[0], r.points[r.points.length - 1]]) if (inMap(p)) b.disc(p, (r.width + extra) / 2, y, color);
    }
  };

  // Bridges: a stone deck a little wider than the road, sides dropping to the
  // river bed, and a parapet along each edge; the road carries on across it.
  for (const br of layout.bridges) {
    const half = br.width / 2 + SIDEWALK + 0.25;
    b.ribbon(br.points, half * 2, 0.01, COLORS.stone);
    b.wall(br.points, half, BED_Y, 0.01, COLORS.stoneShade);
    b.wall(br.points, -half, BED_Y, 0.01, COLORS.stoneShade);
    for (const side of [1, -1]) b.parapet(br.points, side * (half - 0.07), 0.12, 0.01, 0.24, COLORS.stone, COLORS.stoneShade);
  }
  layer(0.014, SIDEWALK * 2 + 0.1, COLORS.curb);
  layer(0.018, SIDEWALK * 2, COLORS.sidewalk);
  layer(0.022, 0.08, COLORS.curb);
  layer(0.026, 0, COLORS.asphalt);

  for (const r of layout.roads.filter((rd) => rd.kind === 'path')) {
    b.ribbon(r.points, r.width, 0.012, COLORS.path);
    for (const p of [r.points[0], r.points[r.points.length - 1]]) b.disc(p, r.width / 2, 0.012, COLORS.path);
  }

  // Dashed centre lines, skipping a little near each end so they don't run into junctions.
  for (const r of streets) {
    const len = polylineLength(r.points);
    const samples = samplePolyline(r.points, 0.1);
    let travelled = 0;
    let run: Vec2[] = [];
    const flush = () => {
      if (run.length > 1) b.ribbon(run, 0.07, 0.03, COLORS.lane);
      run = [];
    };
    for (let k = 1; k < samples.length; k++) {
      const seg = dist(samples[k - 1], samples[k]);
      const mid = travelled + seg / 2;
      travelled += seg;
      if (mid < 1.2 || mid > len - 1.2 || mid % 0.9 > 0.45) {
        flush();
        continue;
      }
      if (!run.length) run.push(samples[k - 1]);
      run.push(samples[k]);
    }
    flush();
  }
  return b.build();
}

/** Collects every placement of each prefab, then draws them as InstancedMeshes. */
class Instancer {
  private readonly items = new Map<Prefab, Matrix4[]>();
  private readonly noShadow = new Set<Prefab>();

  private readonly meshes = new Map<Prefab, InstancedMesh[]>();

  /** Add a copy; returns its index among this prefab's copies. */
  add(p: Prefab, m: Matrix4, castShadow = true): number {
    const list = this.items.get(p);
    if (!castShadow) this.noShadow.add(p);
    if (list) return list.push(m) - 1;
    this.items.set(p, [m]);
    return 0;
  }

  /** Hide one copy (squashed to nothing) or put it back. */
  setVisible(p: Prefab, index: number, visible: boolean): void {
    const m = visible ? this.items.get(p)![index] : ZERO;
    for (const mesh of this.meshes.get(p) ?? []) {
      mesh.setMatrixAt(index, m);
      mesh.instanceMatrix.needsUpdate = true;
    }
  }

  build(root: Group): void {
    for (const [prefab, matrices] of this.items) {
      for (const part of prefab.parts) {
        const mesh = new InstancedMesh(part.geometry, part.material, matrices.length);
        matrices.forEach((m, i) => mesh.setMatrixAt(i, m));
        mesh.castShadow = !this.noShadow.has(prefab);
        mesh.receiveShadow = true;
        mesh.computeBoundingSphere();
        root.add(mesh);
        this.meshes.set(prefab, [...(this.meshes.get(prefab) ?? []), mesh]);
      }
    }
  }
}

const ZERO = new Matrix4().makeScale(0, 0, 0);

const q = new Quaternion();
const Y_AXIS = new Vector3(0, 1, 0);

/**
 * Where to draw a prefab: its footprint centred on map point `at`, turned by
 * `rot` about the vertical (its front, +Z, then faces (sin rot, cos rot) on
 * the map) and scaled by (sx, sy, sz) in its own axes.
 */
export function placement(p: Prefab, at: Vec2, rot: number, sx: number, sy: number, sz: number, lift = 0): Matrix4 {
  const centre = new Matrix4().makeTranslation(-(p.min.x + p.size.x / 2), -p.min.y, -(p.min.z + p.size.z / 2));
  const m = new Matrix4().compose(new Vector3(at.x, lift, at.y), q.setFromAxisAngle(Y_AXIS, rot), new Vector3(sx, sy, sz));
  return m.multiply(centre);
}

/**
 * Kenney's city-kit buildings have their fronts (doors, shopfronts, porches) on
 * -Z, unlike the cars and characters, which face +Z: turn them half round.
 * (tests/models.test.ts checks this from where each model's detail is.)
 */
const BUILDING_TURN = Math.PI;

const FRONT: Record<Facing, Vec2> = { N: { x: 0, y: -1 }, E: { x: 1, y: 0 }, S: { x: 0, y: 1 }, W: { x: -1, y: 0 } };

/**
 * Every blueprint building at its exact footprint, front to the street. A long
 * street front is split into a row of narrower buildings whose proportions
 * suit the models; each piece's model is stretched to fill its share.
 */
function addHouses(world: TownWorld, assets: TownAssets, instancer: Instancer, rng: Rng, shells: { prefab: Prefab; index: number }[]): void {
  for (const house of world.layout.houses) {
    const xs = house.tiles.map((t) => t.x);
    const ys = house.tiles.map((t) => t.y);
    const x0 = Math.min(...xs);
    const y0 = Math.min(...ys);
    const x1 = Math.max(...xs) + 1;
    const y1 = Math.max(...ys) + 1;
    const front = FRONT[house.facing];
    const rot = Math.atan2(front.x, front.y) + BUILDING_TURN;
    const alongX = house.facing === 'N' || house.facing === 'S';
    const frontage = alongX ? x1 - x0 : y1 - y0;
    const depth = alongX ? y1 - y0 : x1 - x0;
    const candidates = modelsFor(house);
    const splittable = house.style === 'house' || house.style === 'shop' || house.style === 'workshop' || house.style === 'office';
    const pieces = splittable ? bestSplit(assets, candidates, frontage, depth) : 1;
    const len = frontage / pieces;
    const colours = assets.variationCount(candidates[0]);
    // One colour scheme per building, so a row reads as one terrace.
    const variation = rng.int(0, colours - 1);
    for (let k = 0; k < pieces; k++) {
      const name = pickModel(assets, candidates, len / depth, house, rng);
      const prefab = assets.variant(name, variation);
      const gap = 0.04;
      const sx = (len - gap * 2) / prefab.size.x;
      const sz = (depth - gap * 2) / prefab.size.z;
      const sy = Math.sqrt(sx * sz) * heightFactor(prefab, Math.sqrt(sx * sz), house);
      // Piece k's centre: along the frontage from the low end, mid-depth.
      const along = (alongX ? x0 : y0) + (k + 0.5) * len;
      const at = alongX ? { x: along, y: (y0 + y1) / 2 } : { x: (x0 + x1) / 2, y: along };
      const index = instancer.add(prefab, placement(prefab, at, rot, sx, sy, sz));
      const halfX = alongX ? len / 2 : depth / 2;
      const halfY = alongX ? depth / 2 : len / 2;
      world.boxes.push({ x0: at.x - halfX, y0: at.y - halfY, x1: at.x + halfX, y1: at.y + halfY, top: prefab.size.y * sy });
      // Its ground-floor room fills the same rectangle, inside the shell.
      shells.push({ prefab, index });
      world.roomBuilding.push(world.layout.houses.indexOf(house));
      world.rooms.push({ id: world.rooms.length, x0: at.x - halfX + gap, y0: at.y - halfY + gap, x1: at.x + halfX - gap, y1: at.y + halfY - gap, facing: house.facing, style: house.style });
    }
  }
}

function modelsFor(house: House): readonly string[] {
  if (house.style === 'office' && house.floors >= 4) return TOWERS;
  return KIND_MODELS[house.style];
}

/** How many pieces to split a street front into so each suits a model's proportions. */
function bestSplit(assets: TownAssets, candidates: readonly string[], frontage: number, depth: number): number {
  let best = 1;
  let bestCost = Infinity;
  for (let n = 1; n <= 4; n++) {
    const len = frontage / n;
    if (n > 1 && len < 1.8) break;
    const fit = Math.min(...candidates.map((c) => aspectMiss(assets.prefab(c), len / depth)));
    const cost = fit + (n - 1) * 0.12;
    if (cost < bestCost) {
      bestCost = cost;
      best = n;
    }
  }
  return best;
}

/** How far a model's width:depth is from the wanted one (0 = exact), on a log scale. */
function aspectMiss(p: Prefab, wanted: number): number {
  return Math.abs(Math.log(p.size.x / p.size.z / wanted));
}

/** A model whose proportions suit the piece: one of the few that fit best (taller ones for more floors). */
function pickModel(assets: TownAssets, candidates: readonly string[], aspect: number, house: House, rng: Rng): string {
  const scored = candidates.map((c) => {
    const p = assets.prefab(c);
    const tall = p.size.y / Math.sqrt(p.size.x * p.size.z);
    return { c, cost: aspectMiss(p, aspect) + Math.abs(tall - storeyRatio(house)) * 0.3 };
  });
  scored.sort((a, b) => a.cost - b.cost);
  return rng.pick(scored.slice(0, Math.min(4, scored.length))).c;
}

/** Roughly how tall for its size a building with this many floors should be. */
function storeyRatio(house: House): number {
  return 0.45 + house.floors * 0.22;
}

/** Nudge a model's height toward its floor count, within limits so it keeps its look. */
function heightFactor(p: Prefab, scale: number, house: House): number {
  const natural = p.size.y * scale;
  const wanted = house.floors * FLOOR_H + (house.style === 'house' ? 0.9 : 0.4);
  return Math.min(1.35, Math.max(0.75, wanted / natural));
}

/** Crops, sheds and fences: everything that stands in the yards and gardens. */
function addYards(world: TownWorld, assets: TownAssets, instancer: Instancer, sheds: MeshBuilder, rng: Rng): void {
  const { model, layout } = world;
  const { width: w, height: h } = model;
  const crops = CROPS.map((c) => assets.prefab(c));
  const props = [assets.prefab('nature/log_stack'), assets.prefab('nature/pot_large')];
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const use = layout.use[index(x, y, w)];
      if (use === Use.GARDEN || use === Use.COMMUNITY_GARDEN) {
        const roll = rng.next();
        if (use === Use.COMMUNITY_GARDEN && roll < 0.15) {
          const p = props[Math.floor(rng.next() * props.length)];
          const s = 0.7 / Math.max(p.size.x, p.size.z);
          instancer.add(p, placement(p, { x: x + 0.5, y: y + 0.5 }, rng.range(0, Math.PI * 2), s, s, s));
          continue;
        }
        // A bed of one crop: four plants in a square.
        const crop = crops[Math.floor(rng.next() * crops.length)];
        const s = 0.42 / Math.max(crop.size.x, crop.size.z);
        for (const [ox, oy] of [[0.27, 0.27], [0.73, 0.27], [0.27, 0.73], [0.73, 0.73]]) {
          instancer.add(crop, placement(crop, { x: x + ox, y: y + oy }, rng.range(0, Math.PI * 2), s, s * rng.range(0.85, 1.15), s), false);
        }
      } else if (use === Use.SHED) {
        addShed(sheds, x + 0.5, y + 0.5, rng.chance(0.5));
      }
    }
  }
  const fence = assets.prefab('suburban/fence');
  const sx = 1 / fence.size.x;
  for (const f of layout.fences) {
    const inset = 0.05;
    const at = {
      N: { x: f.x + 0.5, y: f.y + inset },
      S: { x: f.x + 0.5, y: f.y + 1 - inset },
      W: { x: f.x + inset, y: f.y + 0.5 },
      E: { x: f.x + 1 - inset, y: f.y + 0.5 },
    }[f.side];
    const rot = f.side === 'N' || f.side === 'S' ? 0 : Math.PI / 2;
    instancer.add(fence, placement(fence, at, rot, sx, 1.3, 1.3));
  }
}

/** A little garden shed: plank walls under a dark pitched roof. */
function addShed(b: MeshBuilder, cx: number, cy: number, turned: boolean): void {
  const hw = turned ? 0.3 : 0.36;
  const hd = turned ? 0.36 : 0.3;
  const wall = 0.42;
  const ridge = 0.64;
  b.box(cx - hw, cy - hd, cx + hw, cy + hd, 0, wall, COLORS.shedWall);
  const o = 0.05;
  if (turned) {
    // Ridge along y.
    b.quad(v(cx - hw - o, wall - 0.03, cy - hd - o), v(cx, ridge, cy - hd - o), v(cx, ridge, cy + hd + o), v(cx - hw - o, wall - 0.03, cy + hd + o), COLORS.shedRoof, new Vector3(-1, 1, 0));
    b.quad(v(cx + hw + o, wall - 0.03, cy - hd - o), v(cx, ridge, cy - hd - o), v(cx, ridge, cy + hd + o), v(cx + hw + o, wall - 0.03, cy + hd + o), COLORS.shedRoof, new Vector3(1, 1, 0));
    for (const y of [cy - hd, cy + hd]) b.quad(v(cx - hw, wall, y), v(cx, ridge - 0.02, y), v(cx, ridge - 0.02, y), v(cx + hw, wall, y), COLORS.shedWall, new Vector3(0, 0, y < cy ? -1 : 1));
  } else {
    b.quad(v(cx - hw - o, wall - 0.03, cy - hd - o), v(cx - hw - o, ridge, cy), v(cx + hw + o, ridge, cy), v(cx + hw + o, wall - 0.03, cy - hd - o), COLORS.shedRoof, new Vector3(0, 1, -1));
    b.quad(v(cx - hw - o, wall - 0.03, cy + hd + o), v(cx - hw - o, ridge, cy), v(cx + hw + o, ridge, cy), v(cx + hw + o, wall - 0.03, cy + hd + o), COLORS.shedRoof, new Vector3(0, 1, 1));
    for (const x of [cx - hw, cx + hw]) b.quad(v(x, wall, cy - hd), v(x, ridge - 0.02, cy), v(x, ridge - 0.02, cy), v(x, wall, cy + hd), COLORS.shedWall, new Vector3(x < cx ? -1 : 1, 0, 0));
  }
  // A door on the long side.
  const door = 0x4a3426;
  if (turned) b.quad(v(cx + hw + 0.005, 0, cy - 0.1), v(cx + hw + 0.005, 0, cy + 0.1), v(cx + hw + 0.005, 0.32, cy + 0.1), v(cx + hw + 0.005, 0.32, cy - 0.1), door, new Vector3(1, 0, 0));
  else b.quad(v(cx - 0.1, 0, cy + hd + 0.005), v(cx + 0.1, 0, cy + hd + 0.005), v(cx + 0.1, 0.32, cy + hd + 0.005), v(cx - 0.1, 0.32, cy + hd + 0.005), door, new Vector3(0, 0, 1));
}

/** Trees on open ground (thicker out of town and in the greens), bushes and flowers among them, rocks on rocky ground. */
function addTrees(world: TownWorld, assets: TownAssets, instancer: Instancer, rng: Rng): void {
  const { model, layout } = world;
  const { tiles, width: w, height: h, center, townRadius } = model;
  const trees = TREES.map((t) => assets.prefab(t));
  const bushes = BUSHES.map((t) => assets.prefab(t));
  const flowers = FLOWERS.map((t) => assets.prefab(t));
  const rock = assets.prefab('nature/rock_smallA');
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = index(x, y, w);
      const use = layout.use[i];
      const terrain = tiles.terrain[i];
      if (tiles.overlay[i] !== Overlay.NONE) continue;
      if (use === Use.HOUSE || use === Use.SHED || use === Use.GARDEN || use === Use.COMMUNITY_GARDEN) continue;
      if (terrain === Terrain.ROCK) {
        if (rng.chance(0.22)) {
          const s = rng.range(1.2, 2.4);
          instancer.add(rock, placement(rock, { x: x + rng.range(0.25, 0.75), y: y + rng.range(0.25, 0.75) }, rng.range(0, 6.3), s, s, s));
        }
        continue;
      }
      if (terrain !== Terrain.GRASS) continue;
      let nearStreet = false;
      for (const [dx, dy] of DIRS4) {
        const nx = x + dx;
        const ny = y + dy;
        if (!inBounds(nx, ny, w, h)) continue;
        const j = index(nx, ny, w);
        if (tiles.overlay[j] !== Overlay.NONE || isWaterTerrain(tiles.terrain[j]) || layout.use[j] === Use.HOUSE) nearStreet = true;
      }
      const inTown = Math.hypot(x + 0.5 - center.x, y + 0.5 - center.y) < townRadius;
      const chance = use === Use.YARD ? (nearStreet ? 0.04 : 0.14) : use === Use.GREEN ? 0.28 : nearStreet ? 0.03 : inTown ? 0.1 : 0.22;
      const roll = rng.next();
      const at = { x: x + 0.5 + rng.range(-0.25, 0.25), y: y + 0.5 + rng.range(-0.25, 0.25) };
      const spin = rng.range(0, Math.PI * 2);
      if (roll < chance) {
        // Leafy trees in town, more conifers out in the country.
        const pool = inTown ? trees.filter((_, k) => k < 4 || k > 6) : trees;
        const t = pool[Math.floor(rng.next() * pool.length)];
        const s = rng.range(1.3, 2.1) / t.size.y;
        instancer.add(t, placement(t, at, spin, s, s * rng.range(0.9, 1.1), s));
      } else if (roll < chance + (use === Use.YARD || use === Use.GREEN ? 0.08 : 0.03)) {
        const b = bushes[Math.floor(rng.next() * bushes.length)];
        const s = rng.range(0.4, 0.65) / Math.max(b.size.x, b.size.z);
        instancer.add(b, placement(b, at, spin, s, s, s), false);
      } else if (roll < chance + 0.14 && (use === Use.YARD || use === Use.GREEN)) {
        const f = flowers[Math.floor(rng.next() * flowers.length)];
        for (let k = 0; k < 3; k++) {
          const s = 1.2;
          instancer.add(f, placement(f, { x: at.x + rng.range(-0.25, 0.25), y: at.y + rng.range(-0.25, 0.25) }, rng.range(0, 6.3), s, s, s), false);
        }
      }
    }
  }
}

/** Planters and potted trees round the edge of the plaza. */
function addPlazaDecor(world: TownWorld, assets: TownAssets, instancer: Instancer, rng: Rng): void {
  const { tiles, width: w, height: h } = world.model;
  const planter = assets.prefab('suburban/planter');
  const tree = assets.prefab('nature/tree_small');
  const well = world.model.landmarks.find((l) => l.kind === 'well');
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      if (tiles.overlay[index(x, y, w)] !== Overlay.PLAZA) continue;
      if (well && Math.floor(well.at.x) === x && Math.floor(well.at.y) === y) continue;
      const edges = DIRS4.filter(([dx, dy]) => !inBounds(x + dx, y + dy, w, h) || tiles.overlay[index(x + dx, y + dy, w)] !== Overlay.PLAZA);
      const roll = rng.next();
      if (!edges.length || roll > 0.3) continue;
      const [dx, dy] = edges[0];
      const at = { x: x + 0.5 + dx * 0.3, y: y + 0.5 + dy * 0.3 };
      if (roll < 0.2) {
        const s = 0.8 / Math.max(planter.size.x, planter.size.z);
        instancer.add(planter, placement(planter, at, Math.atan2(dx, dy), s, s, s));
      } else {
        const s = 1.4 / tree.size.y;
        instancer.add(tree, placement(tree, at, rng.range(0, 6.3), s, s, s));
      }
    }
  }
}

/** The scale that makes a car model CAR_LENGTH long. */
export function carScale(p: Prefab): number {
  return CAR_LENGTH / p.size.z;
}

/** Parked cars along the side-street kerbs, lined up with the road; returns each one's instance. */
function addParkedCars(world: TownWorld, assets: TownAssets, instancer: Instancer): { prefab: Prefab; index: number }[] {
  return world.layout.cars.map((car) => {
    // Parked either way round.
    const heading = car.angle + (car.variant % 2 ? Math.PI : 0);
    const model = car.variant % 6;
    const p = assets.prefab(CARS[model]);
    const s = carScale(p);
    world.parked.push({ model, x: car.x, y: car.y, heading });
    return { prefab: p, index: instancer.add(p, placement(p, car, Math.atan2(Math.cos(heading), Math.sin(heading)), s, s, s)) };
  });
}

/**
 * Lamps along the outer edge of the sidewalks, arms reaching over the road.
 * At night each bulb glows, with a soft halo and a pool of light below.
 */
function addLamps(world: TownWorld, rng: Rng, owned: { dispose(): void }[]): void {
  const { model, layout } = world;
  const placed: Vec2[] = [];
  const posts: Matrix4[] = [];
  layout.sidewalks.paths.forEach((path, n) => {
    const total = path.at[path.at.length - 1];
    // Stagger the two sides of a road so lamps alternate.
    for (let s = (n % 2) * (LAMP_SPACING / 2) + 1.5; s < total - 1; s += LAMP_SPACING) {
      let k = 1;
      while (k < path.at.length - 1 && path.at[k] < s) k++;
      const a = path.points[k - 1];
      const b = path.points[k];
      const f = (s - path.at[k - 1]) / Math.max(1e-6, path.at[k] - path.at[k - 1]);
      const on = { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f };
      if (placed.some((p) => dist(p, on) < 2.5)) continue;
      const tx = Math.floor(on.x);
      const ty = Math.floor(on.y);
      if (!inBounds(tx, ty, model.width, model.height) || isWaterTerrain(model.tiles.terrain[index(tx, ty, model.width)])) continue;
      if (layout.use[index(tx, ty, model.width)] === Use.HOUSE) continue;
      placed.push(on);
      // Outward = away from the road: the side the sidewalk was offset to.
      const tangent = { x: b.x - a.x, y: b.y - a.y };
      const tl = Math.hypot(tangent.x, tangent.y) || 1;
      const normal = { x: (tangent.y / tl) * path.side, y: (-tangent.x / tl) * path.side };
      const base = { x: on.x + normal.x * (SIDEWALK / 2 - 0.08), y: on.y + normal.y * (SIDEWALK / 2 - 0.08) };
      const rot = Math.atan2(-normal.x, -normal.y); // the arm (local +Z) reaches toward the road
      posts.push(new Matrix4().compose(new Vector3(base.x, 0, base.y), new Quaternion().setFromAxisAngle(Y_AXIS, rot), new Vector3(1, 1, 1)));
      world.lamps.push({ x: base.x - normal.x * LAMP_REACH, y: base.y - normal.y * LAMP_REACH, h: LAMP_HEIGHT - 0.05, phase: rng.range(0, 100), faulty: rng.chance(0.12) });
    }
  });

  const postGeo = lampGeometry();
  const postMat = new MeshLambertMaterial({ color: COLORS.lampPost });
  const postMesh = new InstancedMesh(postGeo, postMat, Math.max(1, posts.length));
  postMesh.count = posts.length;
  posts.forEach((m, i) => postMesh.setMatrixAt(i, m));
  postMesh.castShadow = true;
  postMesh.computeBoundingSphere();
  world.root.add(postMesh);

  const count = Math.max(1, world.lamps.length);
  const bulbGeo = new SphereGeometry(0.05, 10, 6);
  const bulbMat = new MeshBasicMaterial({ color: 0xffffff });
  const bulbs = new InstancedMesh(bulbGeo, bulbMat, count);
  const poolGeo = new PlaneGeometry(2.2, 2.2).rotateX(-Math.PI / 2);
  const poolMat = new MeshBasicMaterial({ map: glowTexture(), transparent: true, depthWrite: false, blending: AdditiveBlending });
  const pools = new InstancedMesh(poolGeo, poolMat, count);
  const haloGeo = new BufferGeometry();
  const haloPos = new Float32Array(count * 3);
  world.lamps.forEach((l, i) => {
    bulbs.setMatrixAt(i, new Matrix4().makeTranslation(l.x, l.h, l.y));
    pools.setMatrixAt(i, new Matrix4().makeTranslation(l.x, 0.04, l.y));
    haloPos.set([l.x, l.h, l.y], i * 3);
  });
  bulbs.count = pools.count = world.lamps.length;
  const off = new Color(0x8a8a80);
  for (let i = 0; i < world.lamps.length; i++) {
    bulbs.setColorAt(i, off);
    pools.setColorAt(i, new Color(0, 0, 0));
  }
  haloGeo.setAttribute('position', new BufferAttribute(haloPos, 3));
  const haloCol = new BufferAttribute(new Float32Array(count * 3), 3);
  haloCol.setUsage(DynamicDrawUsage);
  haloGeo.setAttribute('color', haloCol);
  haloGeo.setDrawRange(0, world.lamps.length);
  const haloMat = new PointsMaterial({ size: 40, map: glowTexture(), vertexColors: true, transparent: true, depthWrite: false, blending: AdditiveBlending, sizeAttenuation: true }); // attenuation only applies through the perspective camera
  const halos = new Points(haloGeo, haloMat);
  pools.renderOrder = 6;
  halos.renderOrder = 7;
  for (const m of [bulbs, pools]) {
    m.computeBoundingSphere();
    m.frustumCulled = false;
  }
  halos.frustumCulled = false;
  world.root.add(bulbs, pools, halos);
  world.lampFx = { bulbs, halos, pools };
  owned.push(postGeo, postMat, bulbGeo, bulbMat, poolGeo, poolMat, haloGeo, haloMat);
}

const LAMP_HEIGHT = 0.95;
const LAMP_REACH = 0.3;

/** A street lamp: a post, an arm reaching out along +Z, and a lamp head. */
function lampGeometry(): BufferGeometry {
  const post = new CylinderGeometry(0.022, 0.03, LAMP_HEIGHT, 6).translate(0, LAMP_HEIGHT / 2, 0);
  const arm = new BoxGeometry(0.03, 0.03, LAMP_REACH).translate(0, LAMP_HEIGHT - 0.01, LAMP_REACH / 2);
  const head = new BoxGeometry(0.09, 0.04, 0.12).translate(0, LAMP_HEIGHT - 0.02, LAMP_REACH);
  const merged = mergeGeometries([post.toNonIndexed(), arm.toNonIndexed(), head.toNonIndexed()])!;
  for (const g of [post, arm, head]) g.dispose();
  return merged;
}

/** Pale streaks drifting down each river, so the water visibly runs. */
function addStreaks(world: TownWorld, owned: { dispose(): void }[]): void {
  const { model } = world;
  const wet = (p: Vec2) => isWaterTerrain(model.tiles.terrain[Math.floor(p.y) * model.width + Math.floor(p.x)]);
  const items: Streak[] = [];
  for (const river of model.rivers) {
    const path = river.points.filter(wet);
    if (path.length < 2) continue;
    const at: number[] = [0];
    for (let k = 1; k < path.length; k++) at.push(at[k - 1] + Math.hypot(path[k].x - path[k - 1].x, path[k].y - path[k - 1].y));
    const total = at[at.length - 1];
    const count = Math.floor(total / 1.6);
    for (let k = 0; k < count; k++) items.push({ path, at, s: (k / count) * total + (k % 3) * 0.37, speed: 1.1 + (k % 5) * 0.15 });
  }
  const geo = new PlaneGeometry(0.55, 0.05).rotateX(-Math.PI / 2);
  const mat = new MeshBasicMaterial({ color: 0xffffff, transparent: true, depthWrite: false, blending: AdditiveBlending });
  const mesh = new InstancedMesh(geo, mat, Math.max(1, items.length));
  mesh.count = items.length;
  mesh.instanceMatrix.setUsage(DynamicDrawUsage);
  for (let i = 0; i < items.length; i++) mesh.setColorAt(i, new Color(0, 0, 0));
  mesh.frustumCulled = false;
  mesh.renderOrder = 5;
  world.root.add(mesh);
  world.streaks = { mesh, items };
  owned.push(geo, mat);
}

/** The plaza fountain on the well tile: a stone basin, a column and a bowl, with spray. */
function addFountain(world: TownWorld, owned: { dispose(): void }[]): void {
  const well = world.model.landmarks.find((l) => l.kind === 'well');
  if (!well) return;
  const at = { x: Math.floor(well.at.x) + 0.5, y: Math.floor(well.at.y) + 0.5 };
  const stone = mergeGeometries([
    new CylinderGeometry(0.44, 0.47, 0.2, 20).translate(0, 0.1, 0),
    new CylinderGeometry(0.06, 0.08, 0.5, 8).translate(0, 0.25, 0),
    new CylinderGeometry(0.17, 0.1, 0.06, 12).translate(0, 0.5, 0),
  ])!.translate(at.x, 0, at.y);
  const water = new CylinderGeometry(0.39, 0.39, 0.02, 20).translate(at.x, 0.19, at.y);
  const stoneMat = new MeshLambertMaterial({ color: COLORS.stone });
  const waterMat = new MeshLambertMaterial({ color: 0x7fbde3, emissive: 0x1d3f5c });
  const basin = new Mesh(stone, stoneMat);
  basin.castShadow = basin.receiveShadow = true;
  world.root.add(basin, new Mesh(water, waterMat));

  const dropGeo = new SphereGeometry(0.025, 6, 4);
  const dropMat = new MeshBasicMaterial({ color: 0xd8f0ff, transparent: true, opacity: 0.85 });
  const items: Droplet[] = Array.from({ length: 24 }, (_, k) => ({ t: k / 24, vx: Math.cos(k * 2.4) * 0.3, vy: Math.sin(k * 2.4) * 0.3 }));
  const drops = new InstancedMesh(dropGeo, dropMat, items.length);
  drops.instanceMatrix.setUsage(DynamicDrawUsage);
  drops.frustumCulled = false;
  world.root.add(drops);
  world.fountain = { at, drops, items };
  owned.push(stone, water, stoneMat, waterMat, dropGeo, dropMat);
}
