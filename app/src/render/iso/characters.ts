// Townsfolk and the player as RG Poly's rigged Cartoon City people (one
// shared skeleton, with the clips kept apart in people/animations.glb). Each
// walker gets its own copy of the model and an animation mixer: idle, walk and
// run (the sprint clip) blended by speed with the legs kept in step, a lean
// into speeding up and into turns, and jump, fall and landing poses with a
// squash and stretch. The player can also carry an x-ray copy: the same meshes
// drawn in flat orange only where something in front hides them, so you can
// always see yourself through buildings.

import {
  AnimationClip,
  type KeyframeTrack,
  type Texture,
  AnimationMixer,
  Box3,
  Group,
  Mesh,
  MeshBasicMaterial,
  MeshStandardMaterial,
  SkinnedMesh,
  Vector3,
  GreaterDepth,
  LoopOnce,
  type AnimationAction,
  type Material,
  type Object3D,
} from 'three';
import type { GLTF } from 'three/addons/loaders/GLTFLoader.js';
import * as SkeletonUtils from 'three/addons/utils/SkeletonUtils.js';
import type { Rng } from '../../core/rng';
import type { Walker } from './actors';
import { CHARACTERS } from './assets';
import { shortestTurn } from './orbit';

export interface Look {
  /** Which Kenney character model. */
  model: string;
}

export function randomLook(rng: Rng): Look {
  return { model: rng.pick(CHARACTERS) };
}

/**
 * A townsperson's height in tiles (the player is a little taller): about
 * 1.6 m beside a car CAR_LENGTH (about 4.4 m) long.
 */
export const PERSON_HEIGHT = 0.36;
/** Walking speed (tiles per second, at scale 1) at which the walk clip plays at its natural pace. */
const NATURAL_WALK = 0.53;
/** Running speed at which the sprint clip (our run) plays at its natural pace. */
const NATURAL_RUN = 1.1;
/** Below WALK_TOP it's all walk, above RUN_FROM all run (tiles per second, at scale 1). */
const WALK_TOP = 1.7;
const RUN_FROM = 2.5;
/**
 * Faster than its natural pace, a stride lengthens as well as quickens: the
 * legs cycle at (speed / natural) ^ CADENCE times the natural rate, so a dash
 * reads as long strides rather than a blur of feet.
 */
const CADENCE = 0.55;
/** Lean forward (radians) at a full run, and per tile/s² of speeding up; bank per radian/s of turn at a run. */
const RUN_LEAN = 0.12;
const ACCEL_LEAN = 0.012;
const BANK = 0.05;
const MAX_TILT = 0.3;
/** Render order for the x-ray pass: after the town, before the player themself. */
export const XRAY_ORDER = 10;
export const PLAYER_ORDER = 20;
/** Feet stand just clear of the road markings, so soles never x-ray through the paint. */
const FEET = 0.035;
/**
 * The kit's clips (people/animations.glb) under the names Avatar plays: idle,
 * walk, sprint (our run), jump (rising), fall (in the air) and crouch (landing).
 */
const CLIPS: Record<string, string> = {
  Idle_A: 'idle',
  Walk_A: 'walk',
  Runing_A: 'sprint',
  Jump_A_Start: 'jump',
  Jump_A_InAir: 'fall',
  Jump_A_Landing: 'crouch',
};
/** Clips whose hips should never rise above standing: the game lifts the whole body itself mid-jump. */
const GROUNDED = new Set(['jump', 'fall', 'crouch']);
/** Hips' height standing, in the kit's units (its skeleton's rest pose). */
const HIPS_REST = 0.411;
/**
 * Turn the kit's people to face +Z, like the cars and the code. (Posed by the
 * clips they face -Z: the clips' hips turn them a quarter from their rest
 * pose, which faces -X. tests/models.test.ts checks which way they face.)
 */
const MODEL_TURN = Math.PI;
/** How far up a person their head bone starts (the rest is head and hair, which varies). */
const HEAD_SHARE = 0.71;

/** The bone a track drives (`FootL.quaternion` → `FootL`). */
const boneOf = (track: KeyframeTrack) => track.name.slice(0, track.name.lastIndexOf('.'));

/** The time, in a clip, at which its hips are lowest (the bottom of a crouch). */
function lowestHips(clip: AnimationClip): number {
  const hips = clip.tracks.find((t) => t.name === 'Hips.position');
  if (!hips) return 0;
  let best = 0;
  for (let i = 1; i < hips.times.length; i++) if (hips.values[i * 3 + 1] < hips.values[best * 3 + 1]) best = i;
  return hips.times[best];
}

/** The clip from time `from` on, starting at 0. */
function from(clip: AnimationClip, start: number): AnimationClip {
  if (start <= 0) return clip;
  const tracks = clip.tracks.map((t) => {
    const c = t.clone().trim(start, clip.duration);
    c.shift(-start);
    return c;
  });
  return new AnimationClip(clip.name, clip.duration - start, tracks);
}

/**
 * The kit's clips renamed for Avatar, with only what suits every person: bone
 * rotations, and the hips' height (each model keeps its own bone lengths). The
 * jump starts at the bottom of its crouch and the landing at its lowest (the
 * game takes off and lands on the instant), and neither lifts the body.
 */
export function peopleClips(source: AnimationClip[]): AnimationClip[] {
  const out: AnimationClip[] = [];
  for (const clip of source) {
    const name = CLIPS[clip.name];
    if (!name) continue;
    const trimmed = name === 'jump' || name === 'crouch' ? from(clip, lowestHips(clip)) : clip;
    const tracks = trimmed.tracks
      .filter((t) => t.name.endsWith('.quaternion') || t.name === 'Hips.position')
      .map((t) => {
        if (t.name !== 'Hips.position' || !GROUNDED.has(name)) return t;
        const c = t.clone();
        for (let i = 1; i < c.values.length; i += 3) c.values[i] = Math.min(c.values[i], HIPS_REST);
        return c;
      });
    out.push(new AnimationClip(name, trimmed.duration, tracks));
  }
  return out;
}

/** Fluorescent orange, drawn only where something already drawn is in front: the see-through-buildings silhouette. */
export function xrayMaterial(): MeshBasicMaterial {
  return new MeshBasicMaterial({ color: 0xff6a00, depthFunc: GreaterDepth, depthWrite: false });
}

/**
 * Ready a loaded person for the town: matte materials, every person's texture
 * the one shared atlas (`shared` carries it from the first person to the
 * rest, so it's uploaded once), and the clips, keeping only tracks for bones
 * this model has (the clip file's rig also has IK targets).
 */
export function prepareCharacter(gltf: GLTF, clips: AnimationClip[], shared: { atlas?: Texture } = {}): void {
  const bones = new Set<string>();
  gltf.scene.traverse((o) => {
    bones.add(o.name);
    const mesh = o as Mesh;
    if (!mesh.isMesh) return;
    const fix = (m: Material): Material => {
      const basic = m as MeshBasicMaterial;
      const lit = (m as MeshStandardMaterial).isMeshStandardMaterial
        ? (m as MeshStandardMaterial)
        : new MeshStandardMaterial({ name: basic.name, map: basic.map, color: basic.color });
      if (lit !== m) m.dispose();
      lit.metalness = 0;
      lit.roughness = 1;
      if (lit.map && lit.map !== (shared.atlas ??= lit.map)) {
        lit.map.dispose();
        lit.map = shared.atlas;
      }
      return lit;
    };
    mesh.material = Array.isArray(mesh.material) ? mesh.material.map(fix) : fix(mesh.material);
  });
  gltf.animations = clips.map((c) => new AnimationClip(c.name, c.duration, c.tracks.filter((t) => bones.has(boneOf(t)))));
}

const smoothstep = (a: number, b: number, x: number) => {
  const t = Math.min(1, Math.max(0, (x - a) / (b - a)));
  return t * t * (3 - 2 * t);
};
/** Ease `from` toward `to` at `rate` per second (frame-rate independent). */
const ease = (from: number, to: number, rate: number, dt: number) => from + (to - from) * (1 - Math.exp(-rate * dt));

export class Avatar {
  readonly root = new Group();
  /** Between the root and the model: leans, banks and squashes about the feet. */
  private readonly body = new Group();
  private readonly model: Object3D;
  private readonly mixer: AnimationMixer;
  private readonly idle: AnimationAction | null;
  private readonly walk: AnimationAction | null;
  private readonly run: AnimationAction | null;
  private readonly jump: AnimationAction | null;
  private readonly fall: AnimationAction | null;
  private readonly crouch: AnimationAction | null;
  /** How much of the pose is moving rather than standing, and how much of the moving is the run. */
  private moveWeight = 0;
  private runWeight = 0;
  /** Where the legs are in their stride (0..1), shared by the walk and run so they blend in step. */
  private stride: number;
  /** How much of the pose is airborne, and of that how much is falling rather than rising. */
  private airWeight = 0;
  private fallWeight = 0;
  private wasAir = false;
  private lastRise = 0;
  /** Seconds since landing, and how hard (0..1) they came down. */
  private sinceLanding = Infinity;
  private impact = 0;
  /** Seconds since take-off (for the stretch). */
  private sinceTakeoff = Infinity;
  private lastSpeed = 0;
  private lastHeading: number | null = null;
  private accel = 0;
  private turn = 0;
  private readonly xrayMeshes: Mesh[] = [];

  constructor(gltf: GLTF, height: number, phase = 0) {
    this.model = SkeletonUtils.clone(gltf.scene);
    this.model.traverse((o) => {
      if ((o as Mesh).isMesh) o.castShadow = true;
    });
    this.model.updateMatrixWorld(true);
    const box = new Box3().setFromObject(this.model);
    // Sized by where the head starts, so a tall hat or a quiff doesn't make anyone smaller.
    const head = this.model.getObjectByName('Head');
    const h = head ? head.getWorldPosition(new Vector3()).y / HEAD_SHARE : Math.max(1e-3, box.max.y - box.min.y);
    this.model.scale.setScalar(height / h);
    this.model.position.y = (-box.min.y * height) / h;
    this.model.rotation.y = MODEL_TURN;
    this.body.add(this.model);
    this.root.add(this.body);
    this.mixer = new AnimationMixer(this.model);
    const action = (...names: string[]) => {
      for (const name of names) {
        const clip = gltf.animations.find((a) => a.name === name);
        if (clip) {
          const a = this.mixer.clipAction(clip);
          a.play();
          a.setEffectiveWeight(0);
          return a;
        }
      }
      return null;
    };
    this.idle = action('idle', 'static');
    this.walk = action('walk');
    this.run = action('sprint');
    this.jump = action('jump');
    this.fall = action('fall');
    this.crouch = action('crouch');
    for (const a of [this.jump, this.crouch]) {
      a?.setLoop(LoopOnce, 1);
      if (a) a.clampWhenFinished = true;
    }
    this.idle?.setEffectiveWeight(1);
    // Start each walker at a different point in its cycles, so crowds don't march in step.
    this.stride = phase;
    if (this.idle) this.idle.time = phase * this.idle.getClip().duration;
  }

  /**
   * Pose the model where the walker is: face their heading, blend idle, walk
   * and run by speed, lean into speeding up and turning, and jump, fall and
   * land with a stretch and a squash.
   */
  update(w: Walker, dt: number): void {
    const k = Math.max(1e-3, dt);
    this.root.position.set(w.x, FEET + w.lift, w.y);
    this.root.scale.setScalar(w.scale);
    // Turn to face the way they're going, quicker the faster they go.
    this.root.rotation.y += shortestTurn(this.root.rotation.y, w.heading) * Math.min(1, dt * (12 + w.speed * 2));

    const pace = w.speed / w.scale;
    this.accel = ease(this.accel, (pace - this.lastSpeed) / k, 8, dt);
    this.lastSpeed = pace;
    const turned = this.lastHeading === null ? 0 : shortestTurn(this.lastHeading, w.heading);
    this.lastHeading = w.heading;
    this.turn = ease(this.turn, turned / k, 10, dt);

    // Air: rising plays the jump, coming down the fall; landing squashes and crouches.
    const air = w.lift > 0;
    if (air && !this.wasAir) {
      this.jump?.reset().play();
      this.sinceTakeoff = 0;
    }
    if (!air && this.wasAir) {
      this.sinceLanding = 0;
      this.impact = Math.min(1, -this.lastRise / 2.2);
      this.crouch?.reset().play();
    }
    this.wasAir = air;
    this.lastRise = w.rise;
    this.sinceLanding += dt;
    this.sinceTakeoff += dt;
    this.airWeight = ease(this.airWeight, air ? 1 : 0, air ? 20 : 12, dt);
    this.fallWeight = ease(this.fallWeight, air && w.rise < 0.2 && this.fall ? 1 : 0, 10, dt);
    const landing = this.crouch ? Math.max(0, 1 - this.sinceLanding / 0.22) * (0.35 + 0.65 * this.impact) : 0;

    // Feet: idle, walk and run, the walk and run sharing one stride so they blend in step.
    const moving = pace > 0.05;
    this.moveWeight = ease(this.moveWeight, moving ? 1 : 0, moving ? 10 : 7, dt);
    this.runWeight = ease(this.runWeight, this.run ? smoothstep(WALK_TOP, RUN_FROM, pace) : 0, 6, dt);
    if (moving) {
      const walkRate = (1 / (this.walk?.getClip().duration ?? 1)) * Math.pow(pace / NATURAL_WALK, CADENCE);
      const runRate = (1 / (this.run?.getClip().duration ?? 1)) * Math.pow(pace / NATURAL_RUN, CADENCE);
      this.stride = (this.stride + dt * Math.min(6, walkRate + (runRate - walkRate) * this.runWeight)) % 1;
    }
    const ground = (1 - this.airWeight) * (1 - landing);
    if (this.walk) {
      this.walk.time = this.stride * this.walk.getClip().duration;
      this.walk.setEffectiveWeight(ground * this.moveWeight * (1 - this.runWeight));
    }
    if (this.run) {
      this.run.time = this.stride * this.run.getClip().duration;
      this.run.setEffectiveWeight(ground * this.moveWeight * this.runWeight);
    }
    this.idle?.setEffectiveWeight(ground * (1 - this.moveWeight));
    this.jump?.setEffectiveWeight(this.airWeight * (1 - this.fallWeight));
    this.fall?.setEffectiveWeight(this.airWeight * this.fallWeight);
    this.crouch?.setEffectiveWeight((1 - this.airWeight) * landing);
    if (this.walk) this.walk.timeScale = 0;
    if (this.run) this.run.timeScale = 0;
    this.mixer.update(dt);

    // Lean into the run and into speeding up (back when braking), bank into turns.
    const run = this.moveWeight * smoothstep(0.3, RUN_FROM, pace);
    const lean = Math.max(-MAX_TILT, Math.min(MAX_TILT, run * RUN_LEAN + this.accel * ACCEL_LEAN * (1 - this.airWeight)));
    const bank = Math.max(-MAX_TILT, Math.min(MAX_TILT, -this.turn * BANK * run * (1 - this.airWeight)));
    this.body.rotation.x = ease(this.body.rotation.x, lean, 10, dt);
    this.body.rotation.z = ease(this.body.rotation.z, bank, 10, dt);
    // Stretch leaving the ground, squash landing (keeping their volume).
    const stretch = 0.12 * Math.max(0, 1 - this.sinceTakeoff / 0.18);
    const squash = 0.2 * this.impact * Math.sin(Math.PI * Math.min(1, this.sinceLanding / 0.2));
    const sy = 1 + stretch - squash;
    this.body.scale.set(1 / Math.sqrt(sy), sy, 1 / Math.sqrt(sy));
  }

  /**
   * Add the x-ray copy: every mesh again in fluorescent orange, drawn only
   * where it's behind something already drawn (the town), and before the
   * player's own meshes so they don't x-ray themselves.
   */
  addXray(): void {
    const material = xrayMaterial();
    const meshes: Mesh[] = [];
    this.model.traverse((o) => {
      if ((o as Mesh).isMesh) meshes.push(o as Mesh);
    });
    for (const m of meshes) {
      let x: Mesh;
      if ((m as SkinnedMesh).isSkinnedMesh) {
        const s = m as SkinnedMesh;
        const copy = new SkinnedMesh(s.geometry, material);
        copy.bind(s.skeleton, s.bindMatrix);
        x = copy;
      } else {
        x = new Mesh(m.geometry, material);
      }
      x.position.copy(m.position);
      x.quaternion.copy(m.quaternion);
      x.scale.copy(m.scale);
      x.renderOrder = XRAY_ORDER;
      x.frustumCulled = false;
      m.renderOrder = PLAYER_ORDER;
      m.frustumCulled = false;
      m.parent!.add(x);
      this.xrayMeshes.push(x);
    }
  }

  set xrayVisible(on: boolean) {
    for (const m of this.xrayMeshes) m.visible = on;
  }

  dispose(): void {
    this.root.removeFromParent();
    this.mixer.stopAllAction();
    for (const m of this.xrayMeshes) (m.material as Material).dispose();
  }
}
