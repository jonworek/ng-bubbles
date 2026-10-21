const fs = require('node:fs');
const path = require('node:path');
const { createServer } = require('./server.cjs');

/** @summary Provide synthetic conversations/media to the Windows UI acceptance run. @returns {Promise<void>} Fixture ready. */
const start = async () => {
  const server = await createServer();
  server.delayHistory(150);
  server.delayAttachments(1500);
  for (const name of ['portrait', 'landscape']) {
    server.attachments.set(`synthetic-${name}`, fs.readFileSync(path.join(__dirname, `fixtures/attachment-${name}.png`)));
  }
  for (let i = 0; i < 120; i++) {
    server.messages.push({ guid: `scroll-history-${i}`, text: `Earlier conversation message ${i}. `.repeat(1 + i % 12),
      dateCreated: Date.now() - 200000 + i * 1000, isFromMe: false, isRead: true, chats: [server.chat] });
  }
  const rich = { guid: 'synthetic-rich-link', text: 'https://example.test/weekend', balloonBundleId: 'com.apple.messages.URLBalloonProvider',
    dateCreated: Date.now() - 600, isFromMe: true, chats: [server.chat], attachments: [
      { guid: 'synthetic-link-picture', transferName: 'opaque.pluginPayloadAttachment', mimeType: null },
      { guid: 'synthetic-link-icon', transferName: 'icon.pluginPayloadAttachment', mimeType: null },
    ] };
  server.messages.push(rich);
  server.attachments.set('synthetic-link-picture', fs.readFileSync(path.join(__dirname, 'fixtures/attachment-landscape.png')));
  server.attachments.set('synthetic-link-icon', Buffer.from('not an image'));
  server.messagePayloads.set(rich.guid, [{ $objects: ['$null', { richLinkMetadata: { UID: 2 } },
    { title: { UID: 3 }, originalURL: { UID: 4 }, image: { UID: 6 } }, 'Weekend plans',
    { 'NS.relative': { UID: 5 } }, rich.text, { richLinkImageAttachmentSubstituteIndex: 0 }] }]);
  const image = fs.readFileSync(path.join(__dirname, 'fixtures/contact.jpg'));
  server.attachments.set('synthetic-image', image);
  server.messages.push({ guid: 'synthetic-media-message', text: 'Here is the photo for this weekend.',
    dateCreated: Date.now() - 5000, isFromMe: false, isRead: false, chats: [server.chat],
    attachments: [{ guid: 'synthetic-image', mimeType: 'image/jpeg', transferName: 'weekend.jpg', totalBytes: image.length }] });
  server.messages.push({ guid: 'synthetic-outgoing', text: 'Looks good! See you there. 💬',
    dateCreated: Date.now() - 3000, isFromMe: true, dateDelivered: Date.now() - 2000, chats: [server.chat] });
  server.messages.push({ guid: 'synthetic-short-incoming', text: 'Hi', handle: server.chat.participants[0],
    dateCreated: Date.now() - 2000, isFromMe: false, isRead: true, chats: [server.chat] });
  server.messages.push({ guid: 'synthetic-short-outgoing', text: 'sure',
    dateCreated: Date.now() - 1000, isFromMe: true, dateDelivered: Date.now() - 500, chats: [server.chat] });
  const sms = { guid: 'SMS;-;+15551234567', participants: [...server.chat.participants], serviceName: 'SMS' };
  server.chats.push(sms);
  server.messages.push({ guid: 'synthetic-sms-outgoing', text: 'Sent as a text message.', service: 'SMS',
    dateCreated: Date.now() - 400, isFromMe: true, chats: [sms] });
  server.chats.push({ guid: 'synthetic-group', displayName: 'Weekend plans', style: 43,
    participants: [...server.chat.participants, { address: 'alex@example.test' }] });
  server.messages.push({ guid: 'synthetic-group-message', text: 'A group conversation with member portraits.', dateCreated: Date.now() - 10000,
    isFromMe: false, chats: [{ guid: 'synthetic-group' }] });
  for (let i = 0; i < 60; i++) {
    const chat = { guid: `synthetic-history-chat-${i}`, displayName: `Earlier conversation ${i}`, participants: [{ address: `history${i}@example.test` }] };
    server.chats.push(chat);
    server.messages.push({ guid: `synthetic-chat-message-${i}`, text: 'Earlier conversation preview.',
      dateCreated: Date.now() - 86400000 + i * 1000, isFromMe: false, isRead: true, chats: [chat] });
  }
  fs.writeFileSync(process.argv[2], JSON.stringify({ server: server.url, guid: server.chat.guid }));
};
start().catch(() => { process.exitCode = 1; });
