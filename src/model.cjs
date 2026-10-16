/**
 * @summary Validate a user-supplied BlueBubbles server origin.
 * @param {string} value - Server URL.
 * @returns {string} Normalized server origin.
 */
const normalizeServer = (value) => {
  const url = new URL(value.trim());
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) {
    throw new Error('Enter an http:// or https:// server URL without a password or query string.');
  }
  if (url.pathname !== '/' && url.pathname !== '/api/v1' && url.pathname !== '/api/v1/') {
    throw new Error('Enter the server root URL, for example https://your-server.example.com.');
  }
  return url.origin;
};

/**
 * @summary Merge server messages by stable GUID in chronological order.
 * @param {Array} existing - Previously fetched messages.
 * @param {Array} incoming - New or updated messages.
 * @returns {Array} Deduplicated, bounded message history.
 */
const mergeMessages = (existing, incoming) => {
  const map = new Map(existing.filter((m) => m.guid).map((m) => [m.guid, m]));
  for (const item of incoming) {
    if (!item.guid) continue;
    map.set(item.guid, { ...map.get(item.guid), ...item });
  }
  return [...map.values()].sort((a, b) => (a.dateCreated || 0) - (b.dateCreated || 0)).slice(-1000);
};

/**
 * @summary Normalize phone numbers and email addresses for contact lookup.
 * @param {string} address - Phone number or email.
 * @returns {string} Comparable contact key.
 */
const addressKey = (address = '') => {
  if (address.includes('@')) return address.toLowerCase();
  return address.replace(/\D/g, '').slice(-10);
};

/**
 * @summary Resolve a conversation title from server contacts and participants.
 * @param {object} chat - Server conversation.
 * @param {object} contacts - Address-to-name mapping.
 * @returns {string} Display title.
 */
const chatTitle = (chat, contacts = {}) => chat.displayName || (chat.participants || [])
  .map((p) => contacts[addressKey(p.address)] || p.address || 'Unknown').join(', ') || chat.guid || 'Conversation';

/**
 * @summary Extract readable text without treating server content as HTML.
 * @param {object} message - Server message.
 * @returns {string} Text or attachment fallback.
 */
const messageText = (message) => {
  if (message.text?.replace(/\uFFFC/g, '').trim()) return message.text.replace(/\uFFFC/g, '');
  if (message.attachments?.length) return 'Attachment';
  if (message.associatedMessageType) return 'Reaction';
  return 'Message';
};

module.exports = { normalizeServer, mergeMessages, addressKey, chatTitle, messageText };
