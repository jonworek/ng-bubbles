const { parentPort } = require('node:worker_threads');
const UTIF = require('utif');
const { PNG } = require('pngjs');
const decodeHeic = require('heic-decode');

/**
 * @summary Convert TIFF and HEIC image bytes to a portable PNG without blocking Electron's main thread.
 * @param {Buffer} buffer - Encoded image bytes.
 * @returns {Promise<Buffer|null>} Decoded PNG or null for unsupported data.
 */
const decodePhoto = async (buffer) => {
  let pixels;
  const signature = buffer.subarray(0, 4).toString('hex');
  if (signature === '49492a00' || signature === '4d4d002a') {
    const source = buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength);
    const image = UTIF.decode(source)[0];
    const width = image?.t256?.[0];
    const height = image?.t257?.[0];
    if (!width || !height || width * height > 32000000) throw new Error('Invalid or oversized TIFF.');
    UTIF.decodeImage(source, image);
    pixels = { width, height, data: Buffer.from(UTIF.toRGBA8(image)) };
  } else if (buffer.subarray(4, 8).toString() === 'ftyp') {
    const image = await decodeHeic({ buffer });
    if (!image.width || !image.height || image.width * image.height > 32000000) throw new Error('Invalid or oversized HEIC.');
    pixels = { width: image.width, height: image.height, data: Buffer.from(image.data) };
  } else return null;
  return PNG.sync.write(pixels);
};

parentPort?.on('message', async ({ id, bytes }) => {
  try { parentPort.postMessage({ id, bytes: await decodePhoto(Buffer.from(bytes)) }); }
  catch { parentPort.postMessage({ id, bytes: null }); }
});

module.exports = { decodePhoto };
