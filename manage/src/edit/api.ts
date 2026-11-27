// The server's street-editor calls (docs/street-editor-api.md, "The server"), or the mock's with `?mock=1`.

import { mock } from './mock.ts';
import type { CommitAnswer, Day, Draft, EditableTown, PlanAnswer, Problem, Skeleton, Town } from './types.ts';
import { normalize } from './skeleton.ts';

export const isMock = (): boolean => new URLSearchParams(location.search).has('mock');

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const json = (await res.json().catch(() => null)) as (T & { error?: string }) | null;
  if (!res.ok) {
    const err = new Error(json?.error ?? `The server said no (${res.status}).`) as Error & { status?: number; body?: unknown };
    err.status = res.status;
    err.body = json;
    throw err;
  }
  return json as T;
}

const q = (town: string) => `town=${encodeURIComponent(town)}`;

/** The towns this account may edit (and the rest it can see). */
export async function editableTowns(): Promise<EditableTown[]> {
  if (isMock()) return mock.towns();
  try {
    return await call<EditableTown[]>('GET', '/mp/edit/towns');
  } catch {
    return [];
  }
}

export async function loadDraft(town: string): Promise<Draft> {
  const d = isMock() ? await mock.draft() : await call<Draft>('GET', `/mp/edit/draft?${q(town)}`);
  return { ...d, skeleton: normalize(d.skeleton), current: { rev: d.current?.rev ?? d.baseRev, skeleton: d.current?.skeleton ? normalize(d.current.skeleton) : normalize(null) } };
}

export async function saveDraft(town: string, baseRev: number, skeleton: Skeleton, commands: unknown): Promise<{ updatedAt: string }> {
  if (isMock()) return mock.saveDraft({ baseRev, skeleton, commands });
  return call('PUT', '/mp/edit/draft', { town, baseRev, skeleton, commands });
}

export async function discardDraft(town: string): Promise<void> {
  if (isMock()) return mock.discard();
  await call('DELETE', `/mp/edit/draft?${q(town)}`);
}

export async function plan(town: string, skeleton: Skeleton, seq: number): Promise<PlanAnswer> {
  if (isMock()) return mock.plan(skeleton, seq);
  return call<PlanAnswer>('POST', '/mp/edit/plan', { town, skeleton, seq });
}

/** The town as it is now (the current revision's skeleton, planned): the ghost under the draft. */
export async function liveTown(town: string, current: Skeleton): Promise<Town | null> {
  if (isMock()) return mock.live();
  const a = await plan(town, current, -1).catch(() => null);
  return a?.town ?? null;
}

export async function runDay(town: string, skeleton: Skeleton): Promise<{ draft: Day; live: Day }> {
  if (isMock()) return mock.day(skeleton);
  return call('POST', '/mp/edit/day', { town, skeleton });
}

export async function commit(town: string, skeleton: Skeleton, message: string): Promise<CommitAnswer | { problems: Problem[] }> {
  if (isMock()) return mock.commit(skeleton, message);
  try {
    return await call<CommitAnswer>('POST', '/mp/edit/commit', { town, skeleton, message });
  } catch (e) {
    const err = e as Error & { status?: number; body?: { problems?: Problem[] } };
    if (err.status === 409 && err.body?.problems) return { problems: err.body.problems };
    throw e;
  }
}

export async function history(town: string): Promise<{ rev: number; message: string; by: string; at: string }[]> {
  if (isMock()) return mock.history();
  return call('GET', `/mp/edit/history?${q(town)}`).catch(() => []) as Promise<{ rev: number; message: string; by: string; at: string }[]>;
}
