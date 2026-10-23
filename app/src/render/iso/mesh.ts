// A little mesh builder for the procedural parts of the town (ground tiles,
// roads, bridges, sheds): flat-coloured quads, ribbons along polylines and
// walls, collected into one vertex-coloured BufferGeometry. World (x, y) on the
// map is Three's (x, z); height is Three's y.

import { BufferGeometry, Color, Float32BufferAttribute, Vector3 } from 'three';
import type { Vec2 } from '../../model/types';
import { offsetPolyline } from './layout';

const tmp = new Color();
const ab = new Vector3();
const ac = new Vector3();
const n = new Vector3();

export class MeshBuilder {
  private readonly pos: number[] = [];
  private readonly col: number[] = [];
  private readonly idx: number[] = [];

  get empty(): boolean {
    return this.idx.length === 0;
  }

  /** A quad (corners in order round its edge) facing roughly `facing` (winding is fixed up to match). */
  quad(a: Vector3, b: Vector3, c: Vector3, d: Vector3, color: number, facing: Vector3 = UP): void {
    const base = this.pos.length / 3;
    n.crossVectors(ab.subVectors(b, a), ac.subVectors(c, a));
    const flip = n.dot(facing) < 0;
    for (const p of flip ? [a, d, c, b] : [a, b, c, d]) this.pos.push(p.x, p.y, p.z);
    tmp.setHex(color);
    for (let k = 0; k < 4; k++) this.col.push(tmp.r, tmp.g, tmp.b);
    this.idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
  }

  /** A flat axis-aligned rectangle at height y (map coordinates). */
  rect(x0: number, y0: number, x1: number, y1: number, y: number, color: number): void {
    this.quad(v(x0, y, y0), v(x1, y, y0), v(x1, y, y1), v(x0, y, y1), color);
  }

  /** A flat strip `width` wide along a polyline, at height y. */
  ribbon(pts: readonly Vec2[], width: number, y: number, color: number): void {
    if (pts.length < 2) return;
    const l = offsetPolyline(pts, width / 2);
    const r = offsetPolyline(pts, -width / 2);
    for (let k = 1; k < pts.length; k++) {
      this.quad(v(l[k - 1].x, y, l[k - 1].y), v(l[k].x, y, l[k].y), v(r[k].x, y, r[k].y), v(r[k - 1].x, y, r[k - 1].y), color);
    }
  }

  /** A flat disc (a round road end or junction cap). */
  disc(c: Vec2, radius: number, y: number, color: number, segments = 18): void {
    for (let k = 0; k < segments; k += 2) {
      const at = (i: number) => {
        const a = (i / segments) * Math.PI * 2;
        return v(c.x + Math.cos(a) * radius, y, c.y + Math.sin(a) * radius);
      };
      this.quad(v(c.x, y, c.y), at(k), at(k + 1), at(k + 2), color);
    }
  }

  /** A vertical wall along a polyline offset sideways by `offset`, from y0 up to y1, facing away from the line. */
  wall(pts: readonly Vec2[], offset: number, y0: number, y1: number, color: number): void {
    const side = offsetPolyline(pts, offset);
    for (let k = 1; k < side.length; k++) {
      const a = side[k - 1];
      const b = side[k];
      const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      const centre = { x: (pts[k - 1].x + pts[k].x) / 2, y: (pts[k - 1].y + pts[k].y) / 2 };
      const out = new Vector3(mid.x - centre.x, 0, mid.y - centre.y);
      this.quad(v(a.x, y0, a.y), v(b.x, y0, b.y), v(b.x, y1, b.y), v(a.x, y1, a.y), color, out);
    }
  }

  /** A low wall `thickness` thick centred on a polyline offset by `offset`: two sides and a top. */
  parapet(pts: readonly Vec2[], offset: number, thickness: number, y0: number, y1: number, color: number, shade: number): void {
    const o = offset;
    const t = thickness / 2;
    this.wall(pts, o + Math.sign(o || 1) * t, y0, y1, shade);
    this.wall(pts, o - Math.sign(o || 1) * t, y0, y1, color);
    this.ribbon(offsetPolyline(pts, o), thickness, y1, color);
  }

  /** An axis-aligned box (map coordinates), with an optional colour for its top. */
  box(x0: number, y0: number, x1: number, y1: number, h0: number, h1: number, color: number, top = color): void {
    this.rect(x0, y0, x1, y1, h1, top);
    this.quad(v(x0, h0, y0), v(x1, h0, y0), v(x1, h1, y0), v(x0, h1, y0), color, new Vector3(0, 0, -1));
    this.quad(v(x0, h0, y1), v(x1, h0, y1), v(x1, h1, y1), v(x0, h1, y1), color, new Vector3(0, 0, 1));
    this.quad(v(x0, h0, y0), v(x0, h0, y1), v(x0, h1, y1), v(x0, h1, y0), color, new Vector3(-1, 0, 0));
    this.quad(v(x1, h0, y0), v(x1, h0, y1), v(x1, h1, y1), v(x1, h1, y0), color, new Vector3(1, 0, 0));
  }

  build(): BufferGeometry {
    const g = new BufferGeometry();
    g.setAttribute('position', new Float32BufferAttribute(this.pos, 3));
    g.setAttribute('color', new Float32BufferAttribute(this.col, 3));
    g.setIndex(this.idx);
    g.computeVertexNormals();
    g.computeBoundingSphere();
    return g;
  }
}

export const UP = new Vector3(0, 1, 0);

/** A map point (x, y) at height h, as a Three vector. */
export function v(x: number, h: number, y: number): Vector3 {
  return new Vector3(x, h, y);
}
