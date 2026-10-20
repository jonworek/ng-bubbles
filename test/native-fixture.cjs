const readline = require('node:readline');
const fs = require('node:fs');
const path = require('node:path');
const { createServer } = require('./server.cjs');

/** @summary Run the synthetic protocol fixture for native integration tests. @returns {Promise<void>} Server lifetime. */
const run = async () => {
  const server = await createServer();
  process.stdout.write(`${JSON.stringify({ url: server.url, guid: server.chat.guid })}\n`);
  const lines = readline.createInterface({ input: process.stdin });
  for await (const line of lines) {
    const input = JSON.parse(line);
    let result;
    switch (input.action) {
      case 'incoming': result = server.incoming(input.text, input.emit !== false); break;
      case 'read-status': server.readStatus(server.chat.guid, input.read, input.emit !== false); result = true; break;
      case 'manual-unread-event': server.socketServer.emit('chat-read-status-changed', { chatGuid: server.chat.guid, read: false }); result = true; break;
      case 'delay-history': server.delayHistory(input.value); result = true; break;
      case 'acknowledge-reads-only': server.acknowledgeReadsOnly(input.value); result = true; break;
      case 'direct-unread': server.directUnread(input.value); result = true; break;
      case 'fail-reads': server.failReads(input.value); result = true; break;
      case 'private-api': server.privateApi(input.value); result = true; break;
      case 'fail-sends': server.failSends(input.value); result = true; break;
      case 'received': result = server.received; break;
      case 'reads': result = server.readRequests; break;
      case 'history-requests': result = server.historyRequests; break;
      case 'fail-history': server.failHistory(input.value); result = true; break;
      case 'history-seed': {
        server.messages.splice(0);
        const now = Date.now();
        for (let i = 0; i < input.count; i++) {
          server.messages.push({ guid: `history-${String(i).padStart(6, '0')}`, text: `History message ${i}`,
            dateCreated: now - (input.count - i) * 1000, isFromMe: false, isRead: true, chats: [server.chat] });
        }
        result = true; break;
      }
      case 'photos': {
        server.contacts.push({ id: 'other-contact', firstName: 'Taylor', emails: [{ address: 'taylor@example.test' }],
          avatar: fs.readFileSync(path.join(__dirname, 'fixtures/contact.tiff')).toString('base64') });
        server.chats.push({ guid: 'test-group', style: 43, participants: [{ address: '+15551234567' }, { address: 'taylor@example.test' }] });
        server.groupIcons.set('test-group', fs.readFileSync(path.join(__dirname, 'fixtures/contact.jpg')));
        result = true; break;
      }
      case 'remove-group-icon':
        server.groupIcons.delete('test-group');
        server.socketServer.emit('group-icon-removed', { chatGuid: 'test-group' });
        result = true; break;
      case 'attachment': server.attachments.set('test-attachment', Buffer.from('test file')); result = true; break;
      case 'close': await server.close(); process.stdout.write('true\n'); return;
      default: throw new Error('Unknown fixture command');
    }
    process.stdout.write(`${JSON.stringify(result)}\n`);
  }
  await server.close();
};
run().catch(() => { process.exitCode = 1; });
