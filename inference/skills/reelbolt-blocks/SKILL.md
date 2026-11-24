---
name: reelbolt-blocks
description: Ready-made, tested Remotion blocks in nine categories (transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes). Before building one from scratch, call ListBlocks to find it and AddBlock to copy it into src/blocks/.
---

# ReelBolt block library

A block is a small, tested Remotion component (a transition, a kinetic title, a caption style, a chart, a map, an effect, a background, a device frame or a shape) that ReelBolt ships and verifies. Copying one is faster and safer than writing the same thing from scratch, and it renders the same way every time.

Categories: transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes.

## Workflow

1. Before you build a transition, title, caption, chart, map, background, device frame or shape from scratch, call `ListBlocks` (optionally with `category` and/or `query` words). The reply is one compact line per block.
2. `GetBlock(id)` returns the props signature, a usage example, the npm packages the block needs and the files it will write.
3. `AddBlock(id)` copies the block into `src/blocks/<id>/` (and shared helpers into `src/blocks/_shared/`) and records it in `src/blocks/blocks.lock.json`.
4. Import it from your composition, for example `import {KineticTitle} from './blocks/kinetic-title/KineticTitle.tsx'`, and pass the props from the signature. Wire it into a `<Sequence>` like any other component.

## Rules

- `AddBlock` only writes under `src/blocks/`. It never touches your own files.
- If a needed package is not installed, `AddBlock` refuses and tells you which one; install it with `InstallNpmPackages`, then call `AddBlock` again.
- If you edit a copied block, `AddBlock` will refuse to overwrite it and names the file. Edit blocks in place freely; do not re-add them.
- Calling `AddBlock` twice for the same block is harmless: identical files are left untouched.
- Blocks are a starting point. Apply the film's own palette, fonts and timing through their props; do not hard-code colours that the design system already defines.
- If no block fits, build the effect yourself as usual.

The current catalog summary is in `CATALOG.md` next to this file (generated); `ListBlocks` is always the live list.
