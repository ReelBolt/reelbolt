# Skill sources

This directory holds the agent-skill corpus ReelBolt loads at runtime (see
`inference/src/ReelBolt.WorkflowEngine/Services/Skills/SkillCorpusService.cs`). Nothing is fetched
from the network at runtime. It has two parts:

1. [Remotion skills](#remotion-skills-first-party): five `remotion-*` directories, **written for
   ReelBolt and part of this repository**. They have no upstream.
2. [Taste Skill](#leonxlnxtaste-skill-design-taste): three design-taste directories, vendored from
   an open-source (MIT) repository at a pinned commit.

## Remotion skills (first party)

- **Directories:** `remotion-create/`, `remotion-markup/`, `remotion-render/`, `remotion-captions/`,
  `remotion-multimedia/`, matching the `SkillCategory.Remotion` entries of `SkillCatalog.All` in
  `inference/src/ReelBolt.Shared/Skills/SkillCatalog.cs`.
- **Authorship and licence:** written for this project and covered by this repository's licence.
  They are short working guides to Remotion's public API (compositions, timing, sequencing, audio,
  captions, media probing, render flags), not copies of Remotion's documentation or of any other
  skill collection. Each `SKILL.md` carries `name`, `description` and `version` frontmatter, where
  `version` is the Remotion release the guide was checked against (4.0.535, the version pinned in
  `sandbox/template/package.json`).
- **No provenance link:** `SkillCatalog.RemotionSkillsAreVendored` is `false` here, so these skills
  report no source URL or commit.
- **Remotion itself** is not included. It is source-available under its own licence, which anyone
  building or running ReelBolt must follow; see `THIRD_PARTY_NOTICES.md` and the README.
- **Updating:** edit the files directly. When the Remotion pin in `sandbox/template/package.json`
  changes, re-check the render flags in `remotion-render/` and the package names in
  `remotion-create/` and `remotion-markup/` against the new version, then bump `version` in each
  `SKILL.md`.

## Leonxlnx/taste-skill (design taste)

- **Source repository:** https://github.com/Leonxlnx/taste-skill (site: https://www.tasteskill.dev/)
- **Licence:** MIT, Copyright (c) 2026 Leonxlnx. The licence text is vendored verbatim as
  `LICENSE` inside each of the three directories below, so the notice travels with every copy.
- **Pinned commit SHA:** `717446e07a78d4e6d7918b1fde1a263378eebac9` (tip of `main`, 2026-10-09)
- **Pinned on:** 2026-10-10
- **Fetched via:** `git clone https://github.com/Leonxlnx/taste-skill`, then a byte-for-byte copy of
  `skills/{dir}/SKILL.md` and the repository-root `LICENSE`. SHA-256 of the vendored SKILL.md files
  (identical to the upstream blobs at the pinned commit):
  - `taste-skill/SKILL.md` `aa194351b246b8b4799099d4ed7b033d29eab6e6e3d58d8d2172978be7b3ec89`
  - `minimalist-skill/SKILL.md` `36bc7328f085405f43b476938e62460fa573bf7e984e949e072bcf014831a44c`
  - `brutalist-skill/SKILL.md` `fffbaac8597f07679e9d87145533567658aedba3b5eca6f16a7075a333048caa`

### Directories vendored

Matching `SkillCatalog.All` (`SkillCategory.DesignTaste`):

- `taste-skill/` — the core skill, **v2**. Its upstream frontmatter calls it `design-taste-frontend`;
  ReelBolt's catalog name is the directory name.
- `minimalist-skill/` — upstream frontmatter name `minimalist-ui`.
- `brutalist-skill/` — upstream frontmatter name `industrial-brutalist-ui` (marked Beta upstream).

Each `SKILL.md` and `LICENSE` is unmodified.

### ReelBolt-authored files (not upstream)

Each of the three directories also contains `REELBOLT-PREFACE.md`. These skills are written for web
pages and ReelBolt agents write Remotion video, so a verbatim copy would mislead an agent (a hero,
a navbar, `next/font`, Tailwind, GSAP, "ask one question", Picsum placeholders). Rather than edit
the upstream text, the preface says which sections transfer to video and which to skip, and puts
the step's own instructions and the customer's brand ahead of the skill. `SkillCorpusService`
(what `UseSkill` returns) and `SkillsController` (the admin view) join it in front of the upstream
body through `SkillPreface.Compose`, with a divider stating that the upstream text follows
unmodified. A preface is ours: it carries no upstream commit and is not covered by the pin. When
the pin is bumped, re-check each preface's section references against the new text.

### Deliberately excluded (the other ten skills)

- **`taste-skill-v1`** — the superseded v1 of the core skill, kept upstream only for projects that
  depend on its exact behaviour; v2 replaces it.
- **`gpt-tasteskill`** — a GSAP/ScrollTrigger scrolltelling recipe (AIDA page structure, pinned and
  stacked sections, gapless bento grids). Entirely scroll-driven web layout.
- **`output-skill`** (`full-output-enforcement`) — a general anti-truncation rule. ReelBolt agents
  write files through tools, and its "send continue to resume" protocol assumes a human who
  replies mid-run, which a workflow step does not have.
- **`soft-skill`** (`high-end-visual-design`) — component recipes for web pages: double-bezel
  cards, button-in-button, floating glass navbar, hamburger morph.
- **`redesign-skill`** — an audit-and-upgrade protocol for an existing website's code.
- **`image-to-code-skill`**, **`imagegen-frontend-web`**, **`imagegen-frontend-mobile`**,
  **`brandkit`** — image-generation workflows (one image per page section, phone mockups, brand
  boards) that need an image-generation tool, and for the first, a website to build.
- **`stitch-skill`** (including its `DESIGN.md`) — rules for Google Stitch's design-system format.
- Everything else in the repository: `README.md`, `CHANGELOG.md`, `research/`, `examples/`,
  `assets/`, `scripts/`, `.claude-plugin/`, `skill.sh`.

### Refreshing this corpus

Pick a new commit on the taste-skill repository, copy the three `SKILL.md` files and the `LICENSE`
byte for byte, then rebuild the `workflow-engine` image and check
`GET /api/v1/workflow-engine/skills/status` reports all eight skills loaded. After copying, read each `REELBOLT-PREFACE.md` against them and fix any section number or claim that no
longer holds, then update `TasteCommit` in `SkillCatalog.cs` and the pin and hashes here.
