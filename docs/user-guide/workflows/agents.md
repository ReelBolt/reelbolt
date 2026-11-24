---
title: Agents
sidebar_position: 3
description: Every built-in ReelBolt AI agent, what it does, which steps use it, what it is allowed to do, and whether it can render Remotion video.
---

# Agents

An **agent** is an AI worker with one job, a fixed set of instructions and a fixed set of
**tools** (things it is allowed to do besides writing text). You put an agent into a workflow with
an `Agent` step (or a `ReviewLoop` or `ForEach` step); the room steps use several agents together.

In this page, tools are described in plain words:

- **Read project files** — look at, search and read the files uploaded to the project.
- **Write project files** — save a new file into the project.
- **Write code** — work in a private, sealed-off workspace (the "sandbox"): create and edit
  Remotion code, install packages, and check that the code builds.
- **Render Remotion** — turn Remotion code into a real video file and save it.
- **Stop the run** — end the whole workflow with an explanation when it cannot continue.

**Can render Remotion** means the agent can produce a video file from Remotion code itself. Only
three agents can: `AuthorAgent`, `MotionGraphicsPlanner` and `MotionGraphicsDirector`. No other
built-in agent and no custom agent can. There is no separate "render" step type:
rendering always happens inside one of these agents' steps.

## How the Agents page shows each agent

The **Agents** page lists every agent by what it does for your video — for example "Reads how
your app is built" with the line "Learns your app's screens and how they fit together." — grouped
under headings such as **Understanding your app** and **Editing your footage**. The same plain
job names appear on workflow steps and on the run page while a step works. A custom agent keeps
the name you gave it.

The internal details — the agent's internal name (such as `CodeStructureAnalyzer`, the name this
guide uses below), its type, its tools and the shape of what it returns — are under **Details**
on each card, and under **Technical details** on an agent's own page. You do not need them to
build or run a workflow.

## Which agents can render Remotion

The complete list of agents that can turn Remotion code into a video:

| Agent | What it renders | Where its render goes |
|---|---|---|
| `AuthorAgent` (shown as "Author") | The whole promo video | Becomes the run's finished promo (the step fails if it does not render) |
| `MotionGraphicsPlanner` | Small animated graphics (titles, lower-thirds, callouts, transparent background) **and** the scenes shown inside tracked screen inserts (solid, normal video) | Composited into your footage by the `VideoCompile` step |
| `MotionGraphicsDirector` | The same as `MotionGraphicsPlanner`, from a `GraphicsRoom` step's final call | Composited by the `VideoCompile` step |

`RemotionComponentTranslator` writes Remotion code and checks that it builds, but **cannot render**.
Every other agent, and every custom agent, can neither write code nor render.

## Code analysis agents

These five agents read a web app's source code and describe it for the promo pipeline. All five:
**read project files** and **stop the run** only; **can render Remotion: no**. They are used in the
four promo templates (`quick-win-promo`, `lean-context-promo`, `cinematic-feature-spotlight`,
`story-arc-launch-trailer`), not all of them in every template.

| Agent | Job | Answer shape |
|---|---|---|
| `CodeStructureAnalyzer` | Maps folders, modules and entry points. | `CodeStructureOutput` |
| `DependencyAnalyzer` | Lists frameworks and libraries. | `DependencyAnalysisOutput` |
| `ComponentInventoryAnalyzer` | Lists UI components, their inputs and purpose. | `ComponentInventoryOutput` |
| `RouteAndApiAnalyzer` | Lists pages, navigation and API endpoints. | `RouteAndApiOutput` |
| `StyleAndThemeExtractor` | Extracts colours, fonts, spacing and branding. | `StyleAndThemeOutput` |

Because they only read, an identical later run may reuse their results instead of asking the AI
again.

## Promo production agents

These agents turn the code analysis into a rendered promo video. They are used in all four promo
templates.

| Agent (display name) | Job | Answer shape | Tools | Can render Remotion |
|---|---|---|---|---|
| `RemotionComponentTranslator` | Builds the Remotion project: writes scene code into the sandbox and checks it builds. | `RemotionProjectBuildOutput` | Read and write project files, write code (no render) | **No** |
| `AnimationStrategyAgent` ("AnimationStrategy") | Plans scene order, timing and transitions. | `AnimationStrategyOutput` | Read project files, look inside the sandbox | No |
| `ScriptwriterAgent` ("Scriptwriter") | Writes the narration and captions for each scene. | `ScriptwriterOutput` | Read project files, look inside the sandbox | No |
| `DirectorAgent` ("Director") | Writes the shot-by-shot direction. | `DirectorOutput` | Read project files, look inside the sandbox | No |
| `AuthorAgent` ("Author") | Assembles everything, renders the final promo and saves it. If a `Voiceover` step ran, it also places the narration audio into the scenes; if a `VideoGenerate` step ran, it can use the generated clips as background plates. | `RenderManifestOutput` | Read and write project files, write code, render | **Yes** |
| `ReviewAgent` ("Review") | Scores the promo out of 10 and lists improvements; used by `ReviewLoop` steps. | `ReviewOutput` | Read project files, look inside the sandbox, check code | No |

An `AuthorAgent` step **fails** if it finishes without having rendered a video.

## Video editing decision agents

These agents work on your filmed footage. They choose from the short ids a `VideoAnalyze` step
offered (shots `s0`, pauses `g0`, speech `t0`, ...) and never write a time, a position or a
number of their own. They all **read project files** and **stop the run** only; **can render
Remotion: no**.

`VideoStoryEditor` and `SoundDesigner` have one more tool: they can **ask what a moment shows**.
When the analysis step describes moments (`describeEvents`) and a moment has no description yet,
the agent can ask the vision model about that one moment and gets a few words back. Each
question uses a few credits, so an agent may ask at most 6 times in a step and 20 times in a
run. Its answer is read as a description of your video, never as an instruction.

| Agent | Job | Answer shape | Used by |
|---|---|---|---|
| `VideoStoryEditor` | Decides which shots, pauses and spoken phrases to keep. | `VideoEditDecisionOutput` | Step 2 of every footage template except `video-derush-edit-room`; fallback for `EditRoom` |
| `VideoEditDirector` | Moderates an `EditRoom` panel and writes its final keep decision. | `VideoEditDecisionOutput` | `EditRoom` steps |
| `MusicSupervisor` | Picks at most one uploaded music track (`m0`, ...) plus how loud (`Quiet`, `Balanced`, `Feature`), how much it dips under speech (`Off`, `Light`, `Normal`, `Heavy`) and whether it loops (`LoopToFit`, `PlayOnce`). | `MusicPlanOutput` | `video-derush-edit-music` |
| `SoundDesigner` | Plans sound effects: which uploaded clip (`x0`, ...) plays when which shot, pause or phrase begins, with timing (`OnCut`, `Lead`, `Lag`) and volume (`Subtle`, `Normal`, `Strong`). No effects is a valid answer. | `SfxPlanOutput` | `video-derush-edit-sfx` |
| `Colorist` | Picks one colour look for the whole video as words: look (`None`, `Warm`, `Cool`, `Filmic`, `Vibrant`, `Muted`, `Mono`), strength, shadow and highlight words. | `ColorGradePlanOutput` | Fallback for `ColorGradeRoom`; usable as its own step |
| `ColorGradeDirector` | Moderates a `ColorGradeRoom` panel and writes its final look. | `ColorGradePlanOutput` | `ColorGradeRoom` steps |
| `ShotDirector` | Plans AI-generated clips: purpose (`Cutaway`, `ColdOpen`, `EndCard`, `SeamBridge`, `Extend`, `ScreenContent`), where they go, camera and length words, and a description prompt. Cannot spend money itself. | `GeneratedShotPlanOutput` | `video-derush-edit-broll`, `video-derush-edit-dailies` |
| `TakeSelector` | A judge in a `DailiesRoom` panel: compares the AI-generated takes of each planned shot using the measured facts about them. Also the fallback when the panel fails. | `TakeSelectionOutput` | `DailiesRoom` steps |
| `DailiesDirector` | Moderates a `DailiesRoom` panel and writes its final choice: at most one take per shot, by take id. | `TakeSelectionOutput` | `DailiesRoom` steps |
| `DynamicsPlanner` | Plans the beat-aligned moves of the Dynamic edit after generation: it sees the source shot ids and the usable generated clip ids (`v1t1`, ...), so it can alternate a shot with a generated clip on the beat. Words and offered ids only, never a time. Zero moves is a valid answer. | `DynamicsPlanOutput` | `video-derush-edit-broll`, `video-derush-edit-dailies` |
| `NarrationWriter` | Writes narration lines for the edit, each tied to a shot, pause or spoken phrase (`s0`, `g0`, `t0`, ...), never to a time. Zero lines is a valid answer. | `NarrationPlanOutput` | `video-derush-edit-voiceover`, `video-derush-edit-dub` |
| `NarrationTranslator` | Rewrites a narration plan in the language named in the run's request: same lines, same anchors, translated words. | `NarrationPlanOutput` | `video-derush-edit-dub` |
| `PickupPlanner` | Names the spoken phrases (`t0`, ...) the presenter flubbed and the corrected sentence for each. Zero pickups is a valid answer. | `PickupPlanOutput` | `video-derush-edit-pickups` |
| `VideoReviewAgent` (shown as "VideoReview") | Scores a compiled edit using checks ReelBolt measured (sentences not cut mid-way, smooth joins, pacing, graphics coverage, music headroom, and how loud and how clearly the narration was heard). | `VideoReviewOutput` | The `ReviewLoop` step of every footage template |

## Motion graphics agents (these render)

The two motion-graphics agents add designed graphics and screen inserts to filmed footage. Both
have **read project files, write code, render Remotion and stop the run** (no "write project
files"). **Can render Remotion: yes.**

**`MotionGraphicsPlanner`** (answer shape `MotionGraphicsPlanOutput`). Used by step 3 of
`video-derush-edit-graphics`, and as the fallback for a `GraphicsRoom` step. Its answer has two
lists:

- `overlays` — titles, lower-thirds, callouts and tags, each on an offered graphics spot
  (`placementId` such as `p2`), with a duration word (`Short`, `Medium`, `Hold`). By default each
  overlay is a real animation the agent writes and renders itself with a transparent background,
  and returns as `renderedAssetStorageKey`; plain text on a box is the fallback.
- `inserts` — **tracked screen inserts**: for each offered green-screen region (`regionId` such as
  `r0`), a scene the agent writes and renders itself as a normal, solid video, returned as
  `renderedAssetStorageKey`, plus a `reason`. The `VideoCompile` step then pins that scene into
  the moving screen frame by frame. An insert **must** have a rendered scene from the same run;
  there is no text fallback.

**`MotionGraphicsDirector`** (same answer shape). Moderates a `GraphicsRoom` panel. During the
panel discussion it is limited to reading; in its final call it writes the plan and renders the
graphics and insert scenes, exactly like the planner.

Tell these agents what to show through the run's brief (the **Your brief** box when you click
**Run**), for example "In the phone screen, show our app's onboarding screen
with the logo animating in". See [Tracked screen insert recipe](../recipes/tracked-screen-insert.md).

These agents are the slowest in the video pipeline, because writing, checking and rendering code
takes many steps. They are never reused from an earlier run.

## Placeholder agents (not AI)

Two built-in agents exist only to fill the agent slot of non-AI steps and are **never run as an
AI**:

- `ExtractTransform` — used by `Extract` steps.
- `VideoTransform` — used by `VideoAnalyze`, `VideoCompile`, `VideoGenerate`, `Voiceover`,
  `EditRoom`, `GraphicsRoom`, `ColorGradeRoom` and `DailiesRoom` steps. For the four room steps,
  the real AI panel members come from the room's settings, not from this slot.

They have no tools. **Can render Remotion: no.**

## Skills: Remotion knowledge and design taste

A **skill** is a reference document an agent can open while it works. It is knowledge, not a tool:
it does not let the agent do anything new. Each agent is told the names of the skills it has and
opens one only when the task calls for it. Admins can see every skill, which agents use it and
where it came from on the **Skills** page under **Platform**.

There are two kinds of built-in skill:

- **Remotion skills** (`remotion-create`, `remotion-markup`, `remotion-render`, `remotion-captions`,
  `remotion-multimedia`) are working guides to writing and rendering Remotion video code: compositions,
  timing, audio, captions, media and render settings.
- **Design taste skills** (`taste-skill`, `minimalist-skill`, `brutalist-skill`) help the agents that
  decide or write how a video looks avoid the generic "AI template" look: font and colour choices,
  one consistent shape language, plain on-screen copy, and motion that has a reason. They come from
  the open-source Taste Skill collection (MIT licence, github.com/Leonxlnx/taste-skill), which is
  written for web pages. Each one opens with a short ReelBolt note saying which parts apply to
  video, followed by the original text unchanged. `taste-skill` is the general one; the other two
  describe a specific look and are used only when your brief asks for a minimalist or a brutalist
  style.

Which built-in agents carry the design taste skills:

| Agent | Skills it gets | Why |
|---|---|---|
| `DirectorAgent` | all three taste skills | It sets the video's look and mood, and turns a style in your brief into direction. |
| `AuthorAgent` | all three taste skills, plus the five Remotion skills | It writes the final video code, so it applies the look. |
| `RemotionComponentTranslator` | `taste-skill`, plus two Remotion skills | It chooses type and colour for the components it writes. Your app's own brand always comes first. |
| `AnimationStrategyAgent` | `taste-skill`, plus one Remotion skill | Motion with a reason, and sensible pacing. |
| `MotionGraphicsPlanner`, `MotionGraphicsDirector` | `taste-skill`, plus two Remotion skills | The type, colour and wording of titles and lower-thirds. |

Your brief, your style choices and your brand colours and fonts always take priority over a skill.
Agents that only make editing, sound, colour-grade or script decisions do not carry the design
taste skills.

## Custom agents

A **custom agent** is one you create yourself on the **Agents** page: you give it a name, a
description and a **system prompt** (its standing instructions). Built-in agents are read-only;
custom agents can be edited and deleted. A custom agent that a past run used can't be deleted while
that run exists, because the run keeps the steps it ran with: the **Agents** page tells you how many runs
still use it.

**Important limitation: custom agents do not run inside workflows.** You can
create, edit and add a custom agent to an `Agent`, `ForEach` or `Parallel` step, but the workflow
engine has no runner for custom agents, so when the run reaches that step it is marked
**Skipped** ("No agent registered ...") and the run moves on to the next step without doing
anything. Use a built-in agent for any work you need done, and steer it with the run's request
text (see below).

What a custom agent carries:

- **Skills:** you can attach any of the built-in skills described above to it — the five Remotion
  ones and the three design taste ones. A skill is
  reference knowledge, not a tool, and does not grant any new ability. (Because custom agents do
  not run in workflows, attached skills have no effect there.)
- **Which AI model it uses:** an admin can give any agent, built-in or custom, its own AI provider
  (the per-agent provider override on the agent's page). Otherwise the platform's default chat
  provider is used. For built-in agents this override takes effect.
- **Tools it has:** only "read project files" and "stop the run". A custom agent can never
  write code, write project files or **render Remotion**, whatever its prompt says.
- **Answer shape:** free-form text, so a custom agent cannot stand in for a
  built-in agent whose answer another step reads (for example a compile step's `decision` or
  `graphicsPlan`).

## Changing a built-in agent's behaviour

You cannot edit a built-in agent's prompt from the Agents page. Ways to steer one:

- Give instructions in the run's brief (the **Your brief** box that opens when you click **Run**).
  Every AI agent in the run reads it.
- Choose a different context mode on its step (which earlier results it sees).
- Ask an admin to assign it a different AI provider.
- An operator can override any built-in agent's prompt in the server configuration
  (`Agents:<AgentName>:SystemPrompt`).
