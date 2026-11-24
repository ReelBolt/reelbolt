// The ONE definition of the `i18next/no-literal-string` options used by eslint.config.mjs and by the ratchet test.
// Area PRs must not change it (use a line-level eslint-disable with a reason for a genuine non-copy argument).

// Attributes that carry text a user reads. Anything else (className, data-*, href, ...) is not checked.
export const TEXT_ATTRIBUTES = ['label', 'placeholder', 'title', 'description', 'message', 'aria-label', 'alt', 'tooltip'];

// A literal handed to a translator or formatter is a catalogue key or a format name, not copy.
// RegExp OBJECTS on purpose: the plugin turns string entries into `^(?:.*\.)?<entry>$`, which let `t[A-Za-z0-9]{0,6}`
// excuse toast(), tabs(), top(), trim() and ui.title(). A RegExp object is used as written (anchored here).
//  - translators bound from useT()/getT(): t, tr, tx, t1, tNow, tc ... optionally with .rich/.raw/.has/.markup/.msg
//  - the useFormat() object `f` (f.date(d, 'short'))
//  - a few non-copy builtins
export const CALLEES_EXCLUDE = [
  /^(?:t|tr|tx|t[A-Z0-9][A-Za-z0-9]{0,5})(?:\.(?:rich|raw|has|markup|msg))?$/,
  /^f\.[a-z][A-Za-z]*$/,
  /^form\.getInputProps$/,
  /^(?:require|Symbol|Error|msg|getInputProps|i18n|i18next)$/,
];

export const WORDS_EXCLUDE = ['[0-9!-/:-@[-`{-~]+', '[A-Z_-]+', '[·—–…•→←×±]+', '\\s+'];

export const literalStringOptions = {
  mode: 'jsx-only',
  'jsx-attributes': { include: TEXT_ATTRIBUTES },
  callees: { exclude: CALLEES_EXCLUDE },
  words: { exclude: WORDS_EXCLUDE },
};
