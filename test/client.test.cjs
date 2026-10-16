const { test } = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const { MessagingClient } = require('../src/client.cjs');
const { BlueBubblesApi } = require('../src/api.cjs');
const { normalizeServer, mergeMessages } = require('../src/model.cjs');
const { createServer } = require('./server.cjs');

test('loads conversations, sends once, merges socket echoes, recovers missed events, and persists unread state', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  const notifications = [];
  let saved;
  let viewing = false;
  const client = new MessagingClient({
    cache: { cursor: Date.now() - 1000 }, notify: (item) => notifications.push(item),
    save: (cache) => { saved = structuredClone(cache); }, isViewing: () => viewing,
  });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  if (!client.socket.connected) await once(client.socket, 'connect');
  await client.sync();
  assert.equal(client.chats.length, 2);
  assert.equal(notifications.length, 0, 'initial old history must not notify');
  await client.open(server.chat.guid);
  assert.equal(client.snapshot().messages.length, 1);
  await client.send({ guid: server.chat.guid, text: 'Dictated text — punctuation, café, and emoji 💬' });
  await client.sync();
  assert.equal(server.received.length, 1);
  assert.equal(server.received[0].method, 'apple-script');
  assert.ok(server.received[0].tempGuid);
  assert.equal(client.snapshot().messages.filter((m) => m.isFromMe).length, 1, 'socket and HTTP echoes deduplicate');

  const incoming = server.incoming('A new message while minimized', false);
  await client.sync();
  assert.equal(notifications.length, 1);
  assert.equal(notifications[0].body, incoming.text);
  assert.equal(client.snapshot().chats.find((c) => c.guid === server.chat.guid).unread, true);
  client.accept(incoming);
  await client.sync();
  assert.equal(notifications.length, 1, 'repeated live and catch-up events notify once');

  client.read(server.chat.guid);
  viewing = true;
  const visible = server.incoming('Already reading this chat', false);
  client.accept(visible);
  assert.equal(notifications.length, 1, 'focused conversation should not notify');
  assert.equal(client.snapshot().chats.find((c) => c.guid === server.chat.guid).unread, false);
  assert.ok(saved.seen.includes(visible.guid));

  server.failSends(true);
  await assert.rejects(client.send({ guid: server.chat.guid, text: 'Do not resend automatically' }), /Server request failed/);
  assert.equal(server.received.length, 2, 'failed sends are attempted once');
  server.failRequests(true);
  await assert.rejects(client.sync(), /Server request failed/);
  assert.equal(client.status, 'offline');
  assert.ok(client.snapshot().messages.length >= 3, 'cached history remains while disconnected');
  server.failRequests(false);
  await client.sync();
  assert.equal(client.status, 'connected');
});

test('paginates more than one page of missed messages without loss or repeated notifications', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  const notifications = [];
  const client = new MessagingClient({ cache: { cursor: Date.now() - 1000 }, notify: (item) => notifications.push(item) });
  t.after(() => client.stop());
  for (let index = 0; index < 230; index++) server.incoming(`Recovered message ${index}`, false);
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'private-api' });
  await client.sync();
  assert.equal(notifications.length, 230);
  assert.equal(new Set(client.histories[server.chat.guid].map((m) => m.guid)).size, 230);
  await client.sync();
  assert.equal(notifications.length, 230);
});

test('restarted client retains cached history and notification deduplication', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  const incoming = server.incoming('Already notified', false);
  const notifications = [];
  const client = new MessagingClient({
    cache: { cursor: incoming.dateCreated, chats: [server.chat], seen: [incoming.guid],
      histories: { [server.chat.guid]: [incoming] }, unread: [server.chat.guid] },
    notify: (item) => notifications.push(item),
  });
  t.after(() => client.stop());
  assert.equal(client.chats.length, 1);
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  assert.equal(notifications.length, 0);
  assert.equal(client.histories[server.chat.guid].filter((m) => m.guid === incoming.guid).length, 1);
});

test('validates server origins, rejects authentication failures, and preserves updated message fields', async (t) => {
  assert.equal(normalizeServer(' https://example.test/api/v1/ '), 'https://example.test');
  for (const value of ['file:///etc/passwd', 'https://user:secret@example.test', 'https://example.test/?password=secret', 'https://example.test/web']) {
    assert.throws(() => normalizeServer(value));
  }
  const merged = mergeMessages([{ guid: 'same', text: 'hello', dateCreated: 1 }], [{ guid: 'same', dateDelivered: 2 }]);
  assert.deepEqual(merged, [{ guid: 'same', text: 'hello', dateCreated: 1, dateDelivered: 2 }]);
  const server = await createServer();
  t.after(() => server.close());
  await assert.rejects(new BlueBubblesApi(server.url, 'wrong-password').chats(), /rejected the password/);
});

test('imports pre-existing unread messages and clears stale cached unread flags without historical notifications', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  const notifications = [];
  const client = new MessagingClient({ cache: { unread: ['iMessage;-;alex@example.test'] }, notify: (item) => notifications.push(item) });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  assert.equal(client.snapshot().chats.find((chat) => chat.guid === server.chat.guid).unread, true);
  assert.equal(client.snapshot().chats.find((chat) => chat.title === 'Alex').unread, false);
  assert.deepEqual(notifications, []);
  server.readStatus(server.chat.guid, true, false);
  await client.sync();
  assert.equal(client.unread.has(server.chat.guid), false, 'periodic query recovers a missed read event');
  server.readStatus(server.chat.guid, false, false);
  await client.sync();
  assert.equal(client.unread.has(server.chat.guid), true, 'periodic query recovers unread while disconnected');
});

test('opening a conversation marks it read on the server and local read survives restart and stale snapshots', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  let cache;
  const client = new MessagingClient({ isViewing: () => true, save: (value) => { cache = structuredClone(value); } });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  assert.equal(client.unread.has(server.chat.guid), true);
  await client.open(server.chat.guid);
  assert.equal(client.unread.has(server.chat.guid), false);
  assert.deepEqual(server.readRequests, [server.chat.guid]);
  assert.equal(server.messages[0].isRead, true);
  const restarted = new MessagingClient({ cache });
  t.after(() => restarted.stop());
  server.messages[0].isRead = false;
  await restarted.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  assert.equal(restarted.unread.has(server.chat.guid), false, 'old unread rows must not undo a viewed conversation');
  const fresh = server.incoming('New message after viewing', false);
  fresh.dateCreated = cache.readStates[server.chat.guid].through;
  await restarted.sync();
  assert.equal(restarted.unread.has(server.chat.guid), true, 'a new GUID at the same timestamp remains unread');
});

test('read/unread events update indicators without notifications and read receipts do not mark incoming chats unread', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  const notifications = [];
  const client = new MessagingClient({ notify: (value) => notifications.push(value) });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  if (!client.socket.connected) await once(client.socket, 'connect');
  const readUpdate = once(client.socket, 'chat-read-status-changed');
  server.readStatus(server.chat.guid, true);
  await readUpdate;
  assert.equal(client.unread.has(server.chat.guid), false);
  const unreadUpdate = once(client.socket, 'chat-read-status-changed');
  server.socketServer.emit('chat-read-status-changed', { chatGuid: server.chat.guid, read: false });
  await unreadUpdate;
  assert.equal(client.unread.has(server.chat.guid), true);
  await client.sync();
  assert.equal(client.unread.has(server.chat.guid), true, 'explicit manual-unread event survives a stale query');
  client.applyReadStatus({ chatGuid: server.chat.guid, read: 'false' });
  assert.equal(client.unread.has(server.chat.guid), true, 'invalid boolean is ignored');
  client.accept({ guid: 'initial-message', dateRead: Date.now() }, false);
  assert.equal(client.unread.has(server.chat.guid), true, 'recipient receipt does not clear local unread');
  assert.deepEqual(notifications, []);
});

test('unsupported or failed server marking keeps a local read cutoff and explains the synchronization limitation', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  server.privateApi(false);
  const client = new MessagingClient({ isViewing: () => true });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await client.open(server.chat.guid);
  assert.equal(client.unread.has(server.chat.guid), false);
  assert.equal(server.readRequests.length, 0);
  assert.match(client.snapshot().error, /Enable the server’s Private API/);
  await client.sync();
  assert.equal(client.unread.has(server.chat.guid), false);
  server.privateApi(true);
  server.failReads(true);
  client.canMarkRead = true;
  client.applyReadStatus({ chatGuid: server.chat.guid, read: false });
  await client.read(server.chat.guid);
  assert.equal(client.unread.has(server.chat.guid), false);
  assert.match(client.snapshot().error, /retrying automatically/);
});

test('a confirmed shared read does not permanently hide that same message being marked unread elsewhere', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  const client = new MessagingClient({ isViewing: () => true });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await client.open(server.chat.guid);
  await client.sync();
  assert.equal(client.unread.has(server.chat.guid), false);
  client.isViewing = () => false;
  server.readStatus(server.chat.guid, false, false);
  await client.sync();
  assert.equal(client.unread.has(server.chat.guid), true);
});

test('requests and caches contact photos, matches dialing/email aliases, and avoids assigning portraits to groups or foreign numbers', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  server.contacts[0].emails = [{ address: 'Jamie@Example.Test' }];
  const photo = `data:image/jpeg;base64,${server.contacts[0].avatar}`;
  let cache;
  const client = new MessagingClient({
    preparePhoto: (value) => value ? `data:image/jpeg;base64,${value}` : '',
    save: (value) => { cache = structuredClone(value); },
  });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await client.loadContacts(client.api);
  assert.ok(server.photoRequests() > 0, 'photo data must be explicitly requested from BlueBubbles');
  assert.equal(client.snapshot().chats.find((chat) => chat.guid === server.chat.guid).photo, photo);
  assert.equal(client.photoForChat({ participants: [{ address: '(555) 123-4567' }] }), photo);
  assert.equal(client.photoForChat({ participants: [{ address: ' JAMIE@example.test ' }] }), photo);
  assert.equal(client.photoForChat({ participants: [{ address: '+445551234567' }] }), '');
  assert.equal(client.photoForChat({ style: 43, participants: [{ address: '+15551234567' }] }), '');
  assert.equal(client.photoForChat({ participants: [{ address: '+15551234567' }, { address: 'alex@example.test' }] }), '');
  assert.equal(client.photoForChat({ participants: [{ address: 'alex@example.test' }] }), '');
  const restarted = new MessagingClient({ cache });
  assert.equal(restarted.snapshot().chats.find((chat) => chat.guid === server.chat.guid).photo, photo, 'cached photos are available before connecting');
  server.failPhotos(true);
  await client.loadContacts(client.api);
  assert.equal(client.snapshot().chats.find((chat) => chat.guid === server.chat.guid).photo, photo, 'optional photo-fetch failure keeps the cached photo');
  server.failPhotos(false);
  server.contacts[0].avatar = '';
  await client.loadContacts(client.api);
  assert.equal(client.snapshot().chats.find((chat) => chat.guid === server.chat.guid).photo, '', 'a successfully fetched removed photo clears stale cache');
});

test('async decoding covers all contacts, shares duplicate-ID photos across addresses, and reports unavailable formats', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  server.contacts[0].id = 'jamie';
  server.contacts.push({ id: 'jamie', firstName: 'Jamie', emails: [{ address: 'jamie@example.test' }] });
  server.contacts[1].avatar = 'unsupported';
  const client = new MessagingClient({ preparePhoto: async (value) => value === 'unsupported' ? '' : `decoded:${value}` });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await client.loadContacts(client.api);
  assert.ok(client.photoForAddress('jamie@example.test'));
  assert.equal(client.photoForAddress('tel:+1 (555) 123-4567 ext. 123'), client.photoForAddress('jamie@example.test'));
  assert.equal(client.photoStatus.supplied, 2);
  assert.equal(client.photoStatus.loaded, 1);
  assert.equal(client.photoStatus.unreadable, 1);
  assert.equal(client.photoStatus.records, 3);
});

test('prefers server group icons, caches them, preserves transient failures, and falls back to member portraits after removal', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  const group = { guid: 'iMessage;+;group-icon', style: 43, displayName: 'Weekend plans', participants: [
    { address: '+15551234567' }, { address: 'alex@example.test' },
  ] };
  server.chats.push(group);
  const bytes = Buffer.from(server.contacts[0].avatar, 'base64');
  server.groupIcons.set(group.guid, bytes);
  let cache;
  const client = new MessagingClient({ preparePhoto: async (value) => `data:image/jpeg;base64,${Buffer.isBuffer(value) ? value.toString('base64') : value}`,
    save: (value) => { cache = structuredClone(value); } });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await client.loadContacts(client.api);
  await client.loadGroupPhotos(client.api);
  const entry = client.snapshot().chats.find((chat) => chat.guid === group.guid);
  assert.equal(entry.photo, `data:image/jpeg;base64,${bytes.toString('base64')}`);
  assert.equal(entry.avatarMembers.length, 2);
  const restarted = new MessagingClient({ cache });
  assert.equal(restarted.photoForChat(group), entry.photo);
  const requests = server.groupIconRequests.length;
  await client.loadGroupPhotos(client.api);
  assert.equal(server.groupIconRequests.length, requests, 'routine polling does not re-download every group icon');
  server.failGroupIcons(true);
  await client.loadGroupPhotos(client.api, true);
  assert.equal(client.photoForChat(group), entry.photo);
  server.failGroupIcons(false);
  server.groupIcons.delete(group.guid);
  await client.loadGroupPhotos(client.api, true);
  assert.equal(client.photoForChat(group), '');
  assert.equal(client.snapshot().chats.find((chat) => chat.guid === group.guid).avatarMembers[0].photo, entry.photo);
  server.groupIcons.set(group.guid, bytes);
  await client.loadGroupPhotos(client.api, true);
  if (!client.socket.connected) await once(client.socket, 'connect');
  const removed = once(client.socket, 'group-icon-removed');
  server.socketServer.emit('group-icon-removed', { chats: [group] });
  await removed;
  assert.equal(client.photoForChat(group), '');
});


test('retries failed reads, detects newly enabled Private API, and reads REST catch-up only while viewing', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  let cache;
  let viewing = true;
  const client = new MessagingClient({ isViewing: () => viewing, save: (value) => { cache = structuredClone(value); } });
  t.after(() => client.stop());
  server.failReads(true);
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await client.open(server.chat.guid);
  assert.equal(client.unread.has(server.chat.guid), false);
  assert.equal(cache.readStates[server.chat.guid].localOnly, true);
  server.failReads(false);
  await client.sync();
  assert.equal(server.messages[0].isRead, true, 'failed read retries without requiring another unread flag');
  const recovered = server.incoming('Missed live event while looking at this chat', false);
  await client.sync();
  assert.equal(recovered.isRead, true, 'REST catch-up in focused chat marks server read');
  server.privateApi(false);
  const pending = server.incoming('Waiting for Private API', false);
  await client.sync();
  assert.equal(pending.isRead, undefined);
  server.privateApi(true);
  await client.sync();
  assert.equal(pending.isRead, true, 'capability refresh detects Private API enabled after connecting');
  viewing = false;
  const background = server.incoming('Leave unread in background', false);
  await client.sync();
  assert.equal(background.isRead, undefined);
  assert.equal(client.unread.has(server.chat.guid), true);
});

test('uses a decodable thumbnail when a contact full-size image is invalid', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  server.contacts[1].avatar = 'broken';
  server.contacts[1].contactThumbnailImage = 'thumbnail';
  const client = new MessagingClient({ preparePhoto: async (value) => value === 'broken' ? '' : `decoded:${value}` });
  t.after(() => client.stop());
  await client.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await client.loadContacts(client.api);
  assert.equal(client.photoForAddress('alex@example.test'), 'decoded:thumbnail');
  assert.equal(client.contactDetails['email:alex@example.test'], 'Photo decoded');
});


test('pending read survives restart and never marks a new unseen message read in the background', async (t) => {
  const server = await createServer();
  t.after(() => server.close());
  let cache;
  const first = new MessagingClient({ isViewing: () => true, save: (value) => { cache = structuredClone(value); } });
  t.after(() => first.stop());
  server.failReads(true);
  await first.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  await first.open(server.chat.guid);
  first.stop();
  server.failReads(false);
  const background = server.incoming('Unseen after restart', false);
  const restarted = new MessagingClient({ cache });
  t.after(() => restarted.stop());
  await restarted.connect({ server: server.url, password: 'test-password', sendMethod: 'apple-script' });
  assert.equal(background.isRead, undefined);
  assert.equal(restarted.unread.has(server.chat.guid), true);
  restarted.isViewing = () => true;
  await restarted.open(server.chat.guid);
  assert.equal(background.isRead, true);
});
