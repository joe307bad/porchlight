// New seeds for the Regenerate button. This is UI-side entropy, not generator
// randomness, so it uses crypto rather than a seeded stream.

const ALPHABET = 'abcdefghjkmnpqrstuvwxyz23456789';

export function randomSeed(length = 6): string {
  const bytes = crypto.getRandomValues(new Uint8Array(length));
  let out = '';
  for (const b of bytes) out += ALPHABET[b % ALPHABET.length];
  return out;
}
