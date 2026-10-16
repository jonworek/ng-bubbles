const js = require('@eslint/js');
const globals = require('globals');

module.exports = [
  { ignores: ['dist/**', 'node_modules/**'] },
  js.configs.recommended,
  { files: ['**/*.cjs'], languageOptions: { globals: globals.node } },
  { files: ['src/renderer.js'], languageOptions: { globals: globals.browser } },
  { files: ['test/desktop.cjs'], languageOptions: { globals: globals.browser } },
];
