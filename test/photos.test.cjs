const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { PNG } = require('pngjs');
const { decodePhoto } = require('../src/photo-worker.cjs');

for (const format of ['tiff', 'heic']) {
  test(`decodes a real ${format.toUpperCase()} contact portrait into portable PNG pixels`, async () => {
    const bytes = fs.readFileSync(path.join(__dirname, `fixtures/contact.${format}`));
    const result = PNG.sync.read(await decodePhoto(bytes));
    assert.equal(result.width, 180);
    assert.equal(result.height, 120);
    assert.equal(result.data.length, 180 * 120 * 4);
    assert.equal(result.data[3], 255);
  });
}

test('invalid and unrelated image bytes do not masquerade as supported Mac photos', async () => {
  assert.equal(await decodePhoto(Buffer.from('not an image')), null);
  await assert.rejects(decodePhoto(Buffer.from('49492a00', 'hex')));
});
