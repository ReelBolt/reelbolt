# Design-import test data (synthetic)

This directory holds the synthetic inputs the design-system import tests read. Everything here is
**untrusted data written for a test**: none of it came from a client archive, none of it is artwork,
and nothing here is executed, rendered, fetched or interpreted.

## No client archive is in the repository

No real design export — Northwind Staffing or anyone else's — is stored in this directory or anywhere else in
the repository. The live proof runs against the client zip outside the tree, unpacked read-only into
a scratch directory. The fixtures below exist so the parsers and the sanitizer can be tested against
the same hazards without shipping a client's files.

## `svg-hostile/`

Nineteen synthetic SVG documents for `SvgSanitizerTests`, each named after the hazard it carries:
`script.svg`, `foreign-object.svg`, `event-attribute.svg`, `external-href.svg`, `use-external.svg`,
`style-element.svg`, `css-url.svg`, `entity-expansion.svg`, `doctype.svg`, `malformed.svg`,
`deep-nesting.svg`, `data-uri-image.svg`, `image-element.svg`, `smil-set.svg`,
`processing-instruction.svg`, `namespace-confusion.svg`, `c2pa-metadata.svg` (benign),
`plain-logo.svg` (benign) and `attributes.svg`. `svg-hostile/README.md` describes what each one
contains.

`SvgSanitizerTests.CorpusFiles` pins the expected outcome per file — sanitized or refused — and the
theory reads this directory, so a fixture added without a case fails, and a case naming a missing
file fails. Every successful sanitize is also asserted to contain none of `script`,
`foreignObject`, `javascript:`, `onload=`, `data:`, an entity declaration, an XML processing
instruction, an `xlink:` prefix or an `<image>` element.

The fixture in `TestData/claude-design/` is a separate synthetic export (manifest, token
stylesheets, component declarations and prompts, PNG assets) used by `ClaudeDesignImporterTests` and
`ClaudeDesignBundleTests`; it is not a real export either.

## Adding a fixture

- Write the smallest document that shows the one hazard. Do not copy a client file, a screenshot or
  a logo.
- Name the file after the hazard, not the vendor.
- Add the case (and its expected outcome) to `SvgSanitizerTests.CorpusFiles` in the same change.
- Keep the bytes deterministic: no timestamps, no random ids, no real URLs that resolve.
