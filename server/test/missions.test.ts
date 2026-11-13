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
  'bin', 'get_out', 'take_bin', 'dump', 'return_bin', 'get_in', 'load_back', 'take_from_back',
  'your_plane', 'ring', 'land', 'chimney', 'tree',
];
const WAITS = ['talk', 'parked', 'idle', 'inside', 'outside', 'caught', 'doused', 'out', 'countdown', 'finished', 'ring', 'landed', 'chimney', 'gift', 'aboard', 'wait'];
const SETTINGS = [
  'talk_reach', 'talk_reach_car', 'park_reach', 'park_speed', 'park_angle', 'car_reach', 'walk_speed',
  'your_pace', 'chase_speed', 'chase_speed_car', 'pull_up', 'officer_speed', 'officer_reach', 'ring', 'catch_reach', 'catch_time',
  'douse_time', 'water_speed', 'aim_speed',
];
// What a person (or you) can look like: the game's characters, the firefighter, Santa and the animals.
const LOOKS = /^(people\/(character-[\w-]+|policeman-a-4-1|firefighter|santa)|animals\/animal-(penguin|lion|tiger|elephant|giraffe|panda|polar|monkey|koala|parrot))$/;
// The planes a mission can give you (Catalog.PLANES).
const PLANES = /^planes\/(prop-small|biplane-b|biplane-c|prop-warbird|jet-private|airliner-[adfg]|santa-sleigh)$/;

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
  // Each role's bin: 'home' by the kerb, 'held' by its person, and how many have been tipped into the car.
  const bins = new Map<string, 'home' | 'held'>();
  const aboard = new Set<string>();
  // The sky: the plane the story gave you ('' none) and whether you're in its seat (or down a role's chimney,
  // or out on foot); each role's ring (true: flown through); where your plane's coming down, or is down; the
  // chimney trip under way; and each role's tree (true: its present's under it).
  const rings = new Map<string, boolean>();
  const landed = new Map<string, 'roof' | 'kerb'>();
  const trees = new Map<string, boolean>();
  const sky = { plane: '', where: 'ground' as 'seat' | 'ground' | 'chimney', downAt: '', landing: '', landingAt: '' as '' | 'roof' | 'kerb', climbing: '', climbWay: '', rings, landed, trees, ringsFlown: 0, gifts: 0 };
  const town = { cargo: 0, load: 0, delivered: 0, bins, aboard, emptied: 0, out: 0, names: new Map<string, string>(), log: [] as string[], present, marked, placed, chases, standing, burning, courses, unlocked: [] as string[], look: '', clock: false, turret: '', car: '', stopwatch: '', stopwatchRole: '', mine: 0, best: 0, holder: '', kept: [] as number[], sky };
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
    bins.delete(role);
    aboard.delete(role);
    rings.delete(role);
    landed.delete(role);
    trees.delete(role);
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
    oneOf('place', to, ['inside', 'door', 'kerb', 'car', 'bin', 'back']);
    if (to === 'bin') assert.ok(bins.has(role), `${role} walks to a bin that isn't there`);
    if (to === 'back') assert.ok(town.car !== '', `${role} walks round the back of a car you haven't got`);
  });
  bind('bin', (role: string, model: string, size: number) => {
    isPlaced(role);
    assert.match(model, /^props\/wheelie-bin-(trash|recycle)$/, `no such bin '${model}'`);
    assert.ok(size > 0, 'a bin needs a size');
    bins.set(role, 'home');
  });
  bind('get_out', (role: string, name: string, look: string) => {
    isPlaced(role);
    assert.ok(town.car !== '', 'get_out with no car to get out of');
    assert.ok(look === '' || LOOKS.test(look), `no such look '${look}'`);
    present.add(role);
    town.names.set(role, name);
    town.out++;
    return name;
  });
  bind('take_bin', (role: string) => {
    isPresent(role);
    assert.equal(bins.get(role), 'home', `${role} takes a bin that isn't out`);
    bins.set(role, 'held');
  });
  bind('dump', (role: string) => {
    isPresent(role);
    assert.equal(bins.get(role), 'held', `${role} tips out a bin they haven't got`);
    town.emptied++;
  });
  bind('return_bin', (role: string) => {
    isPresent(role);
    assert.equal(bins.get(role), 'held', `${role} puts back a bin they haven't got`);
    bins.set(role, 'home');
  });
  bind('get_in', (role: string) => {
    isPresent(role);
    assert.ok(town.car !== '', 'get_in with no car to get into');
    assert.notEqual(bins.get(role), 'held', `${role} gets in still holding the bin`);
    aboard.add(role);
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
    town.delivered++;
  });
  bind('load_back', (model: string, count: number, size: number) => {
    assert.ok(town.car !== '', 'load_back with no car to load');
    assert.ok(model && size > 0, 'load_back needs a model and a size');
    assert.ok(count >= 0 && count <= 40, 'load_back is 0 to 40 things');
    town.load = count;
  });
  bind('take_from_back', (role: string) => {
    isPresent(role);
    assert.ok(town.load > 0, "there's nothing left in the back to take");
    assert.ok(!holding.has(role), `${role} already has their hands full`);
    town.load--;
    holding.add(role);
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
    oneOf('guide', to, ['spot', 'person', 'house', 'door', 'ring', 'tree', 'car']);
    if (to === 'ring') assert.ok(rings.has(role), `guide to ${role}'s ring with none hung`);
    if (to === 'car') assert.ok(town.car !== '' || sky.plane !== '', `guide to your car with none`);
  });
  bind('dot', (role: string, to: string) => {
    if (role === '') return;
    isPlaced(role);
    oneOf('dot', to, ['spot', 'person', 'house', 'door', 'ring', 'tree', 'car']);
    if (to === 'tree') assert.ok(trees.has(role), `a dot at ${role}'s tree with none there`);
  });
  bind('your_plane', (model: string, height: number) => {
    if (model === '') {
      sky.plane = '';
      if (sky.where === 'seat') sky.where = 'ground';
      return;
    }
    assert.match(model, PLANES, `no such plane '${model}'`);
    assert.ok(height >= 1 && height <= 24, `fly how high? ${height} (1 to 24 tiles)`);
    sky.plane = model;
    sky.where = 'seat';
  });
  bind('ring', (role: string, height: number, size: number) => {
    isPlaced(role);
    if (size <= 0) return rings.delete(role);
    assert.ok(height >= 1 && height <= 24, `a ring how high? ${height}`);
    assert.ok(size <= 12, `a ring how big? ${size}`);
    rings.set(role, false);
  });
  bind('land', (role: string, at: string) => {
    isPlaced(role);
    oneOf('place to land', at, ['roof', 'kerb']);
    assert.ok(sky.plane !== '' && sky.where === 'seat', `land at ${role} when you're not flying a plane`);
    sky.landing = role;
    sky.landingAt = at as 'roof' | 'kerb';
    landed.delete(role);
  });
  bind('chimney', (role: string, way: string) => {
    isPlaced(role);
    oneOf('way', way, ['down', 'up']);
    assert.equal(landed.get(role), 'roof', `a chimney at ${role} with your sleigh not on its roof`);
    assert.equal(sky.climbing, '', 'already on the way up or down a chimney');
    if (way === 'down') assert.equal(sky.where, 'seat', `down ${role}'s chimney from out of your seat`);
    else assert.ok(sky.where === 'chimney' && sky.downAt === role, `up ${role}'s chimney without being down it`);
    sky.climbing = role;
    sky.climbWay = way;
  });
  bind('tree', (role: string, gift: string) => {
    isPlaced(role);
    assert.ok(gift, 'a tree needs a present');
    trees.set(role, false);
  });
  bind('look', (model: string) => {
    assert.ok(model === '' || LOOKS.test(model), `no such look '${model}'`);
    town.look = model;
  });
  bind('put_you', (role: string, at: string) => {
    if (courses.has(role)) return oneOf('place on a course', at, ['start', 'finish']);
    isPlaced(role);
    oneOf('place', at, ['inside', 'door', 'kerb']);
    sky.where = 'ground';
    sky.downAt = '';
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
    oneOf('unlock', what, ['animals', 'firefighter', 'sprint', 'feast']);
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

test('trash day empties five bins into the truck against a stand-in town', async () => {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('trash-day');
  assert.ok(mission?.story);
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = false;
  const town = standInTown(story);
  const waits: string[] = [];
  const lines: string[] = [];
  for (let guard = 0; guard < 500 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) lines.push(story.Continue()!.trim());
    if (story.currentChoices.length === 0) break;
    const pick = story.currentChoices[0] as { text: string; index: number };
    const [wait, role] = pick.text.slice(1).split(' ');
    if (wait === 'parked') {
      assert.ok(town.marked.has(role), `@parked ${role} with no spot marked`);
      assert.equal(town.bins.get(role), 'home', `@parked ${role} with no bin out`);
    }
    if (wait === 'idle') assert.ok(town.aboard.has(role), `@idle ${role} before the crew's back in the truck`);
    waits.push(`@${wait}`);
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  assert.deepEqual(waits, Array(5).fill(['@parked', '@idle']).flat());
  // The garbage truck the whole way (your own car back at the end); out of it, round the back with the bin, and back in, five times.
  assert.ok(town.log.includes('your_car(cars/garbage-truck)'));
  assert.equal(town.car, '');
  assert.equal(town.out, 5);
  assert.equal(town.emptied, 5);
  // What the crew did, in order: walks by where to, the rest by name.
  const crew = town.log
    .filter((l) => /^(get_out|walk|take_bin|dump|return_bin|get_in)\(/.test(l))
    .map((l) => (l.startsWith('walk(') ? `walk ${l.slice(5, -1).split(', ')[1]}` : l.slice(0, l.indexOf('('))));
  assert.deepEqual(crew, Array(5).fill(['get_out', 'walk bin', 'take_bin', 'walk back', 'dump', 'walk bin', 'return_bin', 'get_in']).flat());
  assert.equal(town.marked.size, 0);
  assert.ok(lines.includes('All 5 bins emptied. The street\'s clean for another week.'), lines.join('\n'));
});

test('turkey run carries five frozen turkeys from the pickup into five houses, and lays the feast', async () => {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('turkey-run');
  assert.ok(mission?.story);
  assert.equal(mission.unlocks, 'feast');
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = false;
  const town = standInTown(story);
  const waits: string[] = [];
  const lines: string[] = [];
  for (let guard = 0; guard < 500 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) lines.push(story.Continue()!.trim());
    if (story.currentChoices.length === 0) break;
    const pick = story.currentChoices[0] as { text: string; index: number };
    const [wait, role] = pick.text.slice(1).split(' ');
    if (wait === 'parked') assert.ok(town.marked.has(role), `@parked ${role} with no spot marked`);
    if (wait === 'idle') assert.ok(town.aboard.has(role), `@idle ${role} before the helper's back in the pickup`);
    waits.push(`@${wait}`);
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  assert.deepEqual(waits, Array(5).fill(['@parked', '@idle']).flat());
  // The pickup the whole way, its bed piled with turkeys (your own car back at the end).
  assert.ok(town.log.includes('your_car(cars/truck)'));
  assert.ok(town.log.includes('load_back(props/frozen-turkey, 20, 0.15)'));
  assert.equal(town.car, '');
  assert.equal(town.out, 5);
  assert.equal(town.delivered, 5);
  assert.equal(town.load, 15);
  // What the helper did, in order: walks by where to, the rest by name.
  const helper = town.log
    .filter((l) => /^(get_out|walk|take_from_back|pause|put_down|get_in)\(/.test(l))
    .map((l) => (l.startsWith('walk(') ? `walk ${l.slice(5, -1).split(', ')[1]}` : l.slice(0, l.indexOf('('))));
  assert.deepEqual(helper, Array(5).fill(['get_out', 'walk back', 'take_from_back', 'walk inside', 'pause', 'put_down', 'walk door', 'get_in']).flat());
  assert.equal(town.marked.size, 0);
  assert.deepEqual(town.unlocked, ['feast']);
  assert.ok(lines.includes('All 5 turkeys delivered. Look in through any window: the whole town\'s sitting down to Thanksgiving dinner.'), lines.join('\n'));
});

test('deliver the presents flies Santa to five houses: down the chimney at every other one, in at the front door at the rest', async () => {
  const mission = await new MissionStore(DEFAULT_MISSIONS_DIR).get('deliver-the-presents');
  assert.ok(mission?.story);
  assert.equal(mission.title, 'Deliver the Presents');
  const story = new Story(mission.story);
  story.allowExternalFunctionFallbacks = false;
  const town = standInTown(story);
  const { sky } = town;
  const waits: string[] = [];
  const lines: string[] = [];
  // Where you are on foot: inside the role's house once you've gone in at its door.
  let insideAt = '';
  for (let guard = 0; guard < 500 && (story.canContinue || story.currentChoices.length > 0); guard++) {
    while (story.canContinue) lines.push(story.Continue()!.trim());
    if (story.currentChoices.length === 0) break;
    const choices = story.currentChoices as { text: string; index: number }[];
    assert.equal(choices.length, 1, `one wait at a time: ${choices.map((c) => c.text).join(', ')}`);
    const pick = choices[0];
    const [wait, role] = pick.text.slice(1).split(' ');
    // Each wait met as the town would meet it, once it could be.
    switch (wait) {
      case 'ring':
        assert.ok(sky.rings.has(role), `@ring ${role} with no ring hung`);
        assert.ok(sky.plane !== '' && sky.where === 'seat', `@ring ${role} with no plane to fly through it`);
        sky.rings.delete(role);
        sky.ringsFlown++;
        break;
      case 'landed':
        assert.equal(sky.landing, role, `@landed ${role} with no landing on the way`);
        sky.landed.set(role, sky.landingAt as 'roof' | 'kerb');
        sky.landing = '';
        break;
      case 'chimney':
        assert.equal(sky.climbing, role, `@chimney ${role} with no trip up or down it`);
        if (sky.climbWay === 'down') {
          sky.where = 'chimney';
          sky.downAt = role;
        } else {
          sky.where = 'seat';
          sky.downAt = '';
        }
        sky.climbing = '';
        break;
      case 'inside':
        assert.equal(sky.where, 'ground', `@inside ${role} while you're not on foot outside it`);
        insideAt = role;
        break;
      case 'gift':
        assert.equal(sky.trees.get(role), false, `@gift ${role} with no tree waiting`);
        assert.ok(sky.downAt === role || insideAt === role, `@gift ${role} from outside the house`);
        sky.trees.set(role, true);
        sky.gifts++;
        break;
      case 'aboard':
        assert.equal(sky.where, 'ground', '@aboard while not out on foot');
        assert.ok(sky.plane !== '', '@aboard with no sleigh to climb into');
        sky.where = 'seat';
        insideAt = '';
        break;
      default:
        assert.fail(`@${wait} isn't one this mission waits on`);
    }
    waits.push(`@${wait}`);
    story.ChooseChoiceIndex(pick.index);
  }
  assert.equal(story.canContinue, false);
  assert.equal(story.currentChoices.length, 0);
  // Down the chimney at the first, third and fifth houses; in at the front door at the second and fourth.
  const chimney = ['@ring', '@landed', '@chimney', '@gift', '@chimney'];
  const door = ['@ring', '@landed', '@inside', '@gift', '@aboard'];
  assert.deepEqual(waits, [chimney, door, chimney, door, chimney].flat());
  // Santa in his sleigh from the start, high up; yourself, on the ground, at the end.
  assert.ok(town.log.indexOf('look(people/santa)') < town.log.indexOf('your_plane(planes/santa-sleigh, 16)'));
  assert.equal(town.look, '');
  assert.equal(sky.plane, '');
  // Five rings flown through, five trees with a present under each.
  assert.equal(sky.ringsFlown, 5);
  assert.equal(sky.gifts, 5);
  assert.equal(town.log.filter((l) => l.startsWith('tree(home, props/present)')).length, 5);
  assert.equal(town.log.filter((l) => l.startsWith('ring(home, ')).length, 5);
  // Landed on the roof three times and in the street twice, every other house; down and back up each chimney.
  const landings = town.log.filter((l) => l.startsWith('land(')).map((l) => l.slice(5, -1).split(', ')[1]);
  assert.deepEqual(landings, ['roof', 'kerb', 'roof', 'kerb', 'roof']);
  const trips = town.log.filter((l) => l.startsWith('chimney(')).map((l) => l.slice(8, -1).split(', ')[1]);
  assert.deepEqual(trips, ['down', 'up', 'down', 'up', 'down', 'up']);
  // Out onto the pavement only at the front-door houses, and shown the way back to the sleigh there.
  assert.equal(town.log.filter((l) => l === 'put_you(home, kerb)').length, 2);
  assert.equal(town.log.filter((l) => l === 'guide(home, car, the sleigh)').length, 2);
  // Every house has room in the street to set the sleigh down, a few streets on.
  assert.ok(town.log.filter((l) => l.startsWith('place(')).every((l) => l === 'place(home, house, , 10, 30, true)'));
  assert.ok(lines.includes("You're Santa! Your sleigh's high over the rooftops, nine reindeer in harness and Rudolph's nose lighting the way."), lines.join('\n'));
  assert.ok(lines.includes('Merry Christmas! The whole town wakes up to presents under the tree.'), lines.join('\n'));
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
