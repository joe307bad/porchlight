// The routes, all under /auth on the app's own origin (Caddy in production,
// Vite's proxy in dev), so the cookies are first-party:
//
//   GET  /auth/login?returnTo=/#seed=x   off to Keycloak (and on to Google)
//   GET  /auth/callback                  back from Keycloak: set the session cookie
//   GET  /auth/me                        who's signed in (200), or 401
//   POST /auth/logout                    clear the cookie, end the Keycloak session
//   GET  /auth/health
//
// Two cookies, both HttpOnly + SameSite=Lax (+ Secure over HTTPS):
//   pl_signin   the in-flight handshake (state, nonce, PKCE verifier), 10 minutes, /auth only
//   pl_session  who signed in and their id token, for as long as Keycloak's session

import type { IncomingMessage, ServerResponse } from 'node:http';
import type { Config } from './config.ts';
import { clearCookie, parseCookies, seal, serializeCookie, unseal } from './cookies.ts';
import { authorizeUrl, exchangeCode, logoutUrl, randomToken, type Identity } from './oidc.ts';

export const SESSION_COOKIE = 'pl_session';
export const SIGNIN_COOKIE = 'pl_signin';
const SIGNIN_TTL = 600;

interface Pending {
  state: string;
  nonce: string;
  verifier: string;
  returnTo: string;
}

export interface SessionPayload {
  sub: string;
  name: string;
  email: string | null;
  idToken: string;
}

/** Only ever a path on this site: no scheme, no `//host`, no backslash tricks. */
export function safeReturnTo(raw: string | null): string {
  if (!raw || !raw.startsWith('/') || raw.startsWith('//') || raw.includes('\\')) return '/';
  return raw;
}

type Exchange = (config: Config, code: string, verifier: string, nonce: string) => Promise<Identity>;

export function createHandler(config: Config, exchange: Exchange = exchangeCode) {
  const signinCookie = { path: '/auth', secure: config.secureCookies };

  return async (req: IncomingMessage, res: ServerResponse): Promise<void> => {
    const url = new URL(req.url ?? '/', config.appUrl);
    const cookies = parseCookies(req.headers.cookie);
    const send = (status: number, body: unknown, headers: Record<string, string | string[]> = {}) => {
      res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store', ...headers });
      res.end(JSON.stringify(body));
    };
    const redirect = (location: string, setCookie: string[] = [], status = 302) => {
      res.writeHead(status, { location, 'cache-control': 'no-store', 'set-cookie': setCookie });
      res.end();
    };
    const session = () => unseal<SessionPayload>(config.sessionSecret, cookies.get(SESSION_COOKIE));

    try {
      if (req.method === 'GET' && url.pathname === '/auth/health') return send(200, { ok: true });

      if (req.method === 'GET' && url.pathname === '/auth/me') {
        const s = session();
        if (!s) return send(401, { signIn: config.idpHint });
        return send(200, { name: s.name, email: s.email });
      }

      if (req.method === 'GET' && url.pathname === '/auth/login') {
        const pending: Pending = {
          state: randomToken(),
          nonce: randomToken(),
          verifier: randomToken(),
          returnTo: safeReturnTo(url.searchParams.get('returnTo')),
        };
        const cookie = serializeCookie(SIGNIN_COOKIE, seal(config.sessionSecret, pending, SIGNIN_TTL), {
          ...signinCookie,
          maxAge: SIGNIN_TTL,
        });
        return redirect(authorizeUrl(config, pending), [cookie]);
      }

      if (req.method === 'GET' && url.pathname === '/auth/callback') {
        const pending = unseal<Pending>(config.sessionSecret, cookies.get(SIGNIN_COOKIE));
        const done = clearCookie(SIGNIN_COOKIE, signinCookie);
        const fail = (message: string) =>
          redirect(`${config.appUrl}/?auth_error=${encodeURIComponent(message)}`, [done]);

        const error = url.searchParams.get('error');
        if (error) return fail(url.searchParams.get('error_description') ?? error);
        const code = url.searchParams.get('code');
        if (!pending || !code || url.searchParams.get('state') !== pending.state) {
          return fail('Sign-in was interrupted. Please try again.');
        }
        let identity: Identity;
        try {
          identity = await exchange(config, code, pending.verifier, pending.nonce);
        } catch (err) {
          console.error('sign-in failed:', err);
          return fail('Sign-in failed. Please try again.');
        }
        const payload: SessionPayload = {
          sub: identity.sub,
          name: identity.name,
          email: identity.email,
          idToken: identity.idToken,
        };
        const cookie = serializeCookie(SESSION_COOKIE, seal(config.sessionSecret, payload, identity.sessionSeconds), {
          maxAge: identity.sessionSeconds,
          secure: config.secureCookies,
        });
        return redirect(`${config.appUrl}${pending.returnTo}`, [done, cookie]);
      }

      if (req.method === 'POST' && url.pathname === '/auth/logout') {
        const s = session();
        // 303: the browser follows a POST's redirect with a GET.
        return redirect(logoutUrl(config, s?.idToken ?? null), [clearCookie(SESSION_COOKIE, { secure: config.secureCookies })], 303);
      }

      send(404, { error: 'not found' });
    } catch (err) {
      console.error(err);
      send(500, { error: 'internal error' });
    }
  };
}
