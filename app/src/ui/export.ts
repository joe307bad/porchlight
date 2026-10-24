// Downloads: the TownModel as JSON and the whole blueprint as a PNG.

import { encodeModel } from '../model/serialize';
import type { TownModel } from '../model/types';
import { Camera } from '../render/blueprint/camera';
import { drawScene } from '../render/blueprint/renderer';

/** Target resolution of the exported image's longer side, in pixels. */
const PNG_SIZE = 2048;

function download(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function exportJson(model: TownModel): void {
  download(new Blob([encodeModel(model)], { type: 'application/json' }), `porchlight-${model.seed}.json`);
}

export function exportPng(model: TownModel, visible: ReadonlySet<string>): void {
  const scale = Math.max(1, Math.floor(PNG_SIZE / Math.max(model.width, model.height)));
  const canvas = document.createElement('canvas');
  canvas.width = model.width * scale;
  canvas.height = model.height * scale;
  const camera = new Camera();
  camera.scale = scale;
  drawScene(canvas.getContext('2d')!, model, camera, canvas.width, canvas.height, 1, visible);
  canvas.toBlob((blob) => blob && download(blob, `porchlight-${model.seed}.png`), 'image/png');
}
