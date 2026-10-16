const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');

const root = path.join(__dirname, '..');

/**
 * @summary Collect runtime license notices and verify bundled corresponding source archives.
 * @returns {Promise<void>} Notices and LGPL source archives ready for distribution.
 */
const prepare = async () => {
  const lock = JSON.parse(fs.readFileSync(path.join(root, 'package-lock.json')));
  const notices = ['# Third-party notices', '',
    'Not Green Bubbles application code is MIT licensed. Dependencies retain their own licenses.',
    'HEIC support uses libheif-js 1.23.5, libheif, and libde265 under LGPL-3.0.',
    'Complete corresponding source, build recipes, license texts, and replacement instructions accompany the application in resources/third-party and resources/licenses.',
    'See third-party/README.md for your modification/recombination rights and build instructions.', '',
    'Electron 44.7.2: MIT (LICENSE.electron.txt). Chromium and bundled runtime components: see LICENSES.chromium.html in the installation directory.', '',
    '## Runtime npm dependencies', ''];
  for (const [folder, entry] of Object.entries(lock.packages)) {
    if (!folder || entry.dev) continue;
    const directory = path.join(root, folder);
    if (!fs.existsSync(path.join(directory, 'package.json'))) continue;
    const pkg = JSON.parse(fs.readFileSync(path.join(directory, 'package.json')));
    if (pkg.name === 'libheif-js' && pkg.version !== '1.23.5') throw new Error('Update the corresponding decoder source manifest before changing libheif-js.');
    const license = pkg.license || pkg.licenses?.map((item) => item.type).join(' OR ');
    if (!['MIT', 'ISC', '(MIT AND Zlib)', 'LGPL-3.0'].includes(license)) throw new Error(`Review license for ${pkg.name}: ${license}`);
    const repository = typeof pkg.repository === 'string' ? pkg.repository : pkg.repository?.url || '';
    notices.push(`### ${pkg.name} ${pkg.version}`, '', `License: ${license}`, `Source: ${repository}`, '');
    const files = fs.readdirSync(directory).filter((name) => /^(licen[cs]e|copying|notice)(\.|$)/i.test(name));
    if (!files.length && pkg.name !== 'heic-decode') throw new Error(`Missing license text for ${pkg.name}`);
    for (const filename of files) {
      notices.push(`Upstream ${filename}:`, '', '```text', fs.readFileSync(path.join(directory, filename), 'utf8').trim(), '```', '');
    }
    if (pkg.name === 'pako') notices.push('Zlib-derived source notice:', '', '```text', fs.readFileSync(path.join(directory, 'lib/zlib/inflate.js'), 'utf8').split('\n').slice(2, 20).join('\n'), '```', '');
    if (pkg.name === 'heic-decode') notices.push('The upstream package declares ISC in package.json and has no separate license file. Author: Kiril Vatev. The canonical ISC license text is included in licenses/ISC.txt.', '');
  }
  notices.push('## Native HEIC decoder components', '',
    'Generated Emscripten runtime: Emscripten 3.1.61, MIT and third-party runtime terms; see licenses/Emscripten-LICENSE.txt and https://github.com/emscripten-core/emscripten/tree/3.1.61.', '',
    'libheif: copyright Dirk Farin and other contributors; libde265: copyright struktur AG, Dirk Farin and contributors. Each retains its upstream notices and LGPL-3.0 terms. Their complete COPYING texts are in licenses and their source archives.', '',
    'The corresponding libheif source is revision 413e2a87e6a70b3eccc3a3adc5801179dd2d9e00; libde265 is version 1.0.15. The exact upstream build recipe and libheif-js packaging source are also provided. Optional AOM, OpenJPEG, and WebCodecs backends are disabled by that recipe.', '',
    'This distribution does not restrict modifying or reverse engineering the LGPL components for debugging modifications. The MIT application source permits rebuilding and recombination. See third-party/README.md.', '');
  fs.writeFileSync(path.join(root, 'THIRD_PARTY_NOTICES.md'), notices.join('\n'));
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'third-party/source-manifest.json')));
  const directory = path.join(root, 'third-party/sources');
  fs.mkdirSync(directory, { recursive: true });
  for (const source of manifest) {
    const filename = path.join(directory, source.file);
    let bytes = fs.existsSync(filename) ? fs.readFileSync(filename) : null;
    if (!bytes || createHash('sha256').update(bytes).digest('hex') !== source.sha256) {
      const response = await fetch(source.url, { signal: AbortSignal.timeout(120000) });
      if (!response.ok) throw new Error(`Cannot fetch corresponding source for ${source.name}: ${response.status}`);
      bytes = Buffer.from(await response.arrayBuffer());
      if (createHash('sha256').update(bytes).digest('hex') !== source.sha256) throw new Error(`Source checksum mismatch for ${source.name}`);
      fs.writeFileSync(filename, bytes);
    }
  }
  console.log(`Prepared runtime notices and ${manifest.length} verified LGPL source archives.`);
};

prepare().catch((error) => { console.error(error.message); process.exitCode = 1; });
