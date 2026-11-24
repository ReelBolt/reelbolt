# ReelBolt block library

First-party, editable Remotion blocks that agents copy into a project with the `AddBlock` tool
(design: `docs/design/sandbox-customization.md` section 2.5). MIT licensed (see `LICENSE`); no
third-party code is vendored here. The map blocks embed Natural Earth 1:110m country data (public domain, see `src/_shared/worldData.ts`).

## Layout

- `catalog.json`: generated. `{schemaVersion, blocks:[{id, version, title, category, description, tags[],
  requires[{name,version}], files[{path,sha256}], previewComposition, props, example}]}`.
- `src/<id>/`: the block's TSX and its `block.json` (metadata, not copied to projects).
- `src/_shared/`: helpers several blocks import; copied to `src/blocks/_shared/`.
- `previews/<id>.tsx`: the composition the pipeline renders; not copied to projects.
- `scripts/`: the pipeline.

A file path in the catalog is `<id>/<file>` or `_shared/<file>`, and lands at `src/blocks/<path>` in the project.
The engine ships this directory at `/app/blocks` (`Blocks:CorpusPath`) and refuses any block whose file hash differs from
`catalog.json`.

## Adding a block

1. Create `src/<id>/` with the TSX files and a `block.json` (id equals the directory name; category is one of
   transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes; `requires` may only name packages in
   `sandbox/template/package.json` or `package-catalog.json`). Use `.tsx`/`.ts` import extensions, as the template does.
2. Add `previews/<id>.tsx` exporting `Preview`.
3. `node scripts/build-catalog.mjs` (writes `catalog.json` and `inference/skills/reelbolt-blocks/CATALOG.md`;
   `--check` fails when stale).
4. `node scripts/verify.mjs` (needs Docker and the sandbox runtime image, default `reelbolt-sandbox-runtime:local`): offline
   catalog checks, then inside the image with production flags (read-only root, 256m tmpfs, 2g, 2 cpus, no network, user node)
   typecheck, still render, non-blank check and the layout lint. `--block <id>` verifies one block.
