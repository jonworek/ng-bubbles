# Decoder sources and modification rights

Not Green Bubbles uses unmodified `heic-decode` (ISC) and `libheif-js` 1.23.5
(LGPL-3.0), containing libheif and libde265. These third-party works retain
their own licenses; the application license does not relicense them.

Every Windows distribution includes `resources/third-party/sources/` with
complete source archives for libheif-js, its Emscripten build recipe, the exact
libheif revision, and libde265 1.0.15. `source-manifest.json` records immutable
revisions, URLs, and SHA-256 hashes. GPL-3.0 and LGPL-3.0 texts accompany them.
The same archives are available beside the executables in the GitHub release.
Run `npm run licenses` to fetch and verify these archives before a build.

## Rebuild or replace the decoder

1. Extract libheif-js and libheif-emscripten. Extract the libheif source archive
   into the build recipe's `libheif/` subdirectory (the archive of the parent
   repository contains only a submodule reference). Place the supplied
   `libde265-1.0.15.tar.gz` inside that libheif directory to avoid downloading it.
2. Use the upstream recipe's `.github/workflows/emscripten.yml`, included in
   its archive. It specifies Emscripten 3.1.61 and the exact build flags.
   `libheif/build-emscripten.sh` defaults to libde265 1.0.15; the optional AOM,
   OpenJPEG, and WebCodecs backends are disabled in this distribution.
   Keep the included copyright/license notices when modifying any sources.
3. Apply your modifications, follow the upstream JS/WASM build and
   `dist-prep.sh` steps, and create `libheif.tar.gz` with the `libheif/` and
   `libheif-wasm/` outputs as shown in that workflow.
4. In the extracted libheif-js source, run `npm install`, then
   `node scripts/install.js /absolute/path/to/your/libheif.tar.gz`. This is the
   upstream packaging script; it transforms and bundles the decoder outputs.
5. Clone Not Green Bubbles, run `npm ci`, then add an npm override:
   `npm pkg set 'overrides.libheif-js=file:../modified-libheif-js'` and run
   `npm install`. Adjust the path to your modified package. Run `npm test`
   and `npm run dist:win` (Windows, or Linux with Wine) to produce your modified
   application. When redistributing a modified decoder, update the source
   manifest and notices to include its matching sources rather than the
   unmodified upstream archives.

The application is provided in source form under MIT expressly permitting
modification and recombination. There is no restriction on reverse engineering
for debugging modifications to LGPL components, and no publisher key or
activation is required to build and run modified versions. The npm modules are
loaded at runtime as separate modules, not copied into the application's own
source files. Rebuilding the app is the supported way to replace packaged
modules; rebuilding does not require cooperation from the original publisher.

Electron's own license is included as `LICENSE.electron.txt` and Chromium's
third-party notices as `LICENSES.chromium.html` in the installed application.
