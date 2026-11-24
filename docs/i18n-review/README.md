# Italian review (Owner window 13-15 Nov)

Read in this order:

1. [`it-flagged.md`](it-flagged.md): only the strings the translators flagged as uncertain (legal wording, money, consent, destructive confirmations, glossary gaps), the e-mail templates and the user-guide flags. This is the list that needs a decision.
2. [`docs-it-flags/`](docs-it-flags): per-batch notes from the user-guide translation.
3. [`it.md`](it.md): every Italian string next to its English source (about 10,000 rows), grouped by area, for spot checks.

The glossary and style decisions are in [`../i18n-glossary.md`](../i18n-glossary.md). Regenerate the two tables with `node scripts/i18n-review-export.mjs` (or `npm run i18n:review` in `web/`); never edit them by hand.
