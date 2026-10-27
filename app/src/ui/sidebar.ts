// The right-hand sidebar, the app's one menu in both modes: Regenerate and
// Explore / Back to map, the seed and the handful of params most people want
// (native range inputs). On the map it adds layers and view/export actions;
// while exploring, the explore controls. Every other tunable
// lives in the debug panel.

import { PARAM_SPECS, type GenParams, type ParamSpec } from '../model/params';
import { LAYERS } from '../render/blueprint/layers';

export interface SidebarHandlers {
  onParamsChange(params: GenParams): void;
  onRegenerate(): void;
  onSeed(seed: string): void;
  onExplore(): void;
  onBack(): void;
  onResetParams(): void;
  onLayerToggle(id: string, on: boolean): void;
  onFitView(): void;
  onExportJson(): void;
  onExportPng(): void;
  onToggleDebug(): void;
}

/** A sidebar slider: one spec, or "Map size" driving width and height together. */
interface MainSlider {
  id: string;
  label: string;
  hint: string;
  spec: ParamSpec;
  keys: (keyof GenParams)[];
  format(v: number): string;
}

const spec = (key: keyof GenParams) => PARAM_SPECS.find((s) => s.key === key)!;
const percent = (v: number) => `${Math.round(v * 100)}%`;

const MAIN_SLIDERS: readonly MainSlider[] = [
  {
    id: 'size',
    label: 'Map size',
    hint: 'Tiles per side',
    spec: spec('width'),
    keys: ['width', 'height'],
    format: (v) => `${v} × ${v}`,
  },
  { id: 'town', label: 'Town size', hint: 'How far the town spreads', spec: spec('townRadius'), keys: ['townRadius'], format: percent },
  { id: 'streets', label: 'Street density', hint: 'How often streets branch', spec: spec('roadDensity'), keys: ['roadDensity'], format: percent },
  { id: 'buildings', label: 'Building density', hint: 'Share of lots built on', spec: spec('buildingDensity'), keys: ['buildingDensity'], format: percent },
  { id: 'water', label: 'Water', hint: 'Lakes and low ground', spec: spec('waterFraction'), keys: ['waterFraction'], format: percent },
  { id: 'rivers', label: 'Rivers', hint: 'Number of rivers', spec: spec('riverCount'), keys: ['riverCount'], format: String },
];

function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  props: Record<string, unknown> = {},
  ...children: (Node | string)[]
): HTMLElementTagNameMap[K] {
  const node: HTMLElementTagNameMap[K] = document.createElement(tag);
  Object.assign(node, props);
  node.append(...children);
  return node;
}

export class Sidebar {
  private params: GenParams;
  private readonly inputs = new Map<string, { input: HTMLInputElement; output: HTMLOutputElement; slider: MainSlider }>();
  private readonly layerBoxes = new Map<string, HTMLInputElement>();
  private readonly seedInput: HTMLInputElement;
  private readonly exploreButton: HTMLButtonElement;
  private readonly debugButton: HTMLButtonElement;
  private exploring = false;
  private readonly body: HTMLElement;

  constructor(
    root: HTMLElement,
    initial: GenParams,
    visible: ReadonlySet<string>,
    handlers: SidebarHandlers,
    /** The explore-mode controls (buttons, help), moved in from the page. */
    exploreControls: HTMLElement,
  ) {
    this.params = { ...initial };

    // Seed
    this.seedInput = el('input', {
      id: 'seed',
      type: 'text',
      spellcheck: false,
      autocomplete: 'off',
      title: 'Edit and press Enter to load this seed',
    });
    this.seedInput.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      const value = this.seedInput.value.trim();
      if (value) handlers.onSeed(value);
    });
    const seedRow = el('div', { className: 'sb-seed' }, el('label', { htmlFor: 'seed', textContent: 'Seed' }), this.seedInput);

    // Main sliders
    const sliders = el('div', { className: 'sb-sliders' });
    for (const slider of MAIN_SLIDERS) {
      const id = `param-${slider.id}`;
      const input = el('input', {
        id,
        type: 'range',
        min: String(slider.spec.min),
        max: String(slider.spec.max),
        step: String(slider.spec.step),
      });
      const output = el('output');
      output.htmlFor.add(id);
      input.addEventListener('input', () => {
        output.value = slider.format(Number(input.value));
      });
      input.addEventListener('change', () => {
        const v = Number(input.value);
        for (const key of slider.keys) this.params[key] = v;
        handlers.onParamsChange({ ...this.params });
      });
      sliders.append(
        el(
          'div',
          { className: 'sb-slider' },
          el('div', { className: 'sb-slider-head' }, el('label', { htmlFor: id, textContent: slider.label, title: slider.hint }), output),
          input,
        ),
      );
      this.inputs.set(slider.id, { input, output, slider });
    }

    // Actions
    const regen = el('button', {
      type: 'button',
      className: 'sb-regenerate',
      textContent: 'Regenerate',
      title: 'Build a new town from a fresh seed',
    });
    regen.addEventListener('click', handlers.onRegenerate);
    this.exploreButton = el('button', {
      type: 'button',
      className: 'primary sb-explore',
      textContent: 'Explore',
      title: 'Spawn as a random character and walk the town',
    });
    this.exploreButton.addEventListener('click', () => (this.exploring ? handlers.onBack() : handlers.onExplore()));
    const reset = el('button', { type: 'button', className: 'sb-link', textContent: 'Reset to defaults' });
    reset.addEventListener('click', handlers.onResetParams);

    // Layers
    const layerList = el('div', { className: 'sb-layers' });
    for (const layer of LAYERS) {
      if (layer.debug) continue;
      const box = el('input', { type: 'checkbox', checked: visible.has(layer.id) });
      box.addEventListener('change', () => handlers.onLayerToggle(layer.id, box.checked));
      layerList.append(el('label', {}, box, layer.label));
      this.layerBoxes.set(layer.id, box);
    }
    const layers = el('details', { className: 'sb-section map-only' }, el('summary', { textContent: 'Layers' }), layerList);

    // View & export
    const tool = (label: string, title: string, onClick: () => void) => {
      const b = el('button', { type: 'button', className: 'secondary', textContent: label, title });
      b.addEventListener('click', onClick);
      return b;
    };
    this.debugButton = tool('Debug panel', 'Show every generator setting and debug views (`)', handlers.onToggleDebug);
    const tools = el(
      'div',
      { className: 'sb-tools map-only' },
      tool('Fit', 'Fit the whole map in view (double-click the map)', handlers.onFitView),
      tool('JSON', 'Download the TownModel as JSON', handlers.onExportJson),
      tool('PNG', 'Download the blueprint as a PNG', handlers.onExportPng),
      this.debugButton,
    );

    const collapse = el('button', {
      type: 'button',
      className: 'sb-collapse',
      title: 'Hide or show the sidebar',
      ariaLabel: 'Toggle sidebar',
    });
    collapse.addEventListener('click', () => this.setOpen(document.body.classList.contains('sidebar-closed')));

    this.body = el(
        'div',
        { className: 'sb-body' },
        el('header', { className: 'sb-header' }, el('h1', { textContent: 'Porchlight' }), el('p', { textContent: 'Town generator' })),
        el('div', { className: 'sb-actions' }, regen, this.exploreButton),
        el('section', { className: 'sb-section sb-explore-section iso-only' }, el('h2', { textContent: 'Explore' }), exploreControls),
        seedRow,
        el('section', { className: 'sb-section' }, el('h2', { textContent: 'Shape' }), sliders, reset),
        layers,
        tools,
      );
    root.append(collapse, this.body);
    // A clicked button or slider shouldn't keep focus: the keys belong to the
    // map and the explore view (Space would press the button again, arrows nudge the slider).
    root.addEventListener('pointerup', (e) => {
      const target = e.target as HTMLElement;
      if (target.matches('button, input[type="range"], input[type="checkbox"]')) target.blur();
    });
    this.setParams(initial);
  }

  /** Reflect params loaded from elsewhere (the URL, the debug panel, a reset). */
  setParams(params: GenParams): void {
    this.params = { ...params };
    for (const { input, output, slider } of this.inputs.values()) {
      input.value = String(params[slider.keys[0]]);
      output.value = slider.format(params[slider.keys[0]]);
    }
  }

  /** Who's signed in, with a way out; pinned to the bottom of the sidebar. */
  private characterRow: HTMLElement | null = null;

  private characterName: HTMLElement | null = null;
  private playingAs: string | null = null;

  /** Who you're playing online as (null: playing alone), with the way to switch. */
  setCharacter(name: string | null, onSwitch: () => void): void {
    this.characterRow?.remove();
    const button = el('button', { type: 'button', className: 'sb-link', textContent: name ? 'Switch' : 'Play online' });
    button.addEventListener('click', onSwitch);
    this.playingAs = name;
    this.characterName = el('span', { className: 'sb-account-name', textContent: name ? `Playing as ${name}` : 'Playing alone' });
    this.characterRow = el('div', { className: 'sb-account' }, this.characterName, button);
    this.body.append(this.characterRow);
  }

  /** How it's going online: how many are in this town, or why you're on your own. */
  setOnline(status: { state: 'joining' } | { state: 'online'; here: number } | { state: 'offline'; why: string }): void {
    if (!this.characterName || !this.playingAs) return;
    const detail =
      status.state === 'joining' ? 'joining…' : status.state === 'online' ? (status.here === 1 ? 'just you here' : `${status.here} here`) : `offline: ${status.why}`;
    this.characterName.textContent = `Playing as ${this.playingAs} · ${detail}`;
    this.characterName.title = this.characterName.textContent;
  }

  setAccount(user: { name: string; email: string | null }, onSignOut: () => void): void {
    const out = el('button', { type: 'button', className: 'sb-link', textContent: 'Sign out' });
    out.addEventListener('click', onSignOut);
    this.body.append(
      el(
        'div',
        { className: 'sb-account' },
        el('span', { className: 'sb-account-name', textContent: user.name, title: user.email ?? user.name }),
        out,
      ),
    );
  }

  setSeed(seed: string): void {
    this.seedInput.value = seed;
  }

  setLoading(loading: boolean): void {
    this.exploreButton.disabled = loading;
    this.syncExploreButton(loading);
  }

  /** Explore turns into Back to map while exploring. */
  setExploring(exploring: boolean): void {
    this.exploring = exploring;
    this.syncExploreButton(false);
  }

  private syncExploreButton(loading: boolean): void {
    this.exploreButton.textContent = loading ? 'Loading…' : this.exploring ? 'Back to map' : 'Explore';
    this.exploreButton.title = this.exploring ? 'Back to the blueprint (Esc)' : 'Spawn as a random character and walk the town';
  }

  setDebugOpen(open: boolean): void {
    this.debugButton.classList.toggle('active', open);
  }

  setOpen(open: boolean): void {
    document.body.classList.toggle('sidebar-closed', !open);
  }
}
