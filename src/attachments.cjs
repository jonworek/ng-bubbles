const fs = require('node:fs/promises');
const path = require('node:path');
const { createHash } = require('node:crypto');

class AttachmentCache {
  constructor(directory, nativeImage, decoder, log = () => {}) {
    this.directory = directory;
    this.nativeImage = nativeImage;
    this.decoder = decoder;
    this.log = log;
    this.pending = new Map();
    this.running = 0;
    this.waiters = [];
  }

  /**
   * @summary Download and cache an attachment with bounded concurrent transfers and local storage.
   * @param {object} api - Authenticated server connection.
   * @param {object} attachment - Trusted attachment metadata from cached messages.
   * @returns {Promise<object>} Safe inline media URL or file-only result.
   */
  async load(api, attachment) {
    const key = createHash('sha256').update(`${api.server}:${attachment.guid}`).digest('hex');
    if (this.pending.has(key)) return this.pending.get(key);
    const job = this.download(api, attachment, key);
    this.pending.set(key, job);
    try { return await job; } finally { this.pending.delete(key); }
  }

  /**
   * @summary Fetch media without exposing authenticated remote URLs to the renderer.
   * @param {object} api - Current server connection.
   * @param {object} attachment - Attachment metadata.
   * @param {string} key - Hashed cache filename.
   * @returns {Promise<object>} Preview data.
   */
  async download(api, attachment, key) {
    if (this.running >= 3) await new Promise((resolve) => this.waiters.push(resolve));
    this.running++;
    try {
      await fs.mkdir(this.directory, { recursive: true, mode: 0o700 });
      const target = path.join(this.directory, `${key}.json`);
      try {
        const cached = JSON.parse(await fs.readFile(target, 'utf8'));
        await fs.utimes(target, new Date(), new Date());
        this.log('attachment-cache-hit', {});
        return { kind: cached.kind, url: cached.url };
      } catch { /* A missing or damaged cache entry is downloaded again. */ }
      if (Number(attachment.totalBytes) > 16 * 1024 * 1024) throw new Error('Attachment exceeds the 16 MB automatic preview limit. Use Save attachment.');
      const bytes = await api.request(`/attachment/${encodeURIComponent(attachment.guid)}/download`, {
        responseType: 'bytes', timeout: 120000, query: { original: false },
      });
      const mime = String(attachment.mimeType || '').toLowerCase();
      let result = { kind: 'file', url: '' };
      let image = this.nativeImage.createFromBuffer(bytes);
      if (image.isEmpty() && (mime.startsWith('image/') || /\.(heic|heif|tiff?)$/i.test(attachment.transferName || ''))) {
        const decoded = await this.decoder.decode(bytes);
        if (decoded) image = this.nativeImage.createFromBuffer(decoded);
      }
      if (!image.isEmpty()) {
        const { width, height } = image.getSize();
        const scale = Math.min(1, 1280 / Math.max(width, height));
        result = { kind: 'image', url: image.resize({ width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) }).toDataURL() };
      } else if (['video/mp4', 'video/webm', 'audio/mpeg', 'audio/mp4', 'audio/aac', 'audio/ogg', 'audio/wav', 'audio/x-wav'].includes(mime)) {
        result = { kind: mime.split('/')[0], url: `data:${mime};base64,${bytes.toString('base64')}` };
      } else if (mime.startsWith('image/')) throw new Error('This image format could not be decoded. Use Save attachment.');
      // Keep file-only downloads too, so opening a conversation does not repeatedly fetch them.
      if (result.kind === 'file') result.bytes = bytes.toString('base64');
      await fs.writeFile(target, JSON.stringify(result), { mode: 0o600 });
      const entries = await Promise.all((await fs.readdir(this.directory)).filter((name) => /^[a-f0-9]{64}\.json$/.test(name))
        .map(async (name) => {
          try { return { name, ...await fs.stat(path.join(this.directory, name)) }; } catch { return null; }
        })).then((items) => items.filter(Boolean));
      let total = entries.reduce((sum, entry) => sum + entry.size, 0);
      for (const entry of entries.sort((a, b) => a.mtimeMs - b.mtimeMs)) {
        if (total <= 100 * 1024 * 1024) break;
        await fs.unlink(path.join(this.directory, entry.name)).catch(() => {});
        total -= entry.size;
      }
      this.log('attachment-loaded', { kind: result.kind, bytes: bytes.length });
      return { kind: result.kind, url: result.url };
    } catch (error) {
      this.log('attachment-failed', {});
      throw error;
    } finally {
      this.running--;
      this.waiters.shift()?.();
    }
  }
}
module.exports = { AttachmentCache };
