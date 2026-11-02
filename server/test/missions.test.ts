import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
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

// The game's building blocks (MissionScript.cs binds the same), with the
// values each takes: a stand-in town that checks a story uses them right.
const BLOCKS = [
  'objective', 'hint', 'clear_hints', 'setting', 'place', 'spawn', 'walk', 'pause', 'hold',
  'put_in_car', 'take_from_car', 'put_down', 'leave', 'mark_spot', 'clear_spot', 'guide',
  'dot', 'look', 'put_you', 'chase', 'countdown',
];
const WAITS = ['talk', 'parked', 'idle', 'inside', 'outside', 'caught', 'countdown', 'wait'];
const SETTINGS = [
  'talk_reach', 'talk_reach_car', 'park_reach', 'park_speed', 'park_angle', 'car_reach', 'walk_speed',
  'your_pace', 'chase_speed', 'chase_speed_car', 'pull_up', 'officer_speed', 'officer_reach', 'catch_reach', 'catch_time',
];
// What a person (or you) can look like: the game's characters and animals.
const LOOKS = /^(people\/(character-[\w-]+|policeman-a-4-1)|animals\/animal-(penguin|lion|tiger|elephant|giraffe|panda|polar|monkey|koala|parrot))$/;

/** Binds a stand-in town to a story: it keeps track of who's where and what's held or on the car, and throws on misuse. */
function standInTown(story: InstanceType<typeof Story>) {
  const placed = new Set<string>();
  const present = new Set<string>();
  const marked = new Set<string>();
  const holding = new Set<string>();
  const chases = new Set<string>();
  const town = { cargo: 0, names: new Map<string, string>(), log: [] as string[], present, marked, placed, chases, look: '', clock: false };
  const oneOf = (what: string, value: string, known: string[]) => {
    if (!known.includes(value)) throw new Error(`no such ${what} '${value}' (${known.join(', ')})`);
  };
  const isPlaced = (role: string) => {
    if (!placed.has(role)) throw new Error(`no building for '${role}' yet`);
  };
  const isPresent = (role: string) => {
    isPlaced(role);
    if (!present.has(role)) throw new Error(`no one plays '${role}'`);
  };
  const bind = (name: string, f: (...args: any[]) => unknown) =>
    story.BindExternalFunction(name, (...args: any[]) => {
      town.log.push(`${name}(${args.join(', ')})`);
      return f(...args);
    }, false);
  bind('objective', () => {});
  bind('hint', (when: string) => oneOf('condition', when, ['always', 'no_car', 'near', 'near_driving']));
  bind('clear_hints', () => {});
  bind('setting', (name: string) => oneOf('setting', name, SETTINGS));
  bind('place', (role: string, kind: string, near: string) => {
    oneOf('kind of building', kind, ['', 'house', 'workshop', 'shop', 'office', 'hall', 'church', 'zoo']);
    assert.ok(!chases.has(role), `'${role}' is a chase`);
    if (near !== '') isPlaced(near);
    placed.add(role);
    present.delete(role);
    marked.delete(role);
    return `${role} street`;
  });
  bind('spawn', (role: string, name: string, look: string) => {
    isPlaced(role);
    assert.ok(look === '' || LOOKS.test(look), `no such look '${look}'`);
    present.add(role);
    holding.delete(role);
    town.names.set(role, name);
    return name;
  });
  bind('walk', (role: string, to: string) => {
    isPresent(role);
    oneOf('place', to, ['inside', 'door', 'kerb', 'car']);
  });
  bind('pause', (role: string) => isPresent(role));
  bind('hold', (role: string, model: string, size: number) => {
    isPresent(role);
    assert.ok(model && size > 0, 'hold needs a model and a size');
    assert.ok(!holding.has(role), `${role} already has their hands full`);
    holding.add(role);
  });
  bind('put_in_car', (role: string) => {
    isPresent(role);
    assert.ok(holding.delete(role), `${role} has nothing to put in the car`);
    town.cargo++;
  });
  bind('take_from_car', (role: string) => {
    isPresent(role);
    assert.ok(town.cargo > 0, "there's nothing on the car to take");
    assert.ok(!holding.has(role), `${role} already has their hands full`);
    town.cargo--;
    holding.add(role);
  });
  bind('put_down', (role: string) => {
    isPresent(role);
    assert.ok(holding.delete(role), `${role} has nothing to put down`);
  });
  bind('leave', (role: string) => {
    present.delete(role);
    holding.delete(role);
    chases.delete(role);
  });
  bind('mark_spot', (role: string) => {
    isPlaced(role);
    marked.add(role);
  });
  bind('clear_spot', (role: string) => marked.delete(role));
  bind('guide', (role: string, to: string) => {
    if (role === '') return;
    isPlaced(role);
    oneOf('guide', to, ['spot', 'person', 'house', 'door']);
  });
  bind('dot', (role: string, to: string) => {
    if (role === '') return;
    isPlaced(role);
    oneOf('dot', to, ['spot', 'person', 'house', 'door']);
  });
  bind('look', (model: string) => {
    assert.ok(model === '' || LOOKS.test(model), `no such look '${model}'`);
    town.look = model;
  });
  bind('put_you', (role: string, at: string) => {
    isPlaced(role);
    oneOf('place', at, ['inside', 'door', 'kerb']);
  });
  bind('chase', (role: string, name: string, count: number, car: string, look: string) => {
    assert.ok(role && !placed.has(role), `a chase needs a role that isn't a building's`);
    assert.ok(count >= 1 && count <= 8, 'a chase is 1 to 8 cars');
    assert.match(car, /^cars\//, `no such car '${car}'`);
    assert.ok(look === '' || LOOKS.test(look), `no such look '${look}'`);
    chases.add(role);
    town.names.set(role, name);
    return name;
  });
  bind('countdown', (seconds: number, _label: string, when: string, role: string) => {
    town.clock = seconds > 0;
    if (seconds <= 0) return;
    oneOf('condition', when, ['always', 'inside', 'outside']);
    if (when !== 'always') isPlaced(role);
  });
  return town;
}

test('every mission uses only the game\'s building blocks, and waits it knows', async () => {
  const missions = await new MissionStore(DEFAULT_MISSIONS_DIR).list();
  for (const m of missions) {
    const source = await readFile(path.join(DEFAULT_MISSIONS_DIR, `${m.id}.ink`), 'utf8');
    for (const [, name] of source.matchAll(/^\s*EXTERNAL\s+(\w+)/gm)) {
      assert.ok(BLOCKS.includes(name), `${m.id}: EXTERNAL ${name} isn't one of the game's building blocks`);
    }
    for (const [, wait] of source.matchAll(/^\s*[+*]\s*\[@(\w+)/gm)) {
      assert.ok(WAITS.includes(wait), `${m.id}: @${wait} isn't a wait the game knows`);
    }
  }
});

test('the delivery mission plays through three runs against a stand-in town', async () => {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('delivery');
  assert.ok(mission?.story);
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = false;
  const town = standInTown(story);
  const waits: string[] = [];
  const lines: string[] = [];
  for (let guard = 0; guard < 500 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) lines.push(story.Continue()!.trim());
    if (story.currentChoices.length === 0) break;
    // Agree to everything; every @ wait is met as soon as it could be.
    const pick = story.currentChoices.find((c: { text: string }) => c.text.startsWith('@') || c.text.startsWith('Sure')) ?? story.currentChoices[0];
    if (pick.text.startsWith('@')) {
      const [wait, role] = pick.text.slice(1).split(' ');
      if (wait === 'parked') assert.ok(town.marked.has(role), `@parked ${role} with no spot marked`);
      if (wait === 'talk' || wait === 'idle') assert.ok(town.present.has(role), `@${wait} ${role} with no one there`);
      waits.push(`@${wait}`);
    }
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  assert.deepEqual(waits, Array(3).fill(['@talk', '@parked', '@idle', '@parked', '@idle']).flat());
  // Every box loaded was unloaded, and the neighbours were people, not stand-ins.
  assert.equal(town.cargo, 0);
  assert.equal(story.variablesState.$('in_car'), 0);
  assert.ok(town.log.filter((l) => l.startsWith('put_in_car(')).length >= 6);
  assert.notEqual(town.names.get('sender'), town.names.get('drop'));
  assert.ok(lines.includes('All 3 deliveries done. The neighbourhood owes you one.'));
});

test('the chase plays through, caught once and then free, against a stand-in town', async () => {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('chase');
  assert.ok(mission?.story);
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = false;
  const town = standInTown(story);
  const waits: string[] = [];
  const lines: string[] = [];
  let caughtOnce = false;
  for (let guard = 0; guard < 500 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) lines.push(story.Continue()!.trim());
    if (story.currentChoices.length === 0) break;
    // Out of the zoo; caught the first time, and the second time free.
    const choices = story.currentChoices as { text: string; index: number }[];
    const want = choices.some((c) => c.text === '@caught police') && !caughtOnce ? '@caught police' : choices.find((c) => c.text !== '@caught police')!.text;
    const pick = choices.find((c) => c.text === want)!;
    const [wait, role] = pick.text.slice(1).split(' ');
    if (wait === 'outside') assert.ok(town.placed.has(role), `@outside ${role} with no such place`);
    if (wait === 'caught') {
      assert.ok(town.chases.has(role), `@caught ${role} with no chase on`);
      caughtOnce = true;
    }
    if (wait === 'countdown') assert.ok(town.clock, '@countdown with no countdown running');
    waits.push(`@${wait}`);
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  assert.deepEqual(waits, ['@outside', '@caught', '@outside', '@countdown']);
  // A penguin all the way, and yourself again at the end; the police called off.
  assert.ok(town.log.includes('look(animals/animal-penguin)'));
  assert.equal(town.look, '');
  assert.equal(town.chases.size, 0);
  assert.equal(town.clock, false);
  assert.equal(town.log.filter((l) => l.startsWith('put_you(zoo, inside)')).length, 2);
  assert.ok(lines.some((l) => l.startsWith('A whole minute on the loose, and caught 1 time')), lines.join('\n'));
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
