# Contributing

Forks, bug reports, and pull requests are welcome. Contributions to the application are accepted under its MIT license; third-party code must retain its applicable notices and terms.

Use Node.js 24, `npm ci`, and the commands in README.md. Keep changes focused and add a regression test for behavior changes. Run ESLint on changed JavaScript, targeted tests, and the Electron acceptance test for UI changes. Prefer standard HTML controls so keyboard access, IME, clipboard tools, and voice dictation keep working.

Never include real credentials, message history, contact photos, or private server URLs in issues, fixtures, or screenshots. Use the mock server and synthetic media in `test/`. If reporting a problem, include the app/server versions and reproduction steps; inspect any copied Diagnostics report before posting it.

Run `npm run licenses` after dependency changes. Review every new runtime dependency's license. If a compiled decoder changes, update the immutable source manifest, corresponding source archives, license texts, and rebuild/replacement instructions before distributing binaries. The prebuild step fails on unreviewed licenses or mismatched source checksums.

API behavior can be confirmed against upstream documentation/source, but do not copy upstream implementation or branding without checking its license and retaining required attribution. Mention any third-party source or assets in your PR.

Windows-specific changes should describe checks on real Windows when available. CI/Linux tests cannot establish that notifications, pinning, startup-at-login, or dictation work on every Windows installation.
