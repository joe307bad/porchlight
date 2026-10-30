import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { Story } from 'inkjs/full';
import { compileMission, createMissionRoutes, DEFAULT_MISSIONS_DIR, MissionStore } from '../src/missions.ts';

test('every mission in server/missions compiles, with a title and a summary', async () => {
  const missions = await new MissionStore(DEFAULT_MISSIONS_DIR).list();
  assert.ok(missions.length > 0);
  for (const m of missions) {
    assert.equal(m.error, null, `${m.id}: ${m.error}`);
    assert.ok(m.story);
    assert.notEqual(m.title, m.id, `${m.id} has no # title`);
    assert.ok(m.summary, `${m.id} has no # summary`);
  }
});

test('the delivery mission plays through three runs with the stand-in functions', async () => {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('delivery');
  assert.ok(mission?.story);
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = true;
  const waits: string[] = [];
  for (let guard = 0; guard < 200 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) story.Continue();
    if (story.currentChoices.length === 0) break;
    // Agree to everything; every @ wait is met at once.
    const pick = story.currentChoices.find((c: { text: string }) => c.text.startsWith('@') || c.text.startsWith('Sure')) ?? story.currentChoices[0];
    if (pick.text.startsWith('@')) waits.push(pick.text.split(' ')[0]);
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  assert.deepEqual(waits, Array(3).fill(['@talk', '@park', '@loaded', '@park', '@unloaded']).flat());
});

test('a broken mission is listed with its errors, and not served', async () => {
  const dir = await mkdtemp(path.join(tmpdir(), 'missions-'));
  try {
    await writeFile(path.join(dir, 'good.ink'), '# title: Good\n# summary: Fine.\nHello.\n-> END\n');
    await writeFile(path.join(dir, 'bad.ink'), '-> nowhere\n');
    await writeFile(path.join(dir, 'Not Valid.ink'), 'Ignored.\n');
    const routes = createMissionRoutes(new MissionStore(dir));
    const server = createServer((req, res) => {
      void routes(req, res).then((handled) => {
        if (!handled) res.writeHead(404).end();
      });
    });
    await new Promise<void>((resolve) => server.listen(0, resolve));
    const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}/mp/missions`;
    try {
      const list = (await (await fetch(base)).json()) as { missions: { id: string; title: string; error?: string }[] };
      assert.deepEqual(list.missions.map((m) => m.id), ['bad', 'good']);
      assert.match(list.missions[0].error ?? '', /Divert target not found/);
      assert.equal(list.missions[1].title, 'Good');
      const good = await fetch(`${base}/good`);
      assert.equal(good.status, 200);
      const body = (await good.json()) as { story: string };
      assert.equal(JSON.parse(body.story).inkVersion, 21);
      assert.equal((await fetch(`${base}/bad`)).status, 422);
      assert.equal((await fetch(`${base}/missing`)).status, 404);
      assert.equal((await fetch(`${base}/..%2Fsecret`)).status, 404);
    } finally {
      server.close();
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test('a mission without tags is named after its file', () => {
  const m = compileMission('plain', 'Hello.\n-> END\n');
  assert.equal(m.title, 'plain');
  assert.equal(m.summary, '');
});
