// Guards the architecture rules: gen/ stays pure and nothing uses Math.random().
import { describe, expect, it } from 'vitest';

const sources = import.meta.glob('../src/**/*.ts', { query: '?raw', import: 'default', eager: true }) as Record<
  string,
  string
>;

const stripComments = (text: string) => text.replace(/\/\*[\s\S]*?\*\/|\/\/.*$/gm, '');
const code = Object.entries(sources).map(([path, text]) => ({ path: path.replace('../src/', ''), text: stripComments(text) }));

describe('architecture rules', () => {
  it('finds the sources', () => {
    expect(code.some((f) => f.path === 'gen/index.ts')).toBe(true);
  });

  it('never calls Math.random()', () => {
    expect(code.filter((f) => /Math\.random\s*\(/.test(f.text)).map((f) => f.path)).toEqual([]);
  });

  it('keeps gen/, core/ and model/ free of render imports and the DOM', () => {
    const offenders = code.filter(
      (f) =>
        /^(gen|core|model)\//.test(f.path) &&
        (/from\s+['"][^'"]*\/(render|ui)\//.test(f.text) ||
          /\b(document|window|HTMLElement|CanvasRenderingContext2D)\b/.test(f.text)),
    );
    expect(offenders.map((f) => f.path)).toEqual([]);
  });
});
