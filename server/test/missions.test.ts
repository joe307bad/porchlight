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
  'dot', 'look', 'put_you', 'chase', 'countdown', 'standby', 'unlock', 'your_car', 'fire', 'turret',
  'course', 'stopwatch', 'stopwatch_time', 'record', 'town_record', 'town_record_holder',
];
const WAITS = ['talk', 'parked', 'idle', 'inside', 'outside', 'caught', 'doused', 'out', 'countdown', 'finished', 'wait'];
const SETTINGS = [
  'talk_reach', 'talk_reach_car', 'park_reach', 'park_speed', 'park_angle', 'car_reach', 'walk_speed',
  'your_pace', 'chase_speed', 'chase_speed_car', 'pull_up', 'officer_speed', 'officer_reach', 'ring', 'catch_reach', 'catch_time',
  'douse_time', 'water_speed', 'aim_speed',
];
// What a person (or you) can look like: the game's characters, the firefighter and the animals.
const LOOKS = /^(people\/(character-[\w-]+|policeman-a-4-1|firefighter)|animals\/animal-(penguin|lion|tiger|elephant|giraffe|panda|polar|monkey|koala|parrot))$/;

/** Binds a stand-in town to a story: it keeps track of who's where and what's held or on the car, and throws on misuse. */
function standInTown(story: InstanceType<typeof Story>) {
  const placed = new Set<string>();
  const present = new Set<string>();
  const marked = new Set<string>();
  const holding = new Set<string>();
  const chases = new Set<string>();
  const standing = new Set<string>();
  // Each burning role's windows still alight; the role the turret's offered for ('' none); the car the story gave you ('' none).
  const burning = new Map<string, number>();
  // Courses (course), the stopwatch's label ('' none) and course, and this town's best times: yours, and its record.
  const courses = new Set<string>();
  const town = { cargo: 0, names: new Map<string, string>(), log: [] as string[], present, marked, placed, chases, standing, burning, courses, unlocked: [] as string[], look: '', clock: false, turret: '', car: '', stopwatch: '', stopwatchRole: '', mine: 0, best: 0, holder: '', kept: [] as number[] };
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
  bind('hint', (when: string, role: string) => {
    oneOf('condition', when, ['always', 'no_car', 'near', 'near_driving', 'off_course']);
    if (when === 'off_course') assert.ok(courses.has(role), `off_course ${role} with no such course`);
  });
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
    if (courses.has(role)) return oneOf('place on a course', at, ['start', 'finish']);
    isPlaced(role);
    oneOf('place', at, ['inside', 'door', 'kerb']);
  });
  bind('course', (role: string, crowd: number) => {
    assert.ok(role && !placed.has(role) && !chases.has(role), `a course needs a role of its own`);
    assert.ok(crowd >= 0 && crowd <= 1, 'a crowd is 0 to 1');
    courses.add(role);
    return 4321;
  });
  bind('stopwatch', (label: string, role: string) => {
    if (role !== '') assert.ok(courses.has(role), `a stopwatch on '${role}', which isn't a course`);
    town.stopwatch = label;
    town.stopwatchRole = label === '' ? '' : role;
  });
  bind('stopwatch_time', () => {
    assert.ok(town.stopwatch, 'stopwatch_time with no stopwatch');
    return 754.32;
  });
  bind('record', (seconds: number) => {
    assert.ok(seconds > 0, 'a record needs a time');
    const before = town.mine;
    if (!town.mine || seconds < town.mine) town.mine = seconds;
    town.kept.push(seconds);
    return before;
  });
  bind('town_record', () => town.best);
  bind('town_record_holder', () => town.holder);
  bind('chase', (role: string, name: string, count: number, car: string, look: string) => {
    assert.ok(role && !placed.has(role), `a chase needs a role that isn't a building's`);
    assert.ok(count >= (standing.has(role) ? 0 : 1) && count <= 8, 'a chase is 1 to 8 cars (0 with a standby)');
    assert.match(car, /^cars\//, `no such car '${car}'`);
    assert.ok(look === '' || LOOKS.test(look), `no such look '${look}'`);
    chases.add(role);
    town.names.set(role, name);
    return name;
  });
  bind('standby', (role: string, count: number, car: string, look: string) => {
    assert.ok(role && !placed.has(role), `a standby needs a role that isn't a building's`);
    assert.ok(count >= 1 && count <= 24, 'a standby is 1 to 24 cars');
    assert.match(car, /^cars\//, `no such car '${car}'`);
    assert.ok(look === '' || LOOKS.test(look), `no such look '${look}'`);
    standing.add(role);
  });
  bind('unlock', (what: string) => {
    oneOf('unlock', what, ['animals', 'firefighter', 'sprint']);
    town.unlocked.push(what);
  });
  bind('your_car', (car: string) => {
    assert.ok(car === '' || /^cars\//.test(car), `no such car '${car}'`);
    town.car = car;
  });
  bind('fire', (role: string, windows: number) => {
    isPlaced(role);
    assert.ok(windows >= 0 && windows <= 8, 'a fire is 0 to 8 windows');
    // A town's house can have fewer windows facing the street than asked for: this one has
    // two. Its door burns too, and is put out as they are.
    if (windows > 0) windows = Math.min(windows, 2) + 1;
    if (windows > 0) burning.set(role, windows);
    else burning.delete(role);
    return windows;
  });
  bind('turret', (role: string, crew: number, look: string) => {
    if (role !== '') {
      isPlaced(role);
      assert.ok(crew >= 0 && crew <= 6, "a turret's crew is 0 to 6");
      assert.ok(look === '' || LOOKS.test(look), `no such look '${look}'`);
    }
    town.turret = role;
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
  assert.ok(lines.some((l) => l.startsWith('You stayed free for a whole minute, even though you got caught once')), lines.join('\n'));
  // Police waiting all over town from the start, and the animals yours at the end.
  assert.ok(town.standing.has('police'));
  assert.deepEqual(town.unlocked, ['animals']);
});

test('the firefighter saves three houses against a stand-in town', async () => {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('firefighter');
  assert.ok(mission?.story);
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = false;
  const town = standInTown(story);
  const waits: string[] = [];
  const lines: string[] = [];
  for (let guard = 0; guard < 500 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) lines.push(story.Continue()!.trim());
    if (story.currentChoices.length === 0) break;
    // The windows go out one at a time; the last puts the fire out.
    const choices = story.currentChoices as { text: string; index: number }[];
    const left = town.burning.get('fire') ?? 0;
    const pick = choices.find((c) => c.text === (left > 1 ? '@doused fire' : '@out fire')) ?? choices[0];
    const [wait, role] = pick.text.slice(1).split(' ');
    if (wait === 'parked') assert.ok(town.marked.has(role), `@parked ${role} with no spot marked`);
    if (wait === 'doused' || wait === 'out') {
      assert.ok(left > 0, `@${wait} ${role} with nothing burning`);
      assert.equal(town.turret, role, `@${wait} ${role} with no turret to put it out from`);
      town.burning.set(role, wait === 'out' ? 0 : left - 1);
    }
    waits.push(`@${wait}`);
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  assert.deepEqual(waits, Array(3).fill(['@parked', '@doused', '@doused', '@out']).flat());
  // A firefighter in the fire engine from the start, and yourself in your own car at the end.
  assert.ok(town.log.indexOf('look(people/firefighter)') < town.log.indexOf('your_car(cars/firetruck)'));
  assert.equal(town.look, '');
  assert.equal(town.car, '');
  assert.equal(town.turret, '');
  assert.equal(town.burning.size, 0);
  assert.equal(town.log.filter((l) => l.startsWith('fire(fire, 3)')).length, 3);
  assert.ok(lines.includes('You saved all three houses! The whole town is cheering.'), lines.join('\n'));
  assert.deepEqual(town.unlocked, ['firefighter']);
});

/** Plays the marathon through against a stand-in town with these best times, and returns what it said. */
async function runMarathon(mine: number, best: number, holder: string) {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('marathon');
  assert.ok(mission?.story);
  assert.equal(mission.unlocks, 'sprint');
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = false;
  const town = standInTown(story);
  Object.assign(town, { mine, best, holder });
  const waits: string[] = [];
  const lines: string[] = [];
  for (let guard = 0; guard < 100 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) lines.push(story.Continue()!.trim());
    if (story.currentChoices.length === 0) break;
    const pick = story.currentChoices[0] as { text: string; index: number };
    const [wait, role] = pick.text.slice(1).split(' ');
    if (wait === 'finished') {
      assert.ok(town.courses.has(role), `@finished ${role} with no such course`);
      assert.equal(town.stopwatchRole, role, `@finished ${role} with no stopwatch on it`);
    }
    waits.push(`@${wait}`);
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  assert.deepEqual(waits, ['@finished']);
  assert.ok(town.log.includes('put_you(race, start)'));
  assert.deepEqual(town.kept, [754.32]);
  assert.deepEqual(town.unlocked, ['sprint']);
  assert.equal(town.stopwatch, '');
  return lines;
}

test('the marathon runs the course once, keeps the time, and says how it went', async () => {
  let lines = await runMarathon(0, 0, '');
  assert.ok(lines.includes('The course runs 4.3 km along the streets, right across town. Stay between the ribbons all the way.'), lines.join('\n'));
  assert.ok(lines.includes('No one has run it in this town yet. Be the first!'));
  assert.ok(lines.includes('Your time: 12:34.32.'), lines.join('\n'));
  assert.ok(lines.includes("That's the new record for this town!"));
  // Slower than the record, faster than your last best.
  lines = await runMarathon(800, 700.5, 'Ada');
  assert.ok(lines.includes('The record here is 11:40.50, run by Ada.'), lines.join('\n'));
  assert.ok(lines.includes("That's your best here, 0:45.68 faster than before."), lines.join('\n'));
  // Slower than both.
  lines = await runMarathon(700, 650, 'Ada');
  assert.ok(lines.includes('Your best here is still 11:40.00.'), lines.join('\n'));
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
