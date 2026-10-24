// Grid A* (8-connected). Costs and goals come from the caller, so the same
// search serves main roads, connector paths and anything else on the grid.

import { DIRS8 } from './grid';
import { MinHeap } from './heap';

export interface GridSearch {
  width: number;
  height: number;
  /** Cost to step from cell `from` into cell `to`; Infinity blocks the move. */
  stepCost(from: number, to: number): number;
  isGoal(cell: number): boolean;
  /** Admissible estimate of the remaining cost from `cell`. */
  heuristic(cell: number): number;
}

/** Cheapest path from `start` to any goal cell, start and goal included, or null. */
export function findPath(start: number, search: GridSearch): number[] | null {
  const { width, height } = search;
  const n = width * height;
  const g = new Float64Array(n).fill(Infinity);
  const came = new Int32Array(n).fill(-1);
  const closed = new Uint8Array(n);
  const open = new MinHeap();
  g[start] = 0;
  open.push(start, search.heuristic(start));
  while (open.size) {
    const cur = open.pop();
    if (closed[cur]) continue;
    closed[cur] = 1;
    if (search.isGoal(cur)) {
      const path = [cur];
      for (let c = came[cur]; c !== -1; c = came[c]) path.push(c);
      return path.reverse();
    }
    const cx = cur % width;
    const cy = (cur - cx) / width;
    for (const [dx, dy] of DIRS8) {
      const nx = cx + dx;
      const ny = cy + dy;
      if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
      const next = ny * width + nx;
      if (closed[next]) continue;
      const step = search.stepCost(cur, next);
      if (!Number.isFinite(step)) continue;
      const cost = g[cur] + step;
      if (cost < g[next]) {
        g[next] = cost;
        came[next] = cur;
        open.push(next, cost + search.heuristic(next));
      }
    }
  }
  return null;
}

/** Octile distance between two cells, the natural metric for 8-connected moves. */
export function octile(a: number, b: number, width: number): number {
  const dx = Math.abs((a % width) - (b % width));
  const dy = Math.abs(Math.floor(a / width) - Math.floor(b / width));
  return Math.max(dx, dy) + (Math.SQRT2 - 1) * Math.min(dx, dy);
}

export interface GridSearch4 {
  width: number;
  height: number;
  /** Cost to step from cell `from` into cell `to` (4-connected); Infinity blocks the move. */
  stepCost(from: number, to: number): number;
  /** Extra cost for changing direction, so paths run in long straight lines. */
  turnCost: number;
  isGoal(cell: number): boolean;
  heuristic(cell: number): number;
}

const DX4 = [0, 1, 0, -1];
const DY4 = [-1, 0, 1, 0];

/**
 * A* over (cell, heading) states with 4-connected moves and a penalty per
 * turn: the cheapest path made of straight, grid-aligned runs.
 */
export function findGridPath(start: number, search: GridSearch4): number[] | null {
  const { width, height } = search;
  const n = width * height;
  // State = cell * 5 + heading (0-3), with 4 meaning "just started".
  const g = new Float64Array(n * 5).fill(Infinity);
  const came = new Int32Array(n * 5).fill(-1);
  const closed = new Uint8Array(n * 5);
  const open = new MinHeap();
  const s0 = start * 5 + 4;
  g[s0] = 0;
  open.push(s0, search.heuristic(start));
  while (open.size) {
    const state = open.pop();
    if (closed[state]) continue;
    closed[state] = 1;
    const cell = Math.floor(state / 5);
    const heading = state % 5;
    if (search.isGoal(cell)) {
      const path = [cell];
      for (let s = came[state]; s !== -1; s = came[s]) path.push(Math.floor(s / 5));
      return path.reverse();
    }
    const cx = cell % width;
    const cy = (cell - cx) / width;
    for (let d = 0; d < 4; d++) {
      if (heading !== 4 && d === (heading + 2) % 4) continue; // no U-turns
      const nx = cx + DX4[d];
      const ny = cy + DY4[d];
      if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
      const next = ny * width + nx;
      const ns = next * 5 + d;
      if (closed[ns]) continue;
      const step = search.stepCost(cell, next);
      if (!Number.isFinite(step)) continue;
      const cost = g[state] + step + (heading !== 4 && heading !== d ? search.turnCost : 0);
      if (cost < g[ns]) {
        g[ns] = cost;
        came[ns] = state;
        open.push(ns, cost + search.heuristic(next));
      }
    }
  }
  return null;
}

/** Manhattan distance between two cells. */
export function manhattan(a: number, b: number, width: number): number {
  return Math.abs((a % width) - (b % width)) + Math.abs(Math.floor(a / width) - Math.floor(b / width));
}
