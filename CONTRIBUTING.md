# Contributing

NG Bubbles means Not Green Bubbles. Forks, issues and pull requests are welcome.
Application contributions use MIT; third-party components retain their own terms.

Use .NET 10 for native development. Build WinUI on Windows. Node.js 24 is used only
for the synthetic protocol fixtures and legacy Electron source.

Run `dotnet test native/NgBubbles.Tests` after state or protocol changes. Tests cover
real HTTP/Socket.IO interoperability, read synchronization, reconnect recovery,
draft persistence and safe sending. Run the Native Windows workflow for interface,
packaging or shell changes. Keep network requests, image work and persistence off
the UI thread. Use native controls for text entry and accessible list navigation.

For JavaScript fixture/legacy changes, run ESLint on edited files and the relevant
Node tests. The Electron acceptance test applies only to the legacy interface.

Never include real credentials, messages, contact photos or private server URLs in
source, screenshots or issues. Use the synthetic fixtures under `test/`. Inspect a
copied Diagnostics report before posting it.

Review licenses for every new dependency. The native build collects package notices
and metadata with `scripts/collect-native-licenses.ps1`. Changes to the legacy HEIC
decoder must preserve the corresponding source/replacement requirements described
in `third-party/README.md`.

Describe checks on real Windows for notifications, dictation, pinning and startup.
A successful CI build is not a substitute for those device-specific checks.
