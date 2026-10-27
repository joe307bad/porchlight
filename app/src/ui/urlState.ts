// Seed and params live in the URL hash: #seed=abc123&width=96
// Only params that differ from their default are written.

import { DEFAULT_PARAMS, PARAM_SPECS, normalizeParams, type GenParams } from '../model/params';

export interface UrlState {
  seed: string | null;
  params: GenParams;
}

export function readUrlState(): UrlState {
  const hash = new URLSearchParams(location.hash.slice(1));
  const partial: Partial<GenParams> = {};
  for (const spec of PARAM_SPECS) {
    const raw = hash.get(spec.key);
    if (raw !== null) partial[spec.key] = Number(raw);
  }
  const seed = hash.get('seed')?.trim();
  return { seed: seed || null, params: normalizeParams(partial) };
}

/** The seed and the params that differ from their defaults, as a hash string (`seed=abc&width=96`). */
export function encodeUrlState(seed: string, params: GenParams): string {
  const hash = new URLSearchParams({ seed });
  for (const spec of PARAM_SPECS) {
    if (params[spec.key] !== DEFAULT_PARAMS[spec.key]) hash.set(spec.key, String(params[spec.key]));
  }
  return hash.toString();
}

export function writeUrlState(seed: string, params: GenParams): void {
  // replaceState doesn't fire hashchange, so this never loops back into a regenerate.
  history.replaceState(null, '', `#${encodeUrlState(seed, params)}`);
}
