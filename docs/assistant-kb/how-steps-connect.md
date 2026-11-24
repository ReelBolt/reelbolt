---
title: How steps connect
kb:
  kind: concept
---

# How steps connect

Internal background for the assistant. How the work of one step reaches the next, and what breaks when a connection is wrong. Step numbers count from one in the order the steps run. Every section stands alone.

## Context modes: what an agent step sees

An agent step receives earlier work according to its input context mode. FullWorkflow gives the agent everything accumulated so far, which is the widest and the most expensive. PreviousStepOnly gives only the output of the step just before. SelectedPriorSteps gives only the steps named by number. CustomMappedSubset builds a custom subset from a mapping. The footage recipes use PreviousStepOnly for the story editor, because the analysis is what it needs, and FullWorkflow for planners that need both the analysis and the editor's decision. The code analyzers chain: the first reads the workflow, each next one reads the previous. When an agent produces a poor answer, first check that its context mode includes the step that holds the data it needs. A mode that is too narrow starves the agent; a mode that is too wide costs more and can bury the data.

## Extract references and why Previous is a trap

Many steps take a reference to an earlier output instead of reading it implicitly. A reference says where the data comes from: the previous step, a step by number, the accumulated output, or the project files. Previous means the most recent completed step at the moment the step runs, not the step the author had in mind. Insert a step between two steps and a Previous reference now points at the new step. The catalog's footage recipes avoid this by naming the source step by number wherever a step sits between the producer and the reader, for example a compile that reads the story editor's decision at step 2 while a graphics planner sits at step 3. Rule: use Previous only when the producer is directly before; otherwise name the step by number. A reference to a later step, or to a step of the wrong kind, is reported by the linter as an invalid or forward reference and fails the run.

## Review loops

A review loop is one step that targets an earlier step by number. A reviewer scores the target's work against the brief (length, language, shape, tone, style) and sends it back until the score passes or the rounds run out. The promo recipes review the Author (the step just before the loop); the footage recipes review the story editor at step 2. The target is a number, so adding or removing a step before it silently moves what is reviewed. After any insertion, recheck every target. A loop whose target is missing, later than the loop, or not written by a model never loops; the linter calls this a review loop target error. A voice or generation step inside the repeated span pays again on every round, which the linter warns about.

## Voiceover to compile wiring

Narration is three connected parts. A writer step produces the lines: the Scriptwriter in the code recipes, the narration writer in the footage recipes, the pickup planner for re-recorded lines. A Voiceover step names that writer by number as its source and speaks the lines. The final VideoCompile switches narration on and names the Voiceover step by number. If any link is missing, the film is silent or the run fails: a Voiceover that names a step that does not exist or is later, or a compile that switches narration on with no Voiceover at that number. Burned captions on the final cut come from the text the Voiceover step spoke, so they need the same link and follow the narration language. The subtitles recipes instead take caption text from the transcript of the footage (the compile's caption source is the transcript) and also write SRT and VTT files; the translated one points the compile at a translation step, so the timing stays the original's. The promo recipes' on-screen text and captions are written by the Scriptwriter per scene and need no Voiceover. In the dub, the Voiceover reads the translator's plan, not the writer's. A recipe that ships silent becomes narrated by inserting a Voiceover step after the writer and before the step that times the shots, then re-pointing the review loop.

## The brief, style and design blocks

Agents receive more than the previous step's output. The platform adds the style the run was started with (preset, saved or one-off, and the tone) and the project's pinned design system with the product profile, when there is one. The run brief fields are stored with the run and checked before it starts; each step type reads only some of them, and the step card lists which. A field no step reads is ignored, and the linter reports it as informational. The free notes are read as the person's request, but where notes contradict a field, the field wins. Deterministic steps such as the compile take the length, shape and resolution from the run over their own settings. Language reaches the writing agents and the Voiceover step; it does not reach analyzers, so an analysis is reused across languages.

## Canonical graph 1: the silent code promo

Used by the plain, lighter and profile promos. Text drawing, plain code promo: 1 code structure, 2 UI dependencies, 3 components, 4 routes and API, 5 style tokens (each reads the one before) then 6 scene translation, 7 animation strategy, 8 Scriptwriter, 9 Director, 10 Author, 11 review loop targeting 10. The profile promo replaces steps 1 to 5 with a single Require step: 1 require approved profile, 2 scene translation, 3 animation strategy, 4 Scriptwriter, 5 Director, 6 Author, 7 review loop targeting 6. Both are silent until a Voiceover step is added.

## Canonical graph 2: the narrated code reel or trailer

Text drawing, quick vertical reel: 1 code structure, 2 components, 3 style tokens, 4 scene translation, 5 animation strategy, 6 Scriptwriter, 7 Voiceover reading step 6, 8 Director, 9 Author, 10 review loop targeting 9. The launch trailer has the same shape: the Voiceover at 6 reads the Scriptwriter at 5. The Voiceover sits before the Director so the shots are timed to the spoken lines.

## Canonical graph 3: the highlight edit

Text drawing: 1 VideoAnalyze (source is a project file), 2 story editor reading step 1, 3 VideoCompile that reads decision 2 and analysis 1, 4 review loop targeting 2. Every footage variant inserts one planner between 2 and the compile: graphics planner, music supervisor, sound designer, or a room for the edit, graphics or colour grade. The compile then names the decision at step 2 explicitly because the previous step is no longer the editor.

## Canonical graph 4: footage with narration and captions

Text drawing: 1 VideoAnalyze, 2 story editor, 3 narration writer, 4 Voiceover reading step 3, 5 VideoCompile with narration on at step 4 and captions on, 6 review loop targeting 2. The dub inserts a translator: 3 narration writer, 4 narration translator, 5 Voiceover reading step 4, 6 compile replacing the original dialogue, 7 review loop. The narrated story writes first: 1 analyze, 2 narration writer, 3 story editor, 4 Voiceover reading step 2, 5 compile, 6 review loop targeting 2.

## Canonical graph 5: footage with generated shots

Text drawing: 1 VideoAnalyze, 2 story editor, 3 shot director, 4 VideoGenerate from the shot plan, 5 VideoCompile that places the generated clips, 6 review loop. The dailies version doubles the takes: 4 generate two takes per shot, 5 analyze the takes, 6 dailies room chooses one per shot, 7 compile with the chosen takes, 8 review loop. Generation is paid beyond tokens; state it before the card.

## Canonical graph 6: the two-step tools

Text drawing, region effect: 1 VideoAnalyze finds the object you named (a separate permission lets the clip go to the tracking service), 2 VideoCompile blurs it through the clip. Green-screen insert: 1 VideoAnalyze finds the green screen, 2 VideoCompile puts the content on it and keeps the clip whole. Neither has an editor agent or a review loop, so style and tone do nothing there. The profile builder is the other special graph: parse, then branch on what is missing, analyze only the gaps, then compose.
