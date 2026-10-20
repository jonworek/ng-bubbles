const fs = require('node:fs');
const path = require('node:path');
const { createServer } = require('./server.cjs');

/** @summary Provide synthetic conversations/media to the Windows UI acceptance run. @returns {Promise<void>} Fixture ready. */
const start = async () => {
  const server = await createServer();
  for (let i = 0; i < 120; i++) {
    server.messages.push({ guid: `scroll-history-${i}`, text: `Earlier conversation message ${i}. `.repeat(4),
      dateCreated: Date.now() - 200000 + i * 1000, isFromMe: false, isRead: true, chats: [server.chat] });
  }
  const image = fs.readFileSync(path.join(__dirname, 'fixtures/contact.jpg'));
  server.attachments.set('synthetic-image', image);
  server.messages.push({ guid: 'synthetic-media-message', text: 'Here is the photo for this weekend.',
    dateCreated: Date.now() - 5000, isFromMe: false, isRead: false, chats: [server.chat],
    attachments: [{ guid: 'synthetic-image', mimeType: 'image/jpeg', transferName: 'weekend.jpg', totalBytes: image.length }] });
  server.messages.push({ guid: 'synthetic-outgoing', text: 'Looks good! See you there. 💬',
    dateCreated: Date.now() - 3000, isFromMe: true, dateDelivered: Date.now() - 2000, chats: [server.chat] });
  server.chats.push({ guid: 'synthetic-group', displayName: 'Weekend plans', style: 43,
    participants: [...server.chat.participants, { address: 'alex@example.test' }] });
  server.messages.push({ guid: 'synthetic-group-message', text: 'A group conversation with member portraits.', dateCreated: Date.now() - 10000,
    isFromMe: false, chats: [{ guid: 'synthetic-group' }] });
  fs.writeFileSync(process.argv[2], JSON.stringify({ server: server.url, guid: server.chat.guid }));
};
start().catch(() => { process.exitCode = 1; });
