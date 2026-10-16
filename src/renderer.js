const $ = (id) => document.getElementById(id);
let state = { chats: [], messages: [], active: null, status: 'offline' };
let preferences = {};
let renderedMessages = '';
let activeGuid = null;
let sending = false;
let toastTimer;
let revealChatGuid = null;

/**
 * @summary Unwrap a narrow desktop operation result.
 * @param {Promise<object>} request - IPC result.
 * @returns {Promise<*>} Operation payload.
 */
const invoke = async (request) => {
  const result = await request;
  if (!result.ok) throw new Error(result.error);
  return result.data;
};

/**
 * @summary Display an actionable error without interrupting the composer.
 * @param {Error|string} error - User-visible problem.
 * @returns {void} Shows a temporary alert.
 */
const showError = (error) => {
  $('toast').textContent = error.message || String(error);
  $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $('toast').hidden = true; }, 12000);
};

/**
 * @summary Read or save one conversation draft in local browser storage.
 * @param {string} guid - Conversation GUID.
 * @param {string} value - Optional new value.
 * @returns {string} Stored draft.
 */
const draft = (guid, value) => {
  const key = `draft:${preferences.server}:${guid}`;
  try {
    if (value !== undefined) {
      if (value) localStorage.setItem(key, value);
      else localStorage.removeItem(key);
    }
    return localStorage.getItem(key) || '';
  } catch {
    if (value) showError('Cannot save this draft locally. Check available disk space.');
    return '';
  }
};

/**
 * @summary Derive compact initials for a conversation avatar.
 * @param {string} title - Conversation title.
 * @returns {string} Up to two initials.
 */
const initials = (title) => title.split(/[\s,]+/).filter(Boolean).slice(0, 2).map((part) => part[0]).join('').toUpperCase();

/**
 * @summary Format a conversation timestamp relative to today.
 * @param {number} value - Unix timestamp in milliseconds.
 * @returns {string} Local timestamp label.
 */
const shortDate = (value) => {
  if (!value) return '';
  const date = new Date(value);
  if (date.toDateString() === new Date().toDateString()) return date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  return date.toLocaleDateString([], { month: 'short', day: 'numeric' });
};

/**
 * @summary Create text-only DOM content so messages cannot inject HTML.
 * @param {string} tag - Element type.
 * @param {string} className - CSS class.
 * @param {string} text - Plain text content.
 * @returns {HTMLElement} New element.
 */
const element = (tag, className, text) => {
  const node = document.createElement(tag);
  node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};

/**
 * @summary Show a contact portrait with initials when no usable image is available.
 * @param {HTMLElement} target - Circular avatar container.
 * @param {string} title - Contact name or conversation title.
 * @param {string} photo - Normalized image data URL.
 * @param {Array<object>} members - Group-member portraits used when the server has no group icon.
 * @returns {void} Updates the portrait without using remote URLs or HTML.
 */
const renderAvatar = (target, title, photo, members = []) => {
  target.classList.remove('avatar-group');
  target.removeAttribute('title');
  target.textContent = initials(title);
  if (!photo && members.length) {
    target.classList.add('avatar-group');
    target.title = members.map((member) => member.title).join(', ');
    target.replaceChildren();
    const limit = members.length > 4 ? 3 : 4;
    for (const member of members.slice(0, limit)) {
      const circle = element('span', 'member-avatar');
      renderAvatar(circle, member.title, member.photo);
      target.append(circle);
    }
    if (members.length > 4) target.append(element('span', 'member-avatar member-count', `+${members.length - 3}`));
    return;
  }
  if (!photo) return;
  const image = document.createElement('img');
  image.alt = '';
  image.draggable = false;
  image.loading = 'lazy';
  image.decoding = 'async';
  image.addEventListener('error', () => {
    if (image.parentNode === target) renderAvatar(target, title, '', members);
  }, { once: true });
  image.src = photo;
  target.replaceChildren(image);
};

/** @summary Render the filtered conversation list. @returns {void} Updates sidebar. */
const renderChats = () => {
  const query = $('search').value.trim().toLowerCase();
  const chats = state.chats.filter((chat) => `${chat.title} ${(chat.participants || []).map((p) => p.address).join(' ')}`.toLowerCase().includes(query));
  const fragment = document.createDocumentFragment();
  for (const chat of chats) {
    const button = element('button', `chat${chat.guid === state.active ? ' active' : ''}${chat.unread ? ' unread' : ''}`);
    button.dataset.guid = chat.guid;
    button.setAttribute('aria-pressed', String(chat.guid === state.active));
    button.setAttribute('aria-label', `${chat.title}${chat.unread ? ', unread' : ''}`);
    const avatar = element('span', 'avatar');
    renderAvatar(avatar, chat.title, chat.photo, chat.avatarMembers);
    button.append(avatar);
    const content = element('div', 'chat-content');
    const top = element('div', 'chat-top');
    top.append(element('span', 'chat-name', chat.title), element('time', 'chat-time', shortDate(chat.lastMessage?.dateCreated)));
    content.append(top, element('div', 'chat-preview', (chat.lastMessage?.text || (chat.lastMessage?.attachments?.length ? 'Attachment' : 'No messages yet')).replace(/\uFFFC/g, '')));
    button.append(content);
    if (chat.unread) button.append(element('span', 'unread-dot'));
    button.addEventListener('click', () => openChat(chat.guid));
    fragment.append(button);
  }
  if (!chats.length) fragment.append(element('p', 'empty-list', query ? 'No matching conversations.' : 'Your conversations will appear here when you connect.'));
  $('chat-list').replaceChildren(fragment);
  if (revealChatGuid === state.active) {
    const selected = [...$('chat-list').children].find((item) => item.dataset.guid === revealChatGuid);
    if (selected) {
      selected.scrollIntoView({ block: 'nearest' });
      revealChatGuid = null;
    }
  }
};

/**
 * @summary Render plain message text with explicit external web-link buttons.
 * @param {HTMLElement} target - Message bubble.
 * @param {string} text - Untrusted server text.
 * @returns {void} Appends safe text nodes.
 */
const appendText = (target, text) => {
  const pattern = /https?:\/\/[^\s<>]+/g;
  let offset = 0;
  for (const match of text.matchAll(pattern)) {
    target.append(document.createTextNode(text.slice(offset, match.index)));
    const link = element('button', 'text-link', match[0]);
    link.title = 'Open in your browser';
    link.addEventListener('click', () => invoke(window.desktop.external(match[0])).catch(showError));
    target.append(link);
    offset = match.index + match[0].length;
  }
  target.append(document.createTextNode(text.slice(offset)));
};

/**
 * @summary Load a visible attachment automatically and retain a retry action on failure.
 * @param {HTMLElement} target - Inline preview placeholder.
 * @returns {Promise<void>} Preview completion.
 */
const loadPreview = async (target) => {
  const retry = element('button', 'attachment', 'Loading attachment…');
  retry.disabled = true;
  target.replaceChildren(retry);
  try {
    const result = await invoke(window.desktop.attachmentPreview(target.dataset.guid));
    if (!target.isConnected) return;
    target.replaceChildren();
    if (result.url && ['image', 'audio', 'video'].includes(result.kind)) {
      let tag;
      switch (result.kind) {
        case 'image': tag = 'img'; break;
        case 'audio': tag = 'audio'; break;
        case 'video': tag = 'video'; break;
      }
      const media = element(tag, 'inline-media');
      if (tag === 'img') media.alt = target.dataset.name;
      else { media.controls = true; media.preload = 'metadata'; }
      const thread = $('thread');
      const atBottom = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 150;
      media.addEventListener('load', () => { if (atBottom) thread.scrollTop = thread.scrollHeight; });
      media.addEventListener('error', () => {
        retry.disabled = false;
        retry.textContent = 'Preview unavailable · Retry';
        target.replaceChildren(retry);
      });
      media.src = result.url;
      target.append(media);
    } else target.append(element('span', 'field-hint', 'Downloaded to local cache'));
  } catch (error) {
    retry.textContent = `${error.message} · Retry`;
    retry.disabled = false;
  }
  retry.onclick = () => loadPreview(target);
};

const previewObserver = new IntersectionObserver((entries) => {
  for (const entry of entries) {
    if (entry.isIntersecting) {
      previewObserver.unobserve(entry.target);
      loadPreview(entry.target);
    }
  }
}, { root: $('thread'), rootMargin: '300px' });

/**
 * @summary Render message history while preserving scroll position and selected text.
 * @param {boolean} switched - Whether a different conversation was opened.
 * @returns {void} Updates message log only when data changes.
 */
const renderMessages = (switched) => {
  const signature = JSON.stringify([state.active, state.messages]);
  if (signature === renderedMessages) return;
  renderedMessages = signature;
  previewObserver.disconnect();
  const thread = $('thread');
  const oldHeight = thread.scrollHeight;
  const oldTop = thread.scrollTop;
  const wasAtBottom = oldHeight - oldTop - thread.clientHeight < 100;
  const fragment = document.createDocumentFragment();
  let previousDay;
  for (const message of state.messages) {
    const date = new Date(message.dateCreated || Date.now());
    const day = date.toLocaleDateString([], { weekday: 'short', month: 'long', day: 'numeric', year: 'numeric' });
    if (day !== previousDay) { fragment.append(element('div', 'day-label', day)); previousDay = day; }
    const row = element('article', `message${message.isFromMe ? ' outgoing' : ''}`);
    row.dataset.guid = message.guid;
    if (!message.isFromMe && message.handle?.address) row.append(element('span', 'sender-name', message.handle.address));
    const bubble = element('div', 'bubble');
    const text = (message.text || '').replace(/\uFFFC/g, '');
    if (text) appendText(bubble, text);
    else if (!message.attachments?.length) bubble.append(document.createTextNode(message.associatedMessageType ? 'Reaction' : 'Message'));
    for (const attachment of message.attachments || []) {
      const name = attachment.transferName || attachment.filename?.split(/[\\/]/).pop() || 'Attachment';
      const preview = element('div', 'attachment-preview');
      preview.dataset.guid = attachment.guid;
      preview.dataset.name = name;
      preview.append(element('span', 'field-hint', 'Loading attachment…'));
      bubble.append(preview);
      const button = element('button', 'attachment', `↓ ${name}`);
      button.title = 'Save attachment';
      button.addEventListener('click', async () => {
        button.disabled = true;
        try { await invoke(window.desktop.attachment(attachment.guid, name)); } catch (error) { showError(error); }
        finally { button.disabled = false; }
      });
      bubble.append(button);
    }
    row.append(bubble);
    let status = '';
    if (message.isFromMe) {
      if (message.error > 0) status = ' · Send failed on server';
      else if (message.dateRead) status = ' · Read';
      else if (message.dateDelivered || message.isDelivered) status = ' · Delivered';
      else status = ' · Sent';
    }
    row.append(element('span', `message-meta${message.error > 0 ? ' message-error' : ''}`, `${date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}${status}`));
    fragment.append(row);
  }
  if (!state.messages.length) fragment.append(element('p', 'empty-list', 'No messages loaded. Sync to load this conversation.'));
  $('messages').replaceChildren(fragment);
  for (const target of document.querySelectorAll('.attachment-preview')) previewObserver.observe(target);
  if (switched || wasAtBottom) thread.scrollTop = thread.scrollHeight;
  else thread.scrollTop = oldTop + (thread.scrollHeight - oldHeight);
};

/**
 * @summary Apply a desktop state update without replacing the native composer.
 * @param {object} next - Public application state.
 * @returns {void} Updates interface.
 */
const render = (next) => {
  const switched = activeGuid !== next.active;
  if (switched && activeGuid) draft(activeGuid, $('message-input').value);
  state = next;
  activeGuid = state.active;
  $('connection').className = state.status;
  const labels = { connected: 'Connected to your Mac', connecting: 'Connecting…', reconnecting: 'Reconnecting…', offline: 'Offline · drafts available' };
  $('connection').replaceChildren(element('i', ''), document.createTextNode(labels[state.status] || state.status));
  $('banner').textContent = state.error || '';
  $('banner').hidden = !state.error;
  const chat = state.chats.find((item) => item.guid === state.active);
  $('welcome').hidden = Boolean(state.active);
  $('thread').hidden = !state.active;
  $('composer').hidden = !state.active;
  $('conversation-title').textContent = chat?.title || 'Your conversations, here.';
  renderAvatar($('conversation-avatar'), chat?.title || 'BlueBubbles', chat?.photo, chat?.avatarMembers);
  const photos = state.photoStatus || {};
  $('photo-status').textContent = photos.fetchFailed ? 'Photo refresh unavailable; showing cached photos.'
    : photos.supplied === undefined ? 'Contact photos have not been loaded yet.'
      : `Photos supplied by server: ${photos.supplied} · Loaded: ${photos.loaded} · Unreadable: ${photos.unreadable}`;
  $('conversation-subtitle').textContent = chat
    ? (chat.participants || []).map((p) => p.address).join(' · ') || 'Conversation'
    : 'Connected to your own Mac. Built for your desktop.';
  if (switched) {
    $('message-input').value = state.active ? draft(state.active) : '';
    $('load-older').hidden = false;
    $('compose-status').textContent = 'Enter to send · Shift + Enter for a new line';
  }
  renderChats();
  renderMessages(switched);
  $('send').disabled = sending || !$('message-input').value.trim();
};

/**
 * @summary Open one conversation with immediate cached history.
 * @param {string} guid - Conversation GUID.
 * @returns {Promise<void>} History request completion.
 */
const openChat = async (guid) => {
  try {
    const result = await invoke(window.desktop.open(guid));
    if (state.active === guid) $('load-older').hidden = !result.hasMore;
  } catch (error) { showError(error); }
  if (state.active === guid) $('message-input').focus();
};

/** @summary Fill the connection settings dialog. @returns {void} Displays settings. */
const openSettings = () => {
  $('server').value = preferences.server || '';
  $('password').value = '';
  $('password').required = !preferences.hasPassword;
  $('password').placeholder = preferences.hasPassword ? 'Saved securely; leave blank to keep' : 'Your BlueBubbles server password';
  $('send-method').value = preferences.sendMethod || 'apple-script';
  $('notifications').checked = preferences.notifications !== false;
  $('preview').checked = preferences.preview !== false;
  $('launch-at-login').checked = Boolean(preferences.launchAtLogin);
  $('settings-error').hidden = !preferences.credentialError;
  $('settings-error').textContent = preferences.credentialError || '';
  $('settings-close').hidden = !preferences.hasPassword;
  $('settings-dialog').showModal();
};

/** @summary Display local operational diagnostics on demand. @returns {Promise<void>} Report loaded. */
const openDiagnostics = async () => {
  try {
    const report = await invoke(window.desktop.diagnostics());
    $('diagnostic-report').value = JSON.stringify(report, null, 2);
    if (!$('diagnostics-dialog').open) $('diagnostics-dialog').showModal();
  } catch (error) { showError(error); }
};
$('diagnostics-open').addEventListener('click', openDiagnostics);
$('diagnostics-refresh').addEventListener('click', openDiagnostics);
$('diagnostics-check').addEventListener('click', async () => {
  $('diagnostics-check').disabled = true;
  try { await invoke(window.desktop.sync()); } catch (error) { showError(error); }
  finally { $('diagnostics-check').disabled = false; await openDiagnostics(); }
});
$('diagnostics-close').addEventListener('click', () => $('diagnostics-dialog').close());
$('diagnostics-copy').addEventListener('click', () => {
  $('diagnostic-report').select();
  document.execCommand('copy');
});
window.desktop.onDiagnostics(openDiagnostics);
$('search').addEventListener('input', renderChats);
$('settings-open').addEventListener('click', openSettings);
$('settings-close').addEventListener('click', () => $('settings-dialog').close());
$('settings-dialog').addEventListener('cancel', (event) => { if (!preferences.hasPassword) event.preventDefault(); });
$('settings-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('settings-save').disabled = true;
  $('settings-save').textContent = 'Connecting…';
  $('settings-error').hidden = true;
  try {
    const nextPreferences = await invoke(window.desktop.configure({
      server: $('server').value, password: $('password').value,
      sendMethod: $('send-method').value, notifications: $('notifications').checked,
      preview: $('preview').checked, launchAtLogin: $('launch-at-login').checked,
    }));
    preferences = nextPreferences;
    $('password').value = '';
    $('settings-dialog').close();
  } catch (error) {
    $('settings-error').textContent = error.message;
    $('settings-error').hidden = false;
  } finally {
    $('settings-save').disabled = false;
    $('settings-save').textContent = 'Connect & save';
  }
});
$('test-notification').addEventListener('click', () => invoke(window.desktop.testNotification()).catch(showError));
$('sync').addEventListener('click', async () => {
  $('sync').disabled = true;
  try { await invoke(window.desktop.sync()); } catch (error) { showError(error); }
  finally { $('sync').disabled = false; }
});
$('load-older').addEventListener('click', async () => {
  const guid = state.active;
  const oldest = state.messages[0]?.dateCreated;
  if (!oldest) return;
  $('load-older').disabled = true;
  try {
    const result = await invoke(window.desktop.open(guid, oldest));
    if (state.active === guid) $('load-older').hidden = !result.hasMore;
  } catch (error) { showError(error); }
  finally { $('load-older').disabled = false; }
});
$('message-input').addEventListener('input', () => {
  if (state.active) draft(state.active, $('message-input').value);
  $('send').disabled = sending || !$('message-input').value.trim();
});
$('message-input').addEventListener('keydown', (event) => {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing && event.keyCode !== 229) {
    event.preventDefault();
    if (!sending && $('message-input').value.trim()) $('composer').requestSubmit();
  }
});
$('composer').addEventListener('submit', async (event) => {
  event.preventDefault();
  if (sending || !state.active) return;
  const guid = state.active;
  const text = $('message-input').value;
  if (!text.trim()) return;
  sending = true;
  $('send').disabled = true;
  $('compose-status').textContent = 'Sending…';
  try {
    await invoke(window.desktop.send({ guid, text }));
    if (draft(guid) === text) draft(guid, '');
    if (state.active === guid && $('message-input').value === text) $('message-input').value = '';
    $('compose-status').textContent = 'Enter to send · Shift + Enter for a new line';
  } catch (error) {
    $('compose-status').textContent = 'Not confirmed. Check the conversation before trying again.';
    showError(`${error.message} Your draft is kept. A timed-out send may still arrive; check before sending again.`);
  } finally {
    sending = false;
    $('send').disabled = !$('message-input').value.trim();
    $('message-input').focus();
  }
});
$('new-chat').addEventListener('click', () => {
  if (!preferences.hasPassword) return openSettings();
  $('new-error').hidden = true;
  $('new-dialog').showModal();
});
$('new-close').addEventListener('click', () => $('new-dialog').close());
$('new-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('new-send').disabled = true;
  $('new-error').hidden = true;
  try {
    const guid = await invoke(window.desktop.createChat({
      addresses: $('recipients').value.split(',').map((value) => value.trim()).filter(Boolean),
      service: $('new-service').value, text: $('new-message').value,
    }));
    $('new-dialog').close();
    $('new-form').reset();
    if (guid) await openChat(guid);
  } catch (error) {
    $('new-error').textContent = `${error.message} Check whether the message arrived before trying again.`;
    $('new-error').hidden = false;
  } finally { $('new-send').disabled = false; }
});
window.addEventListener('online', () => invoke(window.desktop.sync()).catch(() => {}));
window.desktop.onState(render);
window.desktop.onOpenChat((guid) => {
  revealChatGuid = guid;
  $('search').value = '';
  for (const dialog of document.querySelectorAll('dialog[open]')) dialog.close();
  openChat(guid);
});
invoke(window.desktop.initial()).then(({ state: initialState, settings, version }) => {
  preferences = settings;
  $('app-name').textContent = `Not Green Bubbles v${version}`;
  render(initialState);
  $('version').textContent = `Not Green Bubbles ${version} · Independent client`;
  if (!settings.hasPassword) openSettings();
}).catch(showError);
