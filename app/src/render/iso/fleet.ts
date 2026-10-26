// Draws the moving traffic: one InstancedMesh per car model part, with every
// car's matrix rewritten each frame from its pose in Traffic. Cars out of
// traffic (the one you're driving, ones you've left) are drawn one by one as
// CarBodies.

import { DynamicDrawUsage, GreaterDepth, Group, InstancedMesh, Matrix4, Mesh, MeshBasicMaterial } from 'three';
import type { Rng } from '../../core/rng';
import { CARS, CAR_WEIGHTS, type Prefab, type TownAssets } from './assets';
import { XRAY_ORDER } from './characters';
import type { CarPose } from './driving';
import type { Traffic } from './traffic';
import { carScale, placement } from './world';

const HIDDEN = new Matrix4().makeScale(0, 0, 0);
/** Car model parts face +Z; a pose's heading is atan2(dy, dx) on the map. */
const carTurn = (heading: number) => Math.atan2(Math.cos(heading), Math.sin(heading));

/** A car model index, picked by how common each model is. */
export function pickCarModel(rng: Rng): number {
  const total = CAR_WEIGHTS.reduce((a, b) => a + b, 0);
  let r = rng.next() * total;
  for (let k = 0; k < CAR_WEIGHTS.length; k++) {
    r -= CAR_WEIGHTS[k];
    if (r <= 0) return k;
  }
  return 0;
}

export class Fleet {
  private readonly groups: { prefab: Prefab; meshes: InstancedMesh[]; cars: number[] }[] = [];

  constructor(
    private readonly traffic: Traffic,
    assets: TownAssets,
    root: Group,
  ) {
    CARS.forEach((name, model) => {
      const cars = traffic.cars.flatMap((c, i) => (c.model === model ? [i] : []));
      if (!cars.length) return;
      const prefab = assets.prefab(name);
      const meshes = prefab.parts.map((part) => {
        const mesh = new InstancedMesh(part.geometry, part.material, cars.length);
        mesh.instanceMatrix.setUsage(DynamicDrawUsage);
        mesh.castShadow = true;
        mesh.receiveShadow = true;
        mesh.frustumCulled = false; // they roam the whole town
        root.add(mesh);
        return mesh;
      });
      this.groups.push({ prefab, meshes, cars });
    });
    this.update();
  }

  update(): void {
    for (const g of this.groups) {
      const s = carScale(g.prefab);
      g.cars.forEach((i, k) => {
        const c = this.traffic.cars[i];
        const m = c.gone ? HIDDEN : placement(g.prefab, c, carTurn(c.heading), s, s, s);
        for (const mesh of g.meshes) mesh.setMatrixAt(k, m);
      });
      for (const mesh of g.meshes) mesh.instanceMatrix.needsUpdate = true;
    }
  }

  dispose(): void {
    for (const g of this.groups) for (const m of g.meshes) {
      m.removeFromParent();
      m.dispose();
    }
  }
}

/** One car out of traffic, drawn on its own (and, while you drive it, seen through buildings in orange). */
export class CarBody {
  readonly root = new Group();
  private xray: MeshBasicMaterial | null = null;
  private readonly xrayMeshes: Mesh[] = [];

  constructor(private readonly prefab: Prefab) {
    for (const part of prefab.parts) {
      const mesh = new Mesh(part.geometry, part.material);
      mesh.castShadow = true;
      mesh.receiveShadow = true;
      this.root.add(mesh);
    }
    this.root.matrixAutoUpdate = false;
  }

  static of(assets: TownAssets, model: number): CarBody {
    return new CarBody(assets.prefab(CARS[model]));
  }

  place(c: CarPose): void {
    const s = carScale(this.prefab);
    this.root.matrix.copy(placement(this.prefab, c, carTurn(c.heading), s, s, s));
    this.root.matrixWorldNeedsUpdate = true;
  }

  /** Show the car through whatever hides it, like the player's own x-ray (see Avatar.addXray). */
  set xrayVisible(on: boolean) {
    if (on && !this.xray) {
      this.xray = new MeshBasicMaterial({ color: 0xff6a00, depthFunc: GreaterDepth, depthWrite: false });
      for (const m of [...this.root.children] as Mesh[]) {
        const x = new Mesh(m.geometry, this.xray);
        x.renderOrder = XRAY_ORDER;
        m.renderOrder = XRAY_ORDER + 5;
        this.root.add(x);
        this.xrayMeshes.push(x);
      }
    }
    for (const m of this.xrayMeshes) m.visible = on;
  }

  dispose(): void {
    this.root.removeFromParent();
    this.xray?.dispose();
  }
}
