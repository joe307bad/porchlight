// Who's signed in, as told by the auth service (server/). The page never sees
// a token: the session is an HttpOnly, SameSite=Lax cookie the service sets
// after it has done the OIDC handshake with Keycloak (which, in production,
// goes straight to Google). All the page can do is ask who it is, start a
// sign-in, and sign out.
//
// On when VITE_AUTH_ENABLED=true (app/.env.development, app/.env.production).

export interface User {
  name: string;
  email: string | null;
}

export type Me = { user: User } | { user: null; provider: string | null };

export const authEnabled = () => import.meta.env.VITE_AUTH_ENABLED === 'true';

export async function fetchMe(): Promise<Me> {
  const response = await fetch('/auth/me', { credentials: 'same-origin', cache: 'no-store' });
  if (response.status === 401) {
    const body = (await response.json().catch(() => ({}))) as { signIn?: string | null };
    return { user: null, provider: body.signIn ?? null };
  }
  if (!response.ok) throw new Error(`The sign-in service isn't answering (${response.status}).`);
  return { user: (await response.json()) as User };
}

/** Off to sign in; comes back to this same town (the hash holds the seed and params). */
export function signIn(): void {
  const returnTo = location.pathname + location.search + location.hash;
  location.assign(`/auth/login?returnTo=${encodeURIComponent(returnTo)}`);
}

/** A POST (so another site can't sign you out with a link); the service redirects on. */
export function signOut(): void {
  const form = document.createElement('form');
  form.method = 'POST';
  form.action = '/auth/logout';
  document.body.append(form);
  form.submit();
}
