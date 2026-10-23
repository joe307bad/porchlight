import { generateTown } from './gen';
import { DEFAULT_PARAMS, type GenParams } from './model/params';
import type { TownModel } from './model/types';
import { createRng } from './core/rng';
import { BlueprintRenderer } from './render/blueprint/renderer';
import type { IsoView } from './render/iso/view';
import { exportJson, exportPng } from './ui/export';
import { describeTile } from './ui/hover';
import { Panel } from './ui/panel';
import { randomSeed } from './ui/seed';
import { readUrlState, writeUrlState } from './ui/urlState';
import { attachViewport } from './ui/viewport';
import './style.css';

const canvas = document.querySelector<HTMLCanvasElement>('#map')!;
const seedInput = document.querySelector<HTMLInputElement>('#seed')!;
const regenButton = document.querySelector<HTMLButtonElement>('#regenerate')!;
const jsonButton = document.querySelector<HTMLButtonElement>('#export-json')!;
const pngButton = document.querySelector<HTMLButtonElement>('#export-png')!;
const hoverEl = document.querySelector<HTMLDivElement>('#hover')!;
const isoHost = document.querySelector<HTMLDivElement>('#iso')!;
const exploreButton = document.querySelector<HTMLButtonElement>('#explore')!;
const dayNightButton = document.querySelector<HTMLButtonElement>('#daynight')!;
const newCharButton = document.querySelector<HTMLButtonElement>('#newchar')!;
const backButton = document.querySelector<HTMLButtonElement>('#back')!;
const walkModeButton = document.querySelector<HTMLButtonElement>('#walkmode')!;
const autoWalkButton = document.querySelector<HTMLButtonElement>('#autowalk')!;
const statusEl = document.querySelector<HTMLDivElement>('#status')!;
const turnLeftButton = document.querySelector<HTMLButtonElement>('#turnleft')!;
const turnRightButton = document.querySelector<HTMLButtonElement>('#turnright')!;
const lookAheadButton = document.querySelector<HTMLButtonElement>('#lookahead')!;

const renderer = new BlueprintRenderer(canvas);
const initial = readUrlState();
let seed = initial.seed ?? randomSeed();
let params: GenParams = initial.params;
let model!: TownModel; // assigned by the first regenerate() below
/** Until the user pans or zooms, keep the whole map fitted to the window. */
let autoFit = true;
let iso: IsoView | null = null;
let exploring = false;
let isoTown: TownModel | null = null;

let frame = 0;
function requestDraw(): void {
  if (frame) return;
  frame = requestAnimationFrame(() => {
    frame = 0;
    renderer.render(model);
  });
}

function regenerate(nextSeed: string, nextParams: GenParams): void {
  const sizeChanged = !model || model.width !== nextParams.width || model.height !== nextParams.height;
  seed = nextSeed;
  params = nextParams;
  const t0 = performance.now();
  try {
    model = generateTown(seed, params, { check: import.meta.env.DEV });
    const counts = `${model.buildings.length} buildings · ${model.roads.edges.length} roads · ${model.lots.length} lots`;
    statusEl.textContent = `${counts} · generated in ${(performance.now() - t0).toFixed(0)} ms`;
    statusEl.classList.remove('error');
  } catch (err) {
    console.error(err);
    statusEl.textContent = `invariant failed: ${(err as Error).message}`;
    statusEl.classList.add('error');
    model = generateTown(seed, params); // still show what we got
  }
  seedInput.value = seed;
  writeUrlState(seed, params);
  renderer.marker = null; // a new town; the character's spot no longer applies
  renderer.route = [];
  renderer.goal = null;
  if (autoFit || sizeChanged) renderer.fit(model);
  requestDraw();
  if (exploring && iso) {
    // A new town: drop the old walk and start afresh at the new town's spawn point.
    iso.setTown(model);
    isoTown = model;
    void spawnCharacter();
  } else if (iso?.isAuto) {
    // Regenerated from the map: stop the old town's auto-walk running behind it.
    iso.setAuto(false);
    syncAutoWalkButton();
  }
}

// Explore mode: the 3D town, loaded on first use so Three.js stays out of the blueprint page.

async function spawnCharacter(): Promise<void> {
  const { lookFrom } = await import('./render/iso/view');
  iso?.spawn(lookFrom(createRng(randomSeed())));
  syncAutoWalkButton();
}

function syncAutoWalkButton(): void {
  const on = iso?.isAuto ?? false;
  autoWalkButton.textContent = on ? 'Stop auto walk' : 'Auto walk';
  autoWalkButton.classList.toggle('active', on);
}

async function explore(): Promise<void> {
  exploreButton.disabled = true;
  exploreButton.textContent = 'Loading…';
  try {
    if (!iso) {
      isoHost.hidden = false;
      const { IsoView } = await import('./render/iso/view');
      iso = await IsoView.create(isoHost);
    }
    exploring = true;
    document.body.classList.add('exploring');
    isoHost.hidden = false;
    iso.setBackground(false);
    if (isoTown !== model) {
      iso.setTown(model);
      isoTown = model;
      await spawnCharacter();
    } else if (!iso.playerPosition) {
      await spawnCharacter();
    }
    // Same town as last time: carry on as the same character, where you left off.
  } catch (err) {
    console.error(err);
    statusEl.textContent = `couldn't start explore mode: ${(err as Error).message}`;
    statusEl.classList.add('error');
    leaveExplore();
  } finally {
    exploreButton.disabled = false;
    exploreButton.textContent = 'Explore';
  }
}

function leaveExplore(): void {
  exploring = false;
  document.body.classList.remove('exploring');
  isoHost.hidden = true;
  iso?.setBackground(true); // keep the town (and an auto-walking character) going behind the map
  renderer.resize();
  // Mark where the character is on the blueprint and bring it into view.
  const at = iso?.playerPosition ?? null;
  renderer.marker = at;
  if (at) {
    autoFit = false;
    renderer.centerOn(at);
  }
  requestDraw();
}

function toggleNight(): void {
  if (!iso) return;
  iso.setNight(!iso.isNight);
  dayNightButton.textContent = iso.isNight ? 'Day' : 'Night';
}

function toggleAutoWalk(): void {
  if (!iso) return;
  iso.setAuto(!iso.isAuto);
  syncAutoWalkButton();
}

function toggleWalkMode(): void {
  if (!iso) return;
  iso.setStreetsOnly(!iso.isStreetsOnly);
  walkModeButton.textContent = iso.isStreetsOnly ? 'Walk: streets only' : 'Walk: anywhere';
}

// Dev-only handle for poking at explore mode from the console or test tooling.
if (import.meta.env.DEV) {
  (window as unknown as { porchlight: object }).porchlight = { iso: () => iso, model: () => model };
}

/**
 * While the map is showing, follow the explore-mode character: move the pin
 * (and redraw auto-walk's route) as they walk, and keep them in view.
 */
let lastFollow = 0;
function follow(now: number): void {
  requestAnimationFrame(follow);
  if (exploring || !iso || isoTown !== model || now - lastFollow < 66) return;
  const at = iso.playerPosition;
  if (!at) return;
  const was = renderer.marker;
  if (was && Math.abs(was.x - at.x) < 1e-4 && Math.abs(was.y - at.y) < 1e-4) return;
  lastFollow = now;
  renderer.marker = at;
  renderer.route = iso.route;
  renderer.goal = iso.goal;
  if (!renderer.isInView(at, 0.12)) {
    autoFit = false;
    renderer.centerOn(at);
  }
  requestDraw();
}
requestAnimationFrame(follow);

exploreButton.addEventListener('click', () => void explore());
walkModeButton.addEventListener('click', toggleWalkMode);
autoWalkButton.addEventListener('click', toggleAutoWalk);
backButton.addEventListener('click', leaveExplore);
dayNightButton.addEventListener('click', toggleNight);
newCharButton.addEventListener('click', () => void spawnCharacter());
turnLeftButton.addEventListener('click', () => iso?.rotate(-1));
turnRightButton.addEventListener('click', () => iso?.rotate(1));
lookAheadButton.addEventListener('click', () => iso?.lookAlongWalk());
window.addEventListener('keydown', (e) => {
  if (!exploring || e.target instanceof HTMLInputElement) return;
  if (e.code === 'KeyN') toggleNight();
  if (e.code === 'KeyR') toggleWalkMode();
  if (e.code === 'KeyT') toggleAutoWalk();
  if (e.code === 'KeyC') void spawnCharacter();
  if (e.code === 'Escape') leaveExplore();
});

function fit(): void {
  autoFit = true;
  renderer.fit(model);
  requestDraw();
}

const panel = new Panel(params, renderer.visible, {
  onParamsChange: (p) => regenerate(seed, p),
  onRegenerate: () => regenerate(randomSeed(), params),
  onResetParams: () => {
    panel.setParams(DEFAULT_PARAMS);
    regenerate(seed, { ...DEFAULT_PARAMS });
  },
  onLayerToggle: (id, on) => {
    if (on) renderer.visible.add(id);
    else renderer.visible.delete(id);
    requestDraw();
  },
  onFitView: fit,
  onExportJson: () => exportJson(model),
  onExportPng: () => exportPng(model, renderer.visible),
});

regenButton.addEventListener('click', () => regenerate(randomSeed(), params));
jsonButton.addEventListener('click', () => exportJson(model));
pngButton.addEventListener('click', () => exportPng(model, renderer.visible));
seedInput.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter') return;
  const value = seedInput.value.trim();
  if (value) regenerate(value, params);
});

window.addEventListener('hashchange', () => {
  const state = readUrlState();
  panel.setParams(state.params);
  regenerate(state.seed ?? randomSeed(), state.params);
});

attachViewport(canvas, renderer.camera, {
  onChange: () => {
    autoFit = false;
    requestDraw();
  },
  onHover: (world) => {
    hoverEl.textContent = describeTile(model, world);
  },
  onDoubleClick: fit,
});

new ResizeObserver(() => {
  renderer.resize();
  if (autoFit && model) renderer.fit(model);
  requestDraw();
}).observe(canvas);

renderer.resize();
regenerate(seed, params);
