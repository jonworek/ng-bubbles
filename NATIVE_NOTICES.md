# NG Bubbles 0.3.1 — native Windows distribution

Application source and original icons: Copyright Jon Worek, MIT (see LICENSE).
Independent interoperability client for BlueBubbles servers. Not affiliated with
BlueBubbles or Apple. No upstream BlueBubbles client/server code or logo is bundled.

Runtime dependencies, distributed under their respective licenses:

- .NET 10 and System.Security.Cryptography.ProtectedData: Microsoft and .NET
  contributors, MIT. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
  Third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- Windows App SDK / WinUI 3: Microsoft, MIT, with third-party notices.
  https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE
  https://github.com/microsoft/microsoft-ui-xaml/blob/main/LICENSE
- C#/WinRT: Microsoft, MIT. https://github.com/microsoft/CsWinRT/blob/master/LICENSE
- Windows SDK/build components are used and redistributed only as allowed by their
  Microsoft SDK/NuGet package terms. Package license files and runtime notices are
  included in the release's licenses directory by the build script.

Images are decoded through Windows Graphics Imaging and installed Windows codecs.
HEIF/HEIC may require Microsoft's HEIF Image Extensions and applicable HEVC codec.
No libheif, libde265, Chromium, Electron, or npm runtime is included in this native
build. The historical THIRD_PARTY_NOTICES.md describes the retained Electron source
and older releases, not the runtime composition of 0.3.1.

Build tools and test libraries are not application runtime dependencies. Their
licenses remain with their packages. Protocol endpoint/event names are used for
interoperability; this project is not described as a clean-room implementation.
