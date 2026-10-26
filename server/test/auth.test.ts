import assert from 'node:assert/strict';
import { createServer, type Server } from 'node:http';
import { after, before, test } from 'node:test';
import { createHandler, safeReturnTo, SESSION_COOKIE, SIGNIN_COOKIE } from '../src/app.ts';
import { loadConfig } from '../src/config.ts';
import { parseCookies, seal, unseal } from '../src/cookies.ts';

const SECRET = 'x'.repeat(40);

test('seal/unseal round-trips, and rejects tampering and expiry', () => {
  const v = seal(SECRET, { a: 1 }, 60);
  assert.equal(unseal<{ a: number }>(SECRET, v)?.a, 1);
  assert.equal(unseal(SECRET, v.slice(0, -2) + 'AA'), null);
  assert.equal(unseal('y'.repeat(40), v), null);
  assert.equal(unseal(SECRET, v, Date.now() + 61_000), null);
});

test('returnTo only ever stays on this site', () => {
  assert.equal(safeReturnTo('/#seed=abc'), '/#seed=abc');
  for (const bad of ['https://evil.example', '//evil.example', '/\\evil.example', null, '']) {
    assert.equal(safeReturnTo(bad), '/');
  }
});

const config = loadConfig({
  APP_URL: 'https://porchlight.example',
  KEYCLOAK_ISSUER: 'https://auth.porchlight.example/realms/porchlight',
  KEYCLOAK_CLIENT_ID: 'porchlight-web',
  KEYCLOAK_CLIENT_SECRET: 's',
  KEYCLOAK_IDP_HINT: 'google',
  SESSION_SECRET: SECRET,
});

let server: Server;
let base = '';
before(async () => {
  const handler = createHandler(config, async (_c, code, verifier, nonce) => {
    assert.equal(code, 'the-code');
    assert.ok(verifier && nonce);
    return { sub: 'u1', name: 'Ada', email: 'ada@example.com', idToken: 'id.tok.en', sessionSeconds: 3600 };
  });
  server = createServer((req, res) => void handler(req, res));
  await new Promise<void>((r) => server.listen(0, r));
  base = `http://127.0.0.1:${(server.address() as { port: number }).port}`;
});
after(() => server.close());

const cookiesOf = (r: Response) => r.headers.getSetCookie();

test('the full sign-in sets HttpOnly SameSite=Lax Secure cookies and never exposes the token', async () => {
  let r = await fetch(`${base}/auth/me`);
  assert.equal(r.status, 401);
  assert.deepEqual(await r.json(), { signIn: 'google' });

  r = await fetch(`${base}/auth/login?returnTo=${encodeURIComponent('/#seed=abc')}`, { redirect: 'manual' });
  assert.equal(r.status, 302);
  const to = new URL(r.headers.get('location')!);
  assert.equal(to.origin + to.pathname, 'https://auth.porchlight.example/realms/porchlight/protocol/openid-connect/auth');
  assert.equal(to.searchParams.get('kc_idp_hint'), 'google');
  assert.equal(to.searchParams.get('code_challenge_method'), 'S256');
  assert.equal(to.searchParams.get('redirect_uri'), 'https://porchlight.example/auth/callback');
  const [signin] = cookiesOf(r);
  assert.match(signin, /^pl_signin=.*; Path=\/auth; Max-Age=600; HttpOnly; SameSite=Lax; Secure$/);

  const jar = `${SIGNIN_COOKIE}=${parseCookies(signin.split(';')[0]).get(SIGNIN_COOKIE)}`;
  r = await fetch(`${base}/auth/callback?code=the-code&state=wrong`, { redirect: 'manual', headers: { cookie: jar } });
  assert.match(r.headers.get('location')!, /auth_error=/);

  r = await fetch(`${base}/auth/callback?code=the-code&state=${to.searchParams.get('state')}`, {
    redirect: 'manual',
    headers: { cookie: jar },
  });
  assert.equal(r.headers.get('location'), 'https://porchlight.example/#seed=abc');
  const session = cookiesOf(r).find((c) => c.startsWith(`${SESSION_COOKIE}=`))!;
  assert.match(session, /; Path=\/; Max-Age=3600; HttpOnly; SameSite=Lax; Secure$/);
  assert.ok(cookiesOf(r).some((c) => c.startsWith(`${SIGNIN_COOKIE}=;`) && c.includes('Max-Age=0')));

  r = await fetch(`${base}/auth/me`, { headers: { cookie: session.split(';')[0] } });
  assert.equal(r.status, 200);
  const me = await r.json();
  assert.deepEqual(me, { name: 'Ada', email: 'ada@example.com' }); // no token in the response

  r = await fetch(`${base}/auth/logout`, { method: 'POST', redirect: 'manual', headers: { cookie: session.split(';')[0] } });
  assert.equal(r.status, 303);
  const out = new URL(r.headers.get('location')!);
  assert.equal(out.searchParams.get('id_token_hint'), 'id.tok.en');
  assert.equal(out.searchParams.get('post_logout_redirect_uri'), 'https://porchlight.example/');
  assert.ok(cookiesOf(r).some((c) => c.startsWith(`${SESSION_COOKIE}=;`) && c.includes('Max-Age=0')));
});
