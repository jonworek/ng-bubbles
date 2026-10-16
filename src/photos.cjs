const { Worker } = require('node:worker_threads');
const { createHash } = require('node:crypto');
const path = require('node:path');

class PhotoDecoder {
  constructor(nativeImage) {
    this.nativeImage = nativeImage;
    this.worker = null;
    this.pending = new Map();
    this.cache = new Map();
    this.nextId = 0;
  }

  /**
   * @summary Prepare JPEG, PNG, TIFF, or HEIC contact/group images as cached square PNG thumbnails.
   * @param {string|Buffer} avatar - Base64 data or binary image bytes.
   * @returns {Promise<string>} PNG data URL or empty fallback.
   */
  async prepare(avatar) {
    let bytes;
    if (Buffer.isBuffer(avatar)) bytes = avatar;
    else if (avatar?.type === 'Buffer' && Array.isArray(avatar.data) && avatar.data.length <= 16 * 1024 * 1024) bytes = Buffer.from(avatar.data);
    else if (Array.isArray(avatar) && avatar.length <= 16 * 1024 * 1024) bytes = Buffer.from(avatar);
    else if (typeof avatar === 'string' && avatar.length <= 24 * 1024 * 1024) {
      const base64 = avatar.replace(/^data:image\/(?:png|jpe?g|gif|webp|bmp|tiff?|heic|heif);base64,/i, '').replace(/\s/g, '');
      if (!/^[A-Za-z0-9+/]+={0,2}$/.test(base64)) return '';
      bytes = Buffer.from(base64, 'base64');
    } else return '';
    if (!bytes.length || bytes.length > 16 * 1024 * 1024) return '';
    const key = createHash('sha256').update(bytes).digest('hex');
    if (this.cache.has(key)) return this.cache.get(key);
    let result = '';
    try {
      let image = this.nativeImage.createFromBuffer(bytes);
      if (image.isEmpty()) {
        const decoded = await this.decode(bytes);
        if (decoded) image = this.nativeImage.createFromBuffer(decoded);
      }
      if (!image.isEmpty()) {
        const { width, height } = image.getSize();
        const edge = Math.min(width, height);
        result = image.crop({ x: Math.floor((width - edge) / 2), y: Math.floor((height - edge) / 2), width: edge, height: edge })
          .resize({ width: 96, height: 96, quality: 'best' }).toDataURL();
      }
    } catch { result = ''; }
    this.cache.set(key, result);
    if (this.cache.size > 1000) this.cache.delete(this.cache.keys().next().value);
    return result;
  }

  /**
   * @summary Decode non-native image formats in an isolated worker with a timeout.
   * @param {Buffer} bytes - Image bytes.
   * @returns {Promise<Buffer|null>} Portable image bytes.
   */
  async decode(bytes) {
    if (!this.worker) {
      const worker = new Worker(path.join(__dirname, 'photo-worker.cjs'));
      this.worker = worker;
      worker.on('message', ({ id, bytes: output }) => {
        const job = this.pending.get(id);
        if (!job) return;
        clearTimeout(job.timer);
        this.pending.delete(id);
        job.resolve(output ? Buffer.from(output) : null);
        if (!this.pending.size) worker.unref();
      });
      worker.on('error', () => { if (this.worker === worker) this.close(); });
      worker.on('exit', () => { if (this.worker === worker) this.close(); });
    }
    this.worker.ref();
    const id = ++this.nextId;
    return new Promise((resolve) => {
      const timer = setTimeout(() => this.close(), 20000);
      this.pending.set(id, { resolve, timer });
      this.worker.postMessage({ id, bytes });
    });
  }

  /** @summary Release worker resources and finish any incomplete decodes. @returns {void} Stops decoder. */
  close() {
    const worker = this.worker;
    this.worker = null;
    for (const job of this.pending.values()) { clearTimeout(job.timer); job.resolve(null); }
    this.pending.clear();
    worker?.terminate();
  }
}

module.exports = { PhotoDecoder };
