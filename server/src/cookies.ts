// Signed cookies. Every token this service holds for the browser goes in one
// of these: HttpOnly (page script can't read it), SameSite=Lax (not sent on
// cross-site subrequests or POSTs, but still on the top-level GET back from
// Keycloak), Secure whenever the app is served over HTTPS.
//
// The value is base64url(JSON) + "." + HMAC-SHA256 of it, with an expiry
// inside, so it can't be forged or kept alive past its time.

import { createHmac, timingSafeEqual } from 'node:crypto';

export interface CookieOptions {
  maxAge: number; // seconds
  path?: string;
  secure: boolean;
}

export function serializeCookie(name: string, value: string, opts: CookieOptions): string {
  const parts = [`${name}=${value}`, `Path=${opts.path ?? '/'}`, `Max-Age=${opts.maxAge}`, 'HttpOnly', 'SameSite=Lax'];
  if (opts.secure) parts.push('Secure');
  return parts.join('; ');
}

export function clearCookie(name: string, opts: { path?: string; secure: boolean }): string {
  return serializeCookie(name, '', { ...opts, maxAge: 0 });
}

export function parseCookies(header: string | undefined): Map<string, string> {
  const out = new Map<string, string>();
  for (const part of (header ?? '').split(';')) {
    const eq = part.indexOf('=');
    if (eq > 0) out.set(part.slice(0, eq).trim(), part.slice(eq + 1).trim());
  }
  return out;
}

const mac = (secret: string, data: string) => createHmac('sha256', secret).update(data).digest('base64url');

/** Sign `payload`, valid for `ttl` seconds. */
export function seal(secret: string, payload: object, ttl: number, now = Date.now()): string {
  const data = Buffer.from(JSON.stringify({ ...payload, exp: Math.floor(now / 1000) + ttl })).toString('base64url');
  return `${data}.${mac(secret, data)}`;
}

/** The payload, or null if the value is malformed, tampered with or expired. */
export function unseal<T>(secret: string, value: string | undefined, now = Date.now()): T | null {
  if (!value) return null;
  const dot = value.lastIndexOf('.');
  if (dot < 1) return null;
  const data = value.slice(0, dot);
  const given = Buffer.from(value.slice(dot + 1));
  const expected = Buffer.from(mac(secret, data));
  if (given.length !== expected.length || !timingSafeEqual(given, expected)) return null;
  try {
    const payload = JSON.parse(Buffer.from(data, 'base64url').toString('utf8')) as T & { exp: number };
    return typeof payload.exp === 'number' && payload.exp * 1000 > now ? payload : null;
  } catch {
    return null;
  }
}
