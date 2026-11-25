// Checks the QR encoder of configPage.html: known vectors, then a full decode of every version.
// Usage: node Jellyfin.Plugin.Sharefin.Tests/qr.test.js Jellyfin.Plugin.Sharefin/Configuration/configPage.html
'use strict';
const fs = require('fs');
const assert = require('assert');
const html = fs.readFileSync(process.argv[2], 'utf8');
const code = html.slice(html.indexOf('// ---------- QR code ----------'), html.indexOf('// ---------- list ----------'));
assert(code.length > 1000, 'QR section not found');
const qr = new Function(code + '; return { rsDivisor, rsRemainder, qrFormatBits, qrVersionBits, qrDataCodewords, qrCode };')();

// Reed-Solomon: the worked "HELLO WORLD" 1-Q example (thonky.com QR tutorial).
assert.deepStrictEqual(
  qr.rsRemainder([32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236], qr.rsDivisor(13)),
  [168, 72, 22, 82, 217, 54, 156, 0, 46, 15, 180, 122, 16]);

// Format bits, level M, from the spec table.
['101010000010010', '101000100100101', '101111001111100', '101101101001011',
 '100010111111001', '100000011001110', '100111110010111', '100101010100000']
  .forEach((s, m) => assert.strictEqual(qr.qrFormatBits(m), parseInt(s, 2), 'format mask ' + m));
// Version bits, spec table D.1.
assert.strictEqual(qr.qrVersionBits(7), 0x07C94);
assert.strictEqual(qr.qrVersionBits(8), 0x085BC);
assert.strictEqual(qr.qrVersionBits(9), 0x09A99);
assert.strictEqual(qr.qrVersionBits(10), 0x0A4D3);
assert.deepStrictEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(qr.qrDataCodewords), [16, 28, 44, 64, 86, 108, 124, 154, 182, 216]);

// Independent tables (ISO 18004, level M) for the decoder below.
const ALIGN = { 1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34], 7: [6, 22, 38], 8: [6, 24, 42], 9: [6, 26, 46], 10: [6, 28, 50] };
const GROUPS = { 1: [[1, 16]], 2: [[1, 28]], 3: [[1, 44]], 4: [[2, 32]], 5: [[2, 43]], 6: [[4, 27]], 7: [[4, 31]], 8: [[2, 38], [2, 39]], 9: [[3, 36], [2, 37]], 10: [[4, 43], [1, 44]] };
const ECC = { 1: 10, 2: 16, 3: 26, 4: 18, 5: 24, 6: 16, 7: 18, 8: 22, 9: 22, 10: 26 };
const MASKS = [(x, y) => (x + y) % 2, (x, y) => y % 2, (x) => x % 3, (x, y) => (x + y) % 3, (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2,
  (x, y) => x * y % 2 + x * y % 3, (x, y) => (x * y % 2 + x * y % 3) % 2, (x, y) => ((x + y) % 2 + x * y % 3) % 2];

function decode(g) {
  const size = g.length, v = (size - 17) / 4;
  assert(Number.isInteger(v));
  // Finders.
  for (const [ox, oy] of [[0, 0], [size - 7, 0], [0, size - 7]]) {
    for (let y = 0; y < 7; y++) for (let x = 0; x < 7; x++) {
      const d = Math.max(Math.abs(x - 3), Math.abs(y - 3));
      assert.strictEqual(g[oy + y][ox + x], d !== 2, 'finder');
    }
  }
  for (let i = 8; i < size - 8; i++) { assert.strictEqual(g[6][i], i % 2 === 0); assert.strictEqual(g[i][6], i % 2 === 0); }
  assert.strictEqual(g[size - 8][8], true, 'dark module');
  const fn = (x, y) => (x <= 8 && y <= 8) || (x >= size - 8 && y <= 8) || (x <= 8 && y >= size - 8) || x === 6 || y === 6
    || (v >= 7 && ((x >= size - 11 && x <= size - 9 && y <= 5) || (y >= size - 11 && y <= size - 9 && x <= 5)))
    || ALIGN[v].some((ay, i) => ALIGN[v].some((ax, j) => {
      const corner = (i === 0 && j === 0) || (i === 0 && j === ALIGN[v].length - 1) || (i === ALIGN[v].length - 1 && j === 0);
      return !corner && Math.abs(x - ax) <= 2 && Math.abs(y - ay) <= 2;
    }));
  ALIGN[v].forEach((ay) => ALIGN[v].forEach((ax) => {
    if (fn(ax, ay) && !(ax <= 8 && ay <= 8) && !(ax >= size - 8 && ay <= 8) && !(ax <= 8 && ay >= size - 8)) {
      assert.strictEqual(g[ay][ax], true, 'alignment centre');
      assert.strictEqual(g[ay][ax + 1], false, 'alignment ring');
    }
  }));
  // Format: copy 1 read along the spec order (bit 14 first), copy 2 too; both must name the same mask.
  const c1 = [[0, 8], [1, 8], [2, 8], [3, 8], [4, 8], [5, 8], [7, 8], [8, 8], [8, 7], [8, 5], [8, 4], [8, 3], [8, 2], [8, 1], [8, 0]]
    .map(([y, x]) => Number(g[y][x]));
  const word1 = parseInt(c1.reverse().join(''), 2);
  let mask = -1;
  for (let m = 0; m < 8; m++) if (qr.qrFormatBits(m) === word1) mask = m;
  assert(mask >= 0, 'format copy 1 unreadable');
  // Unmask and read the zigzag.
  const bits = [];
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vert = 0; vert < size; vert++) {
      for (let h = 0; h < 2; h++) {
        const x = right - h, y = ((right + 1) & 2) === 0 ? size - 1 - vert : vert;
        if (!fn(x, y)) bits.push(Number(g[y][x]) ^ Number(MASKS[mask](x, y) === 0));
      }
    }
  }
  const words = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) words.push(parseInt(bits.slice(i, i + 8).join(''), 2));
  // De-interleave.
  const lens = [];
  GROUPS[v].forEach(([n, len]) => { for (let i = 0; i < n; i++) lens.push(len); });
  const blocks = lens.map(() => []), eccs = lens.map(() => []);
  let k = 0;
  for (let c = 0; c < Math.max(...lens); c++) lens.forEach((len, b) => { if (c < len) blocks[b].push(words[k++]); });
  for (let c = 0; c < ECC[v]; c++) lens.forEach((_, b) => eccs[b].push(words[k++]));
  blocks.forEach((b, i) => assert.deepStrictEqual(qr.rsRemainder(b, qr.rsDivisor(ECC[v])), eccs[i], 'ecc block ' + i));
  const data = [].concat(...blocks).map((b) => b.toString(2).padStart(8, '0')).join('');
  assert.strictEqual(data.slice(0, 4), '0100', 'byte mode');
  const cc = v < 10 ? 8 : 16, n = parseInt(data.slice(4, 4 + cc), 2);
  const out = [];
  for (let i = 0; i < n; i++) out.push(parseInt(data.slice(4 + cc + i * 8, 12 + cc + i * 8), 2));
  return { v, mask, text: Buffer.from(out).toString('utf8') };
}

const seen = new Set();
const texts = [5, 14, 26, 42, 62, 84, 106, 122, 152, 180, 213].map((n) => 'https://media.exemple.fr/Sharefin/s/'.padEnd(n, 'x').slice(0, n));
texts.push('https://média.exemple.fr/Sharefin/s/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdEfG');
for (const t of texts) {
  const r = decode(qr.qrCode(t));
  assert.strictEqual(r.text, t);
  seen.add(r.v);
  console.log('v' + r.v, 'mask', r.mask, t.length + ' chars ok');
}
assert.strictEqual(seen.size, 10, 'every version 1-10 exercised');
assert.strictEqual(qr.qrCode('x'.repeat(214)), null);
console.log('all QR checks passed');
