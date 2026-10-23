// Fixed seed list for invariant tests, derived from a seeded stream so it's stable.
import { createRng } from '../src/core/rng';

export function testSeeds(count = 50): string[] {
  const rng = createRng('porchlight-test-seeds');
  return Array.from({ length: count }, () => Math.floor(rng.next() * 2 ** 32).toString(36));
}
