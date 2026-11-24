// The offline search theme (@easyops-cn/docusaurus-search-local) ships no translations, so its labels would stay
// English on /docs/it/. Docusaurus lets any plugin supply default code translations per locale; these are the
// search theme's message ids. A locale with no entry here falls back to the English text the theme carries.
// (Translators can still override any of them in docs/user-guide-i18n/<code>/code.json.)
const labels = {
  it: {
    'theme.SearchBar.label': 'Cerca',
    'theme.SearchBar.noResultsText': 'Nessun risultato',
    'theme.SearchBar.seeAll': 'Mostra tutti i risultati',
    'theme.SearchBar.searchInContext': 'Mostra tutti i risultati in "{context}"',
    'theme.SearchBar.seeAllOutsideContext': 'Mostra tutti i risultati fuori da "{context}"',
    'theme.SearchPage.documentsFound.plurals': '1 documento trovato|{count} documenti trovati',
    'theme.SearchPage.emptyResultsTitle': 'Cerca nella documentazione',
    'theme.SearchPage.existingResultsTitle': 'Risultati di ricerca per "{query}"',
    'theme.SearchPage.noResultsText': 'Nessun documento trovato',
    'theme.SearchPage.searchContext.everywhere': 'Ovunque',
  },
};

module.exports = function searchLabelsPlugin(/** @type {import('@docusaurus/types').LoadContext} */ context) {
  return {
    name: 'reelbolt-search-labels',
    async getDefaultCodeTranslationMessages() {
      return labels[context.i18n.currentLocale] || {};
    },
  };
};
module.exports.labels = labels;
