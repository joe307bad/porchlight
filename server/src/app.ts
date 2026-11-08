// The routes, all under /auth on the app's own origin (Caddy in production,
// unity/Tools~/serve.mjs in dev), so the cookies are first-party:
//
//   GET  /auth/login?returnTo=/#seed=x   off to Keycloak (and on to Google)
//   GET  /auth/callback                  back from Keycloak: set the session cookie
//   GET  /auth/me                        who's signed in (200), or 401
//   POST /auth/logout                    clear the cookie, end the Keycloak session
//   GET  /auth/health
//
// The desktop app (desktop/, no browser cookies of its own) signs in through
// the system browser and a loopback address, as native apps do (RFC 8252):
//
//   GET  /auth/login?native=<port>&challenge=<S256 of a verifier>
//        the same handshake, but at the end the browser goes to
//        http://127.0.0.1:<port>/?code=<one-time code> (no cookie set there)
//   POST /auth/native {code, verifier}
//        the app trades the code (one use, two minutes) and the verifier only
//        it knows for the session value, which it then sends as the cookie
//
// Two cookies, both HttpOnly + SameSite=Lax (+ Secure over HTTPS):
//   pl_signin   the in-flight handshake (state, nonce, PKCE verifier), 10 minutes, /auth only
//   pl_session  who signed in and their id token, for as long as Keycloak's session

import type { IncomingMessage, ServerResponse } from 'node:http';
import type { Config } from './config.ts';
import { clearCookie, parseCookies, seal, serializeCookie, unseal } from './cookies.ts';
import { authorizeUrl, codeChallenge, exchangeCode, logoutUrl, randomToken, type Identity } from './oidc.ts';

export const SESSION_COOKIE = 'pl_session';
export const SIGNIN_COOKIE = 'pl_signin';
const SIGNIN_TTL = 600;
/** How long the desktop app has to trade its code for the session (ms). */
const HANDOFF_MS = 120_000;

interface Native {
  port: number;
  challenge: string;
}

interface Pending {
  state: string;
  nonce: string;
  verifier: string;
  returnTo: string;
  native?: Native;
}

interface Handoff {
  payload: SessionPayload;
  seconds: number;
  challenge: string;
  until: number;
}

/** The desktop app's loopback port and PKCE challenge, if this is its sign-in; null if it isn't, 'bad' if they're malformed. */
export function nativeOf(port: string | null, challenge: string | null): Native | null | 'bad' {
  if (port === null && challenge === null) return null;
  const n = Number(port);
  if (!Number.isInteger(n) || n < 1024 || n > 65535 || !challenge || !/^[A-Za-z0-9_-]{43}$/.test(challenge)) return 'bad';
  return { port: n, challenge };
}

async function readJson(req: IncomingMessage, limit = 4096): Promise<Record<string, unknown> | null> {
  let body = '';
  for await (const chunk of req) {
    body += chunk;
    if (body.length > limit) return null;
  }
  try {
    const v = JSON.parse(body);
    return v && typeof v === 'object' ? v : null;
  } catch {
    return null;
  }
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

export function createHandler(config: Config, exchange: Exchange = exchangeCode, now: () => number = Date.now) {
  const signinCookie = { path: '/auth', secure: config.secureCookies };
  // The desktop app's codes waiting to be traded: in memory, as they last two minutes at most.
  const handoffs = new Map<string, Handoff>();
  const sweep = () => {
    for (const [code, h] of handoffs) if (h.until <= now()) handoffs.delete(code);
  };

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
        const native = nativeOf(url.searchParams.get('native'), url.searchParams.get('challenge'));
        if (native === 'bad') return send(400, { error: 'bad native sign-in' });
        const pending: Pending = {
          state: randomToken(),
          nonce: randomToken(),
          verifier: randomToken(),
          returnTo: safeReturnTo(url.searchParams.get('returnTo')),
          ...(native ? { native } : {}),
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
        const back = pending?.native ? `http://127.0.0.1:${pending.native.port}/` : null;
        const fail = (message: string) =>
          redirect(back ? `${back}?error=${encodeURIComponent(message)}` : `${config.appUrl}/?auth_error=${encodeURIComponent(message)}`, [done]);

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
        if (back && pending.native) {
          // The desktop app: a one-time code to its loopback address, and no cookie in this browser.
          sweep();
          const handoff = randomToken();
          handoffs.set(handoff, { payload, seconds: identity.sessionSeconds, challenge: pending.native.challenge, until: now() + HANDOFF_MS });
          return redirect(`${back}?code=${encodeURIComponent(handoff)}`, [done]);
        }
        const cookie = serializeCookie(SESSION_COOKIE, seal(config.sessionSecret, payload, identity.sessionSeconds), {
          maxAge: identity.sessionSeconds,
          secure: config.secureCookies,
        });
        return redirect(`${config.appUrl}${pending.returnTo}`, [done, cookie]);
      }

      if (req.method === 'POST' && url.pathname === '/auth/native') {
        const body = await readJson(req);
        const code = typeof body?.code === 'string' ? body.code : '';
        const verifier = typeof body?.verifier === 'string' ? body.verifier : '';
        const h = handoffs.get(code);
        // One go only, right verifier or not.
        handoffs.delete(code);
        if (!h || h.until <= now() || codeChallenge(verifier) !== h.challenge) {
          return send(400, { error: 'Sign-in was interrupted. Please try again.' });
        }
        return send(200, {
          session: seal(config.sessionSecret, h.payload, h.seconds, now()),
          seconds: h.seconds,
          name: h.payload.name,
          email: h.payload.email,
        });
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
