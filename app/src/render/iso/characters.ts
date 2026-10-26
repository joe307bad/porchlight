// Townsfolk and the player as Kenney's rigged mini characters. Each walker gets
// its own copy of the model and an animation mixer that blends the idle and
// walk clips by how fast they're going. The player can also carry an x-ray
// copy: the same skinned meshes drawn in flat orange only where something in
// front hides them, so you can always see yourself through buildings.

import {
  AnimationMixer,
  Box3,
  Group,
  Mesh,
  MeshBasicMaterial,
  SkinnedMesh,
  GreaterDepth,
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
/** Walking speed (tiles per second) at which the walk clip plays at its natural pace. */
const NATURAL_WALK = 0.53;
/** Render order for the x-ray pass: after the town, before the player themself. */
export const XRAY_ORDER = 10;
const PLAYER_ORDER = 20;
/** Feet stand just clear of the road markings, so soles never x-ray through the paint. */
const FEET = 0.035;

export class Avatar {
  readonly root = new Group();
  private readonly model: Object3D;
  private readonly mixer: AnimationMixer;
  private readonly walk: AnimationAction | null;
  private readonly idle: AnimationAction | null;
  private walkWeight = 0;
  private readonly xrayMeshes: Mesh[] = [];

  constructor(gltf: GLTF, height: number, phase = 0) {
    this.model = SkeletonUtils.clone(gltf.scene);
    this.model.traverse((o) => {
      if ((o as Mesh).isMesh) o.castShadow = true;
    });
    const box = new Box3().setFromObject(this.model);
    const h = Math.max(1e-3, box.max.y - box.min.y);
    this.model.scale.setScalar(height / h);
    this.model.position.y = (-box.min.y * height) / h;
    this.root.add(this.model);
    this.mixer = new AnimationMixer(this.model);
    const clip = (name: string) => gltf.animations.find((a) => a.name === name);
    const walkClip = clip('walk');
    const idleClip = clip('idle') ?? clip('static');
    this.walk = walkClip ? this.mixer.clipAction(walkClip) : null;
    this.idle = idleClip ? this.mixer.clipAction(idleClip) : null;
    this.idle?.play();
    this.walk?.play();
    this.walk?.setEffectiveWeight(0);
    // Start each walker at a different point in its cycles, so crowds don't march in step.
    if (this.walk) this.walk.time = phase * this.walk.getClip().duration;
    if (this.idle) this.idle.time = phase * this.idle.getClip().duration;
  }

  /** Pose the model where the walker is: face their heading, blend walk and idle by speed. */
  update(w: Walker, dt: number): void {
    this.root.position.set(w.x, FEET, w.y);
    this.root.scale.setScalar(w.scale);
    this.root.rotation.y += shortestTurn(this.root.rotation.y, w.heading) * Math.min(1, dt * 12);
    const moving = w.speed > 0.05;
    this.walkWeight += ((moving ? 1 : 0) - this.walkWeight) * Math.min(1, dt * 10);
    if (this.walk) {
      this.walk.setEffectiveWeight(this.walkWeight);
      if (moving) this.walk.timeScale = Math.min(6, Math.max(0.5, w.speed / (NATURAL_WALK * w.scale)));
    }
    this.idle?.setEffectiveWeight(1 - this.walkWeight);
    this.mixer.update(dt);
  }

  /**
   * Add the x-ray copy: every mesh again in fluorescent orange, drawn only
   * where it's behind something already drawn (the town), and before the
   * player's own meshes so they don't x-ray themselves.
   */
  addXray(): void {
    const material = new MeshBasicMaterial({ color: 0xff6a00, depthFunc: GreaterDepth, depthWrite: false });
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
