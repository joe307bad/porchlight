// Pan (drag), zoom (wheel / trackpad pinch), double-click to refit, and hover tracking on the canvas.

import type { Vec2 } from '../model/types';
import type { Camera } from '../render/blueprint/camera';

export interface ViewportHandlers {
  /** The camera moved. */
  onChange(): void;
  onHover(world: Vec2 | null): void;
  onDoubleClick(): void;
}

export function attachViewport(canvas: HTMLCanvasElement, camera: Camera, handlers: ViewportHandlers): void {
  let dragging: { x: number; y: number } | null = null;

  const local = (e: MouseEvent): Vec2 => {
    const r = canvas.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  };

  canvas.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    dragging = { x: e.clientX, y: e.clientY };
    canvas.setPointerCapture(e.pointerId);
    canvas.classList.add('dragging');
  });

  canvas.addEventListener('pointermove', (e) => {
    if (dragging) {
      camera.pan(e.clientX - dragging.x, e.clientY - dragging.y);
      dragging = { x: e.clientX, y: e.clientY };
      handlers.onChange();
    }
    handlers.onHover(camera.screenToWorld(local(e)));
  });

  const endDrag = (e: PointerEvent) => {
    if (!dragging) return;
    dragging = null;
    canvas.releasePointerCapture(e.pointerId);
    canvas.classList.remove('dragging');
  };
  canvas.addEventListener('pointerup', endDrag);
  canvas.addEventListener('pointercancel', endDrag);
  canvas.addEventListener('pointerleave', () => handlers.onHover(null));

  canvas.addEventListener('dblclick', () => handlers.onDoubleClick());

  canvas.addEventListener(
    'wheel',
    (e) => {
      e.preventDefault();
      // Trackpad pinch arrives as ctrl+wheel with small deltas; scale accordingly.
      const sensitivity = e.ctrlKey ? 0.01 : 0.0015;
      camera.zoomAt(local(e), Math.exp(-e.deltaY * sensitivity));
      handlers.onChange();
      handlers.onHover(camera.screenToWorld(local(e)));
    },
    { passive: false },
  );
}
