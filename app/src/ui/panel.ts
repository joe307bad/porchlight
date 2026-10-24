// lil-gui panel: a slider per GenParams value (grouped by stage), layer
// toggles, debug views, view and export actions.

import GUI from 'lil-gui';
import { PARAM_SPECS, type GenParams, type ParamGroup } from '../model/params';
import { LAYERS } from '../render/blueprint/layers';

export interface PanelHandlers {
  onParamsChange(params: GenParams): void;
  onRegenerate(): void;
  onResetParams(): void;
  onLayerToggle(id: string, on: boolean): void;
  onFitView(): void;
  onExportJson(): void;
  onExportPng(): void;
}

export class Panel {
  private readonly gui = new GUI({ title: 'Porchlight' });
  private readonly params: GenParams;

  constructor(initial: GenParams, visible: ReadonlySet<string>, handlers: PanelHandlers) {
    this.params = { ...initial };

    this.gui.add({ regenerate: handlers.onRegenerate }, 'regenerate').name('Regenerate (new seed)');

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

    const layerFolder = this.gui.addFolder('Layers');
    const debugFolder = this.gui.addFolder('Debug views');
    debugFolder.close();
    const toggles: Record<string, boolean> = {};
    for (const layer of LAYERS) {
      toggles[layer.id] = visible.has(layer.id);
      (layer.debug ? debugFolder : layerFolder)
        .add(toggles, layer.id)
        .name(layer.label)
        .onChange((on: boolean) => handlers.onLayerToggle(layer.id, on));
    }

    const viewFolder = this.gui.addFolder('View & export');
    viewFolder.add({ fit: handlers.onFitView }, 'fit').name('Fit to screen');
    viewFolder.add({ json: handlers.onExportJson }, 'json').name('Download JSON');
    viewFolder.add({ png: handlers.onExportPng }, 'png').name('Download PNG');
  }

  /** Reflect params loaded from elsewhere (e.g. the URL) in the sliders. */
  setParams(params: GenParams): void {
    Object.assign(this.params, params);
    for (const c of this.gui.controllersRecursive()) c.updateDisplay();
  }
}
