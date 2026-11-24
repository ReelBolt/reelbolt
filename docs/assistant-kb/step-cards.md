---
title: Step cards
kb:
  kind: step-card
---

<!--
How this file is built. Each section has hand-written text and a generated block between the
"step-card:begin" and "step-card:end" comments. The generated block is rendered from StepCatalog
(inference/src/ReelBolt.Shared/Workflows/StepCatalog.cs). Never edit between the markers.
The test StepCardsDocDriftTests (inference/tests/ReelBolt.WorkflowEngine.Tests/Docs/AssistantKbDocsTests.cs)
fails when a committed block differs from the render, and when a StepType has no section.
To regenerate after a card changes, from the inference folder:
  UPDATE_STEP_CARDS_DOC=1 dotnet test tests/ReelBolt.WorkflowEngine.Tests --filter "FullyQualifiedName~StepCardsDocDriftTests"
Then commit the file. Hand-written text sits above each begin marker.
-->

# Step cards

Internal background for the assistant. One section per step type, in the order of the step type list. The facts block of each section (needs, produces, cost class, brief fields read, pairings, checks) is generated from code; the text above it explains in plain words what the step is for, what to say in the brief and what usually goes wrong. No prices appear here: the cost class says whether a step spends beyond tokens, and the estimate gives the number. Every section stands alone.

## Agent

An Agent step runs one named AI agent and stores its answer. Which earlier work the agent sees is the step's input context: the whole accumulated workflow, only the previous step, a chosen set of earlier steps, or a custom mapped subset. Choose the narrowest that still holds what the agent needs; the whole workflow is the most expensive. Creative and editorial agents (script, direction, authoring, the story editor, planners, reviewers) are set never to reuse a cached answer, so a rerun makes a fresh decision; the code analyzers may be reused.

What to put in the brief: Put language, length, tone, shape and style in the brief fields, not in the notes. A setting the agent does not read is ignored: check the brief fields listed below.

Typical mistakes: An agent that needs the output of a step that comes after it, or whose context is the previous step only when it also needs an older step. Read the context mode before blaming the agent.

<!-- step-card:begin Agent -->
Generated from the step catalog; do not edit by hand.

- Agent step. Runs one AI agent with its prompt and tools and records its answer as the step's output.
- Cost: Uses credits.
- Needs: project files of the role the step reads (check WL010)
- Needs: An agent chosen on the step
- Produces: The agent's text or schema-checked JSON answer
- Read by: Any later step
- Pairs with Voiceover: the Voiceover source names this step when it writes the lines
- Pairs with VideoCompile: reads an editor, planner or supervisor agent's decision by step order
- Common mistake WL010 source-media-missing, fix: Upload the missing media to the project
<!-- step-card:end Agent -->

## Conditional

A Conditional step reads one earlier step's output, evaluates a simple expression on it and picks which later step runs next, with one target for yes and one for no. The profile builder uses it to run the code analyzers only when the product profile is missing. It reads a named step, not the previous one, when its config says so.

What to put in the brief: Nothing in the brief changes a branch. Use it only in a custom graph that needs a branch.

Typical mistakes: Pointing the yes or no branch at a step that does not exist, or reading the previous step when the decision lives in step one.

<!-- step-card:begin Conditional -->
Generated from the step catalog; do not edit by hand.

- Conditional step. Chooses which later steps run by checking an earlier step's output.
- Cost: No credits.
- Needs: An earlier step whose output decides the branch
- Produces: A yes or no branch decision
- Read by: Later steps in the branch
- Pairs with Agent: the branch runs the steps it guards
<!-- step-card:end Conditional -->

## ForEach

A ForEach step repeats one agent once per item in a list an earlier step produced, for example once per scene. It is the way to run the same agent many times without copying steps.

What to put in the brief: Nothing in the brief; length and count come from the earlier list.

Typical mistakes: Feeding it something that is not a list, or a very long list: every item is one agent run.

<!-- step-card:begin ForEach -->
Generated from the step catalog; do not edit by hand.

- ForEach step. Repeats one agent once for every item an earlier step produced.
- Cost: No credits.
- Needs: An earlier step that outputs a list
- Produces: A list with one result per item
- Read by: Any later step
- Pairs with Agent: runs once per item
<!-- step-card:end ForEach -->

## ReviewLoop

A ReviewLoop step has a reviewer score the work of one earlier step (the loop target, by step number) and send it back for another round until the score is high enough or the rounds run out. In the catalog the target is the model-written step being reviewed: the Author in the promo recipes, the story editor in most footage recipes and the narration writer in the narrated story. If steps are inserted, the target number must move with them.

What to put in the brief: Length, language, shape, tone and style are read by the reviewer, so a mismatch is caught here. State them in the brief fields.

Typical mistakes: A target that is missing, later than the loop, or not model-written, so the loop never loops. A voice or generation step inside the loop span repeats its spend each round.

<!-- step-card:begin ReviewLoop -->
Generated from the step catalog; do not edit by hand.

- ReviewLoop step. Has a reviewer score a step's work and sends it back for another round until it passes or the rounds run out.
- Cost: Uses credits.
- Needs: an earlier step written by a model, as the loop target (check WL003)
- Needs: a ReviewLoop that runs after every Voiceover, AudioGenerate and VideoCompile step (check WL020)
- Produces: The reviewer's score and notes for each round (contract ReviewOutput)
- Read by: The loop target on the next round, Any later step
- Reads from the run brief: format, language, lengthSec, resolution, stylePreset, tone
- Pairs with Agent: the loop target is the writer being reviewed
- Common mistake WL003 reviewloop-target-invalid, fix: Point the reference at the right earlier step
- Common mistake WL020 review-before-final-cut, fix: Point the reference at the right earlier step
<!-- step-card:end ReviewLoop -->

## Parallel

A Parallel step runs two or more agents on the same input at the same time and merges their answers into one list a later agent can read.

What to put in the brief: Nothing in the brief.

Typical mistakes: Expecting the agents to see each other's answers: they do not.

<!-- step-card:begin Parallel -->
Generated from the step catalog; do not edit by hand.

- Parallel step. Runs several agents at the same time and merges their answers into one list.
- Cost: Uses credits.
- Needs: Two or more agents chosen on the step
- Produces: A list of each agent's answer
- Read by: Any later step
- Pairs with Agent: a later agent can read the merged answers
<!-- step-card:end Parallel -->

## Extract

An Extract step reshapes data without a model. It has three operations: project (keep chosen fields of a list or object), resolve (look up an ordered list of ids in an earlier view) and files (list the project's files). Each named input says where it comes from: the previous step, a step by number, the accumulated output or the project files. The lean promo uses it to give later agents a smaller view of the component inventory.

What to put in the brief: Nothing in the brief.

Typical mistakes: Using Previous when the data comes from a step further back: name the step by number. A path that matches nothing fails the step.

<!-- step-card:begin Extract -->
Generated from the step catalog; do not edit by hand.

- Extract step. Picks and reshapes parts of an earlier step's output without calling a model.
- Cost: No credits.
- Needs: An earlier step with output to project
- Produces: The projected fields as JSON
- Read by: Any later step
- Pairs with Agent: feeds a smaller, cleaner input
<!-- step-card:end Extract -->

## VideoAnalyze

A VideoAnalyze step reads footage: it splits it into shots and sub-shots and measures the picture; transcription and vision run only when a provider is configured and turned on. The cutting steps read its analysis. As the first step of a workflow its source must be a project file, not the previous step, because nothing comes before it.

What to put in the brief: sourceLanguage tells transcription what the footage speaks. Choose the clips in the run, not in the notes.

Typical mistakes: Source set to the previous step in a first step, so the run cannot find a video. Transcription marked required with no provider. Region effects need the permission to send the clip to the vendor.

<!-- step-card:begin VideoAnalyze -->
Generated from the step catalog; do not edit by hand.

- VideoAnalyze step. Reads video footage: splits it into shots and measures the picture; speech transcription and vision use a provider only when one is configured and turned on. Optionally finds the moments inside each shot (things appearing, moving, text, camera moves, beats) for sound and graphics to anchor to.
- Cost: No credits.
- Needs: a provider for the capability, chosen on the step or set as the organization default (check WL006)
- Needs: project files of the role the step reads (check WL010)
- Needs: the permission to send media, given for the run on the app page (the field "Allow sending your media to AI services") (check WL015)
- Needs: Video footage in the project, or a video from an earlier step
- Needs: Optional: a transcription or vision provider, used when configured
- Produces: A footage analysis the cutting steps read
- Read by: EditRoom, GraphicsRoom, ColorGradeRoom, DailiesRoom, VideoCompile, Editor agents
- Reads from the run brief: sourceLanguage
- Pairs with EditRoom: reads the analysis to decide the cut
- Pairs with VideoCompile: resolves the cut against the analysis
- Common mistake WL006 provider-missing-for-capability, fix: Connect or choose a provider
- Common mistake WL010 source-media-missing, fix: Upload the missing media to the project
- Common mistake WL015 egress-permission-missing, fix: Fill in the app field the message names, on the run page
<!-- step-card:end VideoAnalyze -->

## VideoCompile

<!-- step-card:begin VideoCompile -->
Generated from the step catalog; do not edit by hand.

- VideoCompile step. Cuts and assembles the final film from an editing decision and the footage.
- Cost: No credits.
- Needs: an earlier Voiceover step at the named order (check WL002)
- Needs: earlier steps of the right kind for each input (check WL004)
- Needs: Mode = Reencode when adding picture or sound (check WL005)
- Needs: project files of the role the step reads (check WL010)
- Needs: EnableVoiceover with a Voiceover step, or transcribed speech (check WL019)
- Needs: source sound muted when the film already speaks (check WL021)
- Needs: censor labels on feature tracks (check WL022)
- Needs: the picture or video the overlay names, usable (check WL023)
- Needs: a start and an end inside the film (check WL024)
- Needs: a card under a quarter of the film or 30 seconds (check WL025)
- Needs: An editing decision from an earlier step
- Needs: A VideoAnalyze step before it
- Needs: Pictures or videos to show
- Produces: The finished video file
- Read by: Any later step, The project's files
- Reads from the run brief: designSystem, format, lengthSec, overlayCardAudio, pace, resolution, stylePreset, tone
- Pairs with ThumbnailGenerate: picks a cover frame of the finished film
- Pairs with ExtractFrame: grabs a still of the finished film
- Common mistake WL002 compile-voiceover-unwired, fix: Point the reference at the right earlier step
- Common mistake WL004 compile-ref-invalid, fix: Point the reference at the right earlier step
- Common mistake WL005 compile-mode-conflict, fix: Change the step setting the message names
- Common mistake WL010 source-media-missing, fix: Upload the missing media to the project
- Common mistake WL019 captions-without-text-source, fix: Change the step setting the message names
- Common mistake WL021 voiceover-over-narrated-film, fix: Change the step setting the message names
- Common mistake WL022 censor-needs-feature-track, fix: Change the step setting the message names
- Common mistake WL023 media-overlay-source-missing, fix: Point the reference at the right earlier step
- Common mistake WL024 media-overlay-span-invalid, fix: Change the step setting the message names
- Common mistake WL025 media-overlay-card-too-long, fix: Change the step setting the message names
<!-- step-card:end VideoCompile -->

## EditRoom

An EditRoom step convenes several editor agents and a director who discuss the footage analysis and settle one editing decision, which the compile then cuts from. It is slower and uses more tokens than the single story editor.

What to put in the brief: length, shape, tone and style. Ask for it when quality matters more than speed.

Typical mistakes: Using it on a plan without rooms, or before any analysis step.

<!-- step-card:begin EditRoom -->
Generated from the step catalog; do not edit by hand.

- EditRoom step. Has several editor agents and a director discuss the footage, then settle one editing decision.
- Cost: Uses credits.
- Needs: a plan that includes the feature or resolution (check WL007)
- Needs: A footage analysis from an earlier VideoAnalyze step
- Produces: One editing decision the compile can cut from (contract VideoEditDecisionOutput)
- Read by: VideoCompile
- Reads from the run brief: format, language, lengthSec, resolution, stylePreset, tone
- Pairs with VideoCompile: reads the decision as its cut
- Common mistake WL007 plan-not-entitled, fix: Choose a different plan, resolution or step
<!-- step-card:end EditRoom -->

## GraphicsRoom

A GraphicsRoom step has several motion-graphics planners and a director agree where titles, name tags and callouts go, using the footage analysis. The compile places the plan.

What to put in the brief: Put the names and key points to show in the notes. The room places them; it does not invent them.

Typical mistakes: A compile that does not have graphics switched on, so the plan is never drawn.

<!-- step-card:begin GraphicsRoom -->
Generated from the step catalog; do not edit by hand.

- GraphicsRoom step. Has several motion-graphics planners and a director agree where on-screen graphics go.
- Cost: Uses credits.
- Needs: a plan that includes the feature or resolution (check WL007)
- Needs: A footage analysis from an earlier VideoAnalyze step
- Produces: One graphics plan the compile can place (contract MotionGraphicsPlanOutput)
- Read by: VideoCompile
- Reads from the run brief: format, language, resolution, stylePreset, tone
- Pairs with VideoCompile: reads the plan as its graphics
- Common mistake WL007 plan-not-entitled, fix: Choose a different plan, resolution or step
<!-- step-card:end GraphicsRoom -->

## ColorGradeRoom

A ColorGradeRoom step has several colourist agents and a director agree one consistent grade for each shot from what the footage looks like. The compile applies it.

What to put in the brief: Describe the look (warm, cool, filmic, muted) in the notes or choose a cinematic style.

Typical mistakes: Expecting it to fix one badly exposed clip; it sets a consistent look across the film.

<!-- step-card:begin ColorGradeRoom -->
Generated from the step catalog; do not edit by hand.

- ColorGradeRoom step. Has several colourist agents and a director agree the colour grade for each shot.
- Cost: Uses credits.
- Needs: a plan that includes the feature or resolution (check WL007)
- Needs: A footage analysis from an earlier VideoAnalyze step
- Produces: One colour grade plan the compile can apply (contract ColorGradePlanOutput)
- Read by: VideoCompile
- Pairs with VideoCompile: reads the plan as its colour grade
- Common mistake WL007 plan-not-entitled, fix: Choose a different plan, resolution or step
<!-- step-card:end ColorGradeRoom -->

## VideoGenerate

A VideoGenerate step creates new video shots from prompts through a video provider and returns several takes per shot. It is paid beyond tokens and capped per run. The prompts come inline, from a project file or from an earlier planner such as the shot director.

What to put in the brief: Describe the shots in the notes. Tell the person that generation is paid before the card appears.

Typical mistakes: No provider connected, a plan without generation, or the step inside a review loop so the spend repeats.

<!-- step-card:begin VideoGenerate -->
Generated from the step catalog; do not edit by hand.

- VideoGenerate step. Generates new video shots from prompts through a video provider.
- Cost: Uses credits and AI shots.
- Needs: a provider for the capability, chosen on the step or set as the organization default (check WL006)
- Needs: a plan that includes the feature or resolution (check WL007)
- Needs: paid steps outside the repeat span of a review loop (check WL013)
- Needs: the permission to send media, given for the run on the app page (the field "Allow sending your media to AI services") (check WL015)
- Needs: an avatar with consent on record, in this project (check WL027)
- Needs: a model that takes pictures, to show an avatar (check WL034)
- Needs: A video-generation provider
- Needs: A shot plan: written inline, taken from a project file, or from an earlier step
- Needs: Optional: up to three avatars (people who consented or objects, one person at most) and the permission to send media
- Produces: The generated shots, several takes each
- Read by: DailiesRoom, VideoAnalyze, VideoCompile
- Reads from the run brief: avatar, format, resolution, stylePreset
- Pairs with DailiesRoom: chooses between the takes
- Pairs with VideoCompile: places the chosen take
- Common mistake WL006 provider-missing-for-capability, fix: Connect or choose a provider
- Common mistake WL007 plan-not-entitled, fix: Choose a different plan, resolution or step
- Common mistake WL013 paid-step-in-loop, fix: Change the step setting the message names
- Common mistake WL015 egress-permission-missing, fix: Fill in the app field the message names, on the run page
- Common mistake WL027 avatar-unusable, fix: Change the step setting the message names
- Common mistake WL034 subject-model-unsupported, fix: Choose another model
<!-- step-card:end VideoGenerate -->

## Voiceover

A Voiceover step speaks lines with a chosen voice. Its source names the earlier writer step it reads: the Scriptwriter's scene lines, a narration writer's plan or a pickup planner's plan. The voice speaks what it is given, so pick one that speaks the film's language. A project voice needs consent on record.

What to put in the brief: voice narrated, plus the project voice when the person wants their own; language so the writing agents write in it.

Typical mistakes: A source that points at a step that does not exist, is later, or is the wrong kind. A compile that does not switch narration on. A voice that does not speak the language.

<!-- step-card:begin Voiceover -->
Generated from the step catalog; do not edit by hand.

- Voiceover step. Speaks the lines an earlier writer step produced, one clip per line, with the chosen voice.
- Cost: Uses credits and a voice.
- Needs: when the lines are not written inline: an earlier Scriptwriter, NarrationWriter or PickupPlanner step, named by the Voiceover source (check WL001)
- Needs: a provider for the capability, chosen on the step or set as the organization default (check WL006)
- Needs: when a project voice is chosen: that voice with live consent (check WL008)
- Needs: a voice that speaks the film's language (check WL009)
- Needs: paid steps outside the repeat span of a review loop (check WL013)
- Needs: a Voiceover wired to the step that writes the lines, unless the user supplied the words exactly (check WL026)
- Needs: Lines to speak: written inline, or from an earlier writer step
- Needs: A voice (a project voice also needs live consent)
- Produces: One narration audio clip per line, with timings
- Read by: VideoCompile
- Reads from the run brief: language, voice
- Pairs with VideoCompile: mixes the narration in when its voiceover switch is on
- Common mistake WL001 voiceover-source-missing, fix: Point the reference at the right earlier step
- Common mistake WL006 provider-missing-for-capability, fix: Connect or choose a provider
- Common mistake WL008 voice-consent-missing, fix: Pick a usable voice
- Common mistake WL009 voice-language-mismatch, fix: Pick a usable voice
- Common mistake WL013 paid-step-in-loop, fix: Change the step setting the message names
- Common mistake WL026 voiceover-ignores-script, fix: Change the step setting the message names
<!-- step-card:end Voiceover -->

## ThumbnailGenerate

A ThumbnailGenerate step picks one frame of the project's video as a cover image. It uses no model.

What to put in the brief: Nothing in the brief.

Typical mistakes: Running it before the video exists.

<!-- step-card:begin ThumbnailGenerate -->
Generated from the step catalog; do not edit by hand.

- ThumbnailGenerate step. Chooses one frame of the project's video as a cover image.
- Cost: No credits.
- Needs: A project video, or a footage analysis
- Produces: A cover image
- Read by: The project's files
<!-- step-card:end ThumbnailGenerate -->

## Revise

A Revise step revises a finished film from a change request. Only the project's hidden revision workflow runs it; it is never part of a recipe and never authored.

What to put in the brief: Do not propose it. Send revision requests through a composition revision or a new run.

Typical mistakes: Trying to add it to a custom graph.

<!-- step-card:begin Revise -->
Generated from the step catalog; do not edit by hand.

- Revise step. Revises a finished film from a change request. Run only by the project's hidden revision workflow, never authored.
- Cost: Uses credits.
- Needs: A finished film and a change request
- Produces: The revised film version (contract CompositionRevisionOutput)
- Read by: The request that asked for it
<!-- step-card:end Revise -->

## DesignGraphic

A DesignGraphic step designs one still graphic for a picture. Only the project's hidden graphic workflow runs it; it is never authored.

What to put in the brief: Do not propose it. A cover image is made in the picture editor, not by a recipe.

Typical mistakes: Offering it for a request like a cover image or an Instagram carousel.

<!-- step-card:begin DesignGraphic -->
Generated from the step catalog; do not edit by hand.

- DesignGraphic step. Designs one still graphic for a picture. Run only by the project's hidden graphic workflow, never authored.
- Cost: Uses credits.
- Needs: A picture document and a design request
- Produces: A transparent still graphic (contract GraphicDesignOutput)
- Read by: The picture document
<!-- step-card:end DesignGraphic -->

## DailiesRoom

A DailiesRoom step has several selector agents and a director choose the best take of each generated shot. It follows a generation step that made two takes and an analysis step that measured them; the compile then places only the chosen takes.

What to put in the brief: Describe the shots in the notes; the room judges, it does not write.

Typical mistakes: Used without the analysis of the takes, or without a generation provider.

<!-- step-card:begin DailiesRoom -->
Generated from the step catalog; do not edit by hand.

- DailiesRoom step. Has several selector agents and a director choose the best take of each generated shot.
- Cost: Uses credits.
- Needs: Generated takes from an earlier VideoGenerate step, analysed by VideoAnalyze
- Produces: One take selection the compile can use (contract TakeSelectionOutput)
- Read by: VideoCompile
- Pairs with VideoCompile: places only the chosen take of each shot
<!-- step-card:end DailiesRoom -->

## TimelineRender

A TimelineRender step renders a saved editor timeline at a fixed revision to a delivery format. It belongs to the editor flow, not to the recipes.

What to put in the brief: Nothing in the brief.

Typical mistakes: Using it without a saved timeline.

<!-- step-card:begin TimelineRender -->
Generated from the step catalog; do not edit by hand.

- TimelineRender step. Renders an editor timeline at a fixed revision to a delivery format.
- Cost: No credits.
- Needs: A saved editor timeline
- Produces: The rendered delivery video file
- Read by: The project's files
<!-- step-card:end TimelineRender -->

## DesignSystem

A DesignSystem step has three jobs: parse the project's design files into a draft, compose a profile from them and the analyzers, or require that an approved design exists before the rest runs. The -ds promo recipes begin with a Require step, so they stop at once when nothing is approved.

What to put in the brief: designSystem project (default) or none for a deliberately unbranded film.

Typical mistakes: Running a recipe that requires a profile before anyone approved one. Treating a draft as approved.

<!-- step-card:begin DesignSystem -->
Generated from the step catalog; do not edit by hand.

- DesignSystem step. Builds the run's design system from the project's design sources, or requires the approved one.
- Cost: No credits.
- Needs: an approved design system version on the project (check WL012)
- Needs: Design sources in the project, or an approved design system
- Produces: The run's design system
- Read by: Agents and VideoCompile that read the design
- Reads from the run brief: designSystem
- Pairs with Agent: the authoring agents read the design
- Common mistake WL012 design-system-missing, fix: Approve the project's design system
<!-- step-card:end DesignSystem -->

## ExtractFrame

An ExtractFrame step grabs one frame of a video (first, last or at a time) as an image, optionally saved as a project file. A later generation step can start from it.

What to put in the brief: Nothing in the brief.

Typical mistakes: Asking for a frame of a video that comes later in the workflow.

<!-- step-card:begin ExtractFrame -->
Generated from the step catalog; do not edit by hand.

- ExtractFrame step. Grabs one frame of a video, the first or the last or at a time, as an image.
- Cost: No credits.
- Needs: A video from the project or an earlier step
- Produces: An image, optionally saved as a project file
- Read by: Later steps, The project's files
- Pairs with VideoGenerate: the frame can start the next shot
<!-- step-card:end ExtractFrame -->

## AudioGenerate

An AudioGenerate step generates music tracks or sound effects through a provider and saves each take as a project audio file. It is paid beyond tokens. The footage recipes use uploaded files instead; use this only in a custom graph with a provider.

What to put in the brief: Describe the sound in the notes.

Typical mistakes: No music or sound provider, or a plan without audio generation.

<!-- step-card:begin AudioGenerate -->
Generated from the step catalog; do not edit by hand.

- AudioGenerate step. Generates music tracks or sound effects through a provider and saves each take as a project audio file.
- Cost: Uses credits and AI shots.
- Needs: a provider for the capability, chosen on the step or set as the organization default (check WL006)
- Needs: a plan that includes the feature or resolution (check WL007)
- Needs: A music or sound-effects provider
- Produces: Generated audio tracks saved as project files
- Read by: VideoCompile, The project's files
- Reads from the run brief: format, resolution, stylePreset
- Pairs with VideoCompile: mixes the track in
- Common mistake WL006 provider-missing-for-capability, fix: Connect or choose a provider
- Common mistake WL007 plan-not-entitled, fix: Choose a different plan, resolution or step
<!-- step-card:end AudioGenerate -->

## ImageGenerate

An ImageGenerate step makes pictures from a written prompt, or changes one existing picture by an instruction, with an AI image model. It is paid beyond tokens: every picture is priced and reserved on its own. Editing sends the source picture to a hosted AI service, so the step needs the author's consent (`allowSourceMediaEgress`) or it refuses and sends nothing.

What to put in the brief: Describe the picture, or for an edit name the source picture and say what to change.

Typical mistakes: No image provider, a plan without the feature, or an edit without consent to send the picture.

<!-- step-card:begin ImageGenerate -->
Generated from the step catalog; do not edit by hand.

- ImageGenerate step. Generates pictures from a prompt, or edits one picture by an instruction, with an AI image model.
- Cost: Uses credits and AI shots.
- Needs: a provider for the capability, chosen on the step or set as the organization default (check WL006)
- Needs: the permission to send media, given for the run on the app page (the field "Allow sending your media to AI services") (check WL015)
- Needs: A prompt, and for Edit a source picture from the project or an earlier step (or the run's picture files)
- Needs: Optionally a list of items; {item} in the prompt is replaced by each
- Produces: One to four pictures (or edited versions of one picture), or with a list of items (one per colour, say) a whole set of up to 24, one per item, optionally saved as project image files
- Read by: Later steps, The project's files
- Reads from the run brief: format, resolution, stylePreset
- Pairs with VideoGenerate: a generated picture can be the first frame
- Pairs with ExtractFrame: edit a frame it grabbed
- Common mistake WL006 provider-missing-for-capability, fix: Connect or choose a provider
- Common mistake WL015 egress-permission-missing, fix: Fill in the app field the message names, on the run page
<!-- step-card:end ImageGenerate -->

## PinInsert

A PinInsert step pins content onto the screens tracked in the footage and plans the inserts with no model; the compile then draws them. The green-screen recipe uses the same idea.

What to put in the brief: Name the content to insert in the notes.

Typical mistakes: No tracked screens in the analysis, or no content chosen for the insert.

<!-- step-card:begin PinInsert -->
Generated from the step catalog; do not edit by hand.

- PinInsert step. Pins content onto the screens tracked in the footage and plans the inserts, without a model.
- Cost: No credits.
- Needs: A footage analysis with tracked screens from an earlier VideoAnalyze step
- Produces: A graphics plan with screen inserts only (contract MotionGraphicsPlanOutput)
- Read by: VideoCompile
- Pairs with VideoCompile: reads the plan as its graphics
<!-- step-card:end PinInsert -->

## AudioCleanup

An AudioCleanup step cleans the sound of one audio file or video and saves the result as a project file. Hum, wind, hiss, echo and a basic voice pass run on the server at no cost; isolating the voice and removing the music use an outside AI service, are paid per audio minute, and need the customer's permission to send the audio.

What to put in the brief: Say what is wrong with the sound (hum, wind, hiss, echo, music under the voice) and which clip.

Typical mistakes: Choosing an AI target without the footage-sharing permission, or on a plan without it, or naming a source that comes later in the workflow.

<!-- step-card:begin AudioCleanup -->
Generated from the step catalog; do not edit by hand.

- AudioCleanup step. Cleans the audio of one file or video: hum, wind, hiss and echo for free, or AI voice isolation and music removal.
- Cost: Uses credits.
- Needs: a provider for the capability, chosen on the step or set as the organization default (check WL006)
- Needs: the permission to send media, given for the run on the app page (the field "Allow sending your media to AI services") (check WL015)
- Needs: An audio file or video from the project or an earlier step
- Produces: A cleaned audio file, or the video with its audio cleaned, optionally saved as a project file
- Read by: Later steps, The project's files
- Pairs with VideoAnalyze: the cleaned file can be analysed or transcribed
- Pairs with VideoCompile: the cleaned video can be the source
- Common mistake WL006 provider-missing-for-capability, fix: Connect or choose a provider
- Common mistake WL015 egress-permission-missing, fix: Fill in the app field the message names, on the run page
<!-- step-card:end AudioCleanup -->

## VideoEnhance

A VideoEnhance step makes one video sharper, smoother, cleaner or softer in motion and keeps its own sound. The free local operations run on this server; the AI upscale and the AI smooth slow motion use credits and need the customer's permission to send the video to an outside service.

What to put in the brief: Say which clip to improve and what is wrong with it (too small, choppy, grainy).

Typical mistakes: Choosing an AI operation without the permission, a source longer than the limit, or a target size above the plan's.

<!-- step-card:begin VideoEnhance -->
Generated from the step catalog; do not edit by hand.

- VideoEnhance step. Enhances one video: upscale, smooth slow motion, denoise, motion blur or extend past the border. The local operations are free; the AI ones use credits and need consent.
- Cost: Uses credits.
- Needs: the permission to send media, given for the run on the app page (the field "Allow sending your media to AI services") (check WL015)
- Needs: One video from the project or an earlier step
- Produces: An enhanced MP4 of the same length with the source's own audio, optionally saved as a project file
- Read by: Later steps, The project's files
- Pairs with VideoAnalyze: the enhanced video can be analysed
- Pairs with VideoCompile: the enhanced video can be the source
- Common mistake WL015 egress-permission-missing, fix: Fill in the app field the message names, on the run page
<!-- step-card:end VideoEnhance -->
