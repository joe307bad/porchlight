// One draw function per layer, drawn in registry order. Layers only read the
// TownModel (plus per-model caches derived from it). The context is already
// transformed so 1 unit = 1 tile; `view.px` is one screen pixel in tiles.

import { arcPosition, subPolyline } from '../../core/geometry';
import type { RoadKind, TownModel, Vec2 } from '../../model/types';
import { roadHalfWidth } from '../../gen/raster';
import { contours, debugImage, terrainImage, tileImage, waterImage } from './cache';
import { STYLE } from './style';

export interface DrawView {
  /** CSS pixels per tile. */
  scale: number;
  /** One screen pixel in world units, for constant-width strokes. */
  px: number;
  /** Visible world rectangle, clamped to the map. */
  x0: number;
  y0: number;
  x1: number;
  y1: number;
}

export interface Layer {
  id: string;
  label: string;
  defaultOn: boolean;
  /** Debug views are grouped separately in the panel. */
  debug?: boolean;
  draw(ctx: CanvasRenderingContext2D, model: TownModel, view: DrawView): void;
}

const ROAD_WIDTH = (model: TownModel, kind: RoadKind) =>
  kind === 'main' ? model.params.mainRoadWidth : kind === 'street' ? model.params.streetWidth : 0.4;

function tracePolygon(ctx: CanvasRenderingContext2D, poly: readonly Vec2[]): void {
  ctx.moveTo(poly[0].x, poly[0].y);
  for (let i = 1; i < poly.length; i++) ctx.lineTo(poly[i].x, poly[i].y);
  ctx.closePath();
}

function tracePolyline(ctx: CanvasRenderingContext2D, pts: readonly Vec2[]): void {
  ctx.moveTo(pts[0].x, pts[0].y);
  for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i].x, pts[i].y);
}

function drawRaster(ctx: CanvasRenderingContext2D, img: HTMLCanvasElement, model: TownModel, smooth: boolean, alpha = 1): void {
  ctx.imageSmoothingEnabled = smooth;
  ctx.globalAlpha = alpha;
  ctx.drawImage(img, 0, 0, model.width, model.height);
  ctx.globalAlpha = 1;
}

const terrain: Layer = {
  id: 'terrain',
  label: 'Terrain shading',
  defaultOn: true,
  draw(ctx, model, view) {
    drawRaster(ctx, terrainImage(model), model, true);
    ctx.strokeStyle = STYLE.border;
    ctx.lineWidth = 1.5 * view.px;
    ctx.strokeRect(0, 0, model.width, model.height);
  },
};

const contourLayer: Layer = {
  id: 'contours',
  label: 'Contours',
  defaultOn: true,
  draw(ctx, model, view) {
    const c = contours(model);
    ctx.lineWidth = view.px;
    ctx.strokeStyle = STYLE.contourMinor;
    ctx.stroke(c.minor);
    ctx.lineWidth = 1.2 * view.px;
    ctx.strokeStyle = STYLE.contourMajor;
    ctx.stroke(c.major);
  },
};

const tileView: Layer = {
  id: 'tileView',
  label: 'Tile view',
  defaultOn: false,
  draw(ctx, model) {
    drawRaster(ctx, tileImage(model), model, false);
  },
};

const water: Layer = {
  id: 'water',
  label: 'Water',
  defaultOn: true,
  draw(ctx, model, view) {
    drawRaster(ctx, waterImage(model), model, true);
    ctx.strokeStyle = STYLE.shore;
    ctx.lineWidth = 1.2 * view.px;
    ctx.stroke(contours(model).shore);
  },
};

const rivers: Layer = {
  id: 'rivers',
  label: 'Rivers',
  defaultOn: true,
  draw(ctx, model, view) {
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    for (const pass of [0, 1]) {
      for (const r of model.rivers) {
        for (let k = 0; k + 1 < r.points.length; k++) {
          ctx.beginPath();
          ctx.moveTo(r.points[k].x, r.points[k].y);
          ctx.lineTo(r.points[k + 1].x, r.points[k + 1].y);
          const width = Math.max(r.widthAt[k], 3 * view.px);
          ctx.lineWidth = pass === 0 ? width + 2 * view.px : width;
          ctx.strokeStyle = pass === 0 ? STYLE.riverEdge : STYLE.river;
          ctx.stroke();
        }
      }
    }
  },
};

const blocks: Layer = {
  id: 'blocks',
  label: 'Blocks',
  defaultOn: true,
  draw(ctx, model, view) {
    ctx.fillStyle = STYLE.block;
    ctx.strokeStyle = STYLE.blockEdge;
    ctx.lineWidth = view.px;
    for (const b of model.blocks) {
      ctx.beginPath();
      tracePolygon(ctx, b.polygon);
      for (const hole of b.holes) tracePolygon(ctx, hole);
      ctx.fill('evenodd');
      ctx.stroke();
    }
  },
};

const lots: Layer = {
  id: 'lots',
  label: 'Lots',
  defaultOn: true,
  draw(ctx, model, view) {
    ctx.strokeStyle = STYLE.lotEdge;
    ctx.lineWidth = 0.75 * view.px;
    ctx.setLineDash([3 * view.px, 2 * view.px]);
    ctx.beginPath();
    for (const l of model.lots) tracePolygon(ctx, l.polygon);
    ctx.stroke();
    ctx.setLineDash([]);
  },
};

const roads: Layer = {
  id: 'roads',
  label: 'Roads',
  defaultOn: true,
  draw(ctx, model, view) {
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    // Sidewalks cover every tile the road owns on the grid (tiles whose centre is
    // within the road's half-width, so reaching up to half a tile further), which
    // brings them right up to the building fronts drawn on top.
    ctx.strokeStyle = STYLE.sidewalk;
    for (const kind of ['street', 'main'] as const) {
      ctx.lineWidth = roadHalfWidth(kind, model.params) * 2 + 0.9;
      ctx.beginPath();
      for (const e of model.roads.edges) if (e.kind === kind) tracePolyline(ctx, e.points);
      ctx.stroke();
    }
    for (const kind of ['path', 'street', 'main'] as const) {
      ctx.strokeStyle = STYLE.road[kind];
      ctx.lineWidth = Math.max(ROAD_WIDTH(model, kind), (kind === 'main' ? 2 : 1) * view.px);
      ctx.setLineDash(kind === 'path' ? [1, 0.6] : []);
      ctx.beginPath();
      for (const e of model.roads.edges) if (e.kind === kind) tracePolyline(ctx, e.points);
      ctx.stroke();
    }
    ctx.setLineDash([]);
  },
};

const bridges: Layer = {
  id: 'bridges',
  label: 'Bridges',
  defaultOn: true,
  draw(ctx, model, view) {
    ctx.lineCap = 'butt';
    ctx.lineJoin = 'round';
    for (const b of model.bridges) {
      const edge = model.roads.edges[b.roadEdgeId];
      const a = arcPosition(b.from, edge.points);
      const c = arcPosition(b.to, edge.points);
      const span = subPolyline(edge.points, Math.min(a, c), Math.max(a, c));
      if (span.length < 2) continue;
      const width = ROAD_WIDTH(model, edge.kind);
      ctx.beginPath();
      tracePolyline(ctx, span);
      ctx.strokeStyle = STYLE.bridgeRail;
      ctx.lineWidth = width + 0.8 + 2 * view.px;
      ctx.stroke();
      ctx.strokeStyle = STYLE.bridgeDeck;
      ctx.lineWidth = width + 0.4;
      ctx.stroke();
    }
  },
};

const buildings: Layer = {
  id: 'buildings',
  label: 'Buildings',
  defaultOn: true,
  draw(ctx, model, view) {
    ctx.lineWidth = view.px;
    ctx.strokeStyle = STYLE.buildingEdge;
    for (const b of model.buildings) {
      ctx.beginPath();
      tracePolygon(ctx, b.footprint);
      ctx.fillStyle = STYLE.building[b.kind];
      ctx.fill();
      ctx.stroke();
      // Door tick in the middle of the front wall.
      const [p0, p1, p2, p3] = b.footprint;
      const front = { N: [p0, p1], E: [p1, p2], S: [p2, p3], W: [p3, p0] }[b.facing];
      const mid = { x: (front[0].x + front[1].x) / 2, y: (front[0].y + front[1].y) / 2 };
      ctx.fillStyle = STYLE.door;
      const s = 0.35;
      ctx.fillRect(mid.x - s / 2, mid.y - s / 2, s, s);
    }
  },
};

const landmarks: Layer = {
  id: 'landmarks',
  label: 'Landmarks',
  defaultOn: true,
  draw(ctx, model, view) {
    for (const lm of model.landmarks) {
      if (lm.kind !== 'plaza') continue;
      ctx.beginPath();
      for (const id of lm.lotIds) tracePolygon(ctx, model.lots[id].polygon);
      ctx.fillStyle = STYLE.plaza;
      ctx.fill();
      ctx.strokeStyle = STYLE.plazaEdge;
      ctx.lineWidth = view.px;
      ctx.stroke();
    }
    ctx.strokeStyle = STYLE.landmark;
    ctx.fillStyle = STYLE.landmark;
    ctx.lineWidth = 1.5 * view.px;
    const r = Math.max(0.5, 5 * view.px);
    for (const lm of model.landmarks) {
      const { x, y } = lm.at;
      ctx.beginPath();
      if (lm.kind === 'well') {
        ctx.arc(x, y, r * 0.8, 0, Math.PI * 2);
        ctx.fillStyle = STYLE.river;
        ctx.fill();
        ctx.stroke();
      } else if (lm.kind === 'church') {
        ctx.strokeStyle = STYLE.door;
        ctx.moveTo(x, y - r);
        ctx.lineTo(x, y + r);
        ctx.moveTo(x - r * 0.6, y - r * 0.35);
        ctx.lineTo(x + r * 0.6, y - r * 0.35);
        ctx.stroke();
        ctx.strokeStyle = STYLE.landmark;
      }
    }
  },
};

const gates: Layer = {
  id: 'gates',
  label: 'Center & gates',
  defaultOn: true,
  draw(ctx, model, view) {
    const r = Math.max(0.8, 6 * view.px);
    ctx.fillStyle = STYLE.gate;
    for (const g of model.gates) {
      // A small triangle pointing into the map.
      const dx = model.width / 2 - g.x;
      const dy = model.height / 2 - g.y;
      const a = Math.atan2(dy, dx);
      ctx.beginPath();
      ctx.moveTo(g.x + Math.cos(a) * r, g.y + Math.sin(a) * r);
      ctx.lineTo(g.x + Math.cos(a + 2.4) * r, g.y + Math.sin(a + 2.4) * r);
      ctx.lineTo(g.x + Math.cos(a - 2.4) * r, g.y + Math.sin(a - 2.4) * r);
      ctx.closePath();
      ctx.fill();
    }
    ctx.strokeStyle = STYLE.gate;
    ctx.lineWidth = 1.5 * view.px;
    ctx.setLineDash([4 * view.px, 4 * view.px]);
    ctx.beginPath();
    ctx.arc(model.center.x, model.center.y, model.townRadius, 0, Math.PI * 2);
    ctx.stroke();
    ctx.setLineDash([]);
  },
};

const spawn: Layer = {
  id: 'spawn',
  label: 'Spawn',
  defaultOn: true,
  draw(ctx, model, view) {
    const r = Math.max(0.6, 6 * view.px);
    ctx.beginPath();
    ctx.arc(model.spawn.x, model.spawn.y, r, 0, Math.PI * 2);
    ctx.fillStyle = STYLE.spawn;
    ctx.fill();
    ctx.lineWidth = 2 * view.px;
    ctx.strokeStyle = STYLE.spawnRing;
    ctx.stroke();
  },
};

const tileGrid: Layer = {
  id: 'tileGrid',
  label: 'Tile grid',
  defaultOn: false,
  draw(ctx, _model, view) {
    if (view.scale < 4) return; // too dense to read
    ctx.strokeStyle = STYLE.grid;
    ctx.lineWidth = view.px;
    ctx.beginPath();
    for (let x = view.x0; x <= view.x1; x++) {
      ctx.moveTo(x, view.y0);
      ctx.lineTo(x, view.y1);
    }
    for (let y = view.y0; y <= view.y1; y++) {
      ctx.moveTo(view.x0, y);
      ctx.lineTo(view.x1, y);
    }
    ctx.stroke();
  },
};

// Debug views.

const heightDebug: Layer = {
  id: 'debugHeight',
  label: 'Raw heightmap',
  defaultOn: false,
  debug: true,
  draw(ctx, model) {
    const img = debugImage(model, 'height', (i) => {
      const v = Math.round(model.heightmap[i] * 255);
      return [v, v, v, 255];
    });
    drawRaster(ctx, img, model, false);
  },
};

const flowDebug: Layer = {
  id: 'debugFlow',
  label: 'Flow accumulation',
  defaultOn: false,
  debug: true,
  draw(ctx, model) {
    let max = 1;
    for (const v of model.fields.flow) max = Math.max(max, v);
    const img = debugImage(model, 'flow', (i) => {
      const t = Math.log(model.fields.flow[i] || 1) / Math.log(max);
      return [20, 60 + 120 * t, 120 + 135 * t, Math.round(60 + 195 * t)];
    });
    drawRaster(ctx, img, model, false, 0.9);
  },
};

const costDebug: Layer = {
  id: 'debugCost',
  label: 'A* cost field',
  defaultOn: false,
  debug: true,
  draw(ctx, model) {
    let max = 1;
    for (const v of model.fields.roadCost) if (Number.isFinite(v)) max = Math.max(max, v);
    const img = debugImage(model, 'cost', (i) => {
      const v = model.fields.roadCost[i];
      if (!Number.isFinite(v)) return [90, 0, 30, 220];
      const t = Math.min(1, Math.log(v) / Math.log(max));
      return [255 * t, 220 * (1 - t) + 30, 60, 200];
    });
    drawRaster(ctx, img, model, false, 0.85);
  },
};

const walkDebug: Layer = {
  id: 'debugWalkable',
  label: 'Walkable mask',
  defaultOn: false,
  debug: true,
  draw(ctx, model) {
    const img = debugImage(model, 'walk', (i) =>
      model.tiles.walkable[i] ? [40, 170, 70, 110] : [200, 40, 40, 140],
    );
    drawRaster(ctx, img, model, false);
  },
};

export const LAYERS: readonly Layer[] = [
  terrain,
  contourLayer,
  tileView,
  water,
  rivers,
  blocks,
  lots,
  roads,
  bridges,
  buildings,
  landmarks,
  gates,
  spawn,
  heightDebug,
  flowDebug,
  costDebug,
  walkDebug,
  tileGrid,
];
