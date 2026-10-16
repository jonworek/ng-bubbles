const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { _electron: electron } = require('playwright');
const { createServer } = require('./server.cjs');
const { version } = require('../package.json');

/**
 * @summary Exercise the real sandboxed Electron UI against an isolated server.
 * @returns {Promise<void>} Desktop acceptance test completion.
 */
const run = async () => {
  const fixture = await createServer();
  fixture.contacts[1].avatar = fs.readFileSync(path.join(__dirname, 'fixtures/contact.tiff')).toString('base64');
  fixture.contacts.push({ firstName: 'Casey', emails: [{ address: 'casey@example.test' }],
    avatar: fs.readFileSync(path.join(__dirname, 'fixtures/contact.heic')).toString('base64') });
  fixture.chats.push({ guid: 'iMessage;-;casey@example.test', participants: [{ address: 'casey@example.test' }] });
  const members = [{ address: '+15551234567' }, { address: 'alex@example.test' }, { address: 'casey@example.test' }];
  const iconGroup = { guid: 'iMessage;+;weekend', style: 43, displayName: 'Weekend plans', participants: members };
  const familyGroup = { guid: 'iMessage;+;family', style: 43, displayName: 'Family chat', participants: [
    ...members, { address: 'morgan@example.test' }, { address: 'riley@example.test' }, { address: 'sam@example.test' },
  ] };
  fixture.chats.push(iconGroup, familyGroup);
  fixture.groupIcons.set(iconGroup.guid, fs.readFileSync(path.join(__dirname, 'fixtures/contact.jpg')));
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'bluebubbles-desktop-test-'));
  let desktop;
  try {
    const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => name !== 'ELECTRON_RUN_AS_NODE'));
    desktop = await electron.launch({
      args: [path.join(__dirname, '..'), '--no-sandbox', '--disable-gpu', '--password-store=basic'],
      env: { ...env, BLUEBUBBLES_TEST_DATA: directory },
      timeout: 30000,
    });
    const page = await desktop.firstWindow();
    await desktop.evaluate(({ Tray }) => {
      const setImage = Tray.prototype.setImage;
      Tray.prototype.setImage = function (image) {
        globalThis.trayImage = typeof image === 'string' ? image : image.toDataURL();
        return setImage.call(this, image);
      };
    });
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.message));
    await page.locator('#settings-dialog').waitFor({ state: 'visible' });
    assert.equal(await page.evaluate(() => typeof window.require), 'undefined');
    await page.locator('#server').fill(fixture.url);
    await page.locator('#password').fill('wrong-password');
    await page.locator('#settings-save').click();
    await page.locator('#settings-error').filter({ hasText: 'rejected the password' }).waitFor();

    await desktop.evaluate(({ safeStorage }) => {
      safeStorage.setUsePlainTextEncryption(true);
      safeStorage.isEncryptionAvailable = () => true;
      safeStorage.getSelectedStorageBackend = () => 'integration-test-only';
    });
    await page.locator('#password').fill('test-password');
    await page.locator('#settings-save').click();
    await page.locator('#settings-dialog').waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'Jamie Rivera, unread', exact: true }).waitFor();
    assert.equal(await page.locator('.chat.unread .unread-dot').count(), 1);
    assert.equal(await desktop.evaluate(({ nativeImage }, filename) => globalThis.trayImage === nativeImage.createFromPath(filename).resize({ width: 24, height: 24 }).toDataURL(), path.join(__dirname, '../assets/icon-unread.png')), true, 'unread messages add the red tray dot');
    await page.waitForFunction(() => {
      const image = document.querySelector('.chat[aria-label="Jamie Rivera, unread"] .avatar img');
      return image?.complete && image.naturalWidth === 96 && image.naturalHeight === 96;
    });
    await page.waitForFunction(() => [...document.querySelectorAll('.chat .avatar > img')]
      .filter((image) => image.complete && image.naturalWidth === 96).length === 4);
    assert.match(await page.locator('#photo-status').textContent(), /Loaded: 3 · Unreadable: 0/);
    fs.mkdirSync(path.join(__dirname, '../test-output'), { recursive: true });
    await page.screenshot({ path: path.join(__dirname, '../test-output/unread.png') });
    await page.getByRole('button', { name: 'Jamie Rivera, unread', exact: true }).click();
    await page.locator('.bubble').filter({ hasText: 'See you this weekend!' }).waitFor();
    await page.waitForFunction(() => document.querySelector('#conversation-avatar img')?.naturalWidth === 96);
    await page.getByRole('button', { name: 'Jamie Rivera', exact: true }).waitFor();
    assert.equal(fixture.readRequests.length, 1);
    assert.equal(await desktop.evaluate(({ nativeImage }, filename) => globalThis.trayImage === nativeImage.createFromPath(filename).resize({ width: 24, height: 24 }).toDataURL(), path.join(__dirname, '../assets/icon.png')), true, 'reading the last unread chat restores the normal tray icon');
    fixture.readStatus(fixture.chat.guid, false);
    await page.getByRole('button', { name: 'Jamie Rivera, unread', exact: true }).waitFor();
    fixture.readStatus(fixture.chat.guid, true);
    await page.getByRole('button', { name: 'Jamie Rivera', exact: true }).waitFor();
    const composer = page.locator('#message-input');
    await composer.fill('Voice dictation text, café and punctuation. 💬');
    await composer.press('Enter');
    await page.locator('.bubble').filter({ hasText: 'Voice dictation text, café and punctuation. 💬' }).waitFor();
    await page.waitForFunction(() => document.getElementById('message-input').value === '');
    assert.equal(fixture.received.length, 1);
    const outgoing = fixture.messages.find((message) => message.isFromMe);
    fixture.socketServer.emit('updated-message', { guid: outgoing.guid, dateDelivered: Date.now() });
    await page.locator('.message.outgoing .message-meta').filter({ hasText: 'Delivered' }).waitFor();
    fixture.socketServer.emit('updated-message', { guid: outgoing.guid, dateRead: Date.now() });
    await page.locator('.message.outgoing .message-meta').filter({ hasText: 'Read' }).waitFor();

    await composer.fill('Keep this draft when I switch conversations');
    await page.getByRole('button', { name: 'Alex', exact: true }).click();
    assert.equal(await composer.inputValue(), '');
    await page.waitForFunction(() => document.querySelector('#conversation-avatar img')?.naturalWidth === 96);
    await page.getByRole('button', { name: 'Jamie Rivera', exact: true }).click();
    assert.equal(await composer.inputValue(), 'Keep this draft when I switch conversations');
    await page.reload();
    await page.locator('#message-input').waitFor({ state: 'visible' });
    await page.waitForFunction(() => document.querySelector('#conversation-avatar img')?.naturalWidth === 96);
    assert.equal(await composer.inputValue(), 'Keep this draft when I switch conversations');

    fixture.failSends(true);
    await composer.fill('Preserve this failed send');
    await composer.press('Enter');
    await page.locator('#toast').filter({ hasText: 'Your draft is kept' }).waitFor();
    assert.equal(await composer.inputValue(), 'Preserve this failed send');
    assert.equal(fixture.received.length, 2);
    fixture.failSends(false);
    await composer.fill('');

    fixture.incoming('<img src=x onerror="window.pwned=true"> A safe plain-text message');
    await page.locator('.bubble').filter({ hasText: '<img src=x onerror=' }).waitFor();
    assert.equal(await page.evaluate(() => window.pwned), undefined);
    assert.equal(await page.locator('.bubble img').count(), 0);
    const latest = fixture.messages.at(-1);
    latest.text = 'Sounds great. I’ll send the details shortly.';
    fixture.socketServer.emit('updated-message', latest);
    await page.locator('.bubble').filter({ hasText: latest.text }).waitFor();

    fixture.attachments.set('inline-jpeg', fs.readFileSync(path.join(__dirname, 'fixtures/contact.jpg')));
    fixture.attachments.set('inline-heic', fs.readFileSync(path.join(__dirname, 'fixtures/contact.heic')));
    const mediaMessage = fixture.incoming('', false);
    mediaMessage.attachments = [
      { guid: 'inline-jpeg', mimeType: 'image/jpeg', transferName: 'Photo.jpg' },
      { guid: 'inline-heic', mimeType: 'image/heic', transferName: 'Photo.heic' },
      { guid: 'missing-image', mimeType: 'image/jpeg', transferName: 'Unavailable.jpg' },
    ];
    fixture.socketServer.emit('new-message', mediaMessage);
    await page.waitForFunction(() => [...document.querySelectorAll('.inline-media')].filter((image) => image.naturalWidth === 180 && image.naturalHeight === 120).length === 2);
    await page.locator('.attachment-preview button').filter({ hasText: 'Retry' }).waitFor();
    fixture.attachments.set('missing-image', fs.readFileSync(path.join(__dirname, 'fixtures/contact.jpg')));
    await page.locator('.attachment-preview button').filter({ hasText: 'Retry' }).click();
    await page.waitForFunction(() => document.querySelectorAll('.inline-media').length === 3);
    const downloads = fixture.attachmentRequests.length;
    await page.reload();
    await page.waitForFunction(() => [...document.querySelectorAll('.inline-media')].filter((image) => image.naturalWidth === 180).length === 3);
    assert.equal(fixture.attachmentRequests.length, downloads, 'downloaded attachments survive reload in disk cache');
    await page.locator('#settings-open').click();
    await page.locator('#diagnostics-open').click();
    const report = JSON.parse(await page.locator('#diagnostic-report').inputValue());
    assert.equal(report.privateApi, true);
    assert.equal(report.photos.loaded, 3);
    assert.ok(report.events.some((entry) => entry.event === 'read-confirmed'));
    assert.ok(report.events.some((entry) => entry.event === 'attachment-loaded'));
    assert.doesNotMatch(JSON.stringify(report), /test-password|Jamie|Rivera|alex@example|See you this weekend/);
    await page.locator('#diagnostics-copy').click();
    assert.equal(await desktop.evaluate(async ({ clipboard }) => JSON.parse(await clipboard.readText()).version), version);
    await page.screenshot({ path: path.join(__dirname, '../test-output/diagnostics.png') });
    await page.locator('#diagnostics-close').click();
    await page.locator('#settings-close').click();

    fixture.failRequests(true);
    await page.locator('#sync').click();
    await page.locator('#banner').filter({ hasText: 'Server request failed' }).waitFor();
    assert.ok(await page.locator('.bubble').count() >= 3);
    fixture.failRequests(false);
    await page.locator('#sync').click();
    await page.locator('#connection.connected').waitFor();

    await page.locator('#new-chat').click();
    await page.locator('#recipients').fill('+15557654321');
    await page.locator('#new-message').fill('Hello from the new Windows client');
    await page.locator('#new-send').click();
    await page.locator('#new-dialog').waitFor({ state: 'hidden' });
    assert.equal(fixture.received.at(-1).addresses[0], '+15557654321');
    await page.getByRole('button', { name: 'Jamie Rivera', exact: true }).click();
    await page.locator('#toast').evaluate((node) => { node.hidden = true; });
    const originalAvatar = fixture.contacts[0].avatar;
    fixture.failPhotos(true);
    await page.locator('#sync').click();
    await page.waitForFunction(() => !document.getElementById('sync').disabled);
    await page.waitForFunction(() => document.querySelector('#conversation-avatar img')?.naturalWidth === 96);
    fixture.failPhotos(false);
    fixture.contacts[0].avatar = Buffer.from('invalid image bytes').toString('base64');
    await page.locator('#sync').click();
    await page.waitForFunction(() => !document.getElementById('sync').disabled);
    assert.equal(await page.locator('#conversation-avatar img').count(), 1);
    assert.match(await page.locator('#photo-status').textContent(), /Unreadable: 1/);
    fixture.contacts[0].avatar = '';
    await page.locator('#sync').click();
    await page.waitForFunction(() => !document.getElementById('sync').disabled);
    assert.equal(await page.locator('#conversation-avatar img').count(), 0);
    fixture.contacts[0].avatar = originalAvatar;
    await page.locator('#sync').click();
    await page.waitForFunction(() => document.querySelector('#conversation-avatar img')?.naturalWidth === 96);
    await page.getByRole('button', { name: 'Weekend plans', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('#conversation-avatar > img')?.naturalWidth === 96);
    assert.equal(await page.locator('#conversation-avatar .member-avatar').count(), 0);
    fixture.groupIcons.delete(iconGroup.guid);
    fixture.socketServer.emit('group-icon-removed', { chats: [iconGroup] });
    await page.locator('#conversation-avatar.avatar-group').waitFor();
    assert.equal(await page.locator('#conversation-avatar .member-avatar').count(), 3);
    fixture.groupIcons.set(iconGroup.guid, fs.readFileSync(path.join(__dirname, 'fixtures/contact.tiff')));
    fixture.socketServer.emit('group-icon-changed', { chats: [iconGroup] });
    await page.waitForFunction(() => document.querySelector('#conversation-avatar > img')?.naturalWidth === 96);
    await page.getByRole('button', { name: 'Family chat', exact: true }).click();
    await page.locator('#conversation-avatar.avatar-group').waitFor();
    assert.equal(await page.locator('#conversation-avatar .member-avatar').count(), 4);
    assert.equal(await page.locator('#conversation-avatar .member-count').textContent(), '+3');
    await page.waitForFunction(() => [...document.querySelectorAll('#conversation-avatar .member-avatar img')]
      .filter((image) => image.complete && image.naturalWidth === 96).length === 3);
    familyGroup.participants.pop();
    fixture.socketServer.emit('participant-removed', { chats: [familyGroup] });
    await page.locator('#conversation-avatar .member-count').filter({ hasText: '+2' }).waitFor();
    await page.screenshot({ path: path.join(__dirname, '../test-output/groups.png') });
    await page.getByRole('button', { name: 'Jamie Rivera', exact: true }).click();
    fs.mkdirSync(path.join(__dirname, '../test-output'), { recursive: true });
    await page.waitForFunction(() => [...document.querySelectorAll('.inline-media')].filter((image) => image.naturalWidth === 180).length === 3);
    await page.waitForFunction(() => document.querySelector('#conversation-avatar img')?.naturalWidth === 96);
    await page.screenshot({ path: path.join(__dirname, '../test-output/desktop.png') });

    await page.getByRole('button', { name: 'Alex', exact: true }).click();
    await desktop.evaluate(({ BrowserWindow }) => { BrowserWindow.getAllWindows()[0].close(); });
    assert.equal(await desktop.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].isVisible()), false);
    assert.equal(await desktop.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length), 1);
    await desktop.evaluate(({ Notification }) => {
      Notification.isSupported = () => true;
      Notification.prototype.show = function () { globalThis.clickedNotification = this; };
    });
    fixture.incoming('Open the conversation from this notification');
    await page.waitForFunction(() => [...document.querySelectorAll('.chat-preview')].some((node) => node.textContent.includes('Open the conversation from this notification')));
    await page.locator('#search').fill('Alex');
    await desktop.evaluate(() => globalThis.clickedNotification.emit('click'));
    await page.getByRole('button', { name: 'Jamie Rivera', exact: true }).waitFor();
    assert.equal(await page.locator('#search').inputValue(), '');
    assert.equal(await page.locator('.chat.active').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('.chat.active .chat-name').textContent(), 'Jamie Rivera');
    assert.equal(await desktop.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].isVisible()), true);
    await page.waitForFunction(() => document.activeElement === document.getElementById('message-input'));
    for (let index = 0; index < 25; index++) fixture.chats.push({ guid: `iMessage;-;scroll-${index}`, displayName: `History ${index}`, participants: [] });
    await page.locator('#sync').click();
    await page.waitForFunction(() => document.querySelectorAll('.chat').length > 25);
    await page.locator('#chat-list').evaluate((node) => { node.scrollTop = node.scrollHeight; });
    await desktop.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].hide());
    fixture.incoming('Another notification while the conversation list is scrolled');
    await page.waitForFunction(() => [...document.querySelectorAll('.bubble')].some((node) => node.textContent.includes('Another notification while')));
    await desktop.evaluate(() => globalThis.clickedNotification.emit('click'));
    await page.waitForFunction(() => {
      const selected = document.querySelector('.chat.active').getBoundingClientRect();
      const list = document.getElementById('chat-list').getBoundingClientRect();
      return selected.top >= list.top && selected.bottom <= list.bottom;
    });
    const stored = JSON.parse(fs.readFileSync(path.join(directory, 'settings.json'), 'utf8'));
    assert.equal(stored.password, undefined);
    assert.ok(stored.secret);
    assert.deepEqual(errors, []);
    console.log('PASS: real Electron setup, contact/group photos, authentication, sandbox, send/receive, drafts, read status, inline attachments, preview retry/cache, diagnostics/copy privacy, offline recovery, XSS, new chat, close-to-tray, and notification restore/selection/filter/scroll.');
  } finally {
    await desktop?.close();
    await fixture.close();
    fs.rmSync(directory, { recursive: true, force: true });
  }
};

run().catch((error) => { console.error(error); process.exitCode = 1; });
