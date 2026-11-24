// @ts-check
// ReelBolt documentation site. It publishes ONLY the user guide (../docs/user-guide), at /docs/.
// The developer docs (../docs/*.md) stay in the repository and are read by the platform assistant's
// documentation search; they are deliberately not part of this build, not even through a link.
// `npm run build` runs scripts/assert-user-guide-only.mjs afterwards to keep it that way.
// Served under /docs/ by the root nginx — see docs/docs-site.md.
const fs = require('node:fs');
const path = require('node:path');
const { themes: prismThemes } = require('prism-react-renderer');

const registry = require('./locales.json');
const { docsStrings } = require('./i18n-strings');

const SITE_URL = process.env.DOCS_SITE_URL || 'http://localhost';
// `docusaurus build` evaluates this file once per locale with DOCUSAURUS_CURRENT_LOCALE set; the
// per-locale strings (title, navbar, footer, alpha notice) come from i18n-strings.js.
const locale = process.env.DOCUSAURUS_CURRENT_LOCALE || registry.defaultLocale;
const t = docsStrings(locale);
const I18N_PATH = '../docs/user-guide-i18n';
const BASE_URL = '/docs/';

// Every folder that holds docs pages for `locale`: the working copy and each cut version, English and translated
// (the remark plugin in src/remark/i18n-links.js makes links between them resolve). Missing folders are harmless.
function i18nDocRoots(/** @type {string} */ code) {
  const versions = fs.existsSync(path.join(__dirname, 'versions.json'))
    ? JSON.parse(fs.readFileSync(path.join(__dirname, 'versions.json'), 'utf8'))
    : [];
  const translated = path.resolve(__dirname, I18N_PATH, code, 'docusaurus-plugin-content-docs');
  return [
    path.resolve(__dirname, '../docs/user-guide'),
    path.join(translated, 'current'),
    ...versions.flatMap((/** @type {string} */ version) => [
      path.join(__dirname, 'versioned_docs', `version-${version}`),
      path.join(translated, `version-${version}`),
    ]),
  ];
}

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: t.title,
  tagline: t.tagline,
  favicon: 'img/logo.svg',
  url: SITE_URL,
  baseUrl: BASE_URL,
  trailingSlash: false,
  onBrokenLinks: 'throw',
  onBrokenAnchors: 'warn',
  markdown: {
    // Plain .md is parsed as CommonMark, not MDX, so the existing docs' literal `{`/`<` never
    // trip the MDX compiler.
    format: 'detect',
    hooks: { onBrokenMarkdownLinks: 'throw' },
  },
  // Languages are data: docs-site/locales.json (vendored from i18n/locales.json by i18n/sync.mjs). English
  // is served at /docs/, every other locale at /docs/<code>/. Translated pages live OUTSIDE docs-site, in
  // docs/user-guide-i18n/<code>/docusaurus-plugin-content-docs/current/ (same relative path as docs/user-guide),
  // so the docs writers' surface reaches them; a page without a translation falls back to the English one.
  i18n: {
    path: I18N_PATH,
    defaultLocale: registry.defaultLocale,
    locales: registry.locales.map((locale) => locale.code),
    localeConfigs: Object.fromEntries(
      registry.locales.map((locale) => [
        locale.code,
        {
          label: locale.label,
          htmlLang: locale.htmlLang,
          direction: locale.dir,
          // Explicit, because scripts/build-all-locales.mjs builds one locale per process with `--locale`, and
          // Docusaurus then skips the automatic /<code>/ suffix. English /docs/, Italian /docs/it/.
          baseUrl: locale.code === registry.defaultLocale ? BASE_URL : `${BASE_URL}${locale.code}/`,
        },
      ]),
    ),
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          path: '../docs/user-guide',
          routeBasePath: '/',
          sidebarPath: require.resolve('./sidebars.js'),
          // In a translated build a relative `.md` link must reach a page whichever folder holds it (translated
          // or English fallback): src/remark/i18n-links.js rewrites them. English is built as it always was.
          beforeDefaultRemarkPlugins:
            locale === registry.defaultLocale
              ? []
              : [
                  [
                    require('./src/remark/i18n-links'),
                    { roots: i18nDocRoots(locale) },
                  ],
                ],
          // No `editUrl`: the repository is private, so an "Edit this page" link would lead customers nowhere.
        },
        blog: false,
        theme: { customCss: require.resolve('./src/css/custom.css') },
      }),
    ],
  ],

  // Offline search: the index is built at build time (one per locale) and queried in the browser, with no
  // external service. Only the user guide is indexed (the only docs route); the blog is off.
  themes: [
    require.resolve('./src/plugins/search-labels'),
    [
      require.resolve('@easyops-cn/docusaurus-search-local'),
      /** @type {import('@easyops-cn/docusaurus-search-local').PluginOptions} */
      ({
        hashed: true,
        language: ['en', 'it'],
        indexDocs: true,
        indexBlog: false,
        indexPages: false,
        docsRouteBasePath: '/',
        explicitSearchResultPath: true,
        searchResultLimits: 8,
      }),
    ],
  ],

  // The user guide was reorganised into folders; these keep every previously published URL working.
  // Paths are relative to baseUrl, so `from: '/x'` redirects /docs/x to /docs/<to>.
  plugins: [
    [
      '@docusaurus/plugin-client-redirects',
      {
        redirects: [
          { from: '/github-repository', to: '/projects/github-repository' },
          { from: '/cloud-drive-sync', to: '/projects/cloud-drive-sync' },
          { from: '/local-folder-sync', to: '/projects/local-folder-sync' },
          { from: '/claude-code', to: '/assistant/claude-code' },
          { from: '/account/workspaces-and-teams', to: '/workspaces' },
        ],
      },
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      colorMode: { respectPrefersColorScheme: true },
      // Built-in announcement bar (Docusaurus is not swizzled). Colours are existing Infima tokens:
      // the primary-contrast surface and its matching foreground, which is the pair Infima ships for
      // text on a primary-tinted background. The primary ramp alone tops out at 3.30:1 in light mode
      // (darkest on lightest), below WCAG AA's 4.5:1; this pair measures 13.70:1 in both modes.
      announcementBar: {
        id: 'alpha-notice',
        content: t.announcement,
        backgroundColor: 'var(--ifm-color-primary-contrast-background)',
        textColor: 'var(--ifm-color-primary-contrast-foreground)',
        isCloseable: true,
      },
      navbar: {
        title: t.navbarTitle,
        logo: { alt: 'ReelBolt', src: 'img/logo.svg' },
        items: [
          { type: 'docSidebar', sidebarId: 'guide', position: 'left', label: t.userGuide },
          { to: '/releases', label: t.releaseNotes, position: 'left' },
          { type: 'search', position: 'right' },
          { type: 'docsVersionDropdown', position: 'right' },
          { type: 'localeDropdown', position: 'right' },
          // /app is on this origin but outside this site's baseUrl (/docs/). `pathname://` marks it
          // non-internal (a plain <a>, skipped by the broken-link check), and `autoAddBaseUrl:
          // false` stops the baseUrl append that produced /docs/app/ in the built page.
          {
            href: 'pathname:///app/',
            label: t.openReelBolt,
            position: 'right',
            target: '_self',
            autoAddBaseUrl: false,
          },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: t.footerDocs,
            items: [{ label: t.userGuide, to: '/' }],
          },
          {
            title: t.footerProduct,
            items: [
              {
                label: t.dashboard,
                href: 'pathname:///app/',
                target: '_self',
                prependBaseUrlToHref: false,
                autoAddBaseUrl: false,
              },
            ],
          },
        ],
        copyright: t.copyright(new Date().getFullYear()),
      },
      prism: {
        theme: prismThemes.github,
        darkTheme: prismThemes.dracula,
        additionalLanguages: ['csharp', 'json', 'bash', 'go'],
      },
    }),
};

module.exports = config;
