const readline = require('node:readline');
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
      case 'fail-reads': server.failReads(input.value); result = true; break;
      case 'private-api': server.privateApi(input.value); result = true; break;
      case 'fail-sends': server.failSends(input.value); result = true; break;
      case 'received': result = server.received; break;
      case 'reads': result = server.readRequests; break;
      case 'attachment': server.attachments.set('test-attachment', Buffer.from('test file')); result = true; break;
      case 'close': await server.close(); process.stdout.write('true\n'); return;
      default: throw new Error('Unknown fixture command');
    }
    process.stdout.write(`${JSON.stringify(result)}\n`);
  }
  await server.close();
};
run().catch(() => { process.exitCode = 1; });
