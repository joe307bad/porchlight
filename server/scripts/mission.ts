// Compiles one mission (server/missions/<id>.ink) as the server would and
// prints the story's JSON, or its errors (exit 1). For trying a mission
// outside the game, e.g. with unity/Tools~/Checks:
//
//   node scripts/mission.ts delivery > /tmp/delivery.json

import { DEFAULT_MISSIONS_DIR, MissionStore } from '../src/missions.ts';

const id = process.argv[2];
if (!id) {
  console.error('usage: node scripts/mission.ts <id>');
  process.exit(2);
}
const mission = await new MissionStore(process.env.MISSIONS_DIR || DEFAULT_MISSIONS_DIR).get(id);
if (!mission) {
  console.error(`no mission ${id}`);
  process.exit(1);
}
if (!mission.story) {
  console.error(mission.error);
  process.exit(1);
}
process.stdout.write(mission.story);
