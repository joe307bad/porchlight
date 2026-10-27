// Node's Math results for random inputs, as raw bits, for JsMath to match.
// node "unity/Tools~/parity/math.mjs" out.bin [count]
import { writeFileSync } from 'node:fs';
const out = process.argv[2] ?? 'math.bin';
const n = Number(process.argv[3] ?? 1000000);
let s = 12345;
const rnd = () => { s = (s * 1103515245 + 12345) >>> 0; return s / 4294967296; };
const fns = [
  ['atan2', (a, b) => Math.atan2(a, b)],
  ['acos', (a) => Math.acos(a)],
  ['tanh', (a) => Math.tanh(a)],
  ['sin', (a) => Math.sin(a)],
  ['cos', (a) => Math.cos(a)],
  ['atan', (a) => Math.atan(a)],
  ['exp', (a) => Math.exp(a)],
  ['log', (a) => Math.log(a)],
  ['pow', (a, b) => Math.pow(a, b)],
];
const buf = new Float64Array(n * fns.length * 3);
let k = 0;
for (let i = 0; i < n; i++) {
  const scale = [1, 4, 0.01, 50, 1000][i % 5];
  for (const [name, f] of fns) {
    let a = (rnd() * 2 - 1) * scale, b = (rnd() * 2 - 1) * scale;
    if (name === 'acos') a = rnd() * 2 - 1;
    if (name === 'log') a = Math.abs(a) + 1e-9;
    if (name === 'pow') { a = Math.abs(a) + 1e-6; b = (rnd() * 2 - 1) * 2; }
    if (i % 97 === 0 && name === 'atan2') b = 0;
    buf[k++] = a; buf[k++] = b; buf[k++] = f(a, b);
  }
}
writeFileSync(out, Buffer.from(buf.buffer));
console.log('wrote', n, 'samples x', fns.length);
