// Runs the generation pipeline. Pure: no DOM, no rendering, no Math.random(),
// so it works unchanged in the browser, a Web Worker, or a Node test.

import { stageRng } from '../core/rng';
import { createEmptyModel } from '../model/empty';
import { normalizeParams, type GenParams } from '../model/params';
import type { TownModel } from '../model/types';
import { blocks } from './blocks';
import { buildings } from './buildings';
import { lots } from './lots';
import { rasterize } from './rasterize';
import { rivers } from './rivers';
import { roads } from './roads';
import { site } from './site';
import { streets } from './streets';
import type { Stage } from './stage';
import { terrain } from './terrain';

/** The pipeline, in order. Each stage adds one layer and gets its own RNG stream. */
export const STAGES: readonly Stage[] = [
  terrain,
  rivers,
  site,
  roads,
  streets,
  blocks,
  lots,
  buildings,
  rasterize,
];

export interface GenerateOptions {
  /** Run each stage's invariant checks and throw on the first violation. */
  check?: boolean;
  /** Stop after this stage (for tests and debugging). */
  until?: string;
}

export class InvariantError extends Error {
  constructor(
    readonly stage: string,
    readonly violations: string[],
  ) {
    super(`[${stage}] ${violations.join('; ')}`);
  }
}

export function generateTown(
  seed: string,
  params: Partial<GenParams> = {},
  opts: GenerateOptions = {},
): TownModel {
  let model = createEmptyModel(seed, normalizeParams(params));
  for (const stage of STAGES) {
    model = stage.run(model, stageRng(seed, stage.name));
    if (opts.check && stage.check) {
      const violations = stage.check(model);
      if (violations.length) throw new InvariantError(stage.name, violations);
    }
    if (stage.name === opts.until) break;
  }
  return model;
}
