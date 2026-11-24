---
title: Make a promo video from your app's code
sidebar_position: 2
description: Recipe for turning a web app's source code into an animated promotional video with ReelBolt's promo templates.
---

# Make a promo video from your app's code

**Goal:** upload a web app's source code and get an animated promotional video about it, with
scenes based on the app's real screens, colours and features.

ReelBolt reads the code with five analysis agents, turns the app's interface into **Remotion**
scenes (Remotion is the code-based animation tool ReelBolt uses to build and render videos), plans
animation, narration and shots, and then the `AuthorAgent` **renders the final video itself** inside
an `Agent` step. There is no separate render step. A review step scores the result and asks the
`AuthorAgent` to try again if it falls short.

## Prerequisites for a promo from code

- **The app's source code uploaded to the project.** On the Files tab you can upload a whole folder.
  Include the interface code (components, pages, styles); you can leave out build output,
  dependencies folders (such as `node_modules`) and large binaries.
- **A chat AI provider.** The promo pipeline makes many AI calls; a capable model gives better
  results.
- **The Remotion sandbox service running** (part of the standard installation). It is where the
  agents write and render the Remotion code, sealed off from everything else.

## Choosing a promo template

| Template key | Display name | Narrated? | Best for |
|---|---|---|---|
| `quick-win-promo` | Promo video from your code | No (silent) | A first promo. Already added to every new project. |
| `lean-context-promo` | Promo video from your code (lighter) | No (silent) | The same, with fewer AI tokens; good for large apps. |
| `cinematic-feature-spotlight` | Quick vertical reel | Yes | A short, punchy social reel with a strong call to action. |
| `story-arc-launch-trailer` | Narrated launch trailer | Yes | A story-shaped launch trailer. |

All four ask for your brief when you run it. The brief decides the **format and length**: a reel,
short, story, TikTok or vertical request makes a 1080x1920 (9:16) video, "square" makes 1080x1080,
anything else is landscape 1920x1080, and a stated length ("15 s") is the video's length. The two
narrated templates need a speech provider.

All four follow the same pattern. Promo video from your code:

| # | Label | Step type | Agent | Renders? |
|---|---|---|---|---|
| 1 | Analyze code structure | `Agent` | `CodeStructureAnalyzer` | No |
| 2 | Analyze UI dependencies | `Agent` | `DependencyAnalyzer` | No |
| 3 | Inventory components | `Agent` | `ComponentInventoryAnalyzer` | No |
| 4 | Map routes and API | `Agent` | `RouteAndApiAnalyzer` | No |
| 5 | Extract style tokens | `Agent` | `StyleAndThemeExtractor` | No |
| 6 | Translate to Remotion scenes | `Agent` | `RemotionComponentTranslator` | No (writes code only) |
| 7 | Create animation strategy | `Agent` | `AnimationStrategyAgent` | No |
| 8 | Draft narration and captions | `Agent` | `ScriptwriterAgent` | No |
| 9 | Build shot direction | `Agent` | `DirectorAgent` | No |
| 10 | Assemble and render master composition | `Agent` | `AuthorAgent` | **Yes** |
| 11 | Review quality and compliance | `ReviewLoop` (pass 8, back to 10, 2 rounds) | `ReviewAgent` | No |

The step lists of the other three templates are in [Workflow templates](../workflows/templates.md).

## How to run a promo from code

1. Make sure the code is uploaded.
2. Open the workflow (Workflows tab). For Promo video from your code, it already exists; for the
   others, add the template from **New workflow**.
3. Run it and give your brief when asked, for example: "Promote the new team dashboard to
   engineering managers. Upbeat, 30 to 45 seconds, end with 'Start your free trial'." or "15 s
   vertical reel about the timeline editor." (The brief box always appears when you click **Run**; **Require a brief before each run** only
   makes it mandatory.)
4. Follow the run. Steps 6 and 10 take the longest, because the agents write, check and render code.

## What to check after a promo run

- **Step 10 (`AuthorAgent`)** shows the finished video in its **Output Video** player; it also
  appears in the project's **Renders** tab. If this step has no video, it fails with "Author step did
  not produce a rendered media artifact", and ReelBolt retries it.
- **The review step** shows the score and the list of improvements. A score under the passing mark
  sends the run back to step 10 with the comments; after the last round the run finishes with the
  latest video.
- **Step 8 (`ScriptwriterAgent`)** contains the narration and captions per scene. Its on-screen
  text is plain: any markdown such as `*word*` is removed automatically, because the video would
  draw the asterisks. Spoken narration needs a `Voiceover` step (below); the Author's output says
  in `metadata.audio` whether the video has narration or is silent.
- **Nothing invented:** names, quotes, ratings and numbers on screen come only from your code,
  files or brief. If you see a made-up name or figure, say so in the brief and run it again.

## Variations of a promo from code

- **Spoken narration:** add a `Voiceover` step after the scriptwriter with
  `{"source": {"kind": "ScriptScenes", "scriptwriterStepOrder": 8}, "defaultVoiceId": "..."}`; the
  `AuthorAgent` places each line in its scene. Needs a Fish Audio speech provider. See
  [Voiceover recipe](voiceover.md).
- **Faster, cheaper analysis:** combine the five analysis agents into one `Parallel` step so they
  run at the same time.
- **Fewer tokens:** use Promo video from your code (lighter), which trims the component list with an `Extract` step and
  gives each later agent only the earlier results it needs.
- **Stricter quality bar:** raise the review step's `minScore` (for example to 9) and
  `maxIterations` (for example to 3). Each extra round re-renders the video.

## Pitfalls with a promo from code

- **No or little code uploaded:** the analysis agents have nothing to describe and the promo will be
  generic. Upload the interface source.
- **Very large repositories:** slower and more expensive. Leave out generated files and dependency
  folders, or use Promo video from your code (lighter).
- **A step times out:** agents that write code can hit their time limit on slow AI backends. A
  timed-out step is not retried automatically; an operator can raise that agent's time limit. Then
  use **Retry**: earlier read-only analysis steps may be reused from the cache.
- **Re-runs repeat the analysis quickly:** analysis agents that only read are reused from an earlier
  identical run; translation, scripting, directing, authoring and review always run fresh.
- **Asking for the wrong agent to render:** only the `AuthorAgent` renders the promo. The
  `RemotionComponentTranslator` builds and checks the scene code but cannot render.

## Recipe: Promo video from your code

Template: **Promo video from your code**. It is added to every new project.

**Set before you run it.** Language, length (15, 30 or 60 seconds are offered), shape, style and a
note on who the promo is for and what it should show. The suggested style is a fast social reel.
There is no narration setting: this recipe is silent. To add a voice, see [Add a voiceover](voiceover.md).
Setting meanings are in [Run settings](run-settings.md).

**What it costs.** It uses credits, because several AI agents read your code and build the video.
No voice and no AI-generated shots are included. The price shows on the run page before you start.

**What you get.** One promo video of your app's real screens, with on-screen text and captions
written for you.

**Common problems.**

- The promo is generic: little or no code was uploaded. Upload the interface source and run again.
- The run stops at a step that writes code: use **Retry**; earlier read-only steps are reused.
- The video is the wrong shape or length: set **Shape** and **Length** on the run page instead of
  asking for them in the notes.

## Recipe: Promo video from your code (lighter)

Template: **Promo video from your code (lighter)**. Same promo, but each later step reads less of
your code's analysis.

**Set before you run it.** The same settings as the standard promo: language, length, shape, style
and notes. It is silent.

**What it costs.** It uses credits, fewer than the standard promo on a large app. On a small app
the difference is small. The run page shows the price.

**What you get.** One promo video of your app's real screens.

**Common problems.**

- The video leaves out a feature you care about: with less context per step, name that feature in
  the notes, or run the standard promo.
- Very large archive: leave out generated files and dependency folders before you upload.
