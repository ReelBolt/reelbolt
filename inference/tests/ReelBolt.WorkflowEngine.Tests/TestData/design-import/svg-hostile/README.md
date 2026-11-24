# SVG hostile corpus (synthetic)

Every file in this directory is **synthetic and untrusted data**, written for
`SvgSanitizerTests`. None of it came from a client archive, none of it is artwork,
and nothing here is ever executed, rendered by a browser engine, fetched or
interpreted. The bytes are read by `SvgSanitizer` as an inert XML document only.

Each fixture is named after the hazard it carries:

| File | Hazard |
|---|---|
| `script.svg` | an inline `<script>` next to a drawable path |
| `foreign-object.svg` | `<foreignObject>` carrying XHTML and a script |
| `event-attribute.svg` | `onload`/`onclick`/`onmouseover` event handlers |
| `external-href.svg` | `<use>`/`xlink:href` pointing at a remote document |
| `use-external.svg` | a remote `<use>` beside a legitimate local `#id` reference |
| `style-element.svg` | a `<style>` block with `url(javascript:)` |
| `css-url.svg` | a `style` attribute and a `fill` carrying `url(javascript:)` |
| `entity-expansion.svg` | a DOCTYPE internal subset with recursive entities |
| `doctype.svg` | an external DTD reference |
| `malformed.svg` | truncated, not well-formed XML |
| `deep-nesting.svg` | 200 nested `<g>` elements (depth bound) |
| `data-uri-image.svg` | `<image>` with an `data:` URI |
| `image-element.svg` | `<image>` with a remote URL |
| `smil-set.svg` | `<set>`/`<animate>` SMIL animation elements |
| `processing-instruction.svg` | an `xml-stylesheet` processing instruction |
| `namespace-confusion.svg` | XHTML-namespaced `script` elements |
| `c2pa-metadata.svg` | benign logo carrying C2PA `<metadata>` |
| `plain-logo.svg` | benign logo with a gradient, `<title>` and `<text>` |
| `attributes.svg` | one hostile attribute beside each drawable path |

The expected outcome for each fixture (sanitized or refused) is pinned in
`SvgSanitizerTests.CorpusFiles`; the theory reads this directory so a fixture added
without a case fails, and a case naming a missing file fails.
