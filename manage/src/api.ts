// The server's own routes, on this origin: the same sign-in as the game's
// (server/src/app.ts), and the county map (server/src/multiplayer/feed.ts).
// In the desktop app the page is served by the game (Game/Net/ManageLink.cs),
// which passes these on to the server with the game's own session.

export interface CountyMap {
  towns: {
    key: string;
    name: string;
    x0: number;
    y0: number;
    width: number;
    height: number;
    private: boolean;
    /** Each street: [kind (0 main, 1 street, 2 path), width, x0, y0, x1, y1, ...] in the town's tiles. */
    roads: number[][] | null;
    /** What its buildings are for, the town's figures (Zoning.cs); null until known. */
    zoning: { homes: number; residents: number; workers: number; jobs: number } | null;
    /** Each building: [zone (zoning.ts ZONES), x0, y0, x1, y1 (tiles, exclusive), homes, jobs] in the town's tiles. */
    buildings: number[][] | null;
  }[];
  spaces: { kind: string; x0: number; y0: number; width: number; height: number }[];
  highways: number[][];
}

export type Me = { name: string } | null;

/** Who's signed in, or null. */
export async function me(): Promise<Me> {
  const res = await fetch('/auth/me', { cache: 'no-store', credentials: 'same-origin' });
  if (res.status === 401) return null;
  if (!res.ok) throw new Error(`Couldn't reach the server (${res.status}).`);
  return (await res.json()) as { name: string };
}

export async function county(): Promise<CountyMap> {
  const res = await fetch('/mp/traffic/county', { cache: 'no-store', credentials: 'same-origin' });
  if (!res.ok) {
    const body = (await res.json().catch(() => null)) as { error?: string } | null;
    throw new Error(body?.error ?? `Couldn't load the map (${res.status}).`);
  }
  return (await res.json()) as CountyMap;
}

/** Opened by the desktop app (from its own address), rather than on the web. */
export const inDesktopApp = (): boolean => location.hostname === '127.0.0.1';

export const signInUrl = (): string => `/auth/login?returnTo=${encodeURIComponent(location.pathname + location.search)}`;
