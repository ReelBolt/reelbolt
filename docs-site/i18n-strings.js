// @ts-check
// Strings that docusaurus.config.js sets itself (site title, navbar and footer labels, the alpha notice,
// the copyright line). Everything else the theme prints ("Next", "Search", "On this page", ...) comes from
// ../docs/user-guide-i18n/<locale>/docusaurus-theme-classic/*.json. English is the source and the
// fallback: a locale that has no entry here, or misses a key, gets the English string.
// Italian follows docs/i18n-glossary.md (informal "tu", natural wording, product words unchanged).
// To add a language: add its code to locales.json and one block below (see docs/docs-site.md "Languages").

/** @typedef {{title: string, tagline: string, navbarTitle: string, userGuide: string, releaseNotes: string,
 *   openReelBolt: string, announcement: string, footerDocs: string, footerProduct: string, dashboard: string,
 *   copyright: (year: number) => string}} DocsStrings */

/** @type {Record<string, DocsStrings>} */
const strings = {
  en: {
    title: 'ReelBolt Docs',
    tagline: 'Turn code and footage into promo videos with agent workflows',
    navbarTitle: 'ReelBolt Docs',
    userGuide: 'User guide',
    releaseNotes: 'Release notes',
    openReelBolt: 'Open ReelBolt',
    announcement: 'ReelBolt 0.4.0-alpha — public alpha. Expect rough edges; things may change.',
    footerDocs: 'Docs',
    footerProduct: 'Product',
    dashboard: 'Dashboard',
    copyright: (year) => `© ${year} ReelBolt 0.4.0-alpha<br />ReelBolt™ is a trademark of Tommaso Celano.`,
  },
  it: {
    title: 'Documentazione di ReelBolt',
    tagline: 'Trasforma codice e filmati in video promozionali con flussi di lavoro di agenti',
    navbarTitle: 'Documentazione di ReelBolt',
    userGuide: 'Guida utente',
    releaseNotes: 'Note di rilascio',
    openReelBolt: 'Apri ReelBolt',
    announcement: 'ReelBolt 0.4.0-alpha — alpha pubblica. Aspettati qualche spigolo; le cose possono cambiare.',
    footerDocs: 'Documentazione',
    footerProduct: 'Prodotto',
    dashboard: 'Dashboard',
    copyright: (year) => `© ${year} ReelBolt 0.4.0-alpha<br />ReelBolt™ è un marchio di Tommaso Celano.`,
  },
};

/** @param {string | undefined} locale @returns {DocsStrings} */
function docsStrings(locale) {
  return { ...strings.en, ...(strings[locale || 'en'] || {}) };
}

module.exports = { docsStrings, strings };
