const { app, nativeImage } = require('electron');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

/**
 * @summary Exercise image decoding from the actual Windows ASAR payload using the local Electron runtime.
 * @returns {Promise<void>} Packaged worker and dependency verification.
 */
const run = async () => {
  const archive = path.join(__dirname, '../dist/win-unpacked/resources/app.asar');
  const { PhotoDecoder } = require(path.join(archive, 'src/photos.cjs'));
  const decoder = new PhotoDecoder(nativeImage);
  try {
    for (const format of ['jpg', 'tiff', 'heic']) {
      const input = fs.readFileSync(path.join(__dirname, `fixtures/contact.${format}`));
      const result = await decoder.prepare(input);
      assert.ok(result.startsWith('data:image/png;base64,'), `${format} must decode from the packaged application`);
      assert.deepEqual(nativeImage.createFromDataURL(result).getSize(), { width: 96, height: 96 });
    }
    console.log('PASS: packaged ASAR worker and JPEG/TIFF/HEIC dependencies produce 96px portraits.');
  } finally { decoder.close(); }
};

app.whenReady().then(run).then(() => app.exit(0)).catch((error) => { console.error(error); app.exit(1); });
