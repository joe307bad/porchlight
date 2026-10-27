import { requireSignIn } from './auth/gate';
import { signOut } from './auth/session';
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
import { Sidebar } from './ui/sidebar';
import { encodeUrlState, readUrlState, writeUrlState } from './ui/urlState';
import { forgetCharacter, pickCharacter, rememberedCharacter } from './ui/characterPicker';
import type { Multiplayer } from './net/multiplayer';
import { attachViewport } from './ui/viewport';
import './style.css';

// Nothing starts until someone is signed in (when sign-in is configured).
const user = await requireSignIn();

const canvas = document.querySelector<HTMLCanvasElement>('#map')!;
const sidebarEl = document.querySelector<HTMLElement>('#sidebar')!;
const hoverEl = document.querySelector<HTMLDivElement>('#hover')!;
const isoHost = document.querySelector<HTMLDivElement>('#iso')!;
const dayNightButton = document.querySelector<HTMLButtonElement>('#daynight')!;
const newCharButton = document.querySelector<HTMLButtonElement>('#newchar')!;
const exploreControls = document.querySelector<HTMLElement>('#explore-controls')!;
const walkModeButton = document.querySelector<HTMLButtonElement>('#walkmode')!;
const autoWalkButton = document.querySelector<HTMLButtonElement>('#autowalk')!;
const carButton = document.querySelector<HTMLButtonElement>('#car')!;
const statusEl = document.querySelector<HTMLDivElement>('#status')!;
const turnLeftButton = document.querySelector<HTMLButtonElement>('#turnleft')!;
const turnRightButton = document.querySelector<HTMLButtonElement>('#turnright')!;
const lookAheadButton = document.querySelector<HTMLButtonElement>('#lookahead')!;
const closeUpButton = document.querySelector<HTMLButtonElement>('#closeup')!;
const tiltDownButton = document.querySelector<HTMLButtonElement>('#tiltdown')!;
const tiltUpButton = document.querySelector<HTMLButtonElement>('#tiltup')!;

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
/** Explore mode is shared with everyone in the same town when signed in (src/net/multiplayer.ts). */
const online = user !== null && import.meta.env.VITE_MULTIPLAYER !== 'false';
let net: Multiplayer | null = null;
/** Asked who's playing (choosing to play alone counts). */
let asked = false;

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
  sidebar.setSeed(seed);
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
    void spawnCharacter().then(enterTown);
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

/** A new look for your character (C): online, it's saved with them. */
async function newLook(): Promise<void> {
  await spawnCharacter();
  const look = iso?.lookModel;
  if (look) net?.lookChanged(look);
}

/** Online: ask (once) who's playing, then join their town's room. */
async function connect(): Promise<void> {
  if (!online || asked || !iso) return;
  asked = true;
  const { lookFrom } = await import('./render/iso/view');
  const character = (await rememberedCharacter()) ?? (await pickCharacter(() => lookFrom(createRng(randomSeed())).model));
  sidebar.setCharacter(character?.name ?? null, () => void switchCharacter());
  if (!character || !iso) return;
  const { Multiplayer } = await import('./net/multiplayer');
  net = new Multiplayer(iso, character);
  net.onStatus = (status) => sidebar.setOnline(status);
  iso.teleportHook = (x, z) => net?.teleport(x, z) ?? false;
}

/** Play the town on screen with everyone else in it (no-op when it's already the one). */
function enterTown(): void {
  if (isoTown === model) net?.enter({ key: encodeUrlState(seed, params), width: model.width, height: model.height });
}

async function switchCharacter(): Promise<void> {
  await net?.close();
  net = null;
  if (iso) iso.teleportHook = null;
  forgetCharacter();
  asked = false;
  await connect();
  enterTown();
}

function syncAutoWalkButton(): void {
  const on = iso?.isAuto ?? false;
  const what = iso?.isDriving ? 'auto drive' : 'auto walk';
  autoWalkButton.textContent = on ? `Stop ${what}` : what[0].toUpperCase() + what.slice(1);
  autoWalkButton.classList.toggle('active', on);
}

async function explore(): Promise<void> {
  sidebar.setLoading(true);
  try {
    if (!iso) {
      isoHost.hidden = false;
      const { IsoView } = await import('./render/iso/view');
      iso = await IsoView.create(isoHost);
      iso.onCarChange = syncCarButton;
      iso.onAutoChange = syncAutoWalkButton;
      iso.onPadAction = (action) => {
        if (action === 'menu') {
          if (exploring) leaveExplore();
          else void explore();
          return;
        }
        if (!exploring) return;
        if (action === 'car') toggleCar();
        if (action === 'auto') toggleAutoWalk();
        if (action === 'closeUp') toggleCloseUp();
        if (action === 'night') toggleNight();
        if (action === 'newCharacter') void newLook();
      };
    }
    exploring = true;
    document.body.classList.add('exploring');
    sidebar.setExploring(true);
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
    // Online: who's playing (asked once), then into this town's room.
    void connect()
      .then(enterTown)
      .catch((err) => console.error('multiplayer:', err));
  } catch (err) {
    console.error(err);
    statusEl.textContent = `couldn't start explore mode: ${(err as Error).message}`;
    statusEl.classList.add('error');
    leaveExplore();
  } finally {
    sidebar.setLoading(false);
  }
}

function leaveExplore(): void {
  exploring = false;
  document.body.classList.remove('exploring');
  sidebar.setExploring(false);
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

/** Into the nearest car, or out of this one (the view handles G itself). */
function toggleCar(): void {
  if (!iso) return;
  if (!iso.toggleCar()) {
    carButton.classList.add('nudge'); // no car near enough to get into, or nowhere to get out
    setTimeout(() => carButton.classList.remove('nudge'), 400);
  }
}

/** In or out of a car: the car button follows, and auto walk becomes auto drive (or back). */
function syncCarButton(driving: boolean): void {
  carButton.textContent = driving ? 'Get out' : 'Get in car';
  carButton.classList.toggle('active', driving);
  syncAutoWalkButton();
}

function toggleCloseUp(): void {
  if (!iso) return;
  iso.setCameraMode(iso.cameraMode === 'close' ? 'iso' : 'close');
  const close = iso.cameraMode === 'close';
  closeUpButton.textContent = close ? 'Overview' : 'Close up';
  closeUpButton.classList.toggle('active', close);
}

function toggleWalkMode(): void {
  if (!iso) return;
  iso.setStreetsOnly(!iso.isStreetsOnly);
  walkModeButton.textContent = iso.isStreetsOnly ? 'Walk: streets only' : 'Walk: anywhere';
}

// Dev-only handle for poking at explore mode from the console or test tooling.
if (import.meta.env.DEV) {
  (window as unknown as { porchlight: object }).porchlight = { iso: () => iso, model: () => model, net: () => net?.debug() ?? 'not online' };
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

walkModeButton.addEventListener('click', toggleWalkMode);
autoWalkButton.addEventListener('click', toggleAutoWalk);
carButton.addEventListener('click', toggleCar);
dayNightButton.addEventListener('click', toggleNight);
newCharButton.addEventListener('click', () => void newLook());
turnLeftButton.addEventListener('click', () => iso?.rotate(-1));
turnRightButton.addEventListener('click', () => iso?.rotate(1));
lookAheadButton.addEventListener('click', () => iso?.lookAlongWalk());
closeUpButton.addEventListener('click', toggleCloseUp);
tiltDownButton.addEventListener('click', () => iso?.tilt(-1));
tiltUpButton.addEventListener('click', () => iso?.tilt(1));
window.addEventListener('keydown', (e) => {
  if (!exploring || e.target instanceof HTMLInputElement) return;
  if (e.code === 'KeyN') toggleNight();
  if (e.code === 'KeyR') toggleWalkMode();
  if (e.code === 'KeyT') toggleAutoWalk();
  if (e.code === 'KeyV' && !e.repeat) toggleCloseUp();
  if (e.code === 'KeyC') void newLook();
  if (e.code === 'Escape') leaveExplore();
});

function fit(): void {
  autoFit = true;
  renderer.fit(model);
  requestDraw();
}

/** Params changed in one control surface: mirror them in the other and rebuild. */
function changeParams(next: GenParams): void {
  panel.setParams(next);
  sidebar.setParams(next);
  regenerate(seed, next);
}

function resetParams(): void {
  changeParams({ ...DEFAULT_PARAMS });
}

function toggleLayer(id: string, on: boolean): void {
  if (on) renderer.visible.add(id);
  else renderer.visible.delete(id);
  requestDraw();
}

const DEBUG_KEY = 'porchlight.debugPanel';
function setDebugOpen(open: boolean): void {
  panel.setOpen(open);
  sidebar.setDebugOpen(open);
  try {
    localStorage.setItem(DEBUG_KEY, open ? '1' : '0');
  } catch {
    // storage blocked; the panel just won't be remembered
  }
}

const panel = new Panel(params, renderer.visible, {
  onParamsChange: changeParams,
  onResetParams: resetParams,
  onLayerToggle: toggleLayer,
});

const sidebar: Sidebar = new Sidebar(sidebarEl, params, renderer.visible, {
  onParamsChange: changeParams,
  onRegenerate: () => regenerate(randomSeed(), params),
  onSeed: (value) => regenerate(value, params),
  onExplore: () => void explore(),
  onBack: leaveExplore,
  onResetParams: resetParams,
  onLayerToggle: toggleLayer,
  onFitView: fit,
  onExportJson: () => exportJson(model),
  onExportPng: () => exportPng(model, renderer.visible),
  onToggleDebug: () => setDebugOpen(!panel.isOpen),
}, exploreControls);
if (user) sidebar.setAccount(user, signOut);
// Small screens start with the map, not the controls.
if (window.matchMedia('(max-width: 720px)').matches) sidebar.setOpen(false);
try {
  if (localStorage.getItem(DEBUG_KEY) === '1') setDebugOpen(true);
} catch {
  // storage blocked; start with the debug panel hidden
}

window.addEventListener('keydown', (e) => {
  if (exploring || e.target instanceof HTMLInputElement) return;
  if (e.code === 'Backquote') setDebugOpen(!panel.isOpen);
});

window.addEventListener('hashchange', () => {
  const state = readUrlState();
  panel.setParams(state.params);
  sidebar.setParams(state.params);
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
