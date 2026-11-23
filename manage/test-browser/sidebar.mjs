// Manage's sidebar, end to end in a real (headless) Chromium: the real page,
// against a stand-in server (the routes it asks for, answered here). The towns
// with their people and nothing else; into a town (its congestion by street
// name, then everything out in it, a tab a sort, with no box scrolling on its
// own); a van clicked (followed, a crumb on), its driver and back (in its place,
// never a crumb more); then back
// along the breadcrumbs, each as it was left: the tab, the hour, the van picked.
//
//   npm run test:sidebar
//
// Chromium as for stress.mjs: CHROME_PATH, or Playwright's own.

import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { createServer } from 'vite';

// Each town's buildings: [zone, x0, y0, x1, y1, homes, jobs]; Lanternport's 4th is the whole top half, to hover over.
const town = (key, name, x0, buildings = null) => ({ key, name, x0, y0: 0, width: 64, height: 64, private: false, roads: [[0, 2, 0, 32, 64, 32]], roadNames: ['High Street'], zoning: null, buildings });
const flats = [0, 1, 2, 3].map((k) => (k === 3 ? [0, 0, 0, 64, 31, 12, 0] : [0, 70 + k, 70, 71 + k, 71, 1, 0]));
const county = { sim: true, towns: [town('seed=a', 'Lanternport', 0, flats), town('seed=b', 'Porchlight', 80)], spaces: [], highways: [] };
const summary = (people) => ({ clock: '8:05 AM', people, out: 10, cars: 5, trips: { started: 0, finished: 0, failed: 0 }, byHour: Array.from({ length: 24 }, () => [0, 0, 0]), meanTrip: [0, 0, 0], parking: [0, 1], busiestRoads: [], waits: [], problems: 0, gridlockBroken: 0, mainCrossings: 0, hunts: 0 });
// Lanternport: three cars (two residents', one commuter's), a van, two people. Porchlight: one car.
const lives = {
  'seed=a': { town: 'seed=a', clock: 0, time: '8:05 AM', inside: [], offMap: [], gates: [], stops: [], summary: summary(1234), kinds: { cars: 'rrvc', people: 'w-s' } },
  'seed=b': { town: 'seed=b', clock: 0, time: '8:05 AM', inside: [], offMap: [], gates: [], stops: [], summary: summary(5678), kinds: { cars: 'r', people: '' } },
};
const congestion = (key, ago) => ({
  town: key,
  ago,
  hours: Array.from({ length: 24 }, (_, i) => ({ from: i * 3600_000, time: `${(i % 12) + 1}:00 AM`, cars: 10 + i, hold: 60 * (i + 1) })),
  streets: [{ road: 0, name: 'High Street', stretch: 'High Street, Elm Street to Oak Avenue', kind: 0, cars: 9, hold: 300, mean: 33, points: [0, 32, 64, 32] }],
});
// Who's in each record: a car's id is its record + 20, a person's + 40; the van's driver is person 40.
const focus = (key, kind, id) => ({
  town: key, kind, id, clock: 0, time: '8:05 AM',
  who: kind === 'car' ? 'A van' : 'A worker',
  doing: kind === 'car' ? 'Delivering on High Street' : 'Driving the van',
  owner: kind === 'car' ? 40 : undefined, car: kind === 'person' ? 22 : undefined, van: kind === 'car' ? -1 : undefined, bus: -1,
  drawn: kind, slot: 0, at: [10, 32], tree: [], queue: [{ at: null, time: null, what: 'Back to the depot', now: false }], path: [], dest: null,
});

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
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  page.on('pageerror', (e) => {
    console.error('page error:', e);
    failed = true;
  });
  const asked = [];
  const json = (route, body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  // Everything else: nothing to say. First: the last route added wins.
  await page.route(/\/(mp|auth)\//, (r) => json(r, { error: 'Not here.' }, 404));
  await page.route('**/auth/me', (r) => json(r, { name: 'Tester' }));
  await page.route('**/mp/traffic/county', (r) => json(r, county));
  await page.route('**/mp/sim/life?*', (r) => json(r, lives[new URL(r.request().url()).searchParams.get('town')]));
  await page.route('**/mp/sim/congestion?*', (r) => {
    const q = new URL(r.request().url()).searchParams;
    asked.push(`congestion ${q.get('town')} ${q.get('ago')}`);
    return json(r, congestion(q.get('town'), Number(q.get('ago'))));
  });
  await page.route('**/mp/sim/focus?*', (r) => {
    const q = new URL(r.request().url()).searchParams;
    const kind = q.get('kind');
    const id = q.has('id') ? Number(q.get('id')) : Number(q.get('slot')) + (kind === 'car' ? 20 : 40);
    if (q.has('brief')) return json(r, { line: `${kind === 'car' ? 'Car' : 'Person'} ${id}: out and about` });
    return json(r, focus(q.get('town'), kind, id));
  });
  await page.goto(`${base}manage/`);
  const panel = page.locator('.panel-body');
  const crumbs = () => page.locator('.crumbs li').allTextContents().then((t) => t.join(' › '));

  // The county: the towns, each with its people, and nothing else.
  await page.waitForFunction(() => document.querySelector('[aria-label="Towns"]')?.textContent?.includes('1,234'));
  const rows = await page.locator('[aria-label="Towns"] li').allTextContents();
  check(rows.join(' | ') === 'Lanternport1,234 | Porchlight5,678', `the towns, each with its people (${rows.join(' | ')})`);
  check((await panel.locator('h2').count()) === 0 && (await crumbs()) === 'County', 'and nothing else: no layers, kinds or players');

  // Into Lanternport.
  await page.locator('[aria-label="Towns"]').getByRole('button', { name: /^Lanternport/ }).click();
  await page.locator('[aria-label="Most congested streets"] li').first().waitFor();
  check((await crumbs()) === 'County › Lanternport', 'a town is a crumb on');
  const worst = await page.locator('[aria-label="Most congested streets"] li').first().textContent();
  const between = await page.locator('[aria-label="Most congested streets"] li .between').first().textContent();
  check(worst.startsWith('1. High Street') && between === 'Elm Street to Oak Avenue', `its most congested road by name, the streets at its ends under it (${worst})`);
  const tabs = await page.getByRole('tab').allTextContents();
  check(tabs.join(' | ') === 'Cars 3 | Vans 1 | People 2', `under it, a tab a sort out now (${tabs.join(' | ')})`);
  await page.waitForFunction(() => document.querySelector('[role="tabpanel"]')?.textContent?.includes('Car 20: out and about'));
  check(true, "the Cars tab lists each, with the simulation's line on it");
  const scrolling = await page.evaluate(() =>
    [...document.querySelectorAll('.panel-body *')].filter((e) => ['auto', 'scroll'].includes(getComputedStyle(e).overflowY) || ['auto', 'scroll'].includes(getComputedStyle(e).overflowX)).length,
  );
  check(scrolling === 0, `no box in the sidebar scrolls on its own (${scrolling})`);

  // A building hovered on the map: its number, as its crumb has it.
  const map = await page.locator('canvas.map').boundingBox();
  await page.mouse.move(map.x + map.width / 2, map.y + map.height / 2 - 40);
  await page.waitForFunction(() => document.querySelector('.tip')?.textContent?.startsWith('Building'), null, { timeout: 5000 }).catch(() => {});
  const tip = (await page.locator('.tip').textContent().catch(() => '')) ?? '';
  check(tip.startsWith('Building 3 · '), `a building hovered: its number (${tip})`);
  await page.mouse.move(map.x + 5, map.y + map.height - 5);

  // An hour back, the Vans tab, the van: followed, a crumb on.
  await page.locator('.congestion .bar').nth(20).click();
  await page.waitForFunction(() => document.querySelector('.congestion .bar:nth-child(21)')?.classList.contains('on'));
  check(asked.includes('congestion seed=a 3'), 'an hour picked: its congestion asked for');
  await page.getByRole('tab', { name: /Vans/ }).click();
  await page.getByRole('tabpanel').getByRole('button', { name: /Car 22/ }).click();
  await page.waitForFunction(() => document.querySelector('.entity .doing')?.textContent?.includes('Delivering'));
  check((await crumbs()) === 'County › Lanternport › Car 22', 'the van clicked: a crumb on');
  check((await page.getByRole('button', { name: 'Follow' }).getAttribute('aria-pressed')) === 'true', 'and followed on the map');
  await page.getByRole('button', { name: 'Its driver' }).click();
  await page.waitForFunction(() => document.querySelector('.entity .doing')?.textContent?.includes('Driving the van'));
  check((await crumbs()) === 'County › Lanternport › Person 40', 'its driver: in its place, still under the town');
  // Back and forth between the van and its driver: never more crumbs.
  for (let k = 0; k < 3; k++) {
    await page.getByRole('button', { name: 'Their car' }).click();
    await page.waitForFunction(() => document.querySelector('.entity .doing')?.textContent?.includes('Delivering'));
    await page.getByRole('button', { name: 'Its driver' }).click();
    await page.waitForFunction(() => document.querySelector('.entity .doing')?.textContent?.includes('Driving the van'));
  }
  check((await crumbs()) === 'County › Lanternport › Person 40', `toggled van, driver, van, …: still one crumb under the town (${await crumbs()})`);
  await page.getByRole('button', { name: 'Their car' }).click();
  await page.waitForFunction(() => document.querySelector('.entity .doing')?.textContent?.includes('Delivering'));
  check((await crumbs()) === 'County › Lanternport › Car 22' && (await page.getByRole('button', { name: 'Follow' }).getAttribute('aria-pressed')) === 'true', 'back to the van: followed');

  // Back to the town: as left.
  await page.locator('.crumbs').getByRole('button', { name: 'Lanternport' }).click();
  await page.getByRole('tabpanel').waitFor();
  check((await page.getByRole('tab', { selected: true }).textContent()).startsWith('Vans'), 'back to the town: its Vans tab as left');
  await page.waitForFunction(() => document.querySelector('[role="tabpanel"] li.sel')?.textContent?.includes('Car 22'), null, { timeout: 5000 }).catch(() => {});
  check((await page.locator('[role="tabpanel"] li.sel').textContent()).includes('Car 22'), 'with the van picked from it highlighted');
  await page.waitForFunction(() => document.querySelector('.congestion .bar:nth-child(21)')?.classList.contains('on'));
  check(true, 'and the hour it was looking at');

  // Back to the county, and into each town again.
  await page.locator('.crumbs').getByRole('button', { name: 'County' }).click();
  await page.locator('[aria-label="Towns"]').waitFor();
  check((await page.locator('[aria-label="Towns"] li.sel').textContent()).startsWith('Lanternport'), 'back to the county: the town last in highlighted');
  await page.locator('[aria-label="Towns"]').getByRole('button', { name: /^Lanternport/ }).click();
  await page.getByRole('tabpanel').waitFor();
  check((await page.getByRole('tab', { selected: true }).textContent()).startsWith('Vans'), 'into it again: still as left');
  await page.locator('.crumbs').getByRole('button', { name: 'County' }).click();
  await page.locator('[aria-label="Towns"]').getByRole('button', { name: /^Porchlight/ }).click();
  await page.getByRole('tabpanel').waitFor();
  check((await page.getByRole('tab', { selected: true }).textContent()).startsWith('Cars') && (await crumbs()) === 'County › Porchlight', 'another town: its own, from the first tab');
} catch (err) {
  console.error('FAIL', err);
  failed = true;
} finally {
  await browser.close();
  await server.close();
}
console.log(failed ? 'sidebar: failed' : 'sidebar: all ok');
process.exit(failed ? 1 : 0);
