const { app, BrowserWindow, Menu, Tray, nativeImage, ipcMain, Notification, safeStorage, shell, dialog, powerMonitor, session } = require('electron');
const fs = require('node:fs');
const { createHash } = require('node:crypto');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { Readable } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const { MessagingClient } = require('./client.cjs');
const { BlueBubblesApi } = require('./api.cjs');
const { normalizeServer, addressKey } = require('./model.cjs');
const { PhotoDecoder } = require('./photos.cjs');
const { AttachmentCache } = require('./attachments.cjs');

const appId = 'com.jonworek.bluebubblesdesktop';
const photoDecoder = new PhotoDecoder(nativeImage);
const defaults = { server: '', sendMethod: 'apple-script', notifications: true, preview: true, launchAtLogin: false };
let window;
let tray;
let client;
let settings = { ...defaults };
let quitting = false;
let cacheTimer;
let pendingCache;
let credentialError = '';
let attachmentCache;
let diagnosticTimer;
let events = [];

/**
 * @summary Keep bounded operational logs containing only explicitly supplied technical metadata.
 * @param {string} event - Internal event label.
 * @param {object} details - Credential-free counts, HTTP status, or format metadata.
 * @returns {void} Records the event.
 */
const log = (event, details) => {
  events.push({ time: new Date().toISOString(), event, ...details });
  events = events.slice(-400);
  clearTimeout(diagnosticTimer);
  diagnosticTimer = setTimeout(() => { try { writeJson('diagnostics.json', events); } catch { /* Logging must not interrupt messaging. */ } }, 500);
};

app.setPath('userData', path.join(app.getPath('appData'), 'BlueBubbles Desktop'));
if (process.env.BLUEBUBBLES_TEST_DATA && !app.isPackaged) app.setPath('userData', process.env.BLUEBUBBLES_TEST_DATA);
app.setAppUserModelId(appId);

/**
 * @summary Resolve one of the application's private data files.
 * @param {string} name - Local filename.
 * @returns {string} Absolute path.
 */
const dataPath = (name) => path.join(app.getPath('userData'), name);

/**
 * @summary Read JSON without preventing startup after a damaged cache.
 * @param {string} name - Data filename.
 * @param {object} fallback - Default data.
 * @returns {object} Stored or default data.
 */
const readJson = (name, fallback) => {
  try { return JSON.parse(fs.readFileSync(dataPath(name), 'utf8')); } catch { return fallback; }
};

/**
 * @summary Atomically save private application data.
 * @param {string} name - Data filename.
 * @param {object} value - JSON-serializable data.
 * @returns {void} Writes the file.
 */
const writeJson = (name, value) => {
  fs.mkdirSync(app.getPath('userData'), { recursive: true });
  const target = dataPath(name);
  fs.writeFileSync(`${target}.tmp`, JSON.stringify(value), { mode: 0o600 });
  fs.renameSync(`${target}.tmp`, target);
};

/** @summary Flush queued cache writes. @returns {void} Persists the latest cache. */
const flushCache = () => {
  clearTimeout(cacheTimer);
  if (pendingCache) {
    try { writeJson('cache.json', pendingCache); pendingCache = null; } catch {
      window?.webContents.send('state', { ...client.snapshot(), error: 'Cannot save local history. Check available disk space.' });
    }
  }
};

/** @summary Show and focus the application window. @returns {void} Restores window. */
const showWindow = () => {
  if (window.isMinimized()) window.restore();
  window.show();
  window.focus();
};

/**
 * @summary Display an incoming-message notification and open its conversation on click.
 * @param {object} input - Notification title, body, and conversation GUID.
 * @returns {void} Displays a native notification when enabled.
 */
const notify = ({ title, body, chatGuid }) => {
  if (!settings.notifications || !Notification.isSupported()) return;
  const notification = new Notification({
    title: String(title).slice(0, 160), body: settings.preview ? String(body).slice(0, 250) : 'New message',
    icon: path.join(__dirname, '../assets/icon.png'),
  });
  notification.on('click', () => {
    if (chatGuid) client.active = chatGuid;
    showWindow();
    if (chatGuid) window.webContents.send('open-chat', chatGuid);
  });
  notification.show();
};

/**
 * @summary Register a narrow IPC handler restricted to the app's main frame.
 * @param {string} name - Allowed channel.
 * @param {Function} handler - Operation implementation.
 * @returns {void} Registers the channel.
 */
const handle = (name, handler) => ipcMain.handle(name, async (event, ...args) => {
  if (event.sender !== window.webContents || event.senderFrame !== window.webContents.mainFrame
    || event.senderFrame.url !== pathToFileURL(path.join(__dirname, 'index.html')).href) {
    return { ok: false, error: 'Untrusted request.' };
  }
  try { return { ok: true, data: await handler(...args) }; } catch (error) {
    return { ok: false, error: error.message || 'Operation failed.' };
  }
});

/**
 * @summary Save and validate settings without exposing credentials back to the renderer.
 * @param {object} input - Server URL, optional new password, and preferences.
 * @returns {Promise<object>} Public preferences.
 */
const configure = async (input) => {
  const server = normalizeServer(input.server);
  const password = input.password || settings.password;
  if (typeof password !== 'string' || !password || password.length > 4096) throw new Error('Enter the BlueBubbles server password.');
  if (!['apple-script', 'private-api'].includes(input.sendMethod)) throw new Error('Choose a sending method.');
  await new BlueBubblesApi(server, password).request('/ping');
  if (!safeStorage.isEncryptionAvailable() || safeStorage.getSelectedStorageBackend?.() === 'basic_text') {
    throw new Error('Windows credential encryption is unavailable. Your password has not been saved.');
  }
  const next = {
    server, password, sendMethod: input.sendMethod, notifications: Boolean(input.notifications),
    preview: Boolean(input.preview), launchAtLogin: Boolean(input.launchAtLogin),
  };
  writeJson('settings.json', { ...next, password: undefined, secret: safeStorage.encryptString(password).toString('base64') });
  const changedServer = settings.server && settings.server !== server;
  settings = next;
  credentialError = '';
  if (process.platform === 'win32') app.setLoginItemSettings({
    openAtLogin: settings.launchAtLogin,
    path: process.env.PORTABLE_EXECUTABLE_FILE || process.execPath,
    args: ['--hidden'],
  });
  if (changedServer) {
    client.stop();
    client.chats = []; client.histories = {}; client.contacts = {}; client.contactPhotos = {}; client.seen.clear(); client.unread.clear();
    client.groupPhotos = {}; client.groupPhotoVersions.clear(); client.photoStatus = {};
    client.active = null; client.cursor = Date.now();
    client.readStates = {}; client.readChanges.clear(); client.readRevision = 0; client.readSyncError = '';
    client.persist();
  }
  await client.connect(settings).catch(() => {});
  return publicSettings();
};

/** @summary Return preferences without secrets. @returns {object} Public settings. */
const publicSettings = () => ({ ...defaults, ...settings, password: undefined, hasPassword: Boolean(settings.password), credentialError });

/**
 * @summary Decode server contact images and cache small square PNG portraits.
 * @param {string|Buffer} avatar - Base64 image bytes, a raster-image data URL, or binary group icon.
 * @returns {Promise<string>} A normalized PNG data URL, or empty fallback for unavailable images.
 */
const preparePhoto = async (avatar) => {
  const result = await photoDecoder.prepare(avatar);
  const bytes = Buffer.isBuffer(avatar) ? avatar : Buffer.from(typeof avatar === 'string' ? avatar.replace(/^data:[^,]*,/, '') : '', 'base64');
  log('photo-decode', { loaded: Boolean(result), inputType: typeof avatar, bytes: bytes.length, signature: bytes.subarray(0, 12).toString('hex') });
  return result;
};

/** @summary Register application operations. @returns {void} Configures IPC. */
const registerHandlers = () => {
  handle('initial', () => ({ state: client.snapshot(), settings: publicSettings(), version: app.getVersion() }));
  handle('configure', configure);
  handle('diagnostics', () => ({
    version: app.getVersion(), platform: process.platform, status: client.status, privateApi: client.canMarkRead,
    pendingReads: Object.values(client.readStates).filter((state) => state.localOnly && !state.manualUnread).length,
    photos: client.photoStatus, activeConversation: client.chats.findIndex((chat) => chat.guid === client.active) + 1,
    conversations: client.chats.map((chat, index) => ({ conversation: index + 1,
      participants: (chat.participants || []).map((member) => ({
        matched: Boolean(client.contacts[addressKey(member.address)]),
        photo: Boolean(client.photoForAddress(member.address)),
        reason: client.photoAddressKeys(member.address).map((key) => client.contactDetails[key]).find(Boolean) || 'No matching contact record',
      })), groupPhoto: Boolean(client.groupPhotos[chat.guid]),
    })), events,
  }));
  handle('attachment-preview', async (guid) => {
    if (typeof guid !== 'string' || !client.api) throw new Error('Connect to load attachments.');
    const attachment = Object.values(client.histories).flat().flatMap((message) => message.attachments || []).find((item) => item.guid === guid);
    if (!attachment) throw new Error('Attachment is not in loaded history.');
    const api = client.api;
    const result = await attachmentCache.load(api, attachment);
    if (client.api !== api) throw new Error('Connection changed.');
    return result;
  });
  handle('open', (guid, before) => client.open(guid, before));
  handle('read', (guid) => client.read(guid));
  handle('sync', async () => {
    await client.sync();
    await Promise.all([client.loadContacts(client.api).catch(() => {}), client.loadGroupPhotos(client.api, true)]);
  });
  handle('send', (input) => client.send(input));
  handle('create-chat', (input) => client.createChat(input));
  handle('test-notification', () => {
    if (!Notification.isSupported()) throw new Error('Notifications are unavailable on this system.');
    if (!settings.notifications) throw new Error('Enable notifications and save settings first.');
    notify({ title: 'NG Bubbles', body: 'Notifications are working. Incoming messages will appear here.' });
  });
  handle('external', async (value) => {
    const url = new URL(value);
    if (!['https:', 'http:'].includes(url.protocol)) throw new Error('Only web links can be opened.');
    await shell.openExternal(url.href);
  });
  handle('attachment', async (guid, name) => {
    if (!client.api || typeof guid !== 'string' || guid.length > 500) throw new Error('Connect to download attachments.');
    const result = await dialog.showSaveDialog(window, { defaultPath: path.basename(String(name || 'attachment')) });
    if (result.canceled || !result.filePath) return;
    const url = new URL(`${client.api.server}/api/v1/attachment/${encodeURIComponent(guid)}/download`);
    url.searchParams.set('password', client.api.password);
    let response;
    try { response = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(120000) }); } catch {
      throw new Error('Attachment download failed. Check your connection.');
    }
    if (!response.ok) throw new Error(`Attachment download failed (${response.status}).`);
    try {
      await pipeline(Readable.fromWeb(response.body), fs.createWriteStream(`${result.filePath}.download`));
      await fs.promises.rename(`${result.filePath}.download`, result.filePath);
    } catch {
      await fs.promises.unlink(`${result.filePath}.download`).catch(() => {});
      throw new Error('Could not save the attachment. Check disk space and try again.');
    }
  });
};

/** @summary Start desktop integration and the background messaging client. @returns {Promise<void>} Startup completion. */
const start = async () => {
  const storedEvents = readJson('diagnostics.json', []);
  events = Array.isArray(storedEvents) ? storedEvents.slice(-400) : [];
  attachmentCache = new AttachmentCache(dataPath('attachments'), nativeImage, photoDecoder, log);
  log('startup', { version: app.getVersion(), platform: process.platform });
  const applicationTitle = `NG Bubbles v${app.getVersion()}`;
  const stored = readJson('settings.json', {});
  settings = { ...defaults, ...stored, secret: undefined };
  if (stored.secret) {
    try { settings.password = safeStorage.decryptString(Buffer.from(stored.secret, 'base64')); } catch {
      credentialError = 'Saved password could not be unlocked. Please enter it again.';
    }
  }
  window = new BrowserWindow({
    width: 1200, height: 820, minWidth: 760, minHeight: 540,
    title: applicationTitle, backgroundColor: '#101318',
    icon: path.join(__dirname, '../assets/icon.png'), show: false,
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true, spellcheck: true },
  });
  if (process.platform === 'win32' && app.isPackaged && process.env.PORTABLE_EXECUTABLE_FILE) {
    const shortcut = path.join(app.getPath('appData'), 'Microsoft/Windows/Start Menu/Programs/NG Bubbles.lnk');
    if (!fs.existsSync(shortcut)) {
      const iconPath = dataPath('icon.ico');
      fs.mkdirSync(app.getPath('userData'), { recursive: true });
      fs.copyFileSync(path.join(__dirname, '../assets/icon.ico'), iconPath);
      shell.writeShortcutLink(shortcut, 'create', {
        target: process.env.PORTABLE_EXECUTABLE_FILE, appUserModelId: appId,
        description: 'NG Bubbles', icon: iconPath, iconIndex: 0,
      });
    }
  }
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    { label: 'File', submenu: [{ label: 'Show window', click: showWindow }, { label: 'Quit', accelerator: 'Ctrl+Q', click: () => app.quit() }] },
    { label: 'Edit', submenu: [{ role: 'undo' }, { role: 'redo' }, { type: 'separator' }, { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }] },
    { label: 'Help', submenu: [{ label: 'Licenses & source', click: () => shell.openPath(path.join(app.isPackaged ? process.resourcesPath : path.join(__dirname, '..'), 'THIRD_PARTY_NOTICES.md')) }] },
    { label: 'View', submenu: [{ role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' }, { role: 'togglefullscreen' }, { label: 'Diagnostics', accelerator: 'Ctrl+Shift+D', click: () => window.webContents.send('diagnostics-open') }] },
  ]));
  window.setAutoHideMenuBar(true);
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.on('page-title-updated', (event) => event.preventDefault());
  window.webContents.on('will-navigate', (event) => event.preventDefault());
  window.webContents.on('will-attach-webview', (event) => event.preventDefault());
  session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);
  window.webContents.on('context-menu', (_event, params) => {
    if (!params.isEditable && !params.selectionText) return;
    Menu.buildFromTemplate([{ role: 'undo' }, { role: 'redo' }, { type: 'separator' },
      { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }]).popup({ window });
  });
  window.webContents.on('render-process-gone', () => { if (!quitting) window.reload(); });
  window.on('unresponsive', () => {
    dialog.showMessageBox(window, { type: 'warning', message: 'The interface stopped responding.',
      detail: 'Your background connection is still running. Reload the interface to continue.', buttons: ['Reload', 'Wait'] })
      .then(({ response }) => { if (response === 0) window.reload(); });
  });
  window.on('close', (event) => { if (!quitting && tray) { event.preventDefault(); window.hide(); } });
  window.on('focus', () => { if (client?.active) client.read(client.active); });
  const icon = nativeImage.createFromPath(path.join(__dirname, '../assets/icon.png')).resize({ width: 24, height: 24 });
  const unreadIcon = nativeImage.createFromPath(path.join(__dirname, '../assets/icon-unread.png')).resize({ width: 24, height: 24 });
  const trayImages = { normal: icon, unread: unreadIcon };
  let trayGuid;
  if (process.platform === 'win32') {
    trayImages.normal = path.join(__dirname, '../assets/icon.ico');
    trayImages.unread = path.join(__dirname, '../assets/icon-unread.ico');
    const identity = createHash('sha256').update(`${appId}:${path.win32.normalize(process.execPath).toLowerCase()}`).digest('hex');
    trayGuid = `${identity.slice(0, 8)}-${identity.slice(8, 12)}-4${identity.slice(13, 16)}-8${identity.slice(17, 20)}-${identity.slice(20, 32)}`;
  }
  if (process.platform === 'win32') tray = new Tray(trayImages.normal, trayGuid);
  else tray = new Tray(trayImages.normal);
  let trayUnread = false;
  tray.setToolTip('NG Bubbles');
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: 'Open NG Bubbles', click: showWindow },
    { label: 'Reconnect / sync', click: () => client.sync().catch(() => {}) },
    { type: 'separator' }, { label: 'Quit', click: () => app.quit() },
  ]));
  tray.on('click', showWindow);
  client = new MessagingClient({
    cache: readJson('cache.json', {}), notify, preparePhoto, log,
    isViewing: (guid) => window.isVisible() && window.isFocused() && client.active === guid,
    save: (cache) => { pendingCache = cache; clearTimeout(cacheTimer); cacheTimer = setTimeout(flushCache, 500); },
  });
  trayUnread = client.unread.size > 0;
  tray.setImage(trayUnread ? trayImages.unread : trayImages.normal);
  client.on('state', (state) => {
    const unread = client.unread.size > 0;
    if (unread !== trayUnread) {
      tray.setImage(unread ? trayImages.unread : trayImages.normal);
      trayUnread = unread;
    }
    if (!window.isDestroyed()) window.webContents.send('state', state);
    tray.setToolTip(`NG Bubbles · ${state.status} · ${client.unread.size} unread`);
    window.setTitle(client.unread.size ? `(${client.unread.size}) ${applicationTitle}` : applicationTitle);
  });
  registerHandlers();
  await window.loadFile(path.join(__dirname, 'index.html'));
  if (!process.argv.includes('--hidden')) showWindow();
  powerMonitor.on('resume', () => client.sync().catch(() => {}));
  if (settings.server && settings.password) client.connect(settings).catch(() => {});
};

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', () => { if (window) showWindow(); });
  app.on('before-quit', () => { quitting = true; client?.stop(); photoDecoder.close(); flushCache(); clearTimeout(diagnosticTimer); try { writeJson('diagnostics.json', events); } catch { /* Preserve shutdown even when disk is full. */ } });
  app.on('window-all-closed', () => { if (!tray) app.quit(); });
  app.whenReady().then(start).catch((error) => {
    dialog.showErrorBox('NG Bubbles could not start', error.message);
    app.quit();
  });
}
