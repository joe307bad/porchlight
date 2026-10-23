// JSON encode/decode for TownModel. Typed arrays become
// { $typed: 'Float32Array', base64: '...' } so the model round-trips exactly.

import type { TownModel } from './types';

const TYPED = { Float32Array, Uint8Array, Int32Array } as const;
type TypedName = keyof typeof TYPED;
type Typed = InstanceType<(typeof TYPED)[TypedName]>;

interface EncodedTyped {
  $typed: TypedName;
  base64: string;
}

function typedName(v: unknown): TypedName | null {
  for (const name of Object.keys(TYPED) as TypedName[]) if (v instanceof TYPED[name]) return name;
  return null;
}

function toBase64(bytes: Uint8Array): string {
  let bin = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    bin += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(bin);
}

function fromBase64(b64: string): Uint8Array {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}

export function encodeModel(model: TownModel, space?: number): string {
  return JSON.stringify(
    model,
    (_key, value) => {
      const name = typedName(value);
      if (!name) return value;
      const t = value as Typed;
      const encoded: EncodedTyped = {
        $typed: name,
        base64: toBase64(new Uint8Array(t.buffer, t.byteOffset, t.byteLength)),
      };
      return encoded;
    },
    space,
  );
}

export function decodeModel(json: string): TownModel {
  return JSON.parse(json, (_key, value) => {
    if (value && typeof value === 'object' && typeof value.$typed === 'string' && value.$typed in TYPED) {
      const bytes = fromBase64((value as EncodedTyped).base64);
      return new TYPED[(value as EncodedTyped).$typed](bytes.buffer as ArrayBuffer);
    }
    return value;
  }) as TownModel;
}
