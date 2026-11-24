// @ts-check
// Makes a partly translated user guide link correctly.
//
// A translated page lives in docs/user-guide-i18n/<locale>/docusaurus-plugin-content-docs/current/ and an
// untranslated one stays in docs/user-guide/ (Docusaurus falls back to it). Docusaurus resolves a relative
// Markdown link (`./a.md`, `../b.md`) only against the folder of the file that contains it, so a link from a
// translated page to an untranslated one (or the other way round) fails as "couldn't be resolved".
// A link that starts with `/` is looked up in BOTH folders, translated first. This plugin therefore rewrites
// every relative `*.md`/`*.mdx` link of the current docs to that root-relative form
// (`../workflows/agents.md#x` in `account/a.md` becomes `/workflows/agents.md#x`). It runs only for the
// non-default locales, so the English build is untouched, and it leaves a link that escapes the content
// root, an absolute URL and a `/`-link alone (so a real mistake still fails the build).
const path = require('node:path');

const DOC_LINK = /^([^?#]*\.mdx?)([?#].*)?$/;

/** @param {{roots: string[]}} options absolute folders that hold the current docs, translated or English */
function i18nLinks(options) {
  const roots = options.roots.map((root) => path.resolve(root));
  return (/** @type {any} */ tree, /** @type {any} */ file) => {
    const filePath = file && (file.path || (file.history && file.history[0]));
    if (!filePath) return;
    const abs = path.resolve(filePath);
    const root = roots.find((r) => abs === r || abs.startsWith(r + path.sep));
    if (!root) return;
    const dir = path.posix.dirname(path.relative(root, abs).split(path.sep).join('/'));

    /** @param {string} url */
    const rewrite = (url) => {
      if (!url || url.startsWith('/') || url.startsWith('#') || /^[a-z][a-z0-9+.-]*:/i.test(url)) return url;
      const match = DOC_LINK.exec(url);
      if (!match) return url;
      const target = path.posix.normalize(path.posix.join(dir, decodeURIComponent(match[1])));
      if (target.startsWith('..')) return url;
      return `/${target}${match[2] || ''}`;
    };

    /** @param {any} node */
    const walk = (node) => {
      if ((node.type === 'link' || node.type === 'definition') && typeof node.url === 'string') {
        node.url = rewrite(node.url);
      }
      if (Array.isArray(node.children)) node.children.forEach(walk);
    };
    walk(tree);
  };
}

module.exports = i18nLinks;
