// Debug panel (lil-gui): a slider per GenParams value (grouped by stage) and
// the debug views. Hidden by default; the sidebar holds the everyday controls.

import GUI from 'lil-gui';
import { PARAM_SPECS, type GenParams, type ParamGroup } from '../model/params';
import { LAYERS } from '../render/blueprint/layers';

export interface PanelHandlers {
  onParamsChange(params: GenParams): void;
  onResetParams(): void;
  onLayerToggle(id: string, on: boolean): void;
}

export class Panel {
  private readonly gui = new GUI({ title: 'Debug' });
  private readonly params: GenParams;

  constructor(initial: GenParams, visible: ReadonlySet<string>, handlers: PanelHandlers) {
    this.params = { ...initial };
    this.gui.domElement.classList.add('debug-panel');
    this.gui.hide();

    const generator = this.gui.addFolder('Generator');
    const groups = new Map<ParamGroup, GUI>();
    for (const spec of PARAM_SPECS) {
      let folder = groups.get(spec.group);
      if (!folder) {
        folder = generator.addFolder(spec.group);
        if (spec.group !== 'Map') folder.close();
        groups.set(spec.group, folder);
      }
      folder
        .add(this.params, spec.key, spec.min, spec.max, spec.step)
        .name(spec.label)
        .onFinishChange(() => handlers.onParamsChange({ ...this.params }));
    }
    generator.add({ reset: handlers.onResetParams }, 'reset').name('Reset to defaults');

    const debugFolder = this.gui.addFolder('Debug views');
    const toggles: Record<string, boolean> = {};
    for (const layer of LAYERS) {
      if (!layer.debug) continue;
      toggles[layer.id] = visible.has(layer.id);
      debugFolder
        .add(toggles, layer.id)
        .name(layer.label)
        .onChange((on: boolean) => handlers.onLayerToggle(layer.id, on));
    }
  }

  get isOpen(): boolean {
    return !this.gui._hidden;
  }

  setOpen(open: boolean): void {
    if (open) this.gui.show();
    else this.gui.hide();
  }

  /** Reflect params loaded from elsewhere (e.g. the URL) in the sliders. */
  setParams(params: GenParams): void {
    Object.assign(this.params, params);
    for (const c of this.gui.controllersRecursive()) c.updateDisplay();
  }
}
