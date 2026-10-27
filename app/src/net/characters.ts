// The signed-in account's characters (server/src/multiplayer/accounts.ts).
// One account plays as any of them, and more than one at once: a parent and
// child on one sign-in each play their own, in their own tab or device.

export interface Character {
  id: string;
  name: string;
  /** Character model (a CHARACTERS entry), if they've been given one. */
  look: string | null;
}

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/mp/characters${path}`, {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    headers: body ? { 'content-type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const json = (await res.json().catch(() => ({}))) as T & { error?: string };
  if (!res.ok) throw new Error(json.error ?? `The game server isn't answering (${res.status}).`);
  return json;
}

export const listCharacters = async (): Promise<Character[]> => (await call<{ characters: Character[] }>('GET', '')).characters;

export const createCharacter = (name: string, look: string): Promise<Character> => call<Character>('POST', '', { name, look });

export const deleteCharacter = async (id: string): Promise<void> => {
  await call('DELETE', `/${encodeURIComponent(id)}`);
};
