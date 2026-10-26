// Car lights at night: every car on the move (the traffic, and the one you're
// driving) has two headlights and two red tail lights that glow, each with a
// soft halo, and throws a pool of warm white light on the road ahead of it.
// Parked cars stay dark. They fade in and out with the dusk, like the street
// lamps. One InstancedMesh each for the lamps and the beams and one Points
// for the halos, rewritten every frame from the cars' poses.
//
// `carLights` (where a car's lamps and beam are) is pure and unit-tested.

import {
  AdditiveBlending,
  BufferAttribute,
  BufferGeometry,
  Color,
  DynamicDrawUsage,
  InstancedMesh,
  Matrix4,
  MeshBasicMaterial,
  PlaneGeometry,
  Points,
  PointsMaterial,
  Quaternion,
  SphereGeometry,
  Vector3,
  type Group,
} from 'three';
import type { Vec2 } from '../../model/types';
import type { Prefab } from './assets';
import { glowTexture } from './textures';

/** A car's lamp layout (tiles): half its length and width, and how high its lamps sit. */
export interface CarShape {
  halfLength: number;
  halfWidth: number;
  lampHeight: number;
}

export interface CarLights {
  /** Headlights then tail lights, map position and height. */
  front: { x: number; y: number; h: number }[];
  back: { x: number; y: number; h: number }[];
  /** Middle of the pool of light on the road ahead, and the way it points (map space). */
  beam: Vec2;
  forward: Vec2;
}

/** The beam pool's size on the ground (tiles): reach ahead, and width. */
const BEAM_LENGTH = 2.6;
const BEAM_WIDTH = 1.15;
/** How far in from the car's sides the lamps sit, a little more than this (tiles). */
const LAMP_INSET = 0.06;
const MAX_LIGHTS = 80;

/** A car's lamp layout from its model's size at the drawn scale. */
export function carShape(p: Prefab, scale: number): CarShape {
  return { halfLength: (p.size.z * scale) / 2, halfWidth: (p.size.x * scale) / 2, lampHeight: p.size.y * scale * 0.36 };
}

/** Where a car's lamps and beam are, for a car at (x, y) pointing along `heading` (map radians, atan2(dy, dx)). */
export function carLights(x: number, y: number, heading: number, shape: CarShape): CarLights {
  const f = { x: Math.cos(heading), y: Math.sin(heading) };
  const r = { x: -f.y, y: f.x };
  const along = shape.halfLength - 0.012; // just proud of the bumper, so the body doesn't hide them
  const side = Math.max(0.05, shape.halfWidth - LAMP_INSET * 1.4);
  const lamp = (a: number, b: number) => ({ x: x + f.x * a + r.x * b, y: y + f.y * a + r.y * b, h: shape.lampHeight });
  const reach = shape.halfLength + BEAM_LENGTH / 2 - 0.1;
  return {
    front: [lamp(along, side), lamp(along, -side)],
    back: [lamp(-along, side), lamp(-along, -side)],
    beam: { x: x + f.x * reach, y: y + f.y * reach },
    forward: f,
  };
}

const UP = new Vector3(0, 1, 0);

export class Headlights {
  private readonly lamps: InstancedMesh;
  private readonly beams: InstancedMesh;
  private readonly halos: Points<BufferGeometry, PointsMaterial>;
  private readonly owned: { dispose(): void }[] = [];

  constructor(root: Group) {
    const lampGeo = new SphereGeometry(0.028, 8, 5);
    const lampMat = new MeshBasicMaterial({ color: 0xffffff });
    this.lamps = new InstancedMesh(lampGeo, lampMat, MAX_LIGHTS * 4);
    // The glow texture is round; stretched along the car it reads as a beam fanning out ahead.
    const beamGeo = new PlaneGeometry(BEAM_WIDTH, BEAM_LENGTH).rotateX(-Math.PI / 2);
    const beamMat = new MeshBasicMaterial({ map: glowTexture(), transparent: true, depthWrite: false, blending: AdditiveBlending });
    this.beams = new InstancedMesh(beamGeo, beamMat, MAX_LIGHTS);
    const haloGeo = new BufferGeometry();
    const pos = new BufferAttribute(new Float32Array(MAX_LIGHTS * 4 * 3), 3);
    const col = new BufferAttribute(new Float32Array(MAX_LIGHTS * 4 * 3), 3);
    pos.setUsage(DynamicDrawUsage);
    col.setUsage(DynamicDrawUsage);
    haloGeo.setAttribute('position', pos);
    haloGeo.setAttribute('color', col);
    const haloMat = new PointsMaterial({ size: 12, map: glowTexture(), vertexColors: true, transparent: true, depthWrite: false, blending: AdditiveBlending, sizeAttenuation: true });
    this.halos = new Points(haloGeo, haloMat);
    for (const m of [this.lamps, this.beams]) {
      m.instanceMatrix.setUsage(DynamicDrawUsage);
      m.frustumCulled = false;
      m.count = 0;
    }
    this.halos.frustumCulled = false;
    this.beams.renderOrder = 6; // with the lamp pools: after the ground, before the halos
    this.lamps.renderOrder = 21; // after the cars, so the lamps on the driven car don't count as hiding it from the x-ray
    this.halos.renderOrder = 7;
    root.add(this.lamps, this.beams, this.halos);
    this.owned.push(lampGeo, lampMat, beamGeo, beamMat, haloGeo, haloMat, this.lamps, this.beams);
  }

  /**
   * Light the cars for a dusk level (0 day .. 1 night). `haloSize` is the halo
   * material size to use for the current camera (it scales like the lamp halos).
   */
  update(cars: readonly { x: number; y: number; heading: number; shape: CarShape }[], dusk: number, haloSize: number): void {
    const on = dusk > 0.02;
    this.lamps.visible = this.beams.visible = this.halos.visible = on;
    if (!on) return;
    const n = Math.min(cars.length, MAX_LIGHTS);
    const m = new Matrix4();
    const q = new Quaternion();
    const one = new Vector3(1, 1, 1);
    const head = new Color(0xfff4dc).multiplyScalar(0.25 + dusk * 0.75);
    const tail = new Color(0xff2a1a).multiplyScalar(0.3 + dusk * 0.7);
    const pool = new Color(1, 0.93, 0.78).multiplyScalar(dusk * 0.5);
    const pos = this.halos.geometry.getAttribute('position') as BufferAttribute;
    const col = this.halos.geometry.getAttribute('color') as BufferAttribute;
    for (let i = 0; i < n; i++) {
      const c = cars[i];
      const l = carLights(c.x, c.y, c.heading, c.shape);
      [...l.front, ...l.back].forEach((p, k) => {
        const j = i * 4 + k;
        this.lamps.setMatrixAt(j, m.makeTranslation(p.x, p.h, p.y));
        this.lamps.setColorAt(j, k < 2 ? head : tail);
        pos.setXYZ(j, p.x, p.h, p.y);
        if (k < 2) col.setXYZ(j, dusk, dusk * 0.95, dusk * 0.82);
        else col.setXYZ(j, dusk * 0.7, dusk * 0.08, dusk * 0.04);
      });
      // The beam's long side (local -Z after the rotation above) points ahead.
      q.setFromAxisAngle(UP, Math.atan2(-l.forward.x, -l.forward.y));
      this.beams.setMatrixAt(i, m.compose(new Vector3(l.beam.x, 0.045, l.beam.y), q, one));
      this.beams.setColorAt(i, pool);
    }
    this.lamps.count = n * 4;
    this.beams.count = n;
    this.halos.geometry.setDrawRange(0, n * 4);
    this.halos.material.size = haloSize;
    for (const mesh of [this.lamps, this.beams]) {
      mesh.instanceMatrix.needsUpdate = true;
      if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    }
    pos.needsUpdate = true;
    col.needsUpdate = true;
  }

  dispose(): void {
    this.lamps.removeFromParent();
    this.beams.removeFromParent();
    this.halos.removeFromParent();
    for (const d of this.owned) d.dispose();
  }
}
