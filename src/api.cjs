const { normalizeServer } = require('./model.cjs');

class BlueBubblesApi {
  constructor(server, password, log = () => {}) {
    this.server = normalizeServer(server);
    this.password = password;
    this.log = log;
  }

  /**
   * @summary Call a BlueBubbles REST endpoint with a bounded timeout.
   * @param {string} path - Relative API path.
   * @param {object} options - HTTP method, JSON body, and query parameters.
   * @returns {Promise<object>} Response data.
   */
  async request(path, { method = 'GET', body, query = {}, responseType = 'json', allowMissing = false, timeout = 20000 } = {}) {
    const started = Date.now();
    const route = path.replace(/\/(chat|attachment)\/[^/]+/g, '/$1/:id');
    const url = new URL(`${this.server}/api/v1${path}`);
    url.searchParams.set('password', this.password);
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null) url.searchParams.set(key, String(value));
    }
    let response;
    try {
      response = await fetch(url, {
        method, redirect: 'error', signal: AbortSignal.timeout(timeout),
        headers: { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      this.log('http', { route, method, status: 'network-error', durationMs: Date.now() - started });
      throw new Error('Cannot reach the server. Check its URL, connection, and TLS certificate.');
    }
    this.log('http', { route, method, status: response.status, durationMs: Date.now() - started });
    if (response.status === 401 || response.status === 403) throw new Error('The server rejected the password.');
    if (allowMissing && response.status === 404) return null;
    if (responseType === 'bytes') {
      if (!response.ok) throw new Error(`Server image request failed (${response.status}).`);
      if (Number(response.headers.get('content-length')) > 16 * 1024 * 1024) throw new Error('Server image is too large.');
      const chunks = [];
      let length = 0;
      for await (const chunk of response.body) {
        length += chunk.length;
        if (length > 16 * 1024 * 1024) throw new Error('Server attachment exceeds the 16 MB automatic preview limit.');
        chunks.push(Buffer.from(chunk));
      }
      return Buffer.concat(chunks);
    }
    let result;
    try { result = await response.json(); } catch { throw new Error('The server returned an invalid response. Check the URL.'); }
    if (!response.ok || (result.status && result.status >= 400)) {
      throw new Error(`Server request failed (${response.status}). ${String(result.message || 'Please try again.').slice(0, 180)}`);
    }
    return result.data ?? result;
  }

  /**
   * @summary Fetch conversations with participants and latest message.
   * @returns {Promise<Array>} Conversation list.
   */
  async chats() {
    const chats = [];
    for (let offset = 0; offset < 10000; offset += 100) {
      const batch = await this.request('/chat/query', {
        method: 'POST', body: { with: ['participants', 'lastmessage'], sort: 'lastmessage', offset, limit: 100 },
      });
      if (!Array.isArray(batch)) throw new Error('Unexpected conversation response.');
      chats.push(...batch);
      if (batch.length < 100) break;
    }
    return chats;
  }

  /**
   * @summary Fetch one page of conversation history.
   * @param {string} guid - Conversation GUID.
   * @param {number} before - Optional oldest timestamp.
   * @returns {Promise<Array>} Messages, newest first.
   */
  async messages(guid, before) {
    return this.request(`/chat/${encodeURIComponent(guid)}/message`, {
      query: { with: 'attachments,handle', sort: 'DESC', limit: 100, before },
    });
  }

  /**
   * @summary Fetch all unread incoming messages, including ones predating this installation.
   * @returns {Promise<Array>} Unread messages with their conversation GUIDs.
   */
  async unreadMessages() {
    const messages = [];
    for (let offset = 0; ; offset += 100) {
      const batch = await this.request('/message/query', {
        method: 'POST', body: { offset, limit: 100, sort: 'ASC', with: ['chats'],
          where: [{ statement: 'message.is_from_me = :fromMe AND message.is_read = :read', args: { fromMe: 0, read: 0 } }] },
      });
      if (!Array.isArray(batch)) throw new Error('Unexpected unread-message response.');
      messages.push(...batch);
      if (batch.length < 100) return messages;
    }
  }

  /**
   * @summary Fetch the group's server-provided avatar without exposing credentials in image URLs.
   * @param {string} guid - Group conversation GUID.
   * @returns {Promise<Buffer|null>} Image bytes or null when the group has no icon.
   */
  async groupIcon(guid) {
    return this.request(`/chat/${encodeURIComponent(guid)}/icon`, { responseType: 'bytes', allowMissing: true });
  }

  /**
   * @summary Send text once; never automatically retry an ambiguous send.
   * @param {string} chatGuid - Conversation GUID.
   * @param {string} message - Text body.
   * @param {string} tempGuid - Client-generated identifier.
   * @param {string} method - Explicit server sending method.
   * @returns {Promise<object>} Server acknowledgement.
   */
  async send(chatGuid, message, tempGuid, method) {
    return this.request('/message/text', {
      method: 'POST', body: { chatGuid, message, tempGuid, method },
    });
  }
}

module.exports = { BlueBubblesApi };
