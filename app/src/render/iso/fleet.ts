// Draws the moving traffic: one InstancedMesh per car model part, with every
// car's matrix rewritten each frame from its pose in Traffic (a car the
// player has taken is hidden here; the view draws it instead).

import { DynamicDrawUsage, InstancedMesh, Matrix4, type Group } from 'three';
import type { Rng } from '../../core/rng';
import { CARS, CAR_WEIGHTS, type Prefab, type TownAssets } from './assets';
import type { Traffic } from './traffic';
import { carScale, placement } from './world';

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

/** Squashes a car out of sight (the player has taken it out of the traffic). */
const GONE = new Matrix4().makeScale(0, 0, 0);

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
        if (c.taken) {
          for (const mesh of g.meshes) mesh.setMatrixAt(k, GONE);
          return;
        }
        const m = placement(g.prefab, c, Math.atan2(Math.cos(c.heading), Math.sin(c.heading)), s, s, s);
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
