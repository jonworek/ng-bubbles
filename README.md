# Not Green Bubbles — Windows

An independent Electron messaging client for the [BlueBubbles server](https://github.com/BlueBubblesApp/bluebubbles-server). Bring your own Mac and server; this app connects directly to them. It is not affiliated with or endorsed by BlueBubbles or Apple.

[Download the Windows installer or portable build](https://github.com/jonworek/not-green-bubbles-windows/releases/latest).

![Not Green Bubbles with synthetic test conversations and attachments](docs/preview.png)

The UI ships locally and uses a standard Chromium textarea. The project began as a small Windows client intended to work well with voice dictation. Wispr Flow compatibility and Windows-specific integration still need real hardware testing; feedback and pull requests are welcome.

## What works

- Sending and receiving text in individual and group conversations; search and paginated history.
- Native notifications that open, select, and reveal the matching conversation.
- Close-to-tray, optional startup at login, and a red tray dot while conversations are unread.
- Server unread state, delivery/read receipts, and automatic marking as read while viewing a conversation when the server's Private API is enabled. Failed read requests retry automatically.
- Contact photos, server group icons, and member photo/initial circles as a fallback.
- Automatic inline image attachments, including TIFF/HEIC; Chromium-supported audio/video playback. Original files can be saved. Automatic downloads are limited to 16 MB each and a 100 MB local cache.
- Drafts and cached history while offline. Messaging requires a reachable server.
- Settings → Diagnostics or Ctrl+Shift+D for a copyable technical report without passwords, message text, contact names, or addresses.

Attachments cannot yet be uploaded. Reactions, calls, group management, and many upstream features are not implemented. This is an early community project, not a replacement for every feature of the official client.

## Install and connect

Windows 10/11 x64 is the intended platform. Builds are unsigned; Windows may show an unknown-publisher prompt.

1. Quit the old client from its tray menu before updating. Run `Not-Green-Bubbles-0.2.0-x64-Setup.exe`, or put the portable executable in a permanent local folder.
2. Enter your BlueBubbles server's root URL and password. Prefer HTTPS or your trusted private network. Use Standard API unless you want the enabled Private API sending method. Automatic read synchronization uses Private API independently of that sending preference.
3. Open a conversation and type or dictate into the message box. Enter sends; Shift+Enter adds a line.
4. Use Settings → Test notification to check Windows notifications. Closing the window keeps messaging running; Ctrl+Q or the tray's Quit exits.

This project was previously called BlueBubbles Desktop. The internal app ID and `%APPDATA%\BlueBubbles Desktop` data directory are retained to preserve connection settings, history, and drafts. A renamed shortcut or executable can require pinning the tray icon again once; subsequent updates in the same location use a stable tray identity. Windows controls tray visibility preferences.

## Privacy and reliability

The renderer never receives the saved password. Windows DPAPI protects the stored credential. TLS certificates are validated and authenticated requests refuse redirects. No analytics, cloud account, AI service, or third-party relay is used by this client.

Messages, media, and drafts are local plaintext data protected by your Windows account. Histories retain up to 1,000 loaded messages per conversation and approximately 100 conversations. Drafts use local Chromium storage. Diagnostics retain up to 400 technical events. Do not publish your settings, cache, diagnostics, or real message screenshots when reporting problems.

Live updates use Socket.IO with a 20-second REST recovery check. Ambiguous or failed outgoing sends are never retried automatically: the draft is kept so you can check delivery before resending. Photo availability depends on what the Mac server returns; Diagnostics separates server-missing images, decoding failures, and contact-matching failures.

## Develop

Use Node.js 24 (`nvm use`).

```sh
npm ci
npm run lint
npm test
npm run test:desktop
npm start
```

Headless Linux: `xvfb-run -a npm run test:desktop`. Unset `ELECTRON_RUN_AS_NODE` before starting the desktop manually. Tests use synthetic contacts/media and an isolated local server. Only the test launch disables the OS sandbox and mocks the Linux credential provider; shipped builds keep renderer sandboxing enabled.

```sh
npm run dist:win
```

Build on Windows or use a working Wine installation on Linux (`USE_SYSTEM_WINE=true`). The build fetches checksummed corresponding source archives for the LGPL decoder and includes them and all notices in the distribution. The complete list is in [third-party/source-manifest.json](third-party/source-manifest.json). Build output is in `dist/`. A manual GitHub Actions workflow can also build Windows artifacts.

To verify packaged image dependencies on Linux after building, run:

```sh
env -u ELECTRON_RUN_AS_NODE xvfb-run -a node_modules/.bin/electron test/package.cjs --no-sandbox --disable-gpu --user-data-dir=/tmp/ngb-package-check
```

Regenerate the original icons with `python scripts/generate-assets.py` (requires Pillow). The application tests cover API integration, offline recovery, unread/read synchronization, image decoding, attachments, draft preservation, notification activation, tray badge changes, and renderer isolation. Linux acceptance tests do not verify native Windows pin retention, notification delivery, or Wispr Flow.

## License and contributions

Application code and original assets are [MIT licensed](LICENSE). Fork it, improve it, and submit PRs; see [CONTRIBUTING.md](CONTRIBUTING.md).

Dependencies retain their own licenses, including LGPL-3.0 for the HEIC decoder. Read [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), [decoder source/replacement instructions](third-party/README.md), and the [provenance/license review](docs/PROVENANCE.md). Help → Licenses & source opens the bundled notices.

BlueBubbles and iMessage identify compatible upstream services; their names and logos are not licensed as this project's branding. The project does not bundle the BlueBubbles client or server. Server licensing remains the server operator's responsibility.
