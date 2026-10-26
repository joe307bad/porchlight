// The handshake with Keycloak: authorization code flow, confidential client,
// PKCE on top. In production Keycloak brokers Google and every sign-in carries
// `kc_idp_hint=google`, so the user goes straight to Google and never sees a
// Keycloak page. In dev there's no hint and Keycloak's own form takes the
// seeded user.

import { createHash, randomBytes } from 'node:crypto';
import { createRemoteJWKSet, jwtVerify } from 'jose';
import type { Config } from './config.ts';

export const randomToken = () => randomBytes(32).toString('base64url');
export const codeChallenge = (verifier: string) => createHash('sha256').update(verifier).digest('base64url');

/** Keycloak's endpoints, reached from this server (the container network in production). */
const backchannel = (config: Config, path: string) =>
  `${config.keycloakInternalUrl ?? config.issuer}${path}`;

export function authorizeUrl(config: Config, p: { state: string; nonce: string; verifier: string }): string {
  const query = new URLSearchParams({
    client_id: config.clientId,
    redirect_uri: config.redirectUri,
    response_type: 'code',
    scope: 'openid email profile',
    state: p.state,
    nonce: p.nonce,
    code_challenge: codeChallenge(p.verifier),
    code_challenge_method: 'S256',
  });
  if (config.idpHint) query.set('kc_idp_hint', config.idpHint);
  // The browser goes to the public issuer, never the internal one.
  return `${config.issuer}/protocol/openid-connect/auth?${query}`;
}

export interface Identity {
  sub: string;
  name: string;
  email: string | null;
  /** Handed back at sign-out so Keycloak ends its session without asking. */
  idToken: string;
  /** How long Keycloak's session lasts (refresh_expires_in), seconds. */
  sessionSeconds: number;
}

let jwks: ReturnType<typeof createRemoteJWKSet> | null = null;

const str = (v: unknown): string | null => (typeof v === 'string' && v ? v : null);

/** Redeem the code and verify the id token against Keycloak's published keys. */
export async function exchangeCode(config: Config, code: string, verifier: string, nonce: string): Promise<Identity> {
  const response = await fetch(backchannel(config, '/protocol/openid-connect/token'), {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code',
      code,
      redirect_uri: config.redirectUri,
      client_id: config.clientId,
      client_secret: config.clientSecret,
      code_verifier: verifier,
    }),
    signal: AbortSignal.timeout(15_000),
  });
  if (!response.ok) throw new Error(`token exchange failed: ${response.status} ${await response.text()}`);
  const tokens = (await response.json()) as { id_token?: string; refresh_expires_in?: number };
  if (!tokens.id_token) throw new Error('no id_token in the token response');

  jwks ??= createRemoteJWKSet(new URL(backchannel(config, '/protocol/openid-connect/certs')));
  const { payload } = await jwtVerify(tokens.id_token, jwks, { issuer: config.issuer, audience: config.clientId });
  if (payload.nonce !== nonce) throw new Error('id_token nonce does not match the request');
  const sub = str(payload.sub);
  if (!sub) throw new Error('id_token has no subject');

  const email = str(payload.email);
  return {
    sub,
    name: str(payload.name) ?? str(payload.preferred_username) ?? email ?? 'Signed in',
    email,
    idToken: tokens.id_token,
    sessionSeconds: tokens.refresh_expires_in || 86_400,
  };
}

/** Keycloak's end-session URL; with the id token it ends the session and comes straight back. */
export function logoutUrl(config: Config, idToken: string | null): string {
  const query = new URLSearchParams({ client_id: config.clientId, post_logout_redirect_uri: `${config.appUrl}/` });
  if (idToken) query.set('id_token_hint', idToken);
  return `${config.issuer}/protocol/openid-connect/logout?${query}`;
}
