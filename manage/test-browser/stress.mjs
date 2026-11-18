// The map under stress, in a real (headless) Chromium: bench/index.html's
// county, four times as busy as towns can be, drawn and timed frame by frame
// in three views (the whole county, close in on a town, and with trails).
// Fails if drawing a frame takes more than its budget; prints what it took.
//
//   npm run test:browser
//
// SHOT=file.png saves what the last view looked like. Chromium: CHROME_PATH, or Playwright's own (npx playwright-core install
// chromium-headless-shell). The budgets are a few times what a desk takes, for
// a CI runner drawing in software: a real slowdown fails, a slow machine doesn't.

import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { createServer } from 'vite';

/** Milliseconds a frame (95th percentile), by view; and taking in a feed frame (8 towns, 20,000 things). */
const BUDGETS = { county: 25, town: 15, trails: 30 };
const FEED_BUDGET = 10;

const root = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root, configFile: fileURLToPath(new URL('../vite.config.ts', import.meta.url)), server: { port: 0, host: '127.0.0.1' }, logLevel: 'error' });
await server.listen();
const url = `${server.resolvedUrls.local[0]}bench/index.html`;
const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH || undefined });
let failed = false;
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  page.on('pageerror', (e) => console.error('page error:', e));
  await page.goto(url);
  await page.waitForFunction(() => window.__bench, null, { timeout: 120_000 });
  const results = await page.evaluate(() => window.__bench);
  // SHOT=file.png: what the last view looked like.
  if (process.env.SHOT) await page.screenshot({ path: process.env.SHOT });
  if (results.error) throw new Error(results.error);
  for (const [view, r] of Object.entries(results)) {
    const budget = BUDGETS[view];
    const ok = r.p95 <= budget && r.feedAvg <= FEED_BUDGET;
    if (!ok) failed = true;
    console.log(
      `${ok ? 'ok  ' : 'FAIL'} ${view.padEnd(7)} frame avg ${r.avg.toFixed(2)} ms, p95 ${r.p95.toFixed(2)} ms (budget ${budget}), max ${r.max.toFixed(2)} ms; ` +
        `a feed frame ${r.feedAvg.toFixed(2)} ms (budget ${FEED_BUDGET})`,
    );
  }
} finally {
  await browser.close();
  await server.close();
}
process.exit(failed ? 1 : 0);
