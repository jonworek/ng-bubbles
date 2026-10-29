const http = require('node:http');
const { randomUUID } = require('node:crypto');
const { Server } = require('socket.io');
const fs = require('node:fs');
const path = require('node:path');

/**
 * @summary Start an isolated BlueBubbles-compatible HTTP and Socket.IO fixture.
 * @returns {Promise<object>} Fixture controls and cleanup.
 */
const createServer = async () => {
  const chat = { guid: 'iMessage;-;+15551234567', participants: [{ address: '+15551234567' }] };
  const secondChat = { guid: 'iMessage;-;alex@example.test', participants: [{ address: 'alex@example.test' }] };
  const chats = [chat, secondChat];
  const contacts = [
    { firstName: 'Jamie', lastName: 'Rivera', phoneNumbers: [{ address: '+1 (555) 123-4567' }],
      avatar: fs.readFileSync(path.join(__dirname, 'fixtures/contact.jpg')).toString('base64') },
    { firstName: 'Alex', emails: [{ address: 'Alex@Example.Test' }], avatar: '' },
  ];
  const attachments = new Map();
  const messagePayloads = new Map();
  const attachmentRequests = [];
  const groupIcons = new Map();
  const groupIconRequests = [];
  let failGroupIcons = false;
  const messages = [{ guid: 'initial-message', text: 'See you this weekend!', dateCreated: Date.now() - 60000, isFromMe: false, isRead: false, chats: [chat] }];
  const received = [];
  const readRequests = [];
  const restartRequests = [];
  const deleteRequests = [];
  let failDeletes = [];
  let deleteDelay = 0;
  const historyRequests = [];
  let failHistory = false;
  let privateApi = true;
  let failReads = false;
  let acknowledgeReadsOnly = false;
  let directUnread = false;
  let failSends = false;
  let failRequests = false;
  let failPhotos = false;
  let historyDelay = 0;
  let attachmentDelay = 0;
  let photoRequests = 0;
  let socketServer;
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost');
    res.setHeader('Content-Type', 'application/json');
    const respond = (status, data) => { res.statusCode = status; res.end(JSON.stringify({ status, data })); };
    if (url.searchParams.get('password') !== 'test-password') return respond(401, null);
    if (failRequests) return respond(503, null);
    let body = '';
    for await (const chunk of req) body += chunk;
    const payload = body ? JSON.parse(body) : {};
    if (url.pathname === '/api/v1/server/restart/hard' && req.method === 'GET') { restartRequests.push({ method: req.method }); return respond(200, null); }
    if (url.pathname.startsWith('/api/v1/chat/') && req.method === 'DELETE') {
      const guid = decodeURIComponent(url.pathname.slice('/api/v1/chat/'.length));
      deleteRequests.push(guid);
      if (deleteDelay) await new Promise((resolve) => setTimeout(resolve, deleteDelay));
      if (!privateApi) return respond(400, null);
      if (failDeletes.includes(guid)) return respond(503, null);
      const index = chats.findIndex((item) => item.guid === guid);
      if (index < 0) return respond(404, null);
      chats.splice(index, 1);
      for (let i = messages.length - 1; i >= 0; i--) if (messages[i].chats?.some((item) => item.guid === guid)) messages.splice(i, 1);
      return respond(200, null);
    }
    if (url.pathname === '/api/v1/ping') return respond(200, 'pong');
    if (url.pathname === '/api/v1/server/info') return respond(200, { private_api: privateApi, computer_id: "synthetic-test-mac" });
    if (url.pathname.endsWith('/read') && req.method === 'POST') {
      const guid = decodeURIComponent(url.pathname.slice('/api/v1/chat/'.length, -'/read'.length));
      readRequests.push(guid);
      if (failReads || !privateApi) return respond(500, null);
      for (const message of messages) {
        if (!acknowledgeReadsOnly && message.chats?.some((item) => item.guid === guid)) message.isRead = true;
      }
      socketServer.emit('chat-read-status-changed', { chatGuid: guid, read: true });
      return respond(200, null);
    }
    if (url.pathname === '/api/v1/test/attachment-requests') return respond(200, attachmentRequests);
    if (url.pathname.startsWith('/api/v1/attachment/') && url.pathname.endsWith('/download')) {
      const guid = decodeURIComponent(url.pathname.slice('/api/v1/attachment/'.length, -'/download'.length));
      attachmentRequests.push(guid);
      if (attachmentDelay) await new Promise((resolve) => setTimeout(resolve, attachmentDelay));
      if (!attachments.has(guid)) return respond(404, null);
      res.setHeader('Content-Type', 'application/octet-stream');
      res.end(attachments.get(guid));
      return;
    }
    if (url.pathname === '/api/v1/contact') {
      const withPhotos = url.searchParams.get('extraProperties') === 'avatar';
      if (withPhotos) {
        photoRequests++;
        if (failPhotos) return respond(503, null);
      }
      return respond(200, contacts.map((contact) => ({ ...contact, avatar: withPhotos ? contact.avatar : '' })));
    }
    if (url.pathname.endsWith('/icon') && req.method === 'GET') {
      const guid = decodeURIComponent(url.pathname.slice('/api/v1/chat/'.length, -'/icon'.length));
      groupIconRequests.push(guid);
      if (failGroupIcons) return respond(503, null);
      const bytes = groupIcons.get(guid);
      if (!bytes) return respond(404, null);
      res.setHeader('Content-Type', 'image/jpeg');
      res.end(bytes);
      return;
    }
    if (url.pathname === '/api/v1/chat/query') {
      return respond(200, chats.slice(payload.offset || 0, (payload.offset || 0) + payload.limit)
        .map((item) => ({ ...item, lastMessage: messages.filter((m) => m.chats?.[0].guid === item.guid).at(-1) })));
    }
    if (url.pathname.startsWith('/api/v1/message/') && req.method === 'GET') {
      const guid = decodeURIComponent(url.pathname.slice('/api/v1/message/'.length));
      const message = messages.find((item) => item.guid === guid);
      return message ? respond(200, { ...message, payloadData: messagePayloads.get(guid) ?? null }) : respond(404, null);
    }
    if (url.pathname === '/api/v1/message/query') {
      let result = messages.filter((m) => m.dateCreated > (payload.after || 0)).sort((a, b) => a.dateCreated - b.dateCreated);
      if (payload.where?.length) {
        if (payload.where[0].statement !== 'message.is_from_me = :fromMe AND message.is_read = :read'
          || payload.where[0].args.fromMe !== 0 || payload.where[0].args.read !== 0) return respond(400, null);
        result = result.filter((message) => !message.isFromMe && !message.isRead);
        if (directUnread) result = result.map((message) => ({ ...message, chatGuid: message.chats[0].guid, chats: [] }));
      }
      return respond(200, result.slice(payload.offset || 0, (payload.offset || 0) + payload.limit));
    }
    if (url.pathname.endsWith('/message') && req.method === 'GET') {
      if (historyDelay) await new Promise((resolve) => setTimeout(resolve, historyDelay));
      const guid = decodeURIComponent(url.pathname.slice('/api/v1/chat/'.length, -'/message'.length));
      const before = Number(url.searchParams.get('before')) || Infinity;
      historyRequests.push({ guid, before: Number.isFinite(before) ? before : null });
      if (failHistory) return respond(503, null);
      return respond(200, messages.filter((m) => m.chats?.[0]?.guid === guid && m.dateCreated < before)
        .sort((a, b) => b.dateCreated - a.dateCreated).slice(0, 100));
    }
    if (url.pathname === '/api/v1/message/text') {
      received.push(payload);
      if (failSends) return respond(500, null);
      const message = { guid: randomUUID(), text: payload.message, dateCreated: Date.now(), isFromMe: true,
        chats: [{ ...chat, guid: payload.chatGuid }], tempGuid: payload.tempGuid };
      messages.push(message);
      socketServer.emit('new-message', message);
      return respond(200, message);
    }
    if (url.pathname === '/api/v1/chat/new') {
      const created = { guid: `iMessage;-;${payload.addresses[0]}`, participants: payload.addresses.map((address) => ({ address })) };
      received.push(payload);
      return respond(200, created);
    }
    return respond(404, null);
  });
  socketServer = new Server(server);
  socketServer.use((socket, next) => next(socket.handshake.query.guid === 'test-password' ? undefined : new Error('Unauthorized')));
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return {
    url: `http://127.0.0.1:${server.address().port}`, received, readRequests, restartRequests, deleteRequests, delayDeletes: (value) => { deleteDelay = value; }, failDeletes: (value) => { failDeletes = value; }, historyRequests, messages, chat, chats, contacts, socketServer,
    failHistory: (value) => { failHistory = value; },
    groupIcons, groupIconRequests, attachments, attachmentRequests, messagePayloads,
    failGroupIcons: (value) => { failGroupIcons = value; },
    photoRequests: () => photoRequests,
    delayAttachments: (value) => { attachmentDelay = value; },
    delayHistory: (value) => { historyDelay = value; },
    failPhotos: (value) => { failPhotos = value; },
    privateApi: (value) => { privateApi = value; },
    failReads: (value) => { failReads = value; },
    acknowledgeReadsOnly: (value) => { acknowledgeReadsOnly = value; },
    directUnread: (value) => { directUnread = value; },
    readStatus: (guid, read, emit = true) => {
      for (const message of messages) {
        if (message.chats?.some((item) => item.guid === guid)) message.isRead = read;
      }
      if (emit) socketServer.emit('chat-read-status-changed', { chatGuid: guid, read });
    },
    failSends: (value) => { failSends = value; },
    failRequests: (value) => { failRequests = value; },
    incoming: (text, emit = true) => {
      const message = { guid: randomUUID(), text, dateCreated: Date.now(), isFromMe: false, chats: [chat] };
      messages.push(message);
      if (emit) socketServer.emit('new-message', message);
      return message;
    },
    close: async () => { await new Promise((resolve) => socketServer.close(resolve)); server.closeAllConnections(); },
  };
};

module.exports = { createServer };
