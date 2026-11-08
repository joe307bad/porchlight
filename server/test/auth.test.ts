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

test('the desktop app signs in through a loopback address and trades a one-time code', async () => {
  const { codeChallenge } = await import('../src/oidc.ts');
  const verifier = 'v'.repeat(43);
  const challenge = codeChallenge(verifier);

  // Malformed: refused before anything else.
  for (const q of ['native=80&challenge=' + challenge, 'native=50000&challenge=short', 'native=50000']) {
    const bad = await fetch(`${base}/auth/login?${q}`, { redirect: 'manual' });
    assert.equal(bad.status, 400, q);
  }

  let r = await fetch(`${base}/auth/login?native=50123&challenge=${challenge}`, { redirect: 'manual' });
  assert.equal(r.status, 302);
  const state = new URL(r.headers.get('location')!).searchParams.get('state');
  const jar = cookiesOf(r)[0].split(';')[0];

  r = await fetch(`${base}/auth/callback?code=the-code&state=${state}`, { redirect: 'manual', headers: { cookie: jar } });
  const back = new URL(r.headers.get('location')!);
  assert.equal(back.origin, 'http://127.0.0.1:50123');
  // No session cookie in the browser: only the app gets one.
  assert.ok(!cookiesOf(r).some((c) => c.startsWith(`${SESSION_COOKIE}=`) && !c.includes('Max-Age=0')));
  const code = back.searchParams.get('code')!;
  assert.ok(code);

  const trade = (body: unknown) =>
    fetch(`${base}/auth/native`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

  // Someone else who saw the code but hasn't the verifier: refused, and the code is spent.
  r = await trade({ code, verifier: 'w'.repeat(43) });
  assert.equal(r.status, 400);
  r = await trade({ code, verifier });
  assert.equal(r.status, 400);

  // Again, properly this time.
  r = await fetch(`${base}/auth/login?native=50123&challenge=${challenge}`, { redirect: 'manual' });
  const state2 = new URL(r.headers.get('location')!).searchParams.get('state');
  const jar2 = cookiesOf(r)[0].split(';')[0];
  r = await fetch(`${base}/auth/callback?code=the-code&state=${state2}`, { redirect: 'manual', headers: { cookie: jar2 } });
  const code2 = new URL(r.headers.get('location')!).searchParams.get('code')!;
  r = await trade({ code: code2, verifier });
  assert.equal(r.status, 200);
  const got = await r.json();
  assert.equal(got.name, 'Ada');
  assert.equal(got.seconds, 3600);
  // The session works as the cookie, and the code doesn't work twice.
  r = await fetch(`${base}/auth/me`, { headers: { cookie: `${SESSION_COOKIE}=${got.session}` } });
  assert.equal(r.status, 200);
  r = await trade({ code: code2, verifier });
  assert.equal(r.status, 400);

  // A failed sign-in goes back to the app too, to say so.
  r = await fetch(`${base}/auth/login?native=50123&challenge=${challenge}`, { redirect: 'manual' });
  const jar3 = cookiesOf(r)[0].split(';')[0];
  r = await fetch(`${base}/auth/callback?error=access_denied`, { redirect: 'manual', headers: { cookie: jar3 } });
  assert.match(r.headers.get('location')!, /^http:\/\/127\.0\.0\.1:50123\/\?error=/);
});

test('a desktop code runs out after two minutes', async () => {
  const { codeChallenge } = await import('../src/oidc.ts');
  let clock = Date.now();
  const handler = createHandler(config, async () => ({ sub: 'u1', name: 'Ada', email: null, idToken: 't', sessionSeconds: 3600 }), () => clock);
  const s = createServer((req, res) => void handler(req, res));
  await new Promise<void>((r) => s.listen(0, r));
  const at = `http://127.0.0.1:${(s.address() as { port: number }).port}`;
  try {
    const verifier = 'v'.repeat(43);
    let r = await fetch(`${at}/auth/login?native=50123&challenge=${codeChallenge(verifier)}`, { redirect: 'manual' });
    const state = new URL(r.headers.get('location')!).searchParams.get('state');
    r = await fetch(`${at}/auth/callback?code=c&state=${state}`, { redirect: 'manual', headers: { cookie: cookiesOf(r)[0].split(';')[0] } });
    const code = new URL(r.headers.get('location')!).searchParams.get('code');
    clock += 121_000;
    r = await fetch(`${at}/auth/native`, { method: 'POST', body: JSON.stringify({ code, verifier }) });
    assert.equal(r.status, 400);
  } finally {
    s.close();
  }
});
