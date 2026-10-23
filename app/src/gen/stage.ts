import type { Rng } from '../core/rng';
import type { TownModel } from '../model/types';

/** One pipeline step: a pure function that returns the model with one more layer. */
export interface Stage {
  name: string;
  run(model: TownModel, rng: Rng): TownModel;
  /** Invariants that must hold after this stage; returns human-readable violations. */
  check?(model: TownModel): string[];
}
