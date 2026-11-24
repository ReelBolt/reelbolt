# Built-in Agents Reference

> **New:** every agent has access to a `FailWorkflow(reason)` tool. Calling this helper
> will abort the workflow immediately with a human-readable explanation. Check the
> "Tool reference" section for full details.


This document describes every built-in agent shipped with the ReelBolt Workflow Engine. Agents are organised by pipeline phase. Each entry covers the agent's purpose, the tools it can call, the structured output it produces, and any configuration knobs available.

---

## Table of Contents

- [Architecture overview](#architecture-overview)
- [Pipeline phases](#pipeline-phases)
- [Category: Analysis](#category-analysis)
  - [CodeStructureAnalyzer](#codestructureanalyzer)
  - [DependencyAnalyzer](#dependencyanalyzer)
  - [ComponentInventoryAnalyzer](#componentinventoryanalyzer)
  - [RouteAndApiAnalyzer](#routeandapianalyzer)
  - [StyleAndThemeExtractor](#styleandthemeextractor)
- [Category: Translation](#category-translation)
  - [RemotionComponentTranslator](#remotioncomponenttranslator)
  - [AnimationStrategy](#animationstrategy)
- [Category: Production](#category-production)
  - [Scriptwriter](#scriptwriter)
  - [Director](#director)
  - [Author](#author)
- [On-screen copy, frame size and sound in the promo agents](#on-screen-copy-frame-size-and-sound-in-the-promo-agents)
- [Category: Quality](#category-quality)
  - [Review](#review)
- [Tool reference](#tool-reference)
  - [ProjectFileAgentTools](#projectfileagenttools)
  - [ReactRemotionSandboxTools](#reactremotionsandboxtools)
  - [ProjectDataFormatterTools](#projectdataformattertools)
- [Overriding system prompts](#overriding-system-prompts)

---

## Architecture overview

Each built-in agent extends `ReelBoltAgentBase`, which wraps an `IChatClient` and exposes an `AIAgent` (Microsoft Agents AI). Agents receive a curated list of `AIFunction` tools resolved by `AgentToolProvider.GetTools(AgentType)` at registration time. Structured output types are enforced via `ChatResponseFormat.ForJsonSchema<T>()`.

Tool access follows the **principle of least privilege**: an agent is given only the tools it legitimately needs for its phase. Analysis agents never touch the sandbox. Three agents can trigger a Remotion render: `Author` (the promo video), and `MotionGraphicsPlanner` / `MotionGraphicsDirector` (overlay assets and tracked screen inserts for footage edits — see [video-editing.md](video-editing.md) "Motion graphics (Phase 3)" and "Tracked screen inserts (Phase 5)"). The video-editing decision agents (`VideoStoryEditor`, `Colorist`, `MusicSupervisor`, …) have no sandbox or render tools.

---

## Pipeline phases

```
Analysis ──────────────────────────────────── Translation ── Production ── Quality
    │                                               │               │          │
CodeStructureAnalyzer                RemotionComponentTranslator  Scriptwriter  Review
DependencyAnalyzer                   AnimationStrategy            Director      (loop back
ComponentInventoryAnalyzer                                         Author        if score < 9)
RouteAndApiAnalyzer
StyleAndThemeExtractor
```

The default workflow executes phases sequentially. The Review loop re-runs `Author` until `ReviewOutput.PassesReview == true` or `MaxIterations` is reached.

### Parallel Execution

Any subset of agents can be grouped into a **Parallel step** (`StepType.Parallel`). The `ParallelStepExecutor` runs all configured agents concurrently using `Parallel.ForEachAsync` and merges their individual outputs into a JSON array:

```json
[
  { "agentName": "CodeStructureAnalyzer", "output": "{...}" },
  { "agentName": "DependencyAnalyzer",    "output": "{...}" }
]
```

All five Analysis agents are natural candidates for parallel execution because they are independent and read-only. Grouping them into a single Parallel step reduces total pipeline time from ~5× a single agent call to ~1× (bounded by the slowest agent in the group).

A `ForEach` step placed after a Parallel step can iterate over each `{"agentName","output"}` element, passing each result to a downstream agent for further processing.

---

## Category: Analysis

Analysis agents read source files from the project workspace and format findings into structured JSON. They have **no sandbox access** and cannot write files.

---

### CodeStructureAnalyzer

| | |
|---|---|
| **AgentType** | `CodeStructureAnalyzer` |
| **Class** | `Analysis.CodeStructureAnalyzerAgent` |
| **Output schema** | `CodeStructureOutput` |
| **Config key** | `Agents:CodeStructureAnalyzer:SystemPrompt` |

**Purpose**  
Maps the overall directory and module structure of the source code. Identifies the project type (React SPA, Next.js App, etc.), framework version, major directories, entry points, and high-level architectural pattern.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Enumerate all files in the project workspace |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read a specific file by ID, storage key, or filename |
| `ReadFileTree` | `ProjectDataFormatterTools` | Parse and format a directory/file listing |
| `ReadFileContent` | `ProjectDataFormatterTools` | Extract content from a known file path |

**Output** (`CodeStructureOutput`)

```json
{
  "projectType": "Next.js App",
  "framework": "Next.js 16",
  "directories": [
    { "path": "app/", "purpose": "App Router pages and layouts", "fileCount": 14 }
  ],
  "entryPoints": ["app/layout.tsx", "app/page.tsx"],
  "overallArchitecture": "Feature-based"
}
```

---

### DependencyAnalyzer

| | |
|---|---|
| **AgentType** | `DependencyAnalyzer` |
| **Class** | `Analysis.DependencyAnalyzerAgent` |
| **Output schema** | `DependencyAnalysisOutput` |
| **Config key** | `Agents:DependencyAnalyzer:SystemPrompt` |

**Purpose**  
Focuses exclusively on UI-related dependencies: frameworks, styling libraries, component kits, animation libraries, and icon sets. Ignores backend/infra packages. Produces production recommendations relevant to Remotion rendering (e.g., `"Animation library detected — can be replicated in Remotion"`).

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Enumerate project files |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read package manifests by reference |
| `ReadPackageManifest` | `ProjectDataFormatterTools` | Parse `package.json` / `.csproj` content |
| `ReadFileContent` | `ProjectDataFormatterTools` | Read any supplementary file |

**Output** (`DependencyAnalysisOutput`)

```json
{
  "dependencies": [
    { "name": "@mantine/core", "version": "^8.0.0", "purpose": "component library", "isCore": true }
  ],
  "devDependencies": ["@types/react"],
  "packageManager": "pnpm",
  "recommendations": [
    { "type": "info", "description": "Framer Motion detected — animations can be replicated in Remotion." }
  ]
}
```

---

### ComponentInventoryAnalyzer

| | |
|---|---|
| **AgentType** | `ComponentInventoryAnalyzer` |
| **Class** | `Analysis.ComponentInventoryAnalyzerAgent` |
| **Output schema** | `ComponentInventoryOutput` |
| **Config key** | `Agents:ComponentInventoryAnalyzer:SystemPrompt` |

**Purpose**  
Enumerates every UI component in the codebase with its file path, TypeScript props, single responsibility, and internal dependencies. Works across React (`.tsx`/`.jsx`), Vue (`.vue`), and Angular (`.component.ts`).

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | List all project files |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read individual component files |
| `ReadFileContent` | `ProjectDataFormatterTools` | Parse file content |
| `ListFilesByExtension` | `ProjectDataFormatterTools` | Filter files by extension (`.tsx`, `.vue`, etc.) |

**Output** (`ComponentInventoryOutput`)

```json
{
  "components": [
    {
      "name": "ProductCard",
      "filePath": "components/products/ProductCard.tsx",
      "props": [
        { "name": "title", "type": "string", "required": true, "defaultValue": null }
      ],
      "responsibility": "Displays product thumbnail, name, and CTA button",
      "dependencies": ["PriceTag", "AddToCartButton"]
    }
  ],
  "totalComponents": 42,
  "commonPatterns": ["Container/Presentational", "Custom Hooks"]
}
```

---

### RouteAndApiAnalyzer

| | |
|---|---|
| **AgentType** | `RouteAndApiAnalyzer` |
| **Class** | `Analysis.RouteAndApiAnalyzerAgent` |
| **Output schema** | `RouteAndApiOutput` |
| **Config key** | `Agents:RouteAndApiAnalyzer:SystemPrompt` |

**Purpose**  
Extracts all client-side routes, API endpoints, and the routing strategy. Identifies dynamic parameters, authentication guards, and HTTP methods.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | List all project files |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read route/handler files |
| `ReadFileContent` | `ProjectDataFormatterTools` | Parse file content |
| `SearchPatterns` | `ProjectDataFormatterTools` | Find route definitions via regex-like patterns |

**Output** (`RouteAndApiOutput`)

```json
{
  "clientRoutes": [
    { "path": "/products/[id]", "componentName": "ProductPage", "parameters": ["id"], "requiresAuth": false }
  ],
  "apiEndpoints": [
    { "method": "GET", "path": "/api/products", "purpose": "List all products", "parameters": ["page", "limit"] }
  ],
  "routingStrategy": "File-based (Next.js App Router)"
}
```

---

### StyleAndThemeExtractor

| | |
|---|---|
| **AgentType** | `StyleAndThemeExtractor` |
| **Class** | `Analysis.StyleAndThemeExtractorAgent` |
| **Output schema** | `StyleAndThemeOutput` |
| **Config key** | `Agents:StyleAndThemeExtractor:SystemPrompt` |

**Purpose**  
Extracts the complete design system: color palette, typography, spacing scale, and styling approach. Output is used downstream to maintain brand fidelity in Remotion-generated videos.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | List all project files |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read style configuration files |
| `ReadStyleConfig` | `ProjectDataFormatterTools` | Parse CSS, SCSS, or Tailwind config content |
| `ReadFileContent` | `ProjectDataFormatterTools` | Read any supplementary file |

**Output** (`StyleAndThemeOutput`)

```json
{
  "colors": {
    "primary": "#7950f2",
    "secondary": "#228be6",
    "background": "#1a1b1e",
    "text": "#c1c2c5",
    "additional": { "success": "#40c057", "error": "#fa5252" }
  },
  "typography": {
    "primaryFont": "'Inter', sans-serif",
    "secondaryFont": null,
    "fontSizes": { "sm": "14px", "base": "16px", "lg": "18px" }
  },
  "spacing": { "unit": "rem", "scale": { "xs": "0.25rem", "sm": "0.5rem", "md": "1rem" } },
  "componentStyles": ["Card shadows", "Button variants"],
  "stylingApproach": "Mantine v9"
}
```

---

## Category: Translation

Translation agents bridge the gap between the source analysis and a renderable Remotion project. They have **read/write access to both the project workspace and the sandbox**, but only `RemotionComponentTranslator` can run build checks. `InstallNpmPackages` only reports which catalog packages are already installed; it cannot add one.

---

### RemotionComponentTranslator

| | |
|---|---|
| **AgentType** | `RemotionComponentTranslator` |
| **Class** | `Translation.RemotionComponentTranslatorAgent` |
| **Output schema** | `RemotionComponentOutput` |
| **Config key** | `Agents:RemotionComponentTranslator:SystemPrompt` |

**Purpose**  
Converts the component inventory and style analysis into production-ready Remotion React components. Writes source files to the sandbox, installs any extra npm dependencies, and verifies correctness with `CheckLintAndTypeErrors`.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Read project artefacts |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read analysis outputs stored as project files |
| `WriteProjectFile` | `ProjectFileAgentTools` | Persist generated components as project files |
| `GetSandboxStatus` | `ReactRemotionSandboxTools` | Check if the sandbox is ready before writing |
| `EnsureSandbox` | `ReactRemotionSandboxTools` | Initialise the sandbox for this execution |
| `GetSandbox` | `ReactRemotionSandboxTools` | Retrieve sandbox metadata |
| `ListSandboxFiles` | `ReactRemotionSandboxTools` | Inspect existing sandbox contents |
| `ReadSandboxFile` | `ReactRemotionSandboxTools` | Read an existing sandbox file |
| `WriteSandboxFile` | `ReactRemotionSandboxTools` | Write generated TSX/JSX to the sandbox |
| `DeleteSandboxPath` | `ReactRemotionSandboxTools` | Clean up obsolete files |
| `InstallNpmPackages` | `ReactRemotionSandboxTools` | Report which template-catalog packages are installed (cannot add new ones; no network) |
| `CheckLintAndTypeErrors` | `ReactRemotionSandboxTools` | Validate TypeScript types and ESLint rules |
| `RunSandboxNpmScript` | `ReactRemotionSandboxTools` | Run `build` or `typecheck` scripts |

**Output** (`RemotionComponentOutput`)

```json
{
  "components": [
    {
      "componentName": "HeroBanner",
      "remotionCode": "import { AbsoluteFill } from 'remotion'; ...",
      "durationInFrames": 90,
      "description": "Opening hero with animated headline",
      "defaultProps": { "headline": "Ship Faster" }
    }
  ],
  "remotionVersion": "4.0",
  "requiredImports": ["framer-motion"]
}
```

---

### AnimationStrategy

| | |
|---|---|
| **AgentType** | `AnimationStrategyAgent` |
| **Class** | `Translation.AnimationStrategyAgentImpl` |
| **Output schema** | `AnimationStrategyOutput` |
| **Config key** | `Agents:AnimationStrategy:SystemPrompt` |

**Purpose**  
Designs the full frame-level animation plan: scene sequencing, per-element animation timings, transition types, and overall pacing tone. Reads existing sandbox files created by `RemotionComponentTranslator` but does not modify them.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Read project artefacts |
| `ReadProjectFile` | `ProjectFileAgentTools` | Access component/analysis outputs |
| `GetSandboxStatus` | `ReactRemotionSandboxTools` | Confirm sandbox state |
| `GetSandbox` | `ReactRemotionSandboxTools` | Retrieve sandbox metadata |
| `ListSandboxFiles` | `ReactRemotionSandboxTools` | Browse sandbox file tree |
| `ReadSandboxFile` | `ReactRemotionSandboxTools` | Read component files for timing context |

**Output** (`AnimationStrategyOutput`)

```json
{
  "scenes": [
    {
      "id": "scene_01",
      "componentName": "HeroBanner",
      "startFrame": 0,
      "durationInFrames": 90,
      "transitionType": "fade",
      "transitionDurationInFrames": 15,
      "animations": [
        { "elementId": "headline", "animationType": "slideUp", "startFrame": 0, "durationInFrames": 30, "parameters": {} }
      ]
    }
  ],
  "totalDurationInFrames": 270,
  "fps": 30,
  "pacing": {
    "overallTone": "energetic",
    "averageSceneDuration": 90,
    "pacingNotes": ["Keep intro under 3 s for attention retention"]
  }
}
```

---

## Category: Production

Production agents synthesise all prior analysis and translation work into creative deliverables — scripts, shot plans, and finally the full render manifest — before handing off to the quality gate.

---

### Scriptwriter

| | |
|---|---|
| **AgentType** | `ScriptwriterAgent` |
| **Class** | `Production.ScriptwriterAgentImpl` |
| **Output schema** | `ScriptwriterOutput` |
| **Config key** | `Agents:Scriptwriter:SystemPrompt` |

**Purpose**  
Writes the voiceover and on-screen caption script for every scene. Uses the app's purpose, features, and target audience (read from project files) to produce benefit-focused, naturally timed copy.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Enumerate available project artefacts |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read analysis and component outputs |
| `GetSandboxStatus` | `ReactRemotionSandboxTools` | Check sandbox state |
| `ListSandboxFiles` | `ReactRemotionSandboxTools` | Browse sandbox for context |
| `ReadSandboxFile` | `ReactRemotionSandboxTools` | Read component files for content reference |

**Output** (`ScriptwriterOutput`)

```json
{
  "title": "ReelBolt – Ship Faster",
  "durationInSeconds": 30,
  "narrative": "Hero to CTA arc showcasing the workflow builder and fast render times.",
  "scenes": [
    {
      "sceneId": "scene_01",
      "startTime": 0,
      "duration": 8,
      "voiceover": "Building promotional videos used to take days. Not anymore.",
      "onScreenText": ["ReelBolt", "Generate videos in minutes"],
      "visualDescription": "Animated hero banner with headline fade-in"
    }
  ]
}
```

---

### Director

| | |
|---|---|
| **AgentType** | `DirectorAgent` |
| **Class** | `Production.DirectorAgentImpl` |
| **Output schema** | `DirectorOutput` |
| **Config key** | `Agents:Director:SystemPrompt` |

**Purpose**  
Provides shot-level cinematographic direction: camera angles, movement, visual elements to emphasise, and audio guidance (music style, sound effects, voiceover tone). Ensures the video has a clear narrative arc from opening hook to closing call-to-action.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Enumerate project artefacts |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read script and component data |
| `GetSandboxStatus` | `ReactRemotionSandboxTools` | Check sandbox state |
| `ListSandboxFiles` | `ReactRemotionSandboxTools` | Browse sandbox contents |
| `ReadSandboxFile` | `ReactRemotionSandboxTools` | Read Remotion components for visual reference |

**Output** (`DirectorOutput`)

```json
{
  "shots": [
    {
      "shotId": "shot_01",
      "sceneId": "scene_01",
      "description": "Opening hero — brand logo dissolve-in",
      "startTime": 0,
      "duration": 3,
      "camera": { "angle": "wide", "movement": "static", "focus": "center" },
      "visualElements": ["brand logo", "tagline text"]
    }
  ],
  "visualTheme": {
    "mood": "professional and energetic",
    "colorGrading": "dark with vibrant accent highlights",
    "visualMotifs": ["gradient overlays", "rounded corners"]
  },
  "audio": {
    "musicStyle": "upbeat electronic",
    "soundEffects": "subtle UI click sounds at transitions",
    "voiceover": "calm, authoritative, mid-pace"
  },
  "totalDurationInSeconds": 30
}
```

---

### Author

| | |
|---|---|
| **AgentType** | `AuthorAgent` |
| **Class** | `Production.AuthorAgentImpl` |
| **Output schema** | `RenderManifestOutput` |
| **Config key** | `Agents:Author:SystemPrompt` |

**Purpose**  
The final assembler. Takes all prior outputs and produces a `RenderManifestOutput` JSON, then orchestrates the full Remotion render pipeline: writing the manifest to the sandbox, triggering `RenderVideoAndUploadToStorage`, and finalising the sandbox with `CompleteSandbox`. It is the only agent in the code-to-promo pipeline that triggers a render; in footage-editing workflows `MotionGraphicsPlanner` and `MotionGraphicsDirector` also render Remotion assets (overlays and tracked screen inserts).

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Enumerate all project artefacts |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read all prior-stage outputs |
| `WriteProjectFile` | `ProjectFileAgentTools` | Persist the render manifest as a project file |
| `GetSandboxStatus` | `ReactRemotionSandboxTools` | Verify sandbox readiness |
| `EnsureSandbox` | `ReactRemotionSandboxTools` | Initialise sandbox if not already created |
| `GetSandbox` | `ReactRemotionSandboxTools` | Read sandbox metadata |
| `ListSandboxFiles` | `ReactRemotionSandboxTools` | Inspect sandbox contents |
| `ReadSandboxFile` | `ReactRemotionSandboxTools` | Read existing sandbox files |
| `WriteSandboxFile` | `ReactRemotionSandboxTools` | Write the assembled manifest and entry point |
| `DeleteSandboxPath` | `ReactRemotionSandboxTools` | Clean up obsolete sandbox files |
| `InstallNpmPackages` | `ReactRemotionSandboxTools` | Check which catalog packages are installed (nothing can be added) |
| `CheckLintAndTypeErrors` | `ReactRemotionSandboxTools` | Validate code before render |
| `RunSandboxNpmScript` | `ReactRemotionSandboxTools` | Run `build` / `typecheck` scripts |
| `RunSandboxRemotionCommand` | `ReactRemotionSandboxTools` | Execute Remotion CLI (`render`, `still`, `compositions`) |
| `RenderVideoAndUploadToStorage` | `ReactRemotionSandboxTools` | Render composition and upload result to S3 (stored under `projects/{projectId}/outputFiles/`) |
| `CompleteSandbox` | `ReactRemotionSandboxTools` | Finalise and tear down the sandbox |

**Output** (`RenderManifestOutput`)

```json
{
  "projectName": "ReelBolt Promo v1",
  "video": { "width": 1920, "height": 1080, "fps": 30, "durationInFrames": 900 },
  "compositions": [
    {
      "id": "scene_01",
      "componentName": "HeroBanner",
      "startFrame": 0,
      "durationInFrames": 90,
      "props": { "headline": "Ship Faster" },
      "script": {
        "voiceover": "Building promotional videos used to take days. Not anymore.",
        "captions": ["ReelBolt", "Generate videos in minutes"]
      }
    }
  ],
  "assets": [],
  "metadata": {}
}
```

---

## On-screen copy, frame size and sound in the promo agents

The promo pipeline (`RemotionComponentTranslator` → `AnimationStrategy` → `Scriptwriter` →
`Director` → `Author`) carries four hard rules, added after a customer's 15 s vertical reel shipped
with literal markdown asterisks on screen ("Build the edit as a *flowchart.*"), unreadably small
product UI, an invented persona ("Welcome back, Alex") and no audio track at all. Every rule lives in
**both** prompt copies — the agent class's `DefaultPrompt` and `DatabaseSeeder.BuiltInAgents` — held
byte-identical by `VideoStoryEditorPromptConsistencyTests` (which now pins `Scriptwriter` and
`RemotionComponentTranslator` too).

- **Frame size and length come from the brief.** Each prompt reads the `User Request` section every
  agent step's input ends with: "reel", "short", "story", "TikTok", "vertical" or "9:16" means
  1080x1920; "square" or "1:1" means 1080x1080; anything else (or no brief) means 1920x1080, and a
  stated length is the total duration. The Scriptwriter opens `Narrative` with the chosen format,
  the Director repeats it at the start of `VisualTheme.Mood`, the Translator registers its
  compositions at that size and the Author renders the master at it. There is no deterministic
  frame-size choice on this path: the composition size is whatever the agents register in
  `src/root.tsx` (the sandbox template's own `LaunchPromo` is 1920x1080 and is only a starting
  point).
- **The composition's own code decides the split; the author decides what each scene means.**
  `RenderVideoAndUploadToStorage` takes `fps` + `scenes`, and it also reads the `<Sequence>` list out of
  `src/root.tsx` in the same sandbox. The two are reconciled into one scene manifest written beside the
  render (`{videoKey}.scenes.json`): the composition's frames are the scene boundaries — they are what
  the renderer actually used — and the declaration supplies each scene's name, description and
  `onScreenText`. A declared scene whose `fromFrame` matches no `<Sequence>` is dropped and reported, a
  `<Sequence>` no declared scene describes is kept and marked undescribed, and the tool result carries
  those findings as `sceneWarnings` so the author can fix the components or the declaration and re-render.
  A composition whose sequences cannot be read (computed timings, nested `<Sequence>`s, `<Series>`, more
  than one registered composition) is not guessed at: the declaration stands alone and the render says so.
  See [`docs/video-editing.md`](video-editing.md) "Author-declared scenes".
- **Plain text on screen, removed in code too.** The prompts forbid markdown in copy, and
  `OnScreenCopySanitizer` (`ReelBolt.Shared/Workflows`) strips it deterministically:
  `ReelBoltAgentBase.PostProcessOutput` is a virtual hook applied to every agent's final answer
  before the step result is persisted or read downstream, and `ScriptwriterAgentImpl` /
  `DirectorAgentImpl` override it to strip `*x*`, `**x**`, `_x_`, backticks and leading `#` from
  their copy fields (`CopyFields`), leaving ids and numbers alone and returning the output
  byte-identical when nothing changed. A component can still be typed with markdown by hand, so
  `CompositionSceneManifest.FromDeclaration` rejects a render declaration whose `onScreenText`
  carries any, naming the clean text, before the render starts.
- **Legible and true.** Body copy at least about 48 px on a 1080-wide frame; on a vertical frame the
  UI is zoomed to the region a scene is about rather than shrunk whole. No invented people, names,
  testimonials, ratings, prices or metrics; a greeting stays neutral ("Welcome back").
- **Sound.** The only audio the Remotion path can play is the narration `StageVoiceoverAudio`
  stages from a `Voiceover` step (there is no tool that stages a project audio file, and no music
  library). The Author must therefore write one plain sentence into `metadata.audio` — either
  which scenes are narrated, or "No audio: this workflow has no narration step, so the video is
  silent…" (only when the tool's `reason` is `no_voiceover_step`; narration that exists but was not
  staged gets "No audio: the narration could not be added to this video.") — so a mute promo is
  never shipped without saying so. Narration not spoken from the script's scenes (an `Inline` or
  plan-sourced Voiceover) is staged per line as `voiceover/{lineId}.wav` with a `lines` array of
  text, start and measured length (see `docs/voiceover.md` "Narration that is not scene-sourced").
  The `cinematic-feature-spotlight`
  ("Quick vertical reel") and `story-arc-launch-trailer` ("Narrated launch trailer") templates
  carry a `ScriptScenes` `Voiceover` step right after the Scriptwriter; the starter and
  `lean-context-promo` stay silent so a deployment with no speech provider can still run them.

---

## Category: Quality

### Review

| | |
|---|---|
| **AgentType** | `ReviewAgent` |
| **Class** | `Quality.ReviewAgentImpl` |
| **Output schema** | `ReviewOutput` |
| **Config key** | `Agents:Review:SystemPrompt` |

**Purpose**  
Quality gate for the full production pipeline. Scores the `RenderManifest`, script, and component outputs from 1–10 across four criteria: narrative clarity, visual accuracy, timing, and completeness. Sets `passesReview = true` only when `overallScore >= 9` with no critical issues. When `passesReview` is `false`, the `ReviewLoopStepExecutor` sends the pipeline back to `Author` for revision.

**Tools**

| Tool | Source | Description |
|------|--------|-------------|
| `ListProjectFiles` | `ProjectFileAgentTools` | Enumerate all project artefacts |
| `ReadProjectFile` | `ProjectFileAgentTools` | Read the render manifest and prior outputs |
| `GetSandboxStatus` | `ReactRemotionSandboxTools` | Check sandbox state |
| `GetSandbox` | `ReactRemotionSandboxTools` | Retrieve sandbox metadata |
| `ListSandboxFiles` | `ReactRemotionSandboxTools` | Browse sandbox output files |
| `ReadSandboxFile` | `ReactRemotionSandboxTools` | Inspect rendered artefacts |
| `CheckLintAndTypeErrors` | `ReactRemotionSandboxTools` | Verify technical correctness of source code |

**Output** (`ReviewOutput`)

```json
{
  "overallScore": 9,
  "criteria": [
    { "name": "narrativeClarity", "score": 9, "feedback": "Hook is strong, CTA lands clearly." },
    { "name": "visualAccuracy",   "score": 9, "feedback": "Brand colors and typography match source." },
    { "name": "timing",           "score": 8, "feedback": "Scene 3 could be trimmed by ~10 frames." },
    { "name": "completeness",     "score": 10, "feedback": "All compositions present and renderable." }
  ],
  "strengths": ["Consistent brand voice", "Smooth fade transitions"],
  "improvementAreas": ["Scene 3 pacing slightly slow"],
  "passesReview": true,
  "summary": "Production-ready. Minor timing note for future iteration."
}
```

---

## Tool reference

### ProjectFileAgentTools

Scoped to the current execution's project. All methods resolve the project ID from the active `WorkflowExecutionContext`.

| Method | Description |
|--------|-------------|
| `ListProjectFiles()` | Returns JSON array of `ProjectWorkspaceFile` metadata for all files in the project |
| `ReadProjectFile(fileReference)` | Reads file content by ID, storage key, or original filename |
| `WriteProjectFile(fileName, content, contentType?)` | Creates or replaces a text file in the project workspace |

### ReactRemotionSandboxTools

### WorkflowAbortTool

Provides a single helper that agents can call to abort the entire workflow when
further progress is impossible. The tool throws a special exception that bypasses
retry logic and is surfaced to the user as the execution error message.

| Method | Allowed agents | Description |
|--------|---------------|-------------|
| `FailWorkflow(reason)` | **all** | Abort workflow immediately with a human‑readable `reason`. Should be
used only for unrecoverable conditions such as missing required inputs or
inconsistent state; transient issues should allow normal failure + retry.


Scoped to the current execution's sandbox (identified by `executionId`). All write/exec operations are validated server-side.

| Method | Allowed agents | Description |
|--------|---------------|-------------|
| `GetSandboxStatus()` | All translation/production/review | Check sandbox existence and readiness |
| `EnsureSandbox()` | Translator, Author | Create or reuse the execution sandbox |
| `GetSandbox()` | Translator, AnimationStrategy, Author, Review | Retrieve sandbox metadata |
| `ListSandboxFiles(path?)` | Translator, AnimationStrategy, Director, Scriptwriter, Author, Review | List files/directories inside sandbox |
| `ReadSandboxFile(path)` | Translator, AnimationStrategy, Director, Scriptwriter, Author, Review | Read a UTF-8 text file |
| `WriteSandboxFile(path, content)` | Translator, Author | Write UTF-8 text to sandbox |
| `DeleteSandboxPath(path)` | Translator, Author | Delete a file or directory |
| `InstallNpmPackages(packages[])` | Translator, Author | Reports installed catalog packages; names validated server-side, nothing is added |
| `CheckLintAndTypeErrors()` | Translator, Author, Review | Run TypeScript type-check + ESLint |
| `RunSandboxNpmScript(script, args?, timeout?)` | Translator, Author | Run `build`, `render`, `typecheck`, `compositions`, or `lint` |
| `RunSandboxRemotionCommand(command, args?, timeout?)` | Author | Run `render`, `still`, or `compositions` via Remotion CLI |
| `RenderVideoAndUploadToStorage(compositionId, outputFileName, remotionArgs?)` | Author | Full render pipeline: render → upload to S3 → attach storage key to step result |
| `CompleteSandbox()` | Author | Finalise and delete the sandbox |

### ProjectDataFormatterTools

Static helpers that format in-memory data passed to the LLM context. They do not perform I/O themselves.

| Method | Description |
|--------|-------------|
| `ReadFileTree(fileListingData)` | Returns the file listing as-is for LLM consumption |
| `ReadFileContent(filePath, projectData)` | Returns `filePath` + `projectData` for targeted file reads |
| `ListFilesByExtension(extension, fileListingData)` | Filters a listing to files matching `extension` |
| `ReadPackageManifest(manifestContent)` | Passes manifest content directly to the LLM |
| `SearchPatterns(pattern, sourceContent)` | Returns `pattern` + `sourceContent` for pattern-based searches |
| `ReadStyleConfig(styleContent)` | Passes style config content directly to the LLM |

---

## Overriding system prompts

Every built-in agent reads its system prompt from `appsettings.json` before using the hardcoded default:

```json
{
  "Agents": {
    "CodeStructureAnalyzer": {
      "SystemPrompt": "Your custom prompt here."
    },
    "RemotionComponentTranslator": {
      "SystemPrompt": "..."
    }
  }
}
```

The config key is always `Agents:<AgentName>:SystemPrompt` where `<AgentName>` matches the string passed to `ReelBoltAgentBase` constructor (`"CodeStructureAnalyzer"`, `"DependencyAnalyzer"`, `"ComponentInventoryAnalyzer"`, `"RouteAndApiAnalyzer"`, `"StyleAndThemeExtractor"`, `"RemotionComponentTranslator"`, `"AnimationStrategy"`, `"Scriptwriter"`, `"Director"`, `"Author"`, `"Review"`).

Override via Docker Compose environment variables using the standard ASP.NET double-underscore convention:

```yaml
environment:
  Agents__Author__SystemPrompt: "Your custom Author prompt."
```
