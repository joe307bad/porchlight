// The explorable isometric town. Mount it on an element, give it a TownModel,
// spawn a character, and walk the streets with WASD or the arrow keys (or let
// auto-walk wander the sidewalks) while townsfolk stroll and traffic drives the
// main roads. Day and night blend smoothly; at night the street lamps (a few
// of them faulty) light up.

import { Application, Container, CullerPlugin, Graphics, Matrix, Sprite, extensions } from 'pixi.js';
import { index } from '../../core/grid';
import { createRng, type Rng } from '../../core/rng';
import type { TownModel } from '../../model/types';
import { Pedestrian, Player } from './actors';
import { loadIsoAssets, type IsoAssets } from './assets';
import { randomLook, type Look } from './characters';
import { HALF_H, HALF_W, toScreen, toWorld } from './coords';
import { Minimap } from './minimap';
import { streetMask } from './streets';
import { Use } from './layout';
import { Traffic } from './traffic';
import { buildWorld, WATER_DROP, type IsoWorld } from './world';

extensions.add(CullerPlugin);

const NIGHT_TINT = { r: 0.36, g: 0.42, b: 0.66 };
const MIN_ZOOM = 0.35;
const MAX_ZOOM = 2.5;
const MAX_PEDESTRIANS = 70;

const KEYS: Record<string, [number, number]> = {
  KeyW: [0, -1], ArrowUp: [0, -1],
  KeyS: [0, 1], ArrowDown: [0, 1],
  KeyA: [-1, 0], ArrowLeft: [-1, 0],
  KeyD: [1, 0], ArrowRight: [1, 0],
};

export class IsoView {
  private world: IsoWorld | null = null;
  private player: Player | null = null;
  private people: Pedestrian[] = [];
  private traffic: Traffic | null = null;
  private readonly camera = new Container();
  private zoom = 1;
  private night = false;
  /** Tiles the player may walk on: everything but buildings and sheds, or only the streets. */
  private open: Uint8Array = new Uint8Array(0);
  private streetsOnly = false;
  /** An orange outline of the player, shown when a building hides them. */
  private readonly xray = new Container();
  /** Auto-walk's planned route, drawn on the ground in world units. */
  private readonly routeLine = new Graphics();
  private routeTimer = 0;
  /** While the blueprint is showing, keep simulating but skip drawing. */
  private background = false;
  /** 0 = full day, 1 = full night; eases toward the target. */
  private dusk = 0;
  private time = 0;
  /** Tint last applied to the objects layer (-1 forces a refresh, e.g. after new sprites arrive). */
  private objectTint = -1;
  /** Jump the camera straight to its target on the next frame (new town or character). */
  private snap = true;
  private readonly held = new Set<string>();
  private readonly cleanup: (() => void)[] = [];
  private readonly minimap: Minimap;

  private constructor(
    private readonly app: Application,
    private readonly assets: IsoAssets,
    private readonly host: HTMLElement,
  ) {
    app.stage.addChild(this.camera);
    this.minimap = new Minimap(host, (at) => this.teleport(at));
    // Step the town in small slices, so slow frames (e.g. while it runs hidden
    // behind the map) don't slow time down; skip anything over half a second.
    app.ticker.add((t) => {
      let left = Math.min(0.5, t.elapsedMS / 1000); // raw time: deltaMS is clamped by the ticker
      while (left > 1e-6) {
        const dt = Math.min(0.05, left);
        this.tick(dt);
        left -= dt;
      }
    });
    this.listen();
  }

  static async create(host: HTMLElement): Promise<IsoView> {
    const app = new Application();
    await app.init({
      resizeTo: host,
      background: '#87b7d8',
      antialias: true,
      autoDensity: true,
      resolution: Math.min(2, window.devicePixelRatio || 1),
    });
    host.appendChild(app.canvas);
    const assets = await loadIsoAssets();
    return new IsoView(app, assets, host);
  }

  /** Build the scene for a town (replacing any previous one) and fill the sidewalks with people. */
  setTown(model: TownModel): void {
    // The outline and route line outlive any one town: take them out before the old scene goes.
    this.xray.removeFromParent();
    this.routeLine.removeFromParent();
    this.routeLine.clear();
    this.world?.root.destroy({ children: true });
    this.people = [];
    this.player = null; // a fresh character (not auto-walking) is spawned into the new town
    const world = buildWorld(model, this.assets);
    this.world = world;
    this.snap = true;
    this.objectTint = -1;
    this.open = this.walkMask();
    world.lights.addChild(this.xray);
    // The route sits on the ground: above the roads, below anything standing.
    const routeLayer = new Container();
    routeLayer.setFromMatrix(new Matrix(HALF_W, HALF_H, -HALF_W, HALF_H, 0, 0));
    routeLayer.addChild(this.routeLine);
    world.root.addChildAt(routeLayer, world.root.getChildIndex(world.roads) + 1);
    this.camera.addChild(world.root);
    this.minimap.setTown(model);

    this.traffic = new Traffic(world.layout, (s) => world.objects.addChild(s), createRng(`${model.seed}::traffic`));
    const rng = createRng(`${model.seed}::townsfolk`);
    const paths = world.sidewalks.paths;
    const walkable = paths.reduce((sum, p) => sum + p.at[p.at.length - 1], 0);
    const count = paths.length ? Math.min(MAX_PEDESTRIANS, Math.floor(walkable / 12)) : 0;
    for (let k = 0; k < count; k++) {
      const p = new Pedestrian(randomLook(rng), world.sidewalks, model, rng);
      world.objects.addChild(p.sprite);
      this.people.push(p);
    }
  }

  /** Put a new character with the given look at the town's spawn point (or swap the look in place). */
  spawn(look: Look): void {
    const world = this.world;
    if (!world) return;
    const keep = this.player;
    const at = keep ? { x: keep.x, y: keep.y } : world.model.spawn;
    const wasAuto = keep?.isAuto ?? false;
    if (keep) keep.sprite.destroy();
    const z = world.model.tiles.elevation[index(Math.floor(at.x), Math.floor(at.y), world.model.width)];
    this.player = new Player(look, at, z, 1.1);
    world.objects.addChild(this.player.sprite);
    this.objectTint = -1;
    if (wasAuto) this.player.startAuto(world.sidewalks, this.autoRng);
    if (!keep) this.snap = true;
  }

  /** Move the player straight to a map position (from a minimap click). */
  private teleport(at: { x: number; y: number }): void {
    if (!this.player || !this.world) return;
    const { width: w, height: h } = this.world.model;
    let x = Math.min(w - 0.2, Math.max(0.2, at.x));
    let y = Math.min(h - 0.2, Math.max(0.2, at.y));
    // Never land inside a building: step out to the nearest open tile.
    if (this.open.length && !this.open[index(Math.floor(x), Math.floor(y), w)]) {
      let bestD = Infinity;
      for (let i = 0; i < this.open.length; i++) {
        if (!this.open[i]) continue;
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
    this.player.place();
    this.snap = true;
  }

  /** Auto-walk's planned route (world tiles), empty when not auto-walking. */
  get route(): { x: number; y: number }[] {
    return this.player?.autoRoute() ?? [];
  }

  /** Where auto-walk is heading, if anywhere. */
  get goal(): { x: number; y: number } | null {
    return this.player?.autoGoal() ?? null;
  }

  /**
   * Run hidden behind the blueprint: the town keeps going (auto-walk, people,
   * traffic) but nothing is drawn, so the map can follow along cheaply.
   */
  setBackground(on: boolean): void {
    this.background = on;
    this.app.stage.renderable = !on;
  }

  /** Where the player is standing (world tiles), or null before anyone has spawned. */
  get playerPosition(): { x: number; y: number } | null {
    return this.player ? { x: this.player.x, y: this.player.y } : null;
  }

  private readonly autoRng = createRng('auto-walk');

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
      const { model } = this.world;
      const here = index(Math.floor(this.player.x), Math.floor(this.player.y), model.width);
      if (!this.open[here]) {
        let best = -1;
        let bestD = Infinity;
        for (let i = 0; i < this.open.length; i++) {
          if (!this.open[i]) continue;
          const d = Math.hypot((i % model.width) + 0.5 - this.player.x, Math.floor(i / model.width) + 0.5 - this.player.y);
          if (d < bestD) {
            bestD = d;
            best = i;
          }
        }
        if (best >= 0) this.teleport({ x: (best % model.width) + 0.5, y: Math.floor(best / model.width) + 0.5 });
      }
    }
  }

  /** Walkable tiles: streets only, or anywhere except buildings and sheds. */
  private walkMask(): Uint8Array {
    const world = this.world;
    if (!world) return new Uint8Array(0);
    if (this.streetsOnly) return streetMask(world.model.tiles);
    const out = new Uint8Array(world.layout.use.length);
    for (let i = 0; i < out.length; i++) out[i] = world.layout.use[i] === Use.HOUSE || world.layout.use[i] === Use.SHED ? 0 : 1;
    return out;
  }

  get isStreetsOnly(): boolean {
    return this.streetsOnly;
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
      litLamps: world?.lamps.filter((l) => l.glow.alpha > 0.05).length ?? 0,
      houses: world ? world.objects.children.length : 0,
      sidewalkPaths: world?.sidewalks.paths.length ?? 0,
      auto: this.isAuto,
      goal: this.player?.autoGoal() ?? null,
      inBuilding: !!(world && tile >= 0 && world.layout.use[tile] === Use.HOUSE),
      outlined: this.xray.visible,
      cars: this.traffic?.positions() ?? [],
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
    this.app.destroy(true, { children: true });
  }

  private listen(): void {
    const isTyping = (e: KeyboardEvent) => e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;
    const down = (e: KeyboardEvent) => {
      if (isTyping(e) || !KEYS[e.code]) return;
      e.preventDefault();
      if (this.player?.isAuto) {
        // Auto-walking: a key press is a one-off request (turn round / cross here).
        if (!e.repeat) this.player.command({ x: KEYS[e.code][0], y: KEYS[e.code][1] });
        return;
      }
      this.held.add(e.code);
    };
    const up = (e: KeyboardEvent) => this.held.delete(e.code);
    const blur = () => this.held.clear();
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      this.zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, this.zoom * Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0015))));
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

  private tick(dt: number): void {
    const world = this.world;
    if (!world) return;
    this.time += dt;

    // Player.
    const input = { x: 0, y: 0 };
    for (const code of this.held) {
      input.x += KEYS[code][0];
      input.y += KEYS[code][1];
    }
    this.player?.update(input, dt, world.model, this.open);
    for (const p of this.people) p.update(dt);
    this.traffic?.update(dt);

    // Camera: follow the player (or frame the spawn point before one exists).
    const focus = this.player ? toScreen(this.player.x, this.player.y, this.player.z) : toScreen(world.model.spawn.x, world.model.spawn.y);
    const scale = this.snap ? this.zoom : this.camera.scale.x + (this.zoom - this.camera.scale.x) * Math.min(1, dt * 10);
    this.camera.scale.set(scale);
    const tx = this.app.screen.width / 2 - focus.x * scale;
    const ty = this.app.screen.height / 2 - (focus.y - 20) * scale;
    const follow = this.snap ? 1 : Math.min(1, dt * 8);
    this.snap = false;
    this.camera.x += (tx - this.camera.x) * follow;
    this.camera.y += (ty - this.camera.y) * follow;

    this.animateDayNight(world, dt);
    this.animateWater(world, dt);
    if (this.background) return;
    this.updateXray(world);
    this.routeTimer -= dt;
    if (this.routeTimer <= 0) {
      this.drawRoute();
      this.routeTimer = 0.2;
    }
    this.drawMinimap();
  }

  /** A dashed orange line along auto-walk's route, with a flag at the destination. */
  private drawRoute(): void {
    const g = this.routeLine;
    g.clear();
    const pts = this.route;
    const goal = this.goal;
    if (pts.length < 2 || !goal) return;
    // Dashes: walk the polyline, drawing every other 0.35 tiles.
    let on = true;
    let left = 0.35;
    for (let k = 1; k < pts.length; k++) {
      let a = pts[k - 1];
      const b = pts[k];
      let seg = Math.hypot(b.x - a.x, b.y - a.y);
      while (seg > 1e-6) {
        const step = Math.min(left, seg);
        const t = step / seg;
        const c = { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
        if (on) g.moveTo(a.x, a.y).lineTo(c.x, c.y);
        seg -= step;
        left -= step;
        a = c;
        if (left <= 1e-6) {
          on = !on;
          left = 0.35;
        }
      }
    }
    g.stroke({ width: 0.12, color: 0xff6a00, alpha: 0.9, cap: 'round' });
    g.circle(goal.x, goal.y, 0.45).fill({ color: 0xff6a00, alpha: 0.25 });
    g.circle(goal.x, goal.y, 0.18).fill({ color: 0xff6a00 });
  }

  /**
   * When a building in front covers the player, draw their silhouette on top
   * in fluorescent orange: an outline made of offset copies, with a faint fill.
   */
  private updateXray(world: IsoWorld): void {
    const p = this.player;
    const hidden = !!p && hiddenByBuilding(world, p.x, p.y, p.sprite);
    this.xray.visible = hidden;
    if (!hidden || !p) return;
    const parts = this.xray.children as Sprite[];
    if (!parts.length) {
      for (let k = 0; k < 9; k++) {
        const s = new Sprite();
        s.tint = 0xff6a00;
        this.xray.addChild(s);
      }
    }
    const offsets = [[-2, 0], [2, 0], [0, -2], [0, 2], [-1.5, -1.5], [1.5, -1.5], [-1.5, 1.5], [1.5, 1.5], [0, 0]];
    (this.xray.children as Sprite[]).forEach((s, k) => {
      s.texture = p.mask;
      s.anchor.copyFrom(p.sprite.anchor);
      s.scale.copyFrom(p.sprite.scale);
      s.position.set(p.sprite.x + offsets[k][0], p.sprite.y + offsets[k][1]);
      // Eight offset copies make a fluorescent orange outline; the last is a soft fill inside it.
      s.alpha = k === 8 ? 0.45 : 1;
      s.tint = k === 8 ? 0xffd9b0 : 0xff5f00;
    });
    this.xray.zIndex = 1e9;
  }

  /** What the camera sees, as a quad on the ground plane, plus the player's spot. */
  private drawMinimap(): void {
    const { width, height } = this.app.screen;
    const s = this.camera.scale.x;
    const corner = (x: number, y: number) => toWorld((x - this.camera.x) / s, (y - this.camera.y) / s);
    const view = [corner(0, 0), corner(width, 0), corner(width, height), corner(0, height)];
    this.minimap.draw(this.player ? { x: this.player.x, y: this.player.y } : null, view, this.goal, this.route);
  }

  private animateDayNight(world: IsoWorld, dt: number): void {
    this.dusk += ((this.night ? 1 : 0) - this.dusk) * Math.min(1, dt * 2.5);
    const d = this.dusk;
    const ch = (k: number) => Math.round(255 * (1 - d * (1 - k)));
    const tint = (ch(NIGHT_TINT.r) << 16) | (ch(NIGHT_TINT.g) << 8) | ch(NIGHT_TINT.b);
    world.ground.tint = tint;
    world.roads.tint = tint;
    // Objects are tinted one by one so the lamp glows among them stay warm.
    if (tint !== this.objectTint) {
      for (const child of world.objects.children) if (!world.glows.has(child as Sprite)) child.tint = tint;
      this.objectTint = tint;
    }
    world.waterFx.alpha = 0.55 * (1 - d * 0.5);
    this.app.renderer.background.color = lerpColor(0x87b7d8, 0x101828, d);

    const t = this.time;
    for (const lamp of world.lamps) {
      const flicker = lamp.faulty
        ? noise(t * 9 + lamp.phase) > 0.35
          ? 0.15 + 0.2 * noise(t * 30 + lamp.phase)
          : 1
        : 0.9 + 0.1 * Math.sin(t * 11 + lamp.phase) * Math.sin(t * 3.7 + lamp.phase * 2);
      lamp.glow.alpha = d * flicker;
      lamp.pool.alpha = d * 0.55 * flicker;
    }
  }

  private animateWater(world: IsoWorld, dt: number): void {
    const t = this.time;
    for (const wt of world.water) {
      const wave = 0.5 + 0.5 * Math.sin(t * 1.6 + wt.x * 0.55 + wt.y * 0.35);
      const base = wt.deep ? 0.82 : 0.94;
      const r = Math.round(255 * (base - 0.07 + 0.07 * wave) * (wt.deep ? 0.85 : 1));
      const g = Math.round(255 * (base - 0.04 + 0.04 * wave));
      wt.sprite.tint = (r << 16) | (g << 8) | 255;
    }
    for (const s of world.streaks) {
      const total = s.at[s.at.length - 1];
      s.s = (s.s + s.speed * dt) % total;
      let k = 1;
      while (k < s.at.length - 1 && s.at[k] < s.s) k++;
      const a = s.path[k - 1];
      const b = s.path[k];
      const f = (s.s - s.at[k - 1]) / Math.max(1e-6, s.at[k] - s.at[k - 1]);
      const p = toScreen(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f);
      s.sprite.position.set(p.x, p.y + WATER_DROP + 1);
      const dir = toScreen(b.x - a.x, b.y - a.y);
      s.sprite.rotation = Math.atan2(dir.y, dir.x);
      s.sprite.alpha = 0.25 + 0.35 * Math.sin((s.s / total) * Math.PI * 12 + s.speed) ** 2;
    }
    if (world.fountainAt) {
      const f = world.fountainAt;
      const z = world.model.tiles.elevation[index(Math.floor(f.x), Math.floor(f.y), world.model.width)];
      const o = toScreen(f.x, f.y, z);
      for (const d of world.droplets) {
        d.t = (d.t + dt * 0.9) % 1;
        const u = d.t;
        d.sprite.position.set(o.x + d.vx * u, o.y - 32 + d.vy * u - 60 * u * (1 - u) * 1.6 + 6 * u);
        d.sprite.alpha = 0.9 * (1 - u * 0.6);
      }
    }
  }
}

/**
 * True if a building drawn in front of the player overlaps their sprite on
 * screen. Buildings in front are those whose tiles sort later (larger x + y).
 */
function hiddenByBuilding(world: IsoWorld, x: number, y: number, sprite: Sprite): boolean {
  const { model, roofTops } = world;
  const b = sprite.getBounds();
  const tx = Math.floor(x);
  const ty = Math.floor(y);
  for (let dy = 0; dy <= 5; dy++) {
    for (let dx = 0; dx <= 5; dx++) {
      const bx = tx + dx;
      const by = ty + dy;
      if (bx >= model.width || by >= model.height || bx + by + 1 <= x + y) continue;
      const top = roofTops[by * model.width + bx];
      if (!top) continue;
      // The building's screen box, in world-root coordinates.
      const c = toScreen(bx + 0.5, by + 0.5);
      const left = c.x - 88;
      const right = c.x + 88;
      const upper = c.y - top;
      const lower = c.y + 32;
      const g = world.root.toGlobal({ x: left, y: upper });
      const h = world.root.toGlobal({ x: right, y: lower });
      if (b.x < h.x && b.x + b.width > g.x && b.y < h.y && b.y + b.height > g.y) {
        // Only counts if the building really rises over the player's own spot (not just its side).
        const feet = toScreen(x, y);
        if (feet.x > left + 10 && feet.x < right - 10 && feet.y > upper && feet.y < lower + 6) return true;
      }
    }
  }
  return false;
}

function lerpColor(a: number, b: number, t: number): number {
  const ch = (s: number) => Math.round(((a >> s) & 255) + ((((b >> s) & 255) - ((a >> s) & 255)) * t));
  return (ch(16) << 16) | (ch(8) << 8) | ch(0);
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
