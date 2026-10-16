const { EventEmitter } = require('node:events');
const { randomUUID } = require('node:crypto');
const { io } = require('socket.io-client');
const { BlueBubblesApi } = require('./api.cjs');
const { addressKey, mergeMessages, chatTitle, messageText } = require('./model.cjs');

class MessagingClient extends EventEmitter {
  constructor({ cache = {}, save = () => {}, notify = () => {}, isViewing = () => false, preparePhoto = () => '', log = () => {} } = {}) {
    super();
    this.log = log;
    this.readRequests = new Map();
    this.contactDetails = {};
    this.chats = cache.chats || [];
    this.histories = cache.histories || {};
    this.contacts = cache.contacts || {};
    this.contactPhotos = cache.contactPhotos || {};
    this.groupPhotos = cache.groupPhotos || {};
    this.groupPhotoChecked = {};
    this.groupPhotoRequests = new Map();
    this.groupPhotoVersions = new Map();
    this.photoStatus = cache.photoStatus || {};
    this.contactsUpdatedAt = 0;
    this.preparePhoto = preparePhoto;
    this.cursor = cache.cursor || Date.now();
    this.seen = new Set(cache.seen || []);
    this.unread = new Set(cache.unread || []);
    this.readStates = cache.readStates || {};
    this.readRevision = 0;
    this.readChanges = new Map();
    this.canMarkRead = false;
    this.readSyncError = '';
    this.save = save;
    this.notify = notify;
    this.isViewing = isViewing;
    this.status = 'offline';
    this.error = '';
    this.active = null;
    this.syncing = null;
    this.api = null;
  }

  /**
   * @summary Connect background messaging and start periodic recovery.
   * @param {object} settings - Validated server credentials and send method.
   * @returns {Promise<void>} Completion of initial synchronization.
   */
  async connect(settings) {
    this.stop();
    this.settings = settings;
    const api = new BlueBubblesApi(settings.server, settings.password, this.log);
    this.api = api;
    this.status = 'connecting';
    this.publish();
    try {
      const info = await api.request('/server/info');
      if (api !== this.api) return;
      this.canMarkRead = info.private_api === true;
    } catch { this.canMarkRead = null; }
    this.log('capabilities', { privateApi: this.canMarkRead });
    this.socket = io(api.server, {
      query: { guid: settings.password }, transports: ['websocket', 'polling'],
      reconnection: true, reconnectionDelay: 1000, reconnectionDelayMax: 15000, timeout: 15000,
    });
    this.socket.on('connect', () => { this.sync().catch(() => {}); });
    this.socket.on('disconnect', () => { this.status = 'reconnecting'; this.publish(); });
    this.socket.on('connect_error', () => {
      this.status = 'reconnecting';
      this.error = 'Live connection interrupted. Retrying automatically; periodic message checks continue.';
      this.publish();
    });
    this.socket.on('new-message', (message) => {
      this.accept(message);
      this.sync().catch(() => {});
    });
    this.socket.on('updated-message', (message) => this.accept(message, false));
    this.socket.on('chat-read-status-changed', (event) => this.applyReadStatus(event));
    for (const event of ['group-icon-changed', 'group-icon-removed']) {
      this.socket.on(event, (message) => {
        const guid = message?.chats?.[0]?.guid || message?.chatGuid;
        if (!guid) return;
        this.groupPhotoVersions.set(guid, (this.groupPhotoVersions.get(guid) || 0) + 1);
        if (event === 'group-icon-removed') {
          delete this.groupPhotos[guid];
          this.groupPhotoChecked[guid] = Date.now();
          this.persist(); this.publish();
        } else this.loadGroupPhoto(api, guid, true).catch(() => {});
      });
    }
    for (const event of ['participant-added', 'participant-removed', 'participant-left', 'group-name-change']) {
      this.socket.on(event, () => this.sync().catch(() => {}));
    }
    this.timer = setInterval(() => this.sync().catch(() => {}), 20000);
    this.timer.unref?.();
    this.loadContacts(api).catch(() => {});
    await this.sync();
  }

  /**
   * @summary Recover missed messages, refresh conversations, and persist cache.
   * @returns {Promise<void>} Synchronization completion.
   */
  async sync() {
    if (!this.api) return;
    if (this.syncing) return this.syncing;
    const api = this.api;
    this.syncing = (async () => {
      try {
        const chats = await api.chats();
        if (api !== this.api) return;
        this.chats = chats;
        this.loadGroupPhotos(api).catch(() => {});
        const revision = this.readRevision;
        const unreadMessages = await api.unreadMessages();
        if (api !== this.api) return;
        const serverUnread = new Set();
        const rawServerUnread = new Set();
        for (const message of unreadMessages) {
          for (const chat of message.chats || []) {
            rawServerUnread.add(chat.guid);
            const readState = this.readStates[chat.guid];
            const useCutoff = readState && (readState.localOnly || Date.now() - (readState.confirmedAt || 0) < 5000);
            if (!useCutoff || (message.dateCreated || 0) > readState.through
              || (message.dateCreated === readState.through && !(readState.guids || []).includes(message.guid))) serverUnread.add(chat.guid);
          }
        }
        for (const chat of chats) {
          if ((this.readChanges.get(chat.guid) || 0) > revision) continue;
          if (serverUnread.has(chat.guid) || this.readStates[chat.guid]?.manualUnread) this.unread.add(chat.guid);
          else this.unread.delete(chat.guid);
          const readState = this.readStates[chat.guid];
          if (readState && !readState.localOnly
            && ((readState.manualUnread && rawServerUnread.has(chat.guid)) || (!readState.manualUnread && !rawServerUnread.has(chat.guid)))) {
            delete this.readStates[chat.guid];
          }
        }
        let offset = 0;
        let highWater = this.cursor;
        const after = Math.max(0, this.cursor - 3000);
        while (true) {
          const batch = await api.request('/message/query', {
            method: 'POST', body: { after, offset, limit: 100, sort: 'ASC', with: ['chats', 'handle', 'attachments'] },
          });
          if (api !== this.api) return;
          if (!Array.isArray(batch)) throw new Error('Unexpected message response.');
          for (const message of batch) {
            this.accept(message, true, false, false);
            highWater = Math.max(highWater, message.dateCreated || 0);
          }
          offset += batch.length;
          if (batch.length < 100) break;
        }
        this.cursor = highWater;
        if (this.active) {
          const messages = await api.messages(this.active);
          if (api !== this.api) return;
          this.histories[this.active] = mergeMessages(this.histories[this.active] || [], messages);
        }
        for (const [guid, state] of Object.entries(this.readStates)) {
          if (state.localOnly && !state.manualUnread && (guid !== this.active || !this.isViewing(guid)) && !this.unread.has(guid)) await this.read(guid);
        }
        if (this.active && this.isViewing(this.active)) await this.read(this.active);
        this.status = 'connected';
        this.error = this.socket?.connected ? '' : 'Live connection unavailable. Checking for messages every 20 seconds.';
        this.persist();
        this.publish();
        if (Date.now() - this.contactsUpdatedAt > 300000) this.loadContacts(api).catch(() => {});
      } catch (error) {
        if (api !== this.api) return;
        this.status = 'offline';
        this.error = error.message;
        this.publish();
        throw error;
      }
    })();
    try { await this.syncing; } finally { if (api === this.api) this.syncing = null; }
  }

  /**
   * @summary Load contact names and normalized photos while preserving cached photos on fetch failures.
   * @param {BlueBubblesApi} api - Current server connection.
   * @returns {Promise<void>} Contacts loaded.
   */
  async loadContacts(api) {
    if (!api) return;
    this.contactsUpdatedAt = Date.now();
    let contacts;
    let includesPhotos = true;
    try {
      contacts = await api.request('/contact', { query: { extraProperties: 'avatar' }, timeout: 120000 });
    } catch {
      includesPhotos = false;
      contacts = await api.request('/contact');
    }
    if (api !== this.api) return;
    if (!Array.isArray(contacts)) throw new Error('Unexpected contacts response.');
    const mapping = {};
    const photos = {};
    const decoded = [];
    const byIdentity = new Map();
    const status = { records: contacts.length, supplied: 0, loaded: 0, unreadable: 0, fetchFailed: !includesPhotos, checkedAt: new Date().toISOString() };
    const details = {};
    for (const contact of contacts) {
      const candidates = [contact.avatar, contact.contactImage, contact.contactThumbnailImage, contact.contactImageThumbnail].filter(Boolean);
      const raw = candidates[0];
      let photo = '';
      if (includesPhotos && raw) {
        status.supplied++;
        for (const candidate of candidates) {
          try { photo = await this.preparePhoto(candidate); } catch { photo = ''; }
          if (photo) break;
        }
        if (api !== this.api) return;
        if (photo) status.loaded++;
        else status.unreadable++;
      }
      const identity = contact.id == null ? '' : `${contact.sourceType || ''}:${contact.id}`;
      if (photo && identity) byIdentity.set(identity, photo);
      decoded.push({ contact, photo, raw, identity });
      if (decoded.length % 8 === 0) await new Promise((resolve) => setImmediate(resolve));
    }
    if (api !== this.api) return;
    for (const { contact, photo: ownPhoto, raw, identity } of decoded) {
      const name = contact.displayName || [contact.firstName, contact.lastName].filter(Boolean).join(' ');
      const photo = ownPhoto || byIdentity.get(identity) || '';
      for (const address of [...(contact.phoneNumbers || []), ...(contact.emails || [])]) {
        const value = typeof address === 'string' ? address : address.address || address.value;
        if (typeof value !== 'string' || !value.trim()) continue;
        if (name) mapping[addressKey(value)] = name;
        for (const key of this.photoAddressKeys(value)) {
          details[key] = photo ? 'Photo decoded' : raw ? 'Image supplied but decoding failed' : 'Server supplied no photo';
          if (photo) photos[key] = photo;
          else if (raw && this.contactPhotos[key]) photos[key] = this.contactPhotos[key];
        }
      }
    }
    status.sources = {};
    for (const contact of contacts) {
      const source = ['api', 'db'].includes(contact.sourceType) ? contact.sourceType : 'other';
      status.sources[source] = (status.sources[source] || 0) + 1;
    }
    this.contactDetails = details;
    this.log('contacts', status);
    this.contacts = mapping;
    if (includesPhotos) this.contactPhotos = photos;
    this.photoStatus = includesPhotos ? status : { ...this.photoStatus, fetchFailed: true };
    this.persist();
    this.publish();
  }

  /**
   * @summary Match email and phone spellings without sharing photos across international country codes.
   * @param {string} address - Contact address.
   * @returns {Array<string>} Exact and North American dialing aliases.
   */
  photoAddressKeys(address) {
    if (typeof address !== 'string') return [];
    const value = address.trim().toLowerCase().replace(/^mailto:/, '');
    if (value.includes('@')) return [`email:${value}`];
    const digits = value.replace(/(?:ext\.?|extension|x|;ext=)\s*\d+$/, '').replace(/\D/g, '');
    if (!digits) return [];
    const keys = [`phone:${digits}`];
    if (digits.length === 11 && digits.startsWith('1')) keys.push(`phone:${digits.slice(1)}`);
    else if (digits.length === 10 && !value.startsWith('+')) keys.push(`phone:1${digits}`);
    return keys;
  }

  /**
   * @summary Resolve a single participant's portrait without assigning one person's photo to a group.
   * @param {object} chat - Conversation and participants.
   * @returns {string} Safe cached photo data URL or empty fallback.
   */
  photoForChat(chat) {
    if (chat.style === 43 || chat.participants?.length > 1) return this.groupPhotos[chat.guid] || '';
    return this.photoForAddress(chat.participants?.[0]?.address);
  }

  /**
   * @summary Look up a member portrait using its phone or email aliases.
   * @param {string} address - Member address.
   * @returns {string} Cached portrait or empty fallback.
   */
  photoForAddress(address) {
    for (const key of this.photoAddressKeys(address)) {
      if (this.contactPhotos[key]) return this.contactPhotos[key];
    }
    return '';
  }

  /**
   * @summary Refresh group icons in bounded batches, preserving cached icons on transient failures.
   * @param {BlueBubblesApi} api - Current server connection.
   * @param {boolean} force - Whether to bypass the normal refresh interval.
   * @returns {Promise<void>} Group icon refresh completion.
   */
  async loadGroupPhotos(api, force = false) {
    if (!api) return;
    const groups = this.chats.filter((chat) => chat.style === 43 || chat.participants?.length > 1);
    for (let index = 0; index < groups.length; index += 3) {
      if (api !== this.api) return;
      await Promise.all(groups.slice(index, index + 3).map((chat) => this.loadGroupPhoto(api, chat.guid, force).catch(() => {})));
    }
  }

  /**
   * @summary Fetch one authoritative group icon and invalidate removed icons.
   * @param {BlueBubblesApi} api - Current server connection.
   * @param {string} guid - Group conversation GUID.
   * @param {boolean} force - Whether to request a fresh icon immediately.
   * @returns {Promise<void>} Image lookup completion.
   */
  async loadGroupPhoto(api, guid, force = false) {
    const existing = this.groupPhotoRequests.get(guid);
    if (existing) {
      await existing;
      if (force && api === this.api) return this.loadGroupPhoto(api, guid, true);
      return;
    }
    if (!api || api !== this.api || (!force && Date.now() - (this.groupPhotoChecked[guid] || 0) < 300000)) return;
    this.groupPhotoChecked[guid] = Date.now();
    const version = this.groupPhotoVersions.get(guid) || 0;
    const job = (async () => {
      const bytes = await api.groupIcon(guid);
      const photo = bytes ? await this.preparePhoto(bytes) : '';
      if (api !== this.api || version !== (this.groupPhotoVersions.get(guid) || 0)) return;
      if (photo) this.groupPhotos[guid] = photo;
      else if (!bytes) delete this.groupPhotos[guid];
      this.persist(); this.publish();
    })();
    this.groupPhotoRequests.set(guid, job);
    try { await job; } finally { if (this.groupPhotoRequests.get(guid) === job) this.groupPhotoRequests.delete(guid); }
  }

  /**
   * @summary Merge a live or recovered event and notify only once per message.
   * @param {object} message - Server message event.
   * @param {boolean} shouldNotify - Whether a new incoming event can notify.
   * @param {boolean} persist - Whether to save immediately.
   * @param {boolean} updateUnread - Whether this live event should update unread state.
   * @returns {void} Updates internal state.
   */
  accept(message, shouldNotify = true, persist = true, updateUnread = true) {
    if (!message?.guid) return;
    const chatGuid = message.chats?.[0]?.guid || message.chat?.guid || message.chatGuid
      || this.chats.find((chat) => chat.lastMessage?.guid === message.guid)?.guid
      || Object.keys(this.histories).find((guid) => this.histories[guid].some((item) => item.guid === message.guid));
    if (!chatGuid) return;
    const known = this.seen.has(message.guid);
    if (shouldNotify) this.seen.add(message.guid);
    this.histories[chatGuid] = mergeMessages(this.histories[chatGuid] || [], [message]);
    let chat = this.chats.find((item) => item.guid === chatGuid);
    if (!chat) {
      chat = { ...(message.chats?.[0] || message.chat || {}), guid: chatGuid };
      this.chats.push(chat);
    }
    if ((message.dateCreated || 0) >= (chat.lastMessage?.dateCreated || 0)) chat.lastMessage = message;
    if (shouldNotify && !known && updateUnread) {
      this.readChanges.set(chatGuid, ++this.readRevision);
      if (message.isFromMe) {
        this.applyReadStatus({ chatGuid, read: true });
      } else if ((message.dateCreated || 0) > (this.readStates[chatGuid]?.through ?? -1)
        || (message.dateCreated === this.readStates[chatGuid]?.through && !(this.readStates[chatGuid]?.guids || []).includes(message.guid))) {
        this.unread.add(chatGuid);
        if (this.isViewing(chatGuid)) this.read(chatGuid);
      }
    }
    if (shouldNotify && !known && !message.isFromMe && (message.dateCreated || 0) > this.cursor - 3000) {
      if (!this.isViewing(chatGuid) && this.unread.has(chatGuid)) {
        this.notify({ chatGuid, title: chatTitle(chat, this.contacts), body: messageText(message) });
      }
    }
    if (persist) { this.persist(); this.publish(); }
  }

  /**
   * @summary Open a conversation or fetch older history, preserving offline cache.
   * @param {string} guid - Conversation GUID.
   * @param {number} before - Optional older-history boundary.
   * @returns {Promise<object>} Messages and pagination availability.
   */
  async open(guid, before) {
    if (typeof guid !== 'string' || !guid) throw new Error('Select a conversation.');
    if (!before) this.active = guid;
    this.publish();
    if (!this.api) {
      if (!before && this.histories[guid]?.length && this.isViewing(guid)) await this.read(guid);
      return { hasMore: false };
    }
    const api = this.api;
    const messages = await api.messages(guid, before);
    if (api !== this.api) return { hasMore: false };
    this.histories[guid] = mergeMessages(this.histories[guid] || [], messages);
    if (!before && this.active === guid && this.isViewing(guid)) await this.read(guid);
    this.persist();
    this.publish();
    return { hasMore: messages.length === 100 };
  }

  /**
   * @summary Apply a BlueBubbles read-status event without confusing it with recipient read receipts.
   * @param {object} event - Conversation GUID, explicit read boolean, and optional local-only flag.
   * @returns {void} Updates cached shared state.
   */
  applyReadStatus({ chatGuid, read, localOnly = false } = {}) {
    if (typeof chatGuid !== 'string' || typeof read !== 'boolean') return;
    const previous = this.readStates[chatGuid] || {};
    const latest = this.chats.find((chat) => chat.guid === chatGuid)?.lastMessage?.dateCreated || 0;
    const through = Math.max(previous.through || 0, latest);
    const guids = new Set(through === previous.through ? previous.guids || [] : []);
    for (const message of this.histories[chatGuid] || []) {
      if (message.dateCreated === through) guids.add(message.guid);
    }
    const lastMessage = this.chats.find((chat) => chat.guid === chatGuid)?.lastMessage;
    if (lastMessage?.dateCreated === through) guids.add(lastMessage.guid);
    this.readStates[chatGuid] = { through, guids: [...guids], manualUnread: !read, localOnly, confirmedAt: Date.now() };
    this.readChanges.set(chatGuid, ++this.readRevision);
    if (read) this.unread.delete(chatGuid);
    else this.unread.add(chatGuid);
    this.persist();
    this.publish();
  }

  /**
   * @summary Mark a viewed conversation read locally and on the Mac when supported.
   * @param {string} guid - Conversation GUID.
   * @returns {Promise<void>} Read synchronization completion.
   */
  async read(guid) {
    if (typeof guid !== 'string') return;
    if (this.unread.has(guid)) this.applyReadStatus({ chatGuid: guid, read: true, localOnly: true });
    if (this.readRequests.has(guid)) return this.readRequests.get(guid);
    const state = this.readStates[guid];
    if (!state?.localOnly || state.manualUnread) return;
    const api = this.api;
    if (!api) return;
    const job = (async () => {
      try {
        const info = await api.request('/server/info');
        if (api !== this.api) return;
        this.canMarkRead = info.private_api === true;
        if (!this.canMarkRead) {
          this.readSyncError = 'Read locally. Enable the server’s Private API to sync read status to your Mac.';
          this.log('read-unavailable', { privateApi: false });
          return;
        }
        this.log('read-attempt', {});
        await api.request(`/chat/${encodeURIComponent(guid)}/read`, { method: 'POST' });
        if (api !== this.api) return;
        this.readSyncError = '';
        const current = this.readStates[guid];
        if (current === state && !current.manualUnread) {
          current.localOnly = false;
          current.confirmedAt = Date.now();
        }
        this.log('read-confirmed', {});
        this.persist();
      } catch {
        if (api === this.api) {
          this.readSyncError = 'Read locally. Syncing read status to your Mac failed; retrying automatically.';
          this.log('read-failed', {});
        }
      } finally { this.publish(); }
    })();
    this.readRequests.set(guid, job);
    try { await job; } finally {
      if (this.readRequests.get(guid) === job) this.readRequests.delete(guid);
      if (api === this.api && this.isViewing(guid) && this.readStates[guid]?.localOnly
        && this.readStates[guid] !== state) await this.read(guid);
    }
  }

  /**
   * @summary Send a message with no automatic retries on uncertain delivery.
   * @param {object} input - Conversation GUID and text body.
   * @returns {Promise<void>} Successful server acknowledgement.
   */
  async send({ guid, text }) {
    if (!this.api) throw new Error('Connect to your server first.');
    if (typeof text !== 'string' || !text.trim() || text.length > 20000) throw new Error('Enter a message up to 20,000 characters.');
    if (!this.chats.some((chat) => chat.guid === guid)) throw new Error('Select an existing conversation.');
    const api = this.api;
    const message = await api.send(guid, text, randomUUID(), this.settings.sendMethod);
    if (api !== this.api) return;
    if (message?.guid) this.accept({ ...message, isFromMe: true, chatGuid: guid }, false);
    this.sync().catch(() => {});
  }

  /**
   * @summary Create a new chat through the server's supported API.
   * @param {object} input - Recipients, initial text, and service.
   * @returns {Promise<string>} New conversation GUID.
   */
  async createChat({ addresses, text, service }) {
    if (!this.api) throw new Error('Connect to your server first.');
    if (!Array.isArray(addresses) || !addresses.length || addresses.length > 32
      || addresses.some((address) => typeof address !== 'string' || !address.trim() || address.length > 254)) {
      throw new Error('Enter valid recipients.');
    }
    if (typeof text !== 'string' || !text.trim() || text.length > 20000) throw new Error('Enter an initial message.');
    if (!['iMessage', 'SMS'].includes(service)) throw new Error('Select iMessage or SMS.');
    const result = await this.api.request('/chat/new', {
      method: 'POST', body: { addresses, message: text, service, method: this.settings.sendMethod },
    });
    await this.sync();
    return result.guid;
  }

  /**
   * @summary Emit a safe renderer snapshot with no server credentials.
   * @returns {object} View state.
   */
  snapshot() {
    return {
      chats: this.chats.map((chat) => ({ ...chat, title: chatTitle(chat, this.contacts), photo: this.photoForChat(chat),
        avatarMembers: (chat.style === 43 || chat.participants?.length > 1) ? (chat.participants || []).map((member) => ({
          title: this.contacts[addressKey(member.address)] || member.address || 'Unknown', photo: this.photoForAddress(member.address),
        })) : [], unread: this.unread.has(chat.guid) }))
        .sort((a, b) => (b.lastMessage?.dateCreated || 0) - (a.lastMessage?.dateCreated || 0)),
      messages: this.histories[this.active] || [], active: this.active, status: this.status, error: this.error || this.readSyncError, photoStatus: this.photoStatus,
    };
  }

  /** @summary Publish current view state. @returns {void} Emits state. */
  publish() { this.emit('state', this.snapshot()); }

  /** @summary Persist bounded caches and notification deduplication state. @returns {void} Saves cache. */
  persist() {
    this.seen = new Set([...this.seen].slice(-10000));
    const keys = Object.keys(this.histories);
    for (const key of keys.slice(0, Math.max(0, keys.length - 100))) {
      if (key !== this.active) delete this.histories[key];
    }
    this.save({ chats: this.chats, histories: this.histories, contacts: this.contacts, contactPhotos: this.contactPhotos,
      groupPhotos: this.groupPhotos, photoStatus: this.photoStatus, cursor: this.cursor,
      seen: [...this.seen], unread: [...this.unread], readStates: this.readStates });
  }

  /** @summary Disconnect sockets and background timers. @returns {void} Stops connection. */
  stop() {
    clearInterval(this.timer);
    this.socket?.removeAllListeners();
    this.socket?.disconnect();
    this.api = null;
    this.readRequests.clear();
    this.groupPhotoRequests.clear();
    this.groupPhotoChecked = {};
    this.syncing = null;
    this.status = 'offline';
  }
}

module.exports = { MessagingClient };
