---
title: Workflow templates
sidebar_position: 1
description: Every ready-made ReelBolt workflow template, what each one makes, what you need to upload first, and how to add one to a project.
---

# Workflow templates

A **workflow** is the recipe ReelBolt follows to turn your files into a video. A **template** is a
ready-made workflow you can add to a project in one click and then run as-is or adjust.

ReelBolt ships eighteen templates in two families:

- **Promo from code** — reads the source code of a web app you upload and produces an animated
  promotional video about it. Five templates.
- **Edit real footage** — takes the video clips you upload and cuts, narrates, decorates and
  finishes them. Thirteen templates, all built on the same three-step core: *analyze the footage*,
  *decide what to keep*, *produce the edited file*.

Every template in this page lists its display name, its key (the short name the assistant and the
API use), whether it is added automatically, its steps in order, what it produces, and what you
need to upload or set up before you run it.

**Every template asks for your brief when you run it.** Clicking **Run** opens a box where you
say what video you want — the topic, who it is for, how long it should be and the format (for
example "15 s vertical reel about the timeline editor"). The AI steps read it; a reel, short, story
or TikTok request makes the promo templates produce a vertical 9:16 video. You can leave the box
empty to let the AI decide.

**Every template reviews its own work and can fix it.** The review step of each template looks at
the film (frames, the timeline and the sound), and when a problem comes from a setting or from an
earlier step it can change that setting or have that step made again for this run only, such as
a narration line spoken again or a generated shot made again. Redone steps are charged like any
other step, and the run page lists every change. See
[the review loop step](step-types.md#review-loop-step-reviewloop).

## Start from the Apps tab

The project's **Apps** tab (it replaces the old Workflows tab) lists **Your apps**, newest run first, and
**Start from a recipe**. A recipe is a template. Click **Use this recipe** and ReelBolt sets it up and opens it,
with no empty form in between. Every card says in words what the app costs: **No credits**, **Uses credits**,
**Uses credits and a voice** or **Uses credits and AI shots** (that spend is capped for each run). An app's last
run shows as **Finished** or **Failed**. If a recipe needs something your project does not have yet, the card
says so with a link that fixes it, such as **Add clips**. Use the category buttons and the search box to narrow the
list, and **Custom workflow (advanced)** at the end of the recipes to build your own steps.

## Running an app

Opening an app shows its few choices on the left (language, length, shape, narration, style, your clips and a free
note) and the result on the right. Under the choices a small strip tells you, with an icon and a word, whether
anything needs fixing before you run: **Fix needed**, **Check**, **Note** or **Ready**. The **Run** button shows the
price ("Run · about 40 credits") with the usual range under it, or **No credits used**. If something is missing,
such as clips, the button stays greyed out, says why, and offers the fix in one click. While it runs you see plain
steps ("Writing the script", "Recording the narration"); when it finishes you can play and **Download** the video,
**Change one thing and re-run**, or **Ask the assistant to change something**. **Edit steps (advanced)** in the menu
at the top opens the step builder.

## Setting up an app's page

When fields of your own are turned on for your workspace, the step builder's **App inputs** button opens the page
people see when they run the app: a short list of its fields, with a preview of the page beside it (under it on a
phone). Try the fields in the preview; nothing is sent.

- Each field shows its name, its kind (**Short text**, **Long text**, **Choice**, **Switch**, **Number**, **Length**,
  **Shape** or **Model**) and where the workflow uses it, such as "Step 3 · Video prompt". A field nothing uses says
  so, and the app cannot be saved until you use it or remove it.
- **Edit** opens a field: its name on the page, a help line, an example (shown greyed in the empty box, never sent),
  **Must be filled**, **Under "More options"**, its starting value, and what its kind needs: the options of a choice,
  the lowest and highest number, the lengths or shapes to offer, the words of a switch. **Show only when** hides a
  field until another switch or choice has a given value.
- **Add a field** adds a new field, or a run setting the steps already read (language, length and so on). Drag a
  field by its handle, or use the arrows, to reorder the fields. The bin removes one, and every step that used it goes
  back to a fixed value; a field that a condition reads cannot be removed until you change the condition.
- **Kind of field**, in a field's **Edit**, changes a short text into a long text (and back) or a number into a length
  (and back): the steps that use it keep working. Any other change of kind is a new field: add it, use it where the old
  one is used, then remove the old one.
- **Message for the AI steps** is optional text added to each run's request, with **Insert a field** to put a field
  where the cursor is, so the AI writers see what people filled in.
- The page shows at most 8 fields at once and holds 16 in all; the rest sit under **More options**. Keep only what
  changes from one run to the next.

**Filling a step's setting from the page.** Open a step: **App fields** lists the settings of that step the page may
fill (for a generated clip: the video prompt, the clip length, the shape, the takes and the model). **Use a field for
this setting** picks a field you already have, or makes a new one from the setting, with its name, its limits and its
current value as the starting value; for a text, the old words become the field's example. A filled setting shows
**From the app:** and the field's name, with **Stop using the field**. A text can mix fixed words and fields: once it
holds a field, edit it there and use **Insert a field**. A setting that one of the page's run settings already decides
(the shape, when the page asks for the shape) says so. On an AI agent step, **Let people pick this agent's model** adds
a model field.

**Models.** A model field lets people pick the model a step uses. By default they may pick any model of that kind
your workspace has. In the field's **Edit**, switch off **People may pick any model** to tick the **Allowed models**,
and choose the **Recommended** one: it is shown first and used when nobody picks. Save the page once before narrowing
the list.

**A cheaper path.** A **Switch** field can choose between two paths of the workflow. In a condition step, **Read an
app field** puts the switch in the condition, and **Finish the run here** ends a path instead of going on to a step.
The price on the app page follows the path the switch picks.

**What changes when you save** sits under the list: before you save, it says in plain words which fields are added,
changed or removed, the new title or message for the AI steps, and which settings start or stop using a field.

**Save Changes** saves the steps and the page together. The page is checked when it is saved, and a problem is shown
under the field it is about.

## How to add a template to a project

Templates are added to a project from the workflow builder:

1. Open the project and choose **New workflow** (on the Overview panel or the Workflows tab).
2. Under **Start from a template**, click a card. Each card shows the template's name and a
   one-line description of what you get and what you need to upload first.
3. Click **Use this template**. ReelBolt creates the complete workflow straight away, named after the template with
   its version (for example "Highlight edit from footage v1").

The list in the builder shows the opt-in templates only. The one automatically created template,
**Promo video from your code** (`quick-win-promo`), is already added to every new project, so it does not
appear in that list.

You can also ask the **assistant** for a template. The assistant can look up any template by its
key and get its full, ready-to-propose list of steps (through its `GetWorkflowTemplate` tool), then
offer it to you as a new workflow or as changes to an existing one. Nothing is saved until you
allow it, unless you have switched the assistant's **Ask before changes** off. See
[the assistant](../assistant/index.md).

After a template is added it is an ordinary workflow: you can rename it, change any step's
settings, add or remove steps, and save. Changing it never changes the template itself.

## Choosing a template: quick guide

Pick a template by what you start from and what you want to end up with:

| You have | You want | Template (key) |
|---|---|---|
| Web app source code | A clean promo video (silent unless you add narration) | Promo video from your code (`quick-win-promo`) |
| Web app source code, and you care about cost | The same promo with fewer AI tokens spent | Promo video from your code (lighter) (`lean-context-promo`) |
| Web app source code and a speech provider | A short narrated reel, vertical when you ask for one | Quick vertical reel (`cinematic-feature-spotlight`) |
| Web app source code and a speech provider | A narrated, story-shaped launch trailer | Narrated launch trailer (`story-arc-launch-trailer`) |
| Web app source code and a video-generation account | The promo with AI-generated abstract background plates | Promo video from your code with AI backgrounds (`quick-win-promo-plates`) |
| Video clips (screen recordings, product shots, b-roll) and a speech provider | A narrated story written from your brief, with captions | Narrated product story (`narrated-story`) |
| A filmed video (talking head, vlog, interview) | A tightened edit with dead air removed | Highlight edit from footage (`video-derush-edit`) |
| A filmed video | The same, decided by a panel of AI editors | Highlight edit, decided by an editing team (`video-derush-edit-room`) |
| A filmed video | The edit plus titles, lower-thirds and callouts | Footage edit with titles and callouts (`video-derush-edit-graphics`) |
| A filmed video | The edit plus graphics, planned by a panel of AI artists | Footage edit with titles, designed by a graphics team (`video-derush-edit-graphics-room`) |
| A filmed video and music files you uploaded | The edit with a background music bed | Footage edit with background music (`video-derush-edit-music`) |
| A filmed video and short sound files you uploaded | The edit with whooshes, clicks and stingers | Footage edit with sound effects (`video-derush-edit-sfx`) |
| A filmed video | The edit with a colour grade, chosen by a panel | Footage edit with a colour grade (`video-derush-edit-grade-room`) |
| A filmed video and a paid video-generation account | The edit with AI-generated b-roll | Footage edit with AI-generated b-roll (`video-derush-edit-broll`) |
| A filmed video and a paid video-generation account | The same, with two takes per clip and a panel choosing the better one | Footage edit with AI b-roll, best take chosen by a panel (`video-derush-edit-dailies`) |
| A filmed video and a speech provider | The edit with narration and captions written after the cut | Footage edit with narration and captions (`video-derush-edit-voiceover`) |
| Talking-head footage with speech, a speech provider and a consented cloned voice | The edit with flubbed sentences re-recorded in the presenter's voice | Fix flubbed lines in a talking-head video (`video-derush-edit-pickups`) |
| Footage with speech and a speech provider | The edit dubbed into another language, with subtitles | Dubbed video in another language (`video-derush-edit-dub`) |
| A filmed clip with a green phone or laptop screen, and a screen recording (or any video or picture) to show on it | The recording on the screen, following the screen as it moves | Green-screen insert (`green-screen-insert`) |
| A finished video with speech in it | The same video with the spoken words burned in as subtitles, plus SRT and VTT files | Subtitles for your video (`video-subtitles`); for another language, Subtitles in another language (`video-subtitles-translated`). See [the subtitles recipe](../recipes/subtitles.md) |

There is **no built-in music or sound-effects library**: the music and sound-effects templates
choose only from audio files you upload to the project first. With none uploaded, the result has
no music or effects, and the run says so.

**Green-screen insert** (`green-screen-insert`) is described in full [below](#green-screen-insert-green-screen-insert). It is not added automatically: ask the assistant ("put this screen recording on the green screen in that clip") or follow the
[Tracked screen insert recipe](../recipes/tracked-screen-insert.md).

Four templates add narration to footage (`narrated-story`, `video-derush-edit-voiceover`,
`video-derush-edit-pickups` and `video-derush-edit-dub`), and two promo templates are narrated
(`cinematic-feature-spotlight` and `story-arc-launch-trailer`). The other two promo templates are
silent until you insert a `Voiceover` step; see [Voiceover recipe](../recipes/voiceover.md).

## Promo video from your code (`quick-win-promo`)

**Promo video from your code** is the starter template. It is the only template added
automatically to every new project. When you run it, it asks for your brief (audience, message,
length, format); leave it empty to let the AI decide. The video is **silent** — the only sound a
promo can carry is narration, and this template has no narration step. The finished video's notes
say so; insert a `Voiceover` step after step 8 to narrate it (see
[Voiceover recipe](../recipes/voiceover.md)).

**What it makes:** an animated promotional video about a web app, built with Remotion (the
code-based animation tool ReelBolt uses to draw and render promo videos). The finished video
appears as the output of step 10.

**What it needs:** the web app's source code uploaded as project files, and a working chat AI
provider.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze code structure | `Agent` | `CodeStructureAnalyzer` |
| 2 | Analyze UI dependencies | `Agent` | `DependencyAnalyzer` |
| 3 | Inventory components | `Agent` | `ComponentInventoryAnalyzer` |
| 4 | Map routes and API | `Agent` | `RouteAndApiAnalyzer` |
| 5 | Extract style tokens | `Agent` | `StyleAndThemeExtractor` |
| 6 | Translate to Remotion scenes | `Agent` | `RemotionComponentTranslator` |
| 7 | Create animation strategy | `Agent` | `AnimationStrategyAgent` |
| 8 | Draft narration and captions | `Agent` | `ScriptwriterAgent` |
| 9 | Build shot direction | `Agent` | `DirectorAgent` |
| 10 | Assemble and render master composition | `Agent` | `AuthorAgent` |
| 11 | Review quality and compliance | `ReviewLoop` | `ReviewAgent` |

Step 11 scores the result out of 10. Below 8 it sends the work back to step 10 for another try, at
most 2 review rounds in total.

**Choose it when** you want a reliable first promo with no setup. See
[Promo from code recipe](../recipes/promo-from-code.md).

## Promo video from your code, lighter (`lean-context-promo`)

**Promo video from your code (lighter)** is an opt-in version of the starter promo that spends
fewer AI tokens. Like the starter, it asks for your brief and is silent unless you add narration. It adds
one non-AI step that trims the list of app components down to the essentials (at most 40
components, each reduced to name, file and purpose) before later steps read it, and it gives each
later step only the earlier results it actually needs.

**What it makes:** the same kind of animated promo as the starter, rendered by step 11.

**What it needs:** the web app's source code uploaded as project files.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze code structure | `Agent` | `CodeStructureAnalyzer` |
| 2 | Analyze UI dependencies | `Agent` | `DependencyAnalyzer` |
| 3 | Inventory components | `Agent` | `ComponentInventoryAnalyzer` |
| 4 | Map routes and API | `Agent` | `RouteAndApiAnalyzer` |
| 5 | Extract style tokens | `Agent` | `StyleAndThemeExtractor` |
| 6 | Reduce component inventory to a lean view | `Extract` | `ExtractTransform` (not an AI) |
| 7 | Translate to Remotion scenes (lean context) | `Agent` | `RemotionComponentTranslator` |
| 8 | Create animation strategy (lean context) | `Agent` | `AnimationStrategyAgent` |
| 9 | Draft narration and captions | `Agent` | `ScriptwriterAgent` |
| 10 | Build shot direction (lean context) | `Agent` | `DirectorAgent` |
| 11 | Assemble and render master composition (lean context) | `Agent` | `AuthorAgent` |
| 12 | Review quality and compliance | `ReviewLoop` | `ReviewAgent` |

Step 12 sends work back to step 11 when the score is below 8, at most 2 rounds.

**Choose it when** your app is large and the starter promo is slow or expensive.

## Quick vertical reel (`cinematic-feature-spotlight`)

**Quick vertical reel** is a short, punchy, **narrated** feature spotlight for social media, with
reveal transitions and a strong call-to-action ending. It **asks you for a brief** before each run
(for example "15 s vertical reel about the new dashboard, end with a sign-up call to action"), and
every AI step sees it. Asking for a reel, short, story, TikTok or vertical video makes a 1080x1920
(9:16) video; "square" makes 1080x1080; otherwise it is landscape 1920x1080. A stated length is the
video's length.

**What it makes:** a narrated animated promo rendered by step 9. The narration is the script's own
voiceover, recorded by step 7 before the director times the shots to it.

**What it needs:** the web app's source code uploaded as project files, a **speech provider** set
up by an admin (the run fails at step 7 without one), and a short brief typed when you run it.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze cinematic candidate surfaces | `Agent` | `CodeStructureAnalyzer` |
| 2 | Find hero components | `Agent` | `ComponentInventoryAnalyzer` |
| 3 | Extract brand visuals | `Agent` | `StyleAndThemeExtractor` |
| 4 | Translate hero scenes | `Agent` | `RemotionComponentTranslator` |
| 5 | Plan cinematic transitions | `Agent` | `AnimationStrategyAgent` |
| 6 | Write emotional feature hooks | `Agent` | `ScriptwriterAgent` |
| 7 | Record the narration (`"kind": "ScriptScenes"`) | `Voiceover` | `VideoTransform` (not an AI; calls the speech provider) |
| 8 | Define reveal sequence | `Agent` | `DirectorAgent` |
| 9 | Assemble cinematic master cut | `Agent` | `AuthorAgent` |
| 10 | Cinematic quality review | `ReviewLoop` | `ReviewAgent` |

Step 10 sends work back to step 9 when the score is below 9, at most 3 rounds.

## Narrated launch trailer (`story-arc-launch-trailer`)

**Narrated launch trailer** is a promo template shaped like a narrative trailer: set-up, rising
tension, a payoff reveal and a final launch call to action, spoken over the picture. It **asks you
for a brief** before each run; format and length follow the brief exactly as in the quick vertical
reel above.

**What it makes:** a narrated animated trailer rendered by step 9.

**What it needs:** the web app's source code uploaded as project files, a **speech provider** set
up by an admin, and a short brief typed when you run it.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Map narrative assets | `Agent` | `CodeStructureAnalyzer` |
| 2 | Extract user journey flow | `Agent` | `RouteAndApiAnalyzer` |
| 3 | Extract mood and tone tokens | `Agent` | `StyleAndThemeExtractor` |
| 4 | Translate story scenes | `Agent` | `RemotionComponentTranslator` |
| 5 | Write narrative arc script | `Agent` | `ScriptwriterAgent` |
| 6 | Record the narration (`"kind": "ScriptScenes"`) | `Voiceover` | `VideoTransform` (not an AI; calls the speech provider) |
| 7 | Create trailer shotlist | `Agent` | `DirectorAgent` |
| 8 | Plan suspense pacing | `Agent` | `AnimationStrategyAgent` |
| 9 | Assemble and render trailer | `Agent` | `AuthorAgent` |
| 10 | Trailer impact review | `ReviewLoop` | `ReviewAgent` |

Step 10 sends work back to step 9 when the score is below 9, at most 3 rounds.

## What every promo template does with your copy

The four promo templates share three rules, so the video reads well on the screen it is made for:

- **Plain text on screen.** Words are drawn exactly as written, so the AI never writes markdown
  such as `*word*`. Any that slips into the script is removed automatically, and a render whose
  declared on-screen text still carries it is refused before it starts.
- **Readable at phone size.** Body text is at least about 48 pixels tall on a 1080-wide frame, and
  a vertical video zooms into the part of your app's screen that matters instead of shrinking the
  whole desktop layout.
- **Nothing invented.** Names, quotes, ratings, prices and numbers come only from your code, your
  files or your brief. A screen that greets a user says "Welcome back", never a made-up name.

## Highlight edit from footage (`video-derush-edit`)

**Highlight edit from footage** is the basic footage-editing template. "Derushing" means going through raw
footage and keeping only the good parts. It finds pauses, shot changes and (optionally) what is
said, lets an AI editor choose which parts to keep, and then produces the edited video file.

**What it makes:** an edited MP4 (`edited.mp4`), produced by step 3. The edited video is also added
to the project's files so you can edit it again later.

**What it needs:**

- A video file uploaded to the project, **selected as the source in step 1** (the template leaves
  the file empty on purpose; the step shows "no file selected" until you pick one).
- For the editor to understand speech, a **transcription provider** set up by an admin (the
  bundled local `whisper` service works). Without one, the analysis still runs but the editor
  only sees pauses and shot changes.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Compile edited video | `VideoCompile` | `VideoTransform` (not an AI) |
| 4 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

The compile step uses gentle automatic transitions between cuts (`"transitionPolicy": "Auto"`), a
short fade in and out at the start and end, and drops any kept piece shorter than 0.8 seconds.
New workflows from the footage-edit templates, except the dailies template, offer **cut points inside
long shots** in step 1, where the picture pauses or changes. A long single shot (a silent clip with no
cuts is one shot otherwise) can then be cut into parts, and the editor can keep only the good part. The
editor can use these cut points to meet a length you asked for. The step form shows a **Cut points inside
long shots** setting with three choices: **Off**, **Even** (at equal intervals) and **Where the picture
pauses or changes**. Workflows you already created keep their settings.
Step 4 sends work back to step 2 when the score is below 8, at most 3 rounds.

**Choose it when** you have a talking-head video, interview or vlog with dead air, false starts or
retakes. See [Derush edit recipe](../recipes/derush-edit.md).

## Highlight edit, decided by an editing team (`video-derush-edit-room`)

**Highlight edit, decided by an editing team** works like the highlight edit, but the decision about what to keep is
made by a small **panel of AI editors** instead of one. Three editor "seats" (pacing, story and
craft) discuss the footage in a short group conversation, and a director then writes one final
decision. If the panel fails for any reason, a single AI editor makes the decision instead.

**What it makes:** an edited MP4 produced by step 3.

**What it needs:** a video file selected as the source in step 1; a transcription provider is
recommended.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Edit room deliberation | `EditRoom` | panel: three editor seats plus `VideoEditDirector` |
| 3 | Compile edited video | `VideoCompile` | `VideoTransform` (not an AI) |
| 4 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 4 sends work back to step 2 when the score is below 8, at most 3 rounds. The panel costs more
AI calls than a single editor.

**Choose it when** the edit needs judgement (a story to tell, several takes to choose between) and
you can afford a slower, more thorough decision.

## Footage edit with titles and callouts (`video-derush-edit-graphics`)

**Footage edit with titles and callouts** adds on-screen graphics (titles, lower-thirds, callouts) to the
basic edit. The analysis step also finds good, uncluttered places on screen for graphics, and a
motion-graphics AI chooses where to put graphics and designs them. By default it designs each
graphic as a real animation that it renders itself with Remotion; plain text on a box is the
fallback.

**What it makes:** an edited MP4 with graphics, produced by step 4.

**What it needs:** a video file selected as the source in step 1; a transcription provider is
recommended so graphics match what is being said.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video (`"emitOverlayPlacements": true`) | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan motion graphics overlays | `Agent` | `MotionGraphicsPlanner` |
| 4 | Compile edited video with graphics (`"enableGraphics": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 5 sends work back to step 2 when the score is below 8, at most 3 rounds. Step 3 can take a
long time because it writes and renders small animations.

This template is also the easiest starting point for a **tracked screen insert**: turn on insert
detection in step 1 and inserts in step 4. See
[Tracked screen insert recipe](../recipes/tracked-screen-insert.md) and
[Motion graphics recipe](../recipes/motion-graphics-overlays.md).

## Footage edit with titles, designed by a graphics team (`video-derush-edit-graphics-room`)

**Footage edit with titles, designed by a graphics team** replaces the single motion-graphics AI with a **panel of AI
artists**: a layout artist, a timing artist, a copy artist and an insert artist discuss the
options, then a lead director writes one final graphics plan and renders the graphics. If the
panel fails, a single motion-graphics AI plans instead.

**What it makes:** an edited MP4 with graphics, produced by step 4.

**What it needs:** a video file selected as the source in step 1.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video (`"emitOverlayPlacements": true`) | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Graphics room deliberation (reads step 1) | `GraphicsRoom` | panel: four artist seats plus `MotionGraphicsDirector` |
| 4 | Compile edited video with graphics (`"enableGraphics": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 5 sends work back to step 2 when the score is below 8, at most 3 rounds.

## Footage edit with background music (`video-derush-edit-music`)

**Footage edit with background music** adds a background music bed to the basic edit. There is no
built-in music library: **upload a music track to the project's files first**. Every audio
file you uploaded to the project is offered as a music choice, a music-supervisor AI picks at most one track
and how loud and how strongly it should dip under speech, and the compile step mixes it in.

**What it makes:** an edited MP4 with music, produced by step 4. With no audio files uploaded the
video has no music, and the music step's reason says so in plain words.

**What it needs:** a video file selected as the source in step 1, and **one or more music files**
(audio files) uploaded to the project.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video (`"offerMusicTracks": true`) | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Pick background music | `Agent` | `MusicSupervisor` |
| 4 | Compile edited video with music (`"enableMusic": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 5 sends work back to step 2 when the score is below 8, at most 3 rounds. See
[Music, sound effects and colour grade recipe](../recipes/music-sfx-and-grade.md).

## Footage edit with sound effects (`video-derush-edit-sfx`)

**Footage edit with sound effects** adds short sound effects (whooshes, clicks, stingers) at chosen
moments in the edit. There is no built-in sound-effects library: **upload your effects to the
project's files first**. Every audio file in the project is offered as a possible effect, and a
sound-designer AI decides which effect plays when, and how loud. Choosing no effects at all is a
valid result.

**What it makes:** an edited MP4 with sound effects, produced by step 4.

**What it needs:** a video file selected as the source in step 1, and **short sound-effect files**
uploaded to the project (name them clearly, for example `whoosh.wav`; the AI chooses by file name).

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video (`"offerSfxClips": true`) | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan sound-effect cues | `Agent` | `SoundDesigner` |
| 4 | Compile edited video with sound effects (`"enableSfx": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 5 sends work back to step 2 when the score is below 8, at most 3 rounds.

## Footage edit with a colour grade (`video-derush-edit-grade-room`)

**Footage edit with a colour grade** adds a colour grade (an overall "look", such as warm, cool,
filmic or muted) to the edit. A panel of AI colourists discusses the measured colour of each shot,
then a supervising colourist picks one look for the whole video, as words only (for example "Warm,
Subtle"). ReelBolt turns those words into the actual colour adjustment. "None" (no grade) is a
valid result.

**What it makes:** an edited, colour-graded MP4 produced by step 4.

**What it needs:** a video file selected as the source in step 1.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Color grade room deliberation (reads step 1) | `ColorGradeRoom` | panel: three colourist seats plus `ColorGradeDirector` |
| 4 | Compile edited video with grade (`"enableColorGrade": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 5 sends work back to step 2 when the score is below 8, at most 3 rounds.

## Footage edit with AI-generated b-roll (`video-derush-edit-broll`)

**Footage edit with AI-generated b-roll** adds **AI-generated video clips** to the edit: cutaways over speech,
a cold open at the start, or an end card. A shot-director AI plans the clips, a generation step
pays a video-generation service to make them (with a hard spending cap), and the compile step
places them.

**What it makes:** an edited MP4 with generated clips, produced by step 5.

**What it needs:**

- A video file selected as the source in step 1.
- A **video-generation provider** (MiniMax) set up by an admin. This **costs real money**.
- Spending limits: this template's generation step allows at most 4 clips and at most 5 US
  dollars per run (`"maxClips": 4`, `"maxSpendCredits": 5.0`), and admins set daily limits on top.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan generated b-roll shots | `Agent` | `ShotDirector` |
| 4 | Generate planned b-roll clips | `VideoGenerate` | `VideoTransform` (not an AI; calls the paid provider) |
| 5 | Compile edited video with generated clips (`"enableGeneratedClips": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 6 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 6 sends work back to step 2 when the score is below 8, at most 3 rounds. When you run or retry
this workflow, ReelBolt asks whether to **reuse previously generated clips** (free) or
**regenerate** them (costs money). See [Generated b-roll recipe](../recipes/generated-b-roll.md).

## Footage edit with narration and captions (`video-derush-edit-voiceover`)

**Footage edit with narration and captions** adds **spoken narration** written by an AI after the
cut, with captions burned in. It asks for your brief (topic, tone, length) when you run it. A
narration-writer agent writes zero or more lines, each tied to a shot, pause or spoken phrase of the edit (never a
time), a `Voiceover` step turns them into speech and works out where each line starts, and the
compile step mixes them over the original sound.

**What it makes:** an edited MP4 with narration and karaoke-style captions, produced by step 5.

**What it needs:**

- A video file selected as the source in step 1.
- A **speech provider** (Fish Audio, or a local OpenAI-compatible speech server) set up by an
  admin, and a voice id entered as `defaultVoiceId` in step 4 (or a voice from the project's
  **Voices** tab as `projectVoiceId`).
- A **transcription provider** for the captions: karaoke captions follow each spoken word, and
  without word timings a line gets no caption (the compile step reports `no_word_alignment`).
  Switch step 5 to `"captionStyle": "Subtitle"` if you have no transcription provider.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Write narration lines | `Agent` | `NarrationWriter` |
| 4 | Synthesize narration (`"kind": "NarrationPlan"`) | `Voiceover` | `VideoTransform` (not an AI; calls the speech provider) |
| 5 | Compile edited video with narration (`"enableVoiceover": true`, `"enableCaptions": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 6 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 6 sends work back to step 2 when the score is below 8, at most 3 rounds. A line that does not
fit in its moment is reported, and the review asks the narration writer for shorter words; nothing
re-cuts the picture to the voice. Captions are on (`"captionStyle": "Karaoke"`); set
`"enableCaptions": false` in step 5 to turn them off. Because the editor cuts first and the writer
follows, the narration describes the kept clips; for a story written first, use **Narrated product
story** below. See [Voiceover recipe](../recipes/voiceover.md).

## Fix flubbed lines in a talking-head video (`video-derush-edit-pickups`)

**This template needs talking-head footage with clear speech.** **Fix flubbed lines in a
talking-head video** re-records the sentences the presenter **flubbed**, in the
presenter's own cloned voice. A pickup-planner agent names the spoken phrases to replace and the
corrected sentence for each, a `Voiceover` step speaks them, and the compile step mutes the
original take for exactly that phrase and plays the pickup there. The picture is untouched, so the
mouth is still the original take's: **lip sync is not available**, and the result says so.

**What it makes:** an edited MP4 with pickups, produced by step 5.

**What it needs:**

- A video file selected as the source in step 1, and a **transcription provider**, because
  pickups replace spoken phrases (`t0`, `t1`, ...) and there are none without a transcript.
- A **cloned voice with recorded consent** in the project's **Voices** tab (needs a Fish Audio
  provider). Enter it as `projectVoiceId` in step 4; a voice whose consent is missing, revoked or
  expired **fails the step** instead of using another voice.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan pickup lines | `Agent` | `PickupPlanner` |
| 4 | Record pickups (`"kind": "PickupPlan"`) | `Voiceover` | `VideoTransform` (not an AI; calls the speech provider) |
| 5 | Compile edited video with pickups (`"enablePickups": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 6 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 6 sends work back to step 2 when the score is below 8, at most 3 rounds.

## Dubbed video in another language (`video-derush-edit-dub`)

**This template needs footage with speech.** **Dubbed video in another language** makes a version of the edit in **another language**. The narration
writer writes the lines, a translator agent rewrites the same lines in the language you name when
you run the workflow, a `Voiceover` step speaks the translation, and the compile step **replaces**
the original speech under every line and burns **subtitles** of the words actually spoken. The
picture is untouched: **lip sync is not available**, and the result says so.

**What it makes:** a dubbed, subtitled MP4, produced by step 6.

**What it needs:**

- A video file selected as the source in step 1, and a **speech provider**.
- **Text when you run it:** the target language (for example "Spanish"), in the request box.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Write narration lines | `Agent` | `NarrationWriter` |
| 4 | Translate narration | `Agent` | `NarrationTranslator` |
| 5 | Synthesize dub (`"kind": "NarrationPlan"`, pointing at step 4) | `Voiceover` | `VideoTransform` (not an AI; calls the speech provider) |
| 6 | Compile dubbed video (`"voiceoverMode": "ReplaceDialogue"`, `"enableCaptions": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 7 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 7 sends work back to step 2 when the score is below 8, at most 3 rounds.

## Narrated product story (`narrated-story`)

**Narrated product story** writes the **story first** and then cuts your clips to it. A narration
writer reads your brief and what the analysis saw in each clip, and writes a short script with a
beginning, a middle and an end, tying each line to the clip it should play over. The editor then
keeps those clips **in the script's order**, the narration is recorded, and the compile step mixes
it in with karaoke captions. It asks for your brief when you run it: what the video is about, who
it is for, the tone and how long it should be.

**What it makes:** a narrated, captioned MP4 produced by step 5.

**What it needs:**

- Your video clips (screen recordings, product shots, b-roll), uploaded and **selected as the
  sources in step 1**. Clips with no sound of their own are fine.
- A **speech provider** set up by an admin, and a **transcription provider** for the karaoke
  captions (see the narration template above).
- Optional: a **vision provider**, so the analysis can describe what each clip shows and the
  writer can pick the right clip for each line. Without one the run still works.
- The output format: step 5 keeps your clips' own shape by default. Choose vertical, square or
  landscape there before you run it.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze your clips (`"vision": "Optional"`) | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Write the story | `Agent` | `NarrationWriter` |
| 3 | Choose and order clips for the story | `Agent` | `VideoStoryEditor` |
| 4 | Record the narration (`"kind": "NarrationPlan"`, pointing at step 2) | `Voiceover` | `VideoTransform` (not an AI; calls the speech provider) |
| 5 | Compile the narrated video (`"enableVoiceover": true`, `"enableCaptions": true`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 6 | Review the finished video | `ReviewLoop` | `VideoReviewAgent` |

Step 6 sends work back to **step 2** when the score is below 8, at most 3 rounds, so a weak result
gets a rewritten story as well as a new cut. Clips play in the script's order across different
clips; two moments of the **same** clip always play in that clip's own order.

## Footage edit with AI b-roll, best take chosen by a panel (`video-derush-edit-dailies`)

**Footage edit with AI b-roll, best take chosen by a panel** is the b-roll template with a second opinion: the
generation step makes **two takes of every planned clip**, a second analysis step measures each take
exactly as it measures your footage, and a panel of AI judges (a **dailies room**) picks the better
take of each shot. Only the chosen takes are placed.

**What it makes:** an edited MP4 with the chosen generated clips, produced by step 7.

**What it needs:** the same as the b-roll template. It costs roughly twice as much per run, since
every clip is made twice (`"takes": 2`, `"maxClips": 4`, `"maxSpendCredits": 8.0`). The generation step
also has **look matching** and **camera continuity** switched on, so each clip is asked to match the
colour and camera feel of the real shot it sits beside.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan generated b-roll shots | `Agent` | `ShotDirector` |
| 4 | Generate two takes per planned shot | `VideoGenerate` | `VideoTransform` (calls the paid provider) |
| 5 | Measure every generated take (`"source": {"kind": "GeneratedClips", "stepOrder": 4}`) | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 6 | Dailies room: pick one take per shot | `DailiesRoom` | `TakeSelector` seats + `DailiesDirector` |
| 7 | Compile edited video with the chosen takes (`"takeSelection": {"from": "Step", "stepOrder": 6}`) | `VideoCompile` | `VideoTransform` (not an AI) |
| 8 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 8 sends work back to step 2 when the score is below 8, at most 3 rounds. See
[Generated b-roll recipe](../recipes/generated-b-roll.md).

## Promo video from your code with AI backgrounds (`quick-win-promo-plates`)

**Promo video from your code with AI backgrounds** is **Promo video from your code** with one extra step before the
Author: a `VideoGenerate` step that makes one or two short **abstract background plates** from a
fixed, built-in description (soft drifting gradients, no text, no people). The Author stages them
into its workspace and may use them as full-bleed backgrounds behind its animated scenes.

**What it makes:** an animated promo MP4, produced by step 11.

**What it needs:** the same as Promo video from your code, plus a video-generation provider. **It costs
money** (at most 3 US dollars per run, `"maxSpendCredits": 3.0`), so it is not added automatically.
Steps 1 to 9 are Promo video from your code's; step 10 is the generation step; step 11 is the Author; step 12
reviews and loops back to step 11.

## Green-screen insert (`green-screen-insert`)

**What it does:** shows a screen recording, a video or a picture on the green screen of a phone, monitor or
laptop in your clip. The content follows the screen as it moves and turns, and stays behind a hand or a
cat passing in front of it. The clip is kept whole: nothing is cut.

**What it costs:** only processing time. There is no AI step, so no model is used, and the run counts toward the
larger hourly allowance for runs that call no model. It is not added automatically.

**What it needs:** the clip with the green screen and the content to show on it, both uploaded to the
project. Ask the assistant to set it up, or open the workflow and pick the files yourself.

| # | Step label | Step type | Agent |
|---|---|---|---|
| 1 | Find the green screen | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Put the content on the screen | `VideoCompile` | `VideoTransform` (not an AI) |

**What you have to set:** on the app's page you choose the clip with the green screen and "What goes on the
screen" (a picture or a video; the run's brief carries it as the `insertContent` file role, and step 2 uses
it when it names no content of its own). Without a clip or without that file the run fails straight away
with `INSERT_CONTENT_MISSING`, and if no screen is found it ends with `INSERT_NOT_APPLIED` instead of
returning the clip unchanged. The notes box is optional. In a workflow you build yourself the assistant
fills both in for you, and each step's config names the field it is waiting for (`placeholders`:
`source.projectFileId` in step 1, `insertContentRef` in step 2), so a copy you made from the template
picker says what is still missing. The result is a new video under **Outputs**, like any compiled film; it is not added to the project's files.

## What every footage template has in common

All thirteen footage templates (`video-derush-edit`, its eleven variants and `narrated-story`) share
these facts:

- **Step 1 must point at your video.** It is set to a project file with no file chosen, so a run
  fails straight away with `SOURCE_UNRESOLVED` until you open step 1 and select the uploaded video.
- **Later steps point back at earlier steps by number.** For example the compile step in the music
  template reads the editing decision from step 2 and the music choice from step 3. If you insert
  or remove steps, check these numbers (see [Step types](step-types.md)).
- **The AI never writes times or positions.** The analysis step gives every shot, pause, spoken
  phrase, graphics spot and green-screen region a short id (such as `s3`, `g1`, `t7`, `p2`, `r0`),
  and the AI agents only choose among those ids. ReelBolt itself works out the exact frames. This
  is why the AI cannot place something at a moment that does not exist.
- **The review step checks the result** and sends the work back to step 2 (the editing decision,
  or the story in `narrated-story`) when the score is too low, up to 3 rounds; after that the run
  finishes with the best result it has.
- **They ask for your brief before running.** Type instructions for the AI editor (for example
  "keep it under 60 seconds"), or leave the box empty. To make the brief optional, open the
  workflow, switch off **Require a brief before each run** and save.
