// Everything the auth service needs, from the environment. In production
// infra/docker/deploy.sh writes these into .env from Secrets Manager; in dev
// they come from server/.env.development.

export interface Config {
  port: number;
  /** Where the app is served, e.g. https://porchlight.joebad.com (no trailing slash). */
  appUrl: string;
  /** Public realm URL; the `iss` of every token. */
  issuer: string;
  /** The same realm as reached from this server, e.g. http://keycloak:8080/realms/porchlight. */
  keycloakInternalUrl: string | null;
  clientId: string;
  clientSecret: string;
  /** `google` in production, unset in dev. */
  idpHint: string | null;
  sessionSecret: string;
  /** Exactly as registered on the Keycloak client. */
  redirectUri: string;
  secureCookies: boolean;
}

const trim = (url: string) => url.replace(/\/+$/, '');

function required(env: NodeJS.ProcessEnv, key: string): string {
  const value = env[key];
  if (!value) throw new Error(`${key} is not set`);
  return value;
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const appUrl = trim(required(env, 'APP_URL'));
  const sessionSecret = required(env, 'SESSION_SECRET');
  if (sessionSecret.length < 32) throw new Error('SESSION_SECRET must be at least 32 characters');
  return {
    port: Number(env.PORT || 3017),
    appUrl,
    issuer: trim(required(env, 'KEYCLOAK_ISSUER')),
    keycloakInternalUrl: env.KEYCLOAK_INTERNAL_URL ? trim(env.KEYCLOAK_INTERNAL_URL) : null,
    clientId: required(env, 'KEYCLOAK_CLIENT_ID'),
    clientSecret: required(env, 'KEYCLOAK_CLIENT_SECRET'),
    idpHint: env.KEYCLOAK_IDP_HINT || null,
    sessionSecret,
    redirectUri: `${appUrl}/auth/callback`,
    secureCookies: appUrl.startsWith('https://'),
  };
}
