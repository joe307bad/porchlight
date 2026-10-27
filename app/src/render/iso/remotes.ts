// Other players in the town, as the server sends them (net/multiplayer.ts).
// Each gets the same rigged character the townsfolk use, posed from a Walker
// whose position comes from their snapshots, drawn 100 ms in the past and
// interpolated (net/interp.ts), with their name floating over them (through
// buildings, so you can always find each other). At the wheel they're drawn as
// their car instead, and a passenger (riding in anyone's car, yours too) is
// out of sight inside it, their name stacked over the car with the driver's.
// A passenger's tag goes where their driver's car is drawn on this screen
// (your own car, or the driver as interpolated here), not where their own,
// later, position updates put them, so it never trails the car. Spawning is idempotent: sync() makes the set of remotes
// match the state, however often it's called.

import { CanvasTexture, Group, LinearFilter, Mesh, SRGBColorSpace, Sprite, SpriteMaterial, type Object3D } from 'three';
import { createRng } from '../../core/rng';
import { INTERP_DELAY, ServerClock, SnapshotBuffer, turnBetween } from '../../net/interp';
import { ANIM_DRIVE, ANIM_RIDE, yawFromByte } from '../../net/protocol';
import { Walker } from './actors';
import { CARS, CHARACTERS, type TownAssets } from './assets';
import { Avatar, PERSON_HEIGHT } from './characters';
import { carScale, placement } from './world';

/** What the room says about another player (see server/src/multiplayer/schema.ts). */
export interface RemoteState {
  /** Their session id. */
  key: string;
  name: string;
  x: number;
  y: number;
  z: number;
  rotY: number;
  anim: number;
  car: number;
  /** A passenger: the driver's session id; otherwise ''. */
  ride: string;
  appearance: string;
  away: boolean;
}

/** Another player at the wheel, where they are this frame. */
export interface RemoteDriver {
  key: string;
  name: string;
  x: number;
  y: number;
  heading: number;
  car: number;
  /** Passengers already in their car. */
  riders: number;
}

/** Same as the local player's. */
const PLAYER_SCALE = 1.05;
const CAR_LIFT = 0.035;
/** Name tag: its height over their feet, over a car, and the step between a car's tags, and its width (tiles). */
const TAG_HEIGHT = 0.62;
const CAR_TAG_HEIGHT = 0.45;
const TAG_STACK = 0.17;
const TAG_WIDTH = 0.9;
const TAG_ORDER = 30;

interface Remote {
  walker: Walker;
  avatar: Avatar;
  look: string;
  buffer: SnapshotBuffer;
  tag: Sprite;
  tagText: string;
  car: { model: number; group: Group } | null;
  state: RemoteState;
}

function nameTag(text: string): Sprite {
  const canvas = document.createElement('canvas');
  const ctx = canvas.getContext('2d')!;
  const font = '600 40px system-ui, sans-serif';
  ctx.font = font;
  const w = Math.ceil(ctx.measureText(text).width) + 36;
  canvas.width = w;
  canvas.height = 64;
  ctx.font = font;
  ctx.fillStyle = 'rgba(28, 27, 24, 0.82)';
  ctx.beginPath();
  ctx.roundRect(0, 6, w, 52, 26);
  ctx.fill();
  ctx.fillStyle = '#fff8e8';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, w / 2, 33);
  const texture = new CanvasTexture(canvas);
  texture.colorSpace = SRGBColorSpace;
  texture.minFilter = LinearFilter;
  const sprite = new Sprite(new SpriteMaterial({ map: texture, depthTest: false, depthWrite: false, transparent: true }));
  sprite.renderOrder = TAG_ORDER;
  const width = TAG_WIDTH * Math.min(1.6, w / 200);
  sprite.scale.set(width, (width * canvas.height) / canvas.width, 1);
  return sprite;
}

function disposeTag(tag: Sprite): void {
  tag.material.map?.dispose();
  tag.material.dispose();
  tag.removeFromParent();
}

export class RemotePlayers {
  private readonly remotes = new Map<string, Remote>();
  private readonly clock = new ServerClock();
  /** This client's session id in the room. */
  private me = '';
  private readonly root: Object3D;
  private readonly assets: TownAssets;

  constructor(root: Object3D, assets: TownAssets) {
    this.root = root;
    this.assets = assets;
  }

  /** A patch arrived: the room's time and everyone in this client's view (but you). */
  sync(serverTime: number, players: RemoteState[], me: string): void {
    this.me = me;
    this.clock.observe(serverTime, performance.now());
    const seen = new Set<string>();
    for (const s of players) {
      seen.add(s.key);
      let r = this.remotes.get(s.key);
      const look = CHARACTERS.includes(s.appearance) ? s.appearance : CHARACTERS[0];
      if (r && r.look !== look) {
        // A new character: swap the model where they stand.
        this.drop(s.key);
        r = undefined;
      }
      if (!r) r = this.spawn(s, look);
      r.state = s;
      r.buffer.push({ t: serverTime, x: s.x, y: s.y, z: s.z, yaw: yawFromByte(s.rotY) });
    }
    for (const key of [...this.remotes.keys()]) if (!seen.has(key)) this.drop(key);
  }

  /** Pose everyone for this frame. `myCar`: where your car is, while you're driving. */
  update(dt: number, myCar: { x: number; y: number } | null = null): void {
    const now = this.clock.now(performance.now());
    if (now === null) return;
    const t = now - INTERP_DELAY;
    // Passengers' tags stack over the car, in a steady order.
    const seat = new Map<string, number>();
    const taken = new Map<string, number>();
    for (const [key, r] of [...this.remotes].sort(([a], [b]) => (a < b ? -1 : 1))) {
      if (r.state.anim !== ANIM_RIDE || !r.state.ride) continue;
      const n = (taken.get(r.state.ride) ?? 0) + 1;
      taken.set(r.state.ride, n);
      seat.set(key, n);
    }
    for (const [key, r] of this.remotes) {
      const p = r.buffer.sample(t);
      if (!p) continue;
      const w = r.walker;
      const k = Math.max(1e-3, dt);
      const moved = Math.hypot(p.x - w.x, p.z - w.y);
      // Speed and climb from how far they went since the last frame, eased so the walk cycle doesn't flicker.
      w.speed += (moved / k - w.speed) * Math.min(1, dt * 10);
      w.rise = (p.y - w.lift) / k;
      w.x = p.x;
      w.y = p.z;
      w.lift = p.y > 0.004 ? p.y : 0;
      w.heading += turnBetween(w.heading, p.yaw);
      const driving = r.state.anim === ANIM_DRIVE && r.state.car > 0;
      const riding = r.state.anim === ANIM_RIDE;
      r.avatar.root.visible = !driving && !riding;
      if (r.avatar.root.visible) r.avatar.update(w, dt);
      this.placeCar(r, driving);
      const height = driving ? CAR_TAG_HEIGHT : riding ? CAR_TAG_HEIGHT + TAG_STACK * (seat.get(key) ?? 1) : TAG_HEIGHT;
      r.tag.position.set(w.x, height + w.lift, w.y);
      const text = r.state.away ? `${r.state.name} (away)` : r.state.name;
      if (text !== r.tagText) {
        disposeTag(r.tag);
        r.tag = nameTag(text);
        r.tagText = text;
        this.root.add(r.tag);
      }
    }
    // Passengers sit in their driver's car as drawn here (drivers are all posed by now).
    for (const r of this.remotes.values()) {
      if (r.state.anim !== ANIM_RIDE || !r.state.ride) continue;
      const car = r.state.ride === this.me ? myCar : this.remotes.get(r.state.ride)?.walker;
      if (!car) continue;
      r.walker.x = car.x;
      r.walker.y = car.y;
      r.tag.position.x = car.x;
      r.tag.position.z = car.y;
    }
  }

  /** Everyone at the wheel, with how many are riding with them. */
  drivers(): RemoteDriver[] {
    const out: RemoteDriver[] = [];
    for (const [key, r] of this.remotes) {
      if (r.state.anim !== ANIM_DRIVE || r.state.car <= 0) continue;
      let riders = 0;
      for (const o of this.remotes.values()) if (o.state.anim === ANIM_RIDE && o.state.ride === key) riders++;
      out.push({ key, name: r.state.name, x: r.walker.x, y: r.walker.y, heading: r.walker.heading, car: r.state.car, riders });
    }
    return out;
  }

  /** Everyone gone (a new town, leaving the room). */
  clear(): void {
    for (const key of [...this.remotes.keys()]) this.drop(key);
  }

  private spawn(s: RemoteState, look: string): Remote {
    const walker = new Walker({ model: look }, s.x, s.z, PLAYER_SCALE);
    walker.heading = yawFromByte(s.rotY);
    // Their stride's phase from who they are, so two arrivals together don't march in step.
    const avatar = new Avatar(this.assets.character(look), PERSON_HEIGHT, createRng(s.key).next());
    avatar.root.rotation.y = walker.heading;
    const tag = nameTag(s.name);
    this.root.add(avatar.root, tag);
    const r: Remote = { walker, avatar, look, buffer: new SnapshotBuffer(), tag, tagText: s.name, car: null, state: s };
    this.remotes.set(s.key, r);
    return r;
  }

  private drop(key: string): void {
    const r = this.remotes.get(key);
    if (!r) return;
    r.avatar.dispose();
    r.avatar.root.removeFromParent();
    disposeTag(r.tag);
    r.car?.group.removeFromParent();
    this.remotes.delete(key);
  }

  private placeCar(r: Remote, driving: boolean): void {
    const model = driving ? r.state.car - 1 : -1;
    if (r.car && r.car.model !== model) {
      r.car.group.removeFromParent();
      r.car = null;
    }
    if (model < 0 || model >= CARS.length) return;
    const prefab = this.assets.prefab(CARS[model]);
    if (!r.car) {
      const group = new Group();
      for (const part of prefab.parts) {
        const mesh = new Mesh(part.geometry, part.material);
        mesh.castShadow = true;
        mesh.receiveShadow = true;
        group.add(mesh);
      }
      group.matrixAutoUpdate = false;
      this.root.add(group);
      r.car = { model, group };
    }
    const s = carScale(prefab);
    r.car.group.matrix.copy(placement(prefab, { x: r.walker.x, y: r.walker.y }, r.walker.heading, s, s, s, CAR_LIFT));
    r.car.group.matrixWorldNeedsUpdate = true;
  }
}
