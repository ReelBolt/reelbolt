---
title: Project readiness
kb:
  kind: how-to
---

# Project readiness

Internal background for the assistant. What a project needs before each kind of recipe can run, and how to fix a gap. Look at what the project has before asking the person anything; the workflow checks report the same gaps with a plain message and often a fix. Every section stands alone.

## Source code

The code recipes (plain, lighter, with AI backgrounds, quick vertical reel, launch trailer) and the profile builder read source code. In the project this means an uploaded archive, source files, text files or files synced from a code host. Without any, the run is empty, and the linter reports missing source media as an error. Fix: ask the person to upload the code or connect the sync. The profile promos do not read code at run time; they need an approved profile instead.

## Video clips

Every footage recipe, the region effect and the green-screen insert need video clips in the project, and the run chooses which. Photos can stand in for clips in the music edit because stills are turned into clips. Fix when none: upload the clips. The run page lets the person choose among the project's clips; the assistant names files by name and never shows ids.

## Music tracks and sound effects

The music edit needs an uploaded music track and the sound effects edit needs uploaded sound effect files. There is no built-in library of either. Audio files count as both. Missing media is reported as a warning, because a film is still made without it, but the person asked for it, so ask for the upload before running. Generated music is a separate paid step that needs its own provider and is not part of these recipes.

## A voice provider and a usable voice

The narrated recipes (quick vertical reel, launch trailer, narration and captions, dub, narrated story, pickups, and any recipe given a Voiceover step) need a voice provider connected for the organization or chosen on the step. Narration is paid beyond tokens: say so before the card. Fix a missing provider by connecting one in the provider settings. A project voice needs consent on record; the pickups recipe needs the person's own consented voice, set up in the project's Voices tab. A voice that does not speak the film's language produces a warning: choose a voice that does.

## A video-generation provider and plan entitlement

The AI backgrounds recipes, the b-roll edit and the dailies edit need a video-generation provider and a plan that includes video generation. Rooms (editing team, graphics team, colour grade, dailies) need a plan that includes rooms. A resolution above what the plan renders is also refused. Fix: connect or choose a provider, pick another recipe or resolution, or change the plan. The workflow checks report all of these before a run starts, where they are on.

## An approved design and product profile

The profile promos begin with a step that requires an approved profile, and stop at once without one. A draft is not approved. Fix: approve the draft in the project's design system, or build the profile from the code and design files with the profile builder and then approve it. A request that names the design system with nothing built yet needs a question, not a guess.

## Transcription and the tracking permission

Footage recipes read speech more accurately with a transcription provider; the pickups recipe and both subtitles recipes require one (the run stops at the first step and says so without it), and the translated subtitles recipe also needs a chat AI provider for the translation. The workflow checks report a missing provider. The region effect and any step that sends source media to a vendor need the permission to do so switched on in the first step; it is off by default. Sending source media to a vendor is something the person enables, never something to switch on silently: the assistant only proposes it through an approval card after saying what leaves the server. Fix: connect a transcription provider, or propose the permission and let the person approve it.

## Checking in one pass

Order of checks before proposing: files by kind, design system status, voices and consent, configured providers by capability, plan limits, and existing workflows with the recipe each came from. If a workflow from the fitting recipe already exists, run it rather than creating another. Where the workflow checks are available, fix every error they report before showing the person a card, and mention warnings in one line. The estimate gives the range and the paid items.
