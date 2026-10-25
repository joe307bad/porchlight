// The explorable 3D town, drawn with Three.js through an orthographic camera
// at an isometric-style pitch. Mount it on an element, give it a TownModel,
// spawn a character, and walk the streets with WASD or the arrow keys (or let
// auto-walk wander the sidewalks) while townsfolk stroll and traffic drives the
// main roads.
//
// The camera orbits the player between 12 fixed headings (Q / E), so you can
// look down avenues from different sides; F turns it to look the way you're
// walking, and Z / X tilt it down and up through fixed steps. It always
// follows the player; while auto-walking it turns with
// them to stay behind them. Whenever a building hides them, their silhouette
// shows through in orange. Day and night blend smoothly; at night the street lamps (a few of
// them faulty) light up.
//
// Every building piece has a furnished ground floor. Walk up to its door and
// it swings open (and shuts behind you); step inside and that building's
// shell hides, its room shows, and the camera holds a fixed view of the room
// until you walk back out.
//
// V (handled with the other mode keys in main.ts) switches to a close-up third-person camera: a perspective view just
// behind and above the player that turns with them. There, W / S walk
// forward and back, A / D turn, Q / E swing the camera round them in 30°
// steps (F puts it back behind), and scroll moves it nearer or further.

import {
  Color,
  DirectionalLight,
  Fog,
  HemisphereLight,
  PointLight,
  Mesh,
  MeshBasicMaterial,
  OrthographicCamera,
  PCFSoftShadowMap,
  PerspectiveCamera,
  Scene,
  SRGBColorSpace,
  Vector3,
  WebGLRenderer,
  Matrix4,
  Quaternion,
} from 'three';
import { index } from '../../core/grid';
import { createRng, type Rng } from '../../core/rng';
import type { TownModel, Vec2 } from '../../model/types';
import { Pedestrian, Player, Walker, standable } from './actors';
import { ENTER_MARGIN, LEAVE_MARGIN, WALL_HEIGHT, doorOf, frame as roomFrame, inRoom, roomCameraYaw, wallFacesCamera } from './interiors';
import type { Facing } from '../../model/types';
import { loadTownAssets, type TownAssets } from './assets';
import { Avatar, PERSON_HEIGHT, randomLook, type Look } from './characters';
import { Fleet, pickCarModel } from './fleet';
import { Use } from './layout';
import { MeshBuilder } from './mesh';
import { Minimap } from './minimap';
import {
  CAMERA_STOPS,
  DEFAULT_STOP,
  CHASE_TILTS,
  DEFAULT_CHASE_TILT,
  DEFAULT_ISO_TILT,
  ISO_TILTS,
  cameraOffset,
  chaseYaw,
  clampTilt,
  tiltPitch,
  nearestStop,
  rayBoxDistance,
  rayHitsBox,
  screenToWorld,
  shortestTurn,
  stopLookingAlong,
  stopBehind,
  stopYaw,
} from './orbit';
import { streetMask } from './streets';
import { Traffic } from './traffic';
import { WATER_Y, buildWorld, swingDoor, type TownWorld } from './world';
import { windowUniforms } from './windows';

const DAY_SKY = 0x87b7d8;
const NIGHT_SKY = 0x101828;
/** World units (tiles) the view spans top to bottom at zoom 1. */
const VIEW_HEIGHT = 20;
const MIN_ZOOM = 0.35;
const MAX_ZOOM = 2.5;
const MAX_PEDESTRIANS = 70;
/** How far the camera sits from what it looks at (beyond anything it could clip). */
const CAMERA_DISTANCE = 150;
/** The sun's direction (toward it): high, from the north-west, so shadows fall south-east. */
const SUN = new Vector3(-0.5, 1.15, -0.3).normalize();
/** Height on the player the camera tries to keep a clear line to (tiles). */
const SIGHT_HEIGHT = 0.26;
/** How quickly the camera's idea of the walking direction catches up (per second). */
const HEADING_SMOOTHING = 1.2;
/** After a manual turn while auto-walking, leave the camera where it was put for this long. */
const MANUAL_HOLD = 5;

/** Close-up camera: how far behind the player (tiles), how far it may zoom, and how it looks down. */
const CHASE_DISTANCE = 2.4;
const CHASE_MIN = 1.1;
const CHASE_MAX = 8;
const CHASE_FOV = 50;
/** Where on the player the close-up camera looks (tiles up), and how far ahead of them. */
const CHASE_LOOK_HEIGHT = 0.42;
const CHASE_LOOK_AHEAD = 0.6;
/** Turning speed for A / D in close-up (radians per second). */
const TURN_RATE = 2.6;
/** Close-up fog, so the view fades into the sky instead of ending at the map's edge. */
const FOG_NEAR = 22;
const FOG_FAR = 60;

export type CameraMode = 'iso' | 'close';

/** Doors start opening when the player is this close (tiles), and how fast they swing (per second). */
const DOOR_REACH = 0.85;
const DOOR_SPEED = 3;
/** The fixed room camera's pitch, and how much room to leave round the room in view. */
const ROOM_PITCH = (55 * Math.PI) / 180;
const ROOM_MARGIN = 1.35;
/** Roughly how steeply the chase looks down, for picking it back up after a room. */
const CHASE_TILT_GUESS = 0.3;
/** Zoomed out further than this (overview zoom), auto walk leaves the camera's heading alone. */
const AUTO_TURN_MIN_ZOOM = 0.7;

const KEYS: Record<string, [number, number]> = {
  KeyW: [0, -1], ArrowUp: [0, -1],
  KeyS: [0, 1], ArrowDown: [0, 1],
  KeyA: [-1, 0], ArrowLeft: [-1, 0],
  KeyD: [1, 0], ArrowRight: [1, 0],
};

export class IsoView {
  private world: TownWorld | null = null;
  private player: Player | null = null;
  private playerAvatar: Avatar | null = null;
  private people: Pedestrian[] = [];
  private avatars: Avatar[] = [];
  private traffic: Traffic | null = null;
  private fleet: Fleet | null = null;
  private readonly scene = new Scene();
  private readonly camera = new OrthographicCamera(-1, 1, 1, -1, 0.1, CAMERA_DISTANCE * 3);
  private readonly chase = new PerspectiveCamera(CHASE_FOV, 1, 0.05, 300);
  private readonly fog = new Fog(DAY_SKY, FOG_NEAR, FOG_FAR);
  private mode: CameraMode = 'iso';
  /** Close-up heading now, the swing Q / E add on top of following, and the distance behind. */
  private chaseYawNow = 0;
  private chaseSwing = 0;
  private chaseDistance = CHASE_DISTANCE;
  private shownChaseDistance = CHASE_DISTANCE;
  private readonly chaseLook = new Vector3();
  /** The room the player is standing in (index into world.rooms), or null outside. */
  private room: number | null = null;
  /** Shopkeepers, made the first time you walk into their room. */
  private keepers = new Map<number, { walker: Walker; avatar: Avatar }>();
  private readonly roomLight = new PointLight(0xffd9a0, 0, 6, 1.4);
  /** Quarter turns Q / E have taken the room camera round the room (0 = from the street). */
  private roomTurn = 0;
  /**
   * The camera heading the movement keys are read against. It only catches up
   * with the camera once no movement key is held, so a camera swing (into a
   * room, or a Q / E turn) never bends the way you're already walking.
   */
  private inputYaw = stopYaw(DEFAULT_STOP);
  /**
   * Set when you walk out of a room in close-up with a key held: keep reading
   * keys screen-relative (as in the room) until you let go, so the key you
   * walked out with doesn't turn into "walk backwards" into the building.
   */
  private screenKeys = false;
  /** Tilt stop per mode (Z / X), and the pitch being shown (eases toward the stop). */
  private isoTilt = DEFAULT_ISO_TILT;
  private chaseTilt = DEFAULT_CHASE_TILT;
  private pitch = tiltPitch(ISO_TILTS, DEFAULT_ISO_TILT);
  private readonly hemi = new HemisphereLight(0xe2efff, 0x7d8a5a, 1.2);
  private readonly sun = new DirectionalLight(0xfff1d8, 2.6);
  /** Camera heading now (eases toward `targetYaw`, unwrapped so turns go the short way). */
  private yaw = stopYaw(DEFAULT_STOP);
  private targetYaw = stopYaw(DEFAULT_STOP);
  private zoom = 1;
  private shownZoom = 1;
  private readonly focus = new Vector3();
  private night = false;
  /** Tiles the player may walk on: everything but buildings and sheds, or only the streets. */
  private open: Uint8Array = new Uint8Array(0);
  private streetsOnly = false;
  /** Whether a building stands between the player and the camera, and for how long it has. */
  private hidden = false;
  private sightTimer = 0;
  /** Auto-walk's smoothed walking direction, which the camera follows from behind. */
  private walkDir: Vec2 | null = null;
  private manualHold = 0;
  /** Auto-walk's planned route, drawn on the ground. */
  private routeMesh: Mesh | null = null;
  private readonly routeMaterial = new MeshBasicMaterial({ vertexColors: true });
  private routeTimer = 0;
  /** While the blueprint is showing, keep simulating but skip drawing. */
  private background = false;
  /** 0 = full day, 1 = full night; eases toward the target. */
  private dusk = 0;
  private time = 0;
  /** Brightness each lamp was last lit to (for debug state). */
  private lampLevels: number[] = [];
  /** Jump the camera straight to its target on the next frame (new town or character). */
  private snap = true;
  private readonly held = new Set<string>();
  private readonly cleanup: (() => void)[] = [];
  private readonly minimap: Minimap;
  private readonly autoRng = createRng('auto-walk');
  private last = performance.now();

  private constructor(
    private readonly renderer: WebGLRenderer,
    private readonly assets: TownAssets,
    private readonly host: HTMLElement,
  ) {
    this.scene.background = new Color(DAY_SKY);
    this.sun.castShadow = true;
    this.sun.shadow.mapSize.set(2048, 2048);
    this.sun.shadow.bias = -0.0004;
    this.sun.shadow.normalBias = 0.02;
    this.sun.shadow.camera.near = 1;
    this.sun.shadow.camera.far = 220;
    this.scene.add(this.hemi, this.sun, this.sun.target, this.roomLight);
    this.minimap = new Minimap(host, (at) => this.teleport(at));
    this.resize();
    const observer = new ResizeObserver(() => this.resize());
    observer.observe(host);
    this.cleanup.push(() => observer.disconnect());
    // Step the town in small slices, so slow frames (e.g. while it runs hidden
    // behind the map) don't slow time down; skip anything over half a second.
    renderer.setAnimationLoop((now: number) => {
      let left = Math.min(0.5, Math.max(0, (now - this.last) / 1000));
      this.last = now;
      while (left > 1e-6) {
        const dt = Math.min(0.05, left);
        this.tick(dt);
        left -= dt;
      }
      this.frame(Math.min(0.1, (now - this.lastFrame) / 1000));
      this.lastFrame = now;
    });
    this.listen();
  }

  private lastFrame = performance.now();

  static async create(host: HTMLElement): Promise<IsoView> {
    const renderer = new WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
    renderer.outputColorSpace = SRGBColorSpace;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = PCFSoftShadowMap;
    renderer.localClippingEnabled = true; // Kenney buildings are clipped to stand on the modelled ground floors
    renderer.domElement.style.display = 'block';
    host.appendChild(renderer.domElement);
    try {
      const assets = await loadTownAssets();
      return new IsoView(renderer, assets, host);
    } catch (err) {
      renderer.domElement.remove();
      renderer.dispose();
      throw err;
    }
  }

  /** Build the scene for a town (replacing any previous one) and fill the sidewalks with people. */
  setTown(model: TownModel): void {
    this.world?.dispose();
    this.fleet?.dispose();
    for (const a of this.avatars) a.dispose();
    this.playerAvatar?.dispose();
    for (const k of this.keepers.values()) k.avatar.dispose();
    this.keepers.clear();
    this.room = null;
    this.clearRoute();
    this.avatars = [];
    this.people = [];
    this.player = null; // a fresh character (not auto-walking) is spawned into the new town
    this.playerAvatar = null;
    const world = buildWorld(model, this.assets);
    this.world = world;
    this.scene.add(world.root);
    this.snap = true;
    this.open = this.walkMask();
    this.minimap.setTown(model);

    this.traffic = new Traffic(world.layout, createRng(`${model.seed}::traffic`), pickCarModel);
    this.fleet = new Fleet(this.traffic, this.assets, world.root);
    const rng = createRng(`${model.seed}::townsfolk`);
    const paths = world.sidewalks.paths;
    const walkable = paths.reduce((sum, p) => sum + p.at[p.at.length - 1], 0);
    const count = paths.length ? Math.min(MAX_PEDESTRIANS, Math.floor(walkable / 12)) : 0;
    for (let k = 0; k < count; k++) {
      const p = new Pedestrian(randomLook(rng), world.sidewalks, rng);
      const a = new Avatar(this.assets.character(p.look.model), PERSON_HEIGHT, rng.next());
      world.root.add(a.root);
      this.people.push(p);
      this.avatars.push(a);
    }
  }

  /** Put a new character with the given look at the town's spawn point (or swap the look in place). */
  spawn(look: Look): void {
    const world = this.world;
    if (!world) return;
    const keep = this.player;
    const at = keep ? { x: keep.x, y: keep.y } : world.model.spawn;
    const wasAuto = keep?.isAuto ?? false;
    this.playerAvatar?.dispose();
    this.player = new Player(look, at.x, at.y, 1.1);
    if (keep) this.player.heading = keep.heading;
    this.playerAvatar = new Avatar(this.assets.character(look.model), PERSON_HEIGHT, 0);
    this.playerAvatar.addXray();
    this.playerAvatar.root.rotation.y = this.player.heading;
    world.root.add(this.playerAvatar.root);
    if (wasAuto) this.player.startAuto(world.sidewalks, this.autoRng);
    if (!keep) this.snap = true;
  }

  /** Move the player straight to a map position (from a minimap click). */
  private teleport(at: Vec2): void {
    if (!this.player || !this.world) return;
    const { width: w, height: h } = this.world.model;
    let x = Math.min(w - 0.2, Math.max(0.2, at.x));
    let y = Math.min(h - 0.2, Math.max(0.2, at.y));
    // Never land in a wall or on furniture: step out to the nearest open tile.
    const model = this.world.model;
    const solids = this.world.solids;
    if (this.open.length && !standable(x, y, this.open, model, solids)) {
      let bestD = Infinity;
      for (let i = 0; i < this.open.length; i++) {
        if (!this.open[i] || !standable((i % w) + 0.5, Math.floor(i / w) + 0.5, this.open, model, solids)) continue;
        const d = Math.hypot((i % w) + 0.5 - x, Math.floor(i / w) + 0.5 - y);
        if (d < bestD) {
          bestD = d;
          [x, y] = [(i % w) + 0.5, Math.floor(i / w) + 0.5];
        }
      }
    }
    this.player.x = x;
    this.player.y = y;
    if (this.player.isAuto) this.player.startAuto(this.world.sidewalks, this.autoRng);
    this.snap = true;
  }

  /** Auto-walk's planned route (world tiles), empty when not auto-walking. */
  get route(): Vec2[] {
    return this.player?.autoRoute() ?? [];
  }

  /** Where auto-walk is heading, if anywhere. */
  get goal(): Vec2 | null {
    return this.player?.autoGoal() ?? null;
  }

  /**
   * Run hidden behind the blueprint: the town keeps going (auto-walk, people,
   * traffic) but nothing is drawn, so the map can follow along cheaply.
   */
  setBackground(on: boolean): void {
    this.background = on;
    this.held.clear();
  }

  /** Where the player is standing (world tiles), or null before anyone has spawned. */
  get playerPosition(): Vec2 | null {
    return this.player ? { x: this.player.x, y: this.player.y } : null;
  }

  /** Auto-walk: head to the nearest sidewalk and wander; direction keys then turn round or cross. */
  setAuto(on: boolean): void {
    if (!this.player || !this.world) return;
    this.held.clear();
    if (on) this.player.startAuto(this.world.sidewalks, this.autoRng);
    else this.player.stopAuto();
  }

  get isAuto(): boolean {
    return this.player?.isAuto ?? false;
  }

  /** Keep the player to the streets (true) or let them walk anywhere (false). */
  setStreetsOnly(on: boolean): void {
    this.streetsOnly = on;
    this.open = this.walkMask();
    // If they're standing somewhere they can't, put them on the nearest open spot.
    if (this.player && this.world) {
      const here = index(Math.floor(this.player.x), Math.floor(this.player.y), this.world.model.width);
      if (!this.open[here] || !standable(this.player.x, this.player.y, this.open, this.world.model, this.world.solids)) this.teleport({ x: this.player.x, y: this.player.y });
    }
  }

  /** Walkable tiles: streets only, or anywhere except sheds (building walls are solids). */
  private walkMask(): Uint8Array {
    const world = this.world;
    if (!world) return new Uint8Array(0);
    if (this.streetsOnly) return streetMask(world.model.tiles);
    const out = new Uint8Array(world.layout.use.length);
    // Buildings are open ground here: their walls (world.solids) keep you to the doors.
    for (let i = 0; i < out.length; i++) out[i] = world.layout.use[i] === Use.SHED ? 0 : 1;
    return out;
  }

  get isStreetsOnly(): boolean {
    return this.streetsOnly;
  }

  /** Turn the camera one stop round the player (+1 or -1); in close-up, swing it 30° round them. */
  rotate(step: number): void {
    if (this.room !== null) {
      this.roomTurn += step;
      return;
    }
    if (this.mode === 'close') {
      this.chaseSwing += (step * Math.PI) / 6;
      return;
    }
    this.setStop(this.cameraStop + step);
    this.manualHold = MANUAL_HOLD;
  }

  /** Tilt the camera one stop: +1 raises it toward looking straight down, -1 lowers it toward the horizon. */
  tilt(step: number): void {
    if (this.mode === 'close') this.chaseTilt = clampTilt(CHASE_TILTS, this.chaseTilt + step);
    else this.isoTilt = clampTilt(ISO_TILTS, this.isoTilt + step);
  }

  /** The pitch the current mode's tilt stop asks for. */
  private get targetPitch(): number {
    return this.mode === 'close' ? tiltPitch(CHASE_TILTS, this.chaseTilt) : tiltPitch(ISO_TILTS, this.isoTilt);
  }

  /** Switch between the iso overview and the close-up third-person camera. */
  setCameraMode(mode: CameraMode): void {
    if (mode === this.mode) return;
    this.mode = mode;
    this.held.clear();
    this.chaseSwing = 0;
    if (this.player) this.chaseYawNow = chaseYaw(this.player.heading);
    this.shownChaseDistance = this.chaseDistance;
    this.scene.fog = mode === 'close' ? this.fog : null;
    this.snap = true;
  }

  get cameraMode(): CameraMode {
    return this.mode;
  }

  /** Turn the camera to look the way the player is walking (down the street ahead). */
  lookAlongWalk(): void {
    const p = this.player;
    if (!p) return;
    if (this.room !== null) {
      this.roomTurn = 0;
      return;
    }
    if (this.mode === 'close') {
      this.chaseSwing = 0;
      return;
    }
    const dir = p.autoHeading() ?? { x: Math.sin(p.heading), y: Math.cos(p.heading) };
    if (Math.hypot(dir.x, dir.y) < 1e-6) return;
    this.setStop(stopLookingAlong(dir));
    this.manualHold = MANUAL_HOLD;
  }

  /** The camera stop being turned to (0..11). */
  get cameraStop(): number {
    return nearestStop(this.targetYaw);
  }

  private setStop(k: number): void {
    this.targetYaw += shortestTurn(this.targetYaw, stopYaw(k));
  }

  /** A read-only summary of what's on screen, for dev tooling and checks. */
  debugState() {
    const world = this.world;
    const p = this.player;
    const w = world?.model.width ?? 0;
    const tile = p && world ? index(Math.floor(p.x), Math.floor(p.y), w) : -1;
    const onStreet = !!(world && tile >= 0 && streetMask(world.model.tiles)[tile]);
    return {
      player: p ? { x: p.x, y: p.y, onStreet } : null,
      streetsOnly: this.streetsOnly,
      night: this.night,
      dusk: this.dusk,
      people: this.people.map((q) => ({ x: q.x, y: q.y })),
      lamps: world?.lamps.length ?? 0,
      litLamps: this.lampLevels.filter((l) => l > 0.05).length,
      houses: world?.boxes.length ?? 0,
      sidewalkPaths: world?.sidewalks.paths.length ?? 0,
      auto: this.isAuto,
      goal: this.player?.autoGoal() ?? null,
      inBuilding: !!(world && tile >= 0 && world.layout.use[tile] === Use.HOUSE),
      outlined: this.hidden,
      cameraMode: this.mode,
      room: this.room,
      roomTurn: this.roomTurn,
      roomStyle: this.room !== null && world ? world.rooms[this.room].style : null,
      openDoors: world?.doors.filter((d) => d.open > 0.5).length ?? 0,
      cameraStop: this.cameraStop,
      cameraPitch: (this.pitch * 180) / Math.PI,
      cameraStops: CAMERA_STOPS,
      cars: this.traffic?.positions() ?? [],
      drawCalls: this.renderer.info.render.calls,
      triangles: this.renderer.info.render.triangles,
    };
  }

  setNight(on: boolean): void {
    this.night = on;
  }

  get isNight(): boolean {
    return this.night;
  }

  destroy(): void {
    for (const off of this.cleanup) off();
    this.renderer.setAnimationLoop(null);
    this.world?.dispose();
    this.fleet?.dispose();
    this.clearRoute();
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }

  private resize(): void {
    const w = Math.max(1, this.host.clientWidth);
    const h = Math.max(1, this.host.clientHeight);
    this.renderer.setSize(w, h);
  }

  private listen(): void {
    const isTyping = (e: KeyboardEvent) => e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;
    const down = (e: KeyboardEvent) => {
      if (isTyping(e) || this.background) return;
      if (e.code === 'KeyQ' || e.code === 'KeyE') {
        if (!e.repeat) this.rotate(e.code === 'KeyQ' ? -1 : 1);
        return;
      }
      if (e.code === 'KeyZ' || e.code === 'KeyX' || e.code === 'PageUp' || e.code === 'PageDown') {
        if (!e.repeat) this.tilt(e.code === 'KeyX' || e.code === 'PageUp' ? 1 : -1);
        e.preventDefault();
        return;
      }
      if (e.code === 'KeyF') {
        if (!e.repeat) this.lookAlongWalk();
        return;
      }
      if (!KEYS[e.code]) return;
      e.preventDefault();
      if (this.player?.isAuto) {
        // Auto-walking: a key press is a one-off request (turn round / cross here).
        const d = screenToWorld({ x: KEYS[e.code][0], y: KEYS[e.code][1] }, this.inputYaw);
        if (!e.repeat && d) this.player.command(d);
        return;
      }
      this.held.add(e.code);
    };
    const up = (e: KeyboardEvent) => this.held.delete(e.code);
    const blur = () => this.held.clear();
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const k = Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0015));
      if (this.mode === 'close') this.chaseDistance = Math.min(CHASE_MAX, Math.max(CHASE_MIN, this.chaseDistance / k));
      else this.zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, this.zoom * k));
    };
    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('blur', blur);
    this.host.addEventListener('wheel', wheel, { passive: false });
    this.cleanup.push(
      () => window.removeEventListener('keydown', down),
      () => window.removeEventListener('keyup', up),
      () => window.removeEventListener('blur', blur),
      () => this.host.removeEventListener('wheel', wheel),
    );
  }

  /** Simulation: people, traffic and the camera's choice of heading. Runs even behind the map. */
  private tick(dt: number): void {
    const world = this.world;
    if (!world) return;
    this.time += dt;
    const input = { x: 0, y: 0 };
    for (const code of this.held) {
      input.x += KEYS[code][0];
      input.y += KEYS[code][1];
    }
    const p = this.player;
    if (!this.held.size) {
      this.inputYaw = this.viewYaw;
      this.screenKeys = false;
    }
    if (p && this.mode === 'close' && !p.isAuto && this.room === null && !this.screenKeys) {
      // Close-up: forward and back along the way they face, left and right turn.
      const heading = p.heading - input.x * TURN_RATE * dt;
      const ahead = { x: Math.sin(heading), y: Math.cos(heading) };
      p.update(input.y ? { x: -ahead.x * Math.sign(input.y), y: -ahead.y * Math.sign(input.y) } : null, dt, world.model, this.open, world.solids);
      p.heading = heading; // walking backwards keeps them facing forwards
    } else {
      p?.update(screenToWorld(input, this.inputYaw), dt, world.model, this.open, world.solids);
    }
    this.updateDoors(world, dt);
    this.updateRoom(world);
    for (const p of this.people) p.update(dt);
    this.traffic?.update(dt);
    this.dusk += ((this.night ? 1 : 0) - this.dusk) * Math.min(1, dt * 2.5);
    this.keepInSight(world, dt);
  }

  /** Doors swing open as the player comes near, and shut once they've gone. */
  private updateDoors(world: TownWorld, dt: number): void {
    const p = this.player;
    world.doors.forEach((d, i) => {
      const at = doorOf(world.rooms[i]).at;
      const near = !!p && Math.abs(p.x - at.x) < DOOR_REACH && Math.abs(p.y - at.y) < DOOR_REACH && Math.hypot(p.x - at.x, p.y - at.y) < DOOR_REACH;
      const target = near ? 1 : 0;
      if (d.open === target) return;
      const was = d.open;
      d.open = target > d.open ? Math.min(1, d.open + dt * DOOR_SPEED) : Math.max(0, d.open - dt * DOOR_SPEED);
      swingDoor(world, i);
      // An open door shows the room behind it (and whoever minds it).
      if (was === 0 && d.open > 0) this.showRoom(world, i, true);
      if (d.open === 0 && i !== this.room) this.showRoom(world, i, false);
    });
  }

  /** Notice the player stepping into or out of a room: swap the building's shell for its ground floor. */
  private updateRoom(world: TownWorld): void {
    const p = this.player;
    let now: number | null = null;
    if (p) {
      if (this.room !== null && inRoom(world.rooms[this.room], p, LEAVE_MARGIN)) now = this.room;
      else now = world.rooms.findIndex((r) => inRoom(r, p, ENTER_MARGIN));
      if (now === -1) now = null;
    }
    if (now === this.room) return;
    if (this.room !== null) {
      world.setShellVisible(this.room, true);
      // Walls back up to full height for the view through the door.
      for (const wall of world.interior(this.room).children) if (wall.userData.side) wall.scale.y = 1;
      if (world.doors[this.room].open === 0) this.showRoom(world, this.room, false);
    }
    const left = this.room;
    this.room = now;
    this.roomTurn = 0;
    // Back out on the street, keep looking from the side you came out of
    // (the nearest camera stop to the room view), so the keys that walked you
    // out keep walking you out instead of the camera swinging back round.
    if (left !== null && now === null && this.mode === 'iso') {
      this.setStop(nearestStop(this.yaw));
      this.manualHold = MANUAL_HOLD;
    }
    // In close-up: pick the chase up from where the room camera is, and ease round behind.
    if (left !== null && now === null && this.mode === 'close' && p) {
      if (this.held.size) this.screenKeys = true;
      this.chaseYawNow = Math.atan2(this.chase.position.x - p.x, this.chase.position.z - p.y);
      this.shownChaseDistance = Math.min(CHASE_MAX, Math.hypot(this.chase.position.x - p.x, this.chase.position.z - p.y) / Math.cos(CHASE_TILT_GUESS));
    }
    if (now !== null) {
      world.setShellVisible(now, false);
      this.showRoom(world, now, true);
    }
  }

  /** Show or hide a room's furnished ground floor and its keeper. */
  private showRoom(world: TownWorld, room: number, on: boolean): void {
    world.interior(room).visible = on;
    if (on) this.showKeeper(world, room);
    else {
      const k = this.keepers.get(room);
      if (k) k.avatar.root.visible = false;
    }
  }

  /** Someone behind the counter in shops (and at reception in offices). */
  private showKeeper(world: TownWorld, room: number): void {
    const spot = world.plans[room].keeper;
    if (!spot) return;
    let k = this.keepers.get(room);
    if (!k) {
      const rng = createRng(`${world.model.seed}::keeper::${room}`);
      const look = randomLook(rng);
      const walker = new Walker(look, spot.x, spot.y);
      walker.heading = spot.rot;
      const avatar = new Avatar(this.assets.character(look.model), PERSON_HEIGHT, rng.next());
      avatar.root.rotation.y = spot.rot;
      world.root.add(avatar.root);
      k = { walker, avatar };
      this.keepers.set(room, k);
    }
    k.avatar.root.visible = true;
  }

  /**
   * Track whether a building hides the player from the camera (the x-ray
   * shows them through it either way). While auto-walking, keep the camera
   * behind them, following their smoothed heading round the stops, so it looks
   * down the street they're walking. A manual turn holds for a few seconds.
   */
  private keepInSight(world: TownWorld, dt: number): void {
    const p = this.player;
    this.manualHold -= dt;
    this.sightTimer -= dt;
    if (!p) {
      this.hidden = false;
      return;
    }
    if (this.sightTimer <= 0) {
      this.sightTimer = 0.1;
      this.hidden = this.room === null && this.blocked(world, p, stopYaw(this.cameraStop));
    }
    if (!p.isAuto) {
      this.walkDir = null;
      return;
    }
    // Smooth the heading over about a second, so a quick road crossing or a
    // kink in the sidewalk doesn't swing the camera round.
    const h = p.autoHeading();
    if (h && Math.hypot(h.x, h.y) > 1e-6) {
      if (!this.walkDir) this.walkDir = { ...h };
      const k = Math.min(1, dt * HEADING_SMOOTHING);
      this.walkDir.x += (h.x - this.walkDir.x) * k;
      this.walkDir.y += (h.y - this.walkDir.y) * k;
    }
    // Inside, the room camera holds still; zoomed right out, there's no need to turn.
    if (!this.walkDir || this.manualHold > 0 || this.room !== null) return;
    if (this.mode === 'iso' && this.zoom < AUTO_TURN_MIN_ZOOM) return;
    const next = stopBehind(this.cameraStop, this.walkDir);
    if (next !== this.cameraStop) this.setStop(next);
  }

  /** True if a building stands between a spot and a camera at this heading. */
  private blocked(world: TownWorld, at: Vec2, yaw: number): boolean {
    const o = cameraOffset(yaw, this.pitch);
    return world.boxes.some((b) => rayHitsBox(at, SIGHT_HEIGHT, o, b));
  }

  /** Drawing: pose everything, move the camera, animate, render. Skipped behind the map. */
  private frame(dt: number): void {
    const world = this.world;
    if (!world || this.background) return;
    const p = this.player;
    if (p && this.playerAvatar) this.playerAvatar.update(p, dt);
    this.people.forEach((q, k) => this.avatars[k].update(q, dt));
    this.fleet?.update();

    for (const k of this.keepers.values()) if (k.avatar.root.visible) k.avatar.update(k.walker, dt);

    // Camera: ease round to its heading, follow the player, zoom. Inside a
    // room it eases to a fixed view instead: from the street side, looking
    // down into the room, framing all of it.
    const fixed = this.room !== null ? this.roomView(world, this.room) : null;
    const yawTarget = fixed?.yaw ?? this.targetYaw;
    const zoomTarget = fixed?.zoom ?? this.zoom;
    const pitchTarget = fixed?.pitch ?? this.targetPitch;
    this.yaw += shortestTurn(this.yaw, yawTarget) * (this.snap ? 1 : Math.min(1, dt * 5));
    this.shownZoom += (zoomTarget - this.shownZoom) * (this.snap ? 1 : Math.min(1, dt * (fixed ? 5 : 10)));
    const target = fixed?.focus ?? (p ? new Vector3(p.x, 0.3, p.y) : new Vector3(world.model.spawn.x, 0.3, world.model.spawn.y));
    this.focus.lerp(target, this.snap ? 1 : Math.min(1, dt * (fixed ? 5 : 8)));
    this.pitch += (pitchTarget - this.pitch) * (this.snap ? 1 : Math.min(1, dt * 6));
    if (this.mode === 'close' && p && !fixed) this.placeChase(world, p, dt);
    if (this.mode === 'close' && fixed) this.placeChaseInRoom(fixed, dt);
    if (this.room !== null) this.cutWalls(world, this.room, dt);
    // A warm light in the room you're in (brighter at night).
    if (fixed) {
      this.roomLight.position.set(fixed.focus.x, WALL_HEIGHT * 0.9, fixed.focus.z);
      this.roomLight.intensity = 0.8 + this.dusk * 2.6;
    } else {
      this.roomLight.intensity = 0;
    }
    this.snap = false;
    this.placeCamera();
    this.placeSun();

    this.animateDayNight(world);
    this.animateWater(world, dt);
    this.routeTimer -= dt;
    if (this.routeTimer <= 0) {
      this.drawRoute(world);
      this.routeTimer = 0.2;
    }
    this.minimap.draw(p ? { x: p.x, y: p.y } : null, this.viewQuad(), this.goal, this.route);
    this.renderer.render(this.scene, this.view);
  }

  /** The camera being drawn through. */
  private get view(): OrthographicCamera | PerspectiveCamera {
    return this.mode === 'close' ? this.chase : this.camera;
  }

  /** In close-up, the room camera is the chase camera easing to a fixed spot framing the room. */
  private placeChaseInRoom(fixed: { yaw: number; pitch: number; half: number; focus: Vector3 }, dt: number): void {
    const o = cameraOffset(fixed.yaw, fixed.pitch);
    const d = (fixed.half / Math.tan(((CHASE_FOV / 2) * Math.PI) / 180)) * 1.05;
    const at = new Vector3(fixed.focus.x + o.x * d, fixed.focus.y + o.up * d, fixed.focus.z + o.y * d);
    const k = this.snap ? 1 : Math.min(1, dt * 4);
    this.chase.position.lerp(at, k);
    this.chaseLook.lerp(fixed.focus, k);
    const { clientWidth: w, clientHeight: h } = this.host;
    this.chase.aspect = Math.max(1, w) / Math.max(1, h);
    this.chase.updateProjectionMatrix();
    this.chase.lookAt(this.chaseLook);
  }

  /** Cut down the room's walls on the camera's side (eased), so you see in over them. */
  private cutWalls(world: TownWorld, room: number, dt: number): void {
    const cam = this.view.position;
    const r = world.rooms[room];
    const dir = { x: cam.x - (r.x0 + r.x1) / 2, y: cam.z - (r.y0 + r.y1) / 2 };
    const low = 0.12 / WALL_HEIGHT;
    for (const wall of world.interior(room).children) {
      const side = wall.userData.side as Facing | undefined;
      if (!side) continue;
      const want = wallFacesCamera(side, dir) ? low : 1;
      wall.scale.y += (want - wall.scale.y) * (this.snap ? 1 : Math.min(1, dt * 8));
    }
  }

  /** The fixed camera for a room: from its street side, pitched down, zoomed to fit it all. */
  private roomView(world: TownWorld, id: number): { yaw: number; pitch: number; zoom: number; half: number; focus: Vector3 } {
    const room = world.rooms[id];
    const fr = roomFrame(room);
    const { clientWidth: w, clientHeight: h } = this.host;
    const aspect = Math.max(1, w) / Math.max(1, h);
    // Turned a quarter round, the room's depth runs across the screen instead.
    const quarter = Math.abs(this.roomTurn) % 2 === 1;
    const across = quarter ? fr.depth : fr.width;
    const deep = quarter ? fr.width : fr.depth;
    const tall = deep * Math.sin(ROOM_PITCH) + WALL_HEIGHT * Math.cos(ROOM_PITCH);
    const half = Math.max(1.6, (tall / 2) * ROOM_MARGIN, ((across / 2) * ROOM_MARGIN) / aspect);
    return {
      yaw: roomCameraYaw(room, this.roomTurn),
      pitch: ROOM_PITCH,
      zoom: VIEW_HEIGHT / 2 / half,
      half,
      focus: new Vector3((room.x0 + room.x1) / 2, 0.2, (room.y0 + room.y1) / 2),
    };
  }

  /** The heading of the camera being drawn through (for screen-relative keys). */
  private get viewYaw(): number {
    return this.mode === 'close' && this.room === null ? this.chaseYawNow : this.yaw;
  }

  /** The room camera's turn, for tests and tooling. */
  get roomCameraTurn(): number {
    return this.roomTurn;
  }

  /**
   * The close-up camera: behind the player (plus any Q / E swing), easing
   * round as they turn, looking at them from a little above and just ahead.
   * If a building is in the way it comes in closer rather than sit inside it.
   */
  private placeChase(world: TownWorld, p: Player, dt: number): void {
    const want = chaseYaw(p.heading) + this.chaseSwing;
    this.chaseYawNow += shortestTurn(this.chaseYawNow, want) * (this.snap ? 1 : Math.min(1, dt * 3.5));
    this.shownChaseDistance += (this.chaseDistance - this.shownChaseDistance) * (this.snap ? 1 : Math.min(1, dt * 8));
    const o = cameraOffset(this.chaseYawNow, this.pitch);
    let dist = this.shownChaseDistance;
    for (const b of world.boxes) dist = Math.min(dist, Math.max(0.5, rayBoxDistance(p, CHASE_LOOK_HEIGHT, o, b) - 0.2));
    const ahead = { x: -o.x / Math.hypot(o.x, o.y), y: -o.y / Math.hypot(o.x, o.y) };
    const look = new Vector3(p.x + ahead.x * CHASE_LOOK_AHEAD, CHASE_LOOK_HEIGHT, p.y + ahead.y * CHASE_LOOK_AHEAD);
    if (this.snap) this.chaseLook.copy(look);
    else this.chaseLook.lerp(look, Math.min(1, dt * 10));
    const { clientWidth: w, clientHeight: h } = this.host;
    this.chase.aspect = Math.max(1, w) / Math.max(1, h);
    this.chase.updateProjectionMatrix();
    this.chase.position.set(p.x + o.x * dist, CHASE_LOOK_HEIGHT + o.up * dist, p.y + o.y * dist);
    this.chase.lookAt(this.chaseLook);
  }

  private placeCamera(): void {
    const { clientWidth: w, clientHeight: h } = this.host;
    const half = VIEW_HEIGHT / 2 / this.shownZoom;
    const aspect = Math.max(1, w) / Math.max(1, h);
    this.camera.left = -half * aspect;
    this.camera.right = half * aspect;
    this.camera.top = half;
    this.camera.bottom = -half;
    this.camera.updateProjectionMatrix();
    const o = cameraOffset(this.yaw, this.pitch);
    this.camera.position.set(this.focus.x + o.x * CAMERA_DISTANCE, this.focus.y + o.up * CAMERA_DISTANCE, this.focus.z + o.y * CAMERA_DISTANCE);
    this.camera.lookAt(this.focus);
  }

  /** Keep the sun's shadow map centred on what's in view, big enough to cover it. */
  private placeSun(): void {
    const half = this.mode === 'close' ? 30 : Math.min(90, Math.max(20, (VIEW_HEIGHT / this.shownZoom) * 1.25));
    const cam = this.sun.shadow.camera;
    if (cam.right !== half) {
      cam.left = -half;
      cam.right = half;
      cam.top = half;
      cam.bottom = -half;
      cam.updateProjectionMatrix();
    }
    // Snap to shadow texels so edges don't shimmer as the camera glides.
    const texel = (half * 2) / this.sun.shadow.mapSize.x;
    const fx = Math.round(this.focus.x / texel) * texel;
    const fz = Math.round(this.focus.z / texel) * texel;
    this.sun.target.position.set(fx, 0, fz);
    this.sun.position.set(fx + SUN.x * 100, SUN.y * 100, fz + SUN.z * 100);
  }

  /** What the camera sees, as a quad on the ground (for the minimap); rays above the horizon stop at the fog. */
  private viewQuad(): Vec2[] {
    const cam = this.view;
    return [
      [-1, 1],
      [1, 1],
      [1, -1],
      [-1, -1],
    ].map(([nx, ny]) => {
      const near = new Vector3(nx, ny, -1).unproject(cam);
      const dir = new Vector3(nx, ny, 1).unproject(cam).sub(near).normalize();
      let t = dir.y < -1e-4 ? -near.y / dir.y : Infinity;
      if (this.mode === 'close') {
        const flat = Math.hypot(dir.x, dir.z) || 1;
        t = Math.min(t, FOG_FAR / flat);
      }
      return { x: near.x + dir.x * t, y: near.z + dir.z * t };
    });
  }

  private clearRoute(): void {
    if (!this.routeMesh) return;
    this.routeMesh.removeFromParent();
    this.routeMesh.geometry.dispose();
    this.routeMesh = null;
  }

  /** A dashed orange line along auto-walk's route, with a marker at the destination. */
  private drawRoute(world: TownWorld): void {
    this.clearRoute();
    const pts = this.route;
    const goal = this.goal;
    if (pts.length < 2 || !goal) return;
    const b = new MeshBuilder();
    const orange = 0xff6a00;
    // Dashes: walk the polyline, drawing every other 0.35 tiles.
    let on = true;
    let left = 0.35;
    let dash: Vec2[] = [pts[0]];
    for (let k = 1; k < pts.length; k++) {
      let a = pts[k - 1];
      const end = pts[k];
      let seg = Math.hypot(end.x - a.x, end.y - a.y);
      while (seg > 1e-6) {
        const step = Math.min(left, seg);
        const t = step / seg;
        const c = { x: a.x + (end.x - a.x) * t, y: a.y + (end.y - a.y) * t };
        if (on) dash.push(c);
        seg -= step;
        left -= step;
        a = c;
        if (left <= 1e-6) {
          if (on && dash.length > 1) b.ribbon(dash, 0.12, 0.034, orange);
          on = !on;
          left = 0.35;
          dash = [c];
        }
      }
    }
    if (on && dash.length > 1) b.ribbon(dash, 0.12, 0.034, orange);
    b.disc(goal, 0.45, 0.032, 0xffc89a);
    b.disc(goal, 0.18, 0.036, orange);
    // A little flag on a pole.
    b.box(goal.x - 0.02, goal.y - 0.02, goal.x + 0.02, goal.y + 0.02, 0, 0.9, 0xfff8e8);
    b.quad(
      new Vector3(goal.x, 0.9, goal.y),
      new Vector3(goal.x + 0.35, 0.8, goal.y),
      new Vector3(goal.x + 0.35, 0.8, goal.y),
      new Vector3(goal.x, 0.68, goal.y),
      orange,
      new Vector3(0, 0, 1),
    );
    const mesh = new Mesh(b.build(), this.routeMaterial);
    mesh.renderOrder = 4;
    world.root.add(mesh);
    this.routeMesh = mesh;
  }

  private animateDayNight(world: TownWorld): void {
    const d = this.dusk;
    (this.scene.background as Color).setHex(DAY_SKY).lerp(new Color(NIGHT_SKY), d);
    this.fog.color.copy(this.scene.background as Color);
    this.hemi.intensity = 1.2 - d * 0.95;
    this.hemi.color.setHex(0xe2efff).lerp(new Color(0x6f7fb0), d);
    this.hemi.groundColor.setHex(0x7d8a5a).lerp(new Color(0x232a3a), d);
    this.sun.intensity = 2.6 - d * 2.25;
    this.sun.color.setHex(0xfff1d8).lerp(new Color(0x9fb0e0), d);
    world.water.uniforms.night.value = d;
    windowUniforms.uNight.value = d;
    windowUniforms.uTime.value = this.time;

    const { bulbs, halos, pools } = world.lampFx;
    const t = this.time;
    const off = new Color(0x8a8a80);
    const warm = new Color(0xffd890);
    const c = new Color();
    const haloColors = halos.geometry.getAttribute('color');
    this.lampLevels.length = world.lamps.length;
    world.lamps.forEach((lamp, i) => {
      const flicker = lamp.faulty
        ? noise(t * 9 + lamp.phase) > 0.35
          ? 0.15 + 0.2 * noise(t * 30 + lamp.phase)
          : 1
        : 0.9 + 0.1 * Math.sin(t * 11 + lamp.phase) * Math.sin(t * 3.7 + lamp.phase * 2);
      const level = d * flicker;
      this.lampLevels[i] = level;
      bulbs.setColorAt(i, c.copy(off).lerp(warm, level));
      pools.setColorAt(i, c.setRGB(1, 0.78, 0.44).multiplyScalar(level * 0.55));
      haloColors.setXYZ(i, level, level * 0.85, level * 0.55);
    });
    if (bulbs.instanceColor) bulbs.instanceColor.needsUpdate = true;
    if (pools.instanceColor) pools.instanceColor.needsUpdate = true;
    haloColors.needsUpdate = true;
    // Keep halos about a tile across: in iso they're sized in screen pixels, so
    // follow the zoom; in perspective three scales them by height / 2 / depth.
    const pxPerTile = this.host.clientHeight / (VIEW_HEIGHT / this.shownZoom);
    halos.material.size = this.view === this.chase ? 0.9 / Math.tan(((CHASE_FOV / 2) * Math.PI) / 180) : pxPerTile * 0.9;
  }

  private animateWater(world: TownWorld, dt: number): void {
    const t = this.time;
    world.water.uniforms.time.value = t;
    const m = new Matrix4();
    const rot = new Quaternion();
    const pos = new Vector3();
    const one = new Vector3(1, 1, 1);
    const up = new Vector3(0, 1, 0);
    const c = new Color();
    const { mesh, items } = world.streaks;
    const fade = 0.55 * (1 - this.dusk * 0.5);
    items.forEach((s, i) => {
      const total = s.at[s.at.length - 1];
      s.s = (s.s + s.speed * dt) % total;
      let k = 1;
      while (k < s.at.length - 1 && s.at[k] < s.s) k++;
      const a = s.path[k - 1];
      const b = s.path[k];
      const f = (s.s - s.at[k - 1]) / Math.max(1e-6, s.at[k] - s.at[k - 1]);
      pos.set(a.x + (b.x - a.x) * f, WATER_Y + 0.01, a.y + (b.y - a.y) * f);
      rot.setFromAxisAngle(up, Math.atan2(-(b.y - a.y), b.x - a.x));
      mesh.setMatrixAt(i, m.compose(pos, rot, one));
      const alpha = (0.25 + 0.35 * Math.sin((s.s / total) * Math.PI * 12 + s.speed) ** 2) * fade;
      mesh.setColorAt(i, c.setRGB(alpha, alpha, alpha));
    });
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;

    const fountain = world.fountain;
    if (fountain) {
      const { at, drops } = fountain;
      fountain.items.forEach((d, i) => {
        d.t = (d.t + dt * 0.9) % 1;
        const u = d.t;
        pos.set(at.x + d.vx * u * 1.2, 0.52 + 0.9 * u - 1.25 * u * u, at.y + d.vy * u * 1.2);
        drops.setMatrixAt(i, m.compose(pos, rot.identity(), one));
      });
      drops.instanceMatrix.needsUpdate = true;
    }
  }
}

/** Smooth-ish deterministic noise in 0..1 for flicker. */
function noise(t: number): number {
  const i = Math.floor(t);
  const f = t - i;
  const h = (n: number) => {
    const s = Math.sin(n * 127.1) * 43758.5453;
    return s - Math.floor(s);
  };
  const u = f * f * (3 - 2 * f);
  return h(i) * (1 - u) + h(i + 1) * u;
}

/** A random look for the player, from a seeded stream of the caller's choosing. */
export function lookFrom(rng: Rng): Look {
  return randomLook(rng);
}
