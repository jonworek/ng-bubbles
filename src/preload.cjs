const { contextBridge, ipcRenderer } = require('electron');

/**
 * @summary Register a specific desktop event without exposing Electron IPC.
 * @param {string} name - Allowed event name.
 * @param {Function} callback - Listener receiving a safe payload.
 * @returns {Function} Listener cleanup.
 */
const subscribe = (name, callback) => {
  const listener = (_event, value) => callback(value);
  ipcRenderer.on(name, listener);
  return () => ipcRenderer.removeListener(name, listener);
};

contextBridge.exposeInMainWorld('desktop', {
  diagnostics: () => ipcRenderer.invoke('diagnostics'),
  attachmentPreview: (guid) => ipcRenderer.invoke('attachment-preview', guid),
  onDiagnostics: (callback) => subscribe('diagnostics-open', callback),
  initial: () => ipcRenderer.invoke('initial'),
  configure: (settings) => ipcRenderer.invoke('configure', settings),
  open: (guid, before) => ipcRenderer.invoke('open', guid, before),
  send: (input) => ipcRenderer.invoke('send', input),
  createChat: (input) => ipcRenderer.invoke('create-chat', input),
  sync: () => ipcRenderer.invoke('sync'),
  read: (guid) => ipcRenderer.invoke('read', guid),
  external: (url) => ipcRenderer.invoke('external', url),
  attachment: (guid, name) => ipcRenderer.invoke('attachment', guid, name),
  testNotification: () => ipcRenderer.invoke('test-notification'),
  onState: (callback) => subscribe('state', callback),
  onOpenChat: (callback) => subscribe('open-chat', callback),
});
