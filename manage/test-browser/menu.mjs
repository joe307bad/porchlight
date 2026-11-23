// Manage's right-click menu, end to end in a real (headless) Chromium: the
// real page, against a stand-in server (the routes it asks for, answered
// here). Right-click a spot in a town, see it named by the street it's by,
// pick "Take … here" and "Directions here for …", and check what's sent to
// the server for the game (POST /mp/goto). Also that a spot outside every town
// offers nothing, and that with nobody playing the menu says so.
//
//   npm run test:menu
//
// Chromium as for stress.mjs: CHROME_PATH, or Playwright's own.

import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { createServer } from 'vite';

const town = {
  key: 'seed=menu',
  name: 'Lanternport',
  x0: 0,
  y0: 0,
  width: 64,
  height: 64,
  private: false,
  roads: [
    [0, 2, 0, 32, 64, 32],
    [1, 1, 32, 0, 32, 64],
  ],
  roadNames: ['High Street', 'Elm Street'],
  zoning: null,
  buildings: null,
};
const county = { sim: true, towns: [town], spaces: [], highways: [] };

const root = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root, configFile: fileURLToPath(new URL('../vite.config.ts', import.meta.url)), server: { port: 0, host: '127.0.0.1', proxy: {} }, logLevel: 'error' });
await server.listen();
const base = server.resolvedUrls.local[0];
const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH || undefined });
let failed = false;
const check = (ok, what) => {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`);
  if (!ok) failed = true;
};
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  page.on('pageerror', (e) => console.error('page error:', e));
  let playing = [{ id: 'c1', name: 'Sam', town: 'seed=menu' }];
  const sent = [];
  const json = (route, body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  // Everything else the page asks the server for: nothing to say (no feed, no simulation). First: the last route added wins.
  await page.route(/\/(mp|auth)\//, (r) => json(r, { error: 'Not here.' }, 404));
  await page.route('**/auth/me', (r) => json(r, { name: 'Tester' }));
  await page.route('**/mp/traffic/county', (r) => json(r, county));
  await page.route('**/mp/goto', (r) => {
    if (r.request().method() === 'GET') return json(r, { playing });
    sent.push(r.request().postDataJSON());
    return json(r, { ok: true });
  });
  await page.goto(`${base}manage/`);
  const map = page.locator('canvas.map');
  await map.waitFor();
  const box = await map.boundingBox();
  // Fitted to the county: the town on the left. Where on screen is a spot in it? Asked of the page's own camera.
  const toScreen = async (x, y) => {
    // The county's towns and highways (here the one town, 64 × 64 tiles) fill the canvas less a 24 px pad, centred.
    const w = 64, h = 64, pad = 24;
    const scale = Math.min((box.width - pad * 2) / w, (box.height - pad * 2) / h);
    return { x: box.x + box.width / 2 + (x - w / 2) * scale, y: box.y + box.height / 2 + (y - h / 2) * scale };
  };

  // By Elm Street (x 32), well below High Street (y 32).
  let at = await toScreen(33, 50);
  await page.mouse.click(at.x, at.y, { button: 'right' });
  const menu = page.locator('.spot-menu');
  await menu.waitFor();
  check((await menu.locator('.spot-title').textContent()) === 'Lanternport, near Elm Street', 'the spot is named by its town and the street it is by');
  await menu.getByRole('menuitem', { name: 'Take Sam here' }).click();
  await page.waitForFunction(() => document.querySelector('.spot-said')?.textContent?.includes('on the way'));
  check(sent.length === 1 && sent[0].character === 'c1' && sent[0].town === 'seed=menu' && sent[0].how === 'transport', 'Take Sam here sends Sam there');
  check(Math.abs(sent[0].x - 33) < 1 && Math.abs(sent[0].y - 50) < 1, `to the spot clicked, in the town's own tiles (${sent[0].x}, ${sent[0].y})`);
  check(sent[0].label === 'near Elm Street', 'with what is there, for the directions');
  await page.locator('.spot-menu').waitFor({ state: 'detached' });

  at = await toScreen(10, 33);
  await page.mouse.click(at.x, at.y, { button: 'right' });
  await menu.getByRole('menuitem', { name: 'Directions here for Sam' }).click();
  await page.waitForFunction(() => document.querySelector('.spot-said')?.textContent?.includes('Directions sent'));
  check(sent.length === 2 && sent[1].how === 'directions' && sent[1].label === 'near High Street', 'Directions here for Sam sends the way to a spot by High Street');
  await page.keyboard.press('Escape');
  await page.locator('.spot-menu').waitFor({ state: 'detached' });

  // Outside every town: nothing to offer.
  at = await toScreen(-1.5, 30);
  await page.mouse.click(at.x, at.y, { button: 'right' });
  await menu.waitFor();
  check((await menu.locator('.spot-title').textContent()) === 'Not in a town' && (await menu.getByRole('menuitem').count()) === 0, 'outside every town: no one to send');
  await page.keyboard.press('Escape');

  // Nobody playing.
  playing = [];
  at = await toScreen(33, 50);
  await page.mouse.click(at.x, at.y, { button: 'right' });
  await page.waitForFunction(() => document.querySelector('.spot-none')?.textContent?.includes('None of your characters'));
  check(true, 'nobody playing: the menu says so');
  assert.equal(sent.length, 2, 'nothing more sent');
} catch (err) {
  console.error('FAIL', err);
  failed = true;
} finally {
  await browser.close();
  await server.close();
}
console.log(failed ? 'menu: failed' : 'menu: all ok');
process.exit(failed ? 1 : 0);
