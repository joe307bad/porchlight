// The desktop app serves Manage itself (unity/.../Game/Net/ManageLink.cs) and passes
// on to the server only the routes it lists. A route Manage asks for that isn't
// listed gets "Not found" in the app, and Manage then shows nothing: no lights, no
// one in the buildings, nothing on a click. So every server route the page fetches
// must be on the app's list.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';

const root = new URL('../../', import.meta.url);
const read = (path: string) => readFileSync(new URL(path, root), 'utf8');

test("the desktop app passes on every server route Manage asks for", () => {
  const link = read('unity/Assets/Porchlight/Runtime/Game/Net/ManageLink.cs');
  const list = link.match(/HashSet<string> PASSED = new HashSet<string>\s*\{([^}]*)\}/);
  assert.ok(list, "ManageLink.cs's PASSED list");
  const passed = new Set([...list[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]));
  const posted = new Set([...link.matchAll(/const string \w+ = "(\/[^"]+)"/g)].map((m) => m[1]));

  const asked = new Set<string>();
  const src = new URL('manage/src/', root);
  for (const file of readdirSync(src).filter((f) => /\.tsx?$/.test(f) && !f.endsWith('.test.ts'))) {
    for (const m of read(`manage/src/${file}`).matchAll(/fetch\(\s*['`](\/(?:mp|auth)\/[^'`?$]+)/g)) asked.add(m[1]);
  }
  assert.ok(asked.size >= 6, `found Manage's requests (${[...asked].join(', ')})`);
  for (const route of asked) assert.ok(passed.has(route) || posted.has(route), `${route}: Manage asks for it, but the desktop app doesn't pass it on`);
});
