---
title: From a goal to a recipe
kb:
  kind: concept
---

# From a goal to a recipe

Internal background for the assistant. Worked decisions distilled from the 50-goal evaluation set. The order of preference is always: reuse a workflow the project already has from the fitting recipe; create from the fitting recipe; fit plus a known change such as adding a Voiceover step; a custom graph only when no recipe fits and every step exists; an honest decline with the nearest real option; at most two questions when a required field is missing. Every section stands alone.

## Narrated explainer with the company design system

Goal: "Ci serve un video esplicativo di circa 60 secondi sul portale note spese, in italiano, 16:9, con una voce che racconta. Usa il nostro design system." Choose the profile promo (`quick-win-promo-ds`) because a design system is named and the project has an approved profile, so the code is not re-read. It is silent as shipped, so add narration (a Voiceover step after the Scriptwriter, review loop re-pointed). Brief: language it, format Landscape, lengthSec 60, designSystem project, voice narrated. Say the run uses a voice before the card. Without the Voiceover step the film is silent.

## Short vertical reel with a voice

Goal: "15 sec vertical reel about our timeline feature, with a voice pls". Choose the quick vertical reel (`cinematic-feature-spotlight`): it is the narrated vertical reel from code and already has its Voiceover step. Brief: language en, format Vertical, lengthSec 15, voice narrated, tone Energetic, designSystem none. Requires a voice provider; if none is connected, say so before the run. If the goal says no sound, use the plain code promo instead with voice none.

## Launch trailer

Goal: "We launch Monday. Need a proper launch trailer, around 45 seconds, landscape, dramatic." Choose the narrated launch trailer (`story-arc-launch-trailer`). Brief: language en, format Landscape, lengthSec 45, tone Inspirational, voice narrated. It needs a voice provider; if the project has none, tell the person first. For a calm explainer rather than a dramatic arc, prefer the code promo with a Voiceover step added.

## Cheapest promo for a huge code base

Goal: "Quiero un video promo de mi app pero la versión más barata, mi código es enorme." Choose the lighter promo (`lean-context-promo`), which reads less code per step. Brief: language es, designSystem none, voice none. Do not quote a saving; say that the estimate shows the range. It is silent unless the voiceover patch is added.

## Silent promo for a social network or release notes

Goals: "30s square video for LinkedIn showing what our app does, no voiceover"; "Make a 30 second video for our release notes, the changelog is in the project". Choose the plain code promo (`quick-win-promo`). Brief: format Square, lengthSec 30, voice none, stylePreset corporate-explainer, tone Calm. The changelog goes in as a document file and as the notes. With an approved brand profile and a request for brand colours, use the profile promo instead.

## Promo with AI backgrounds

Goals: "vertical reel of our app w/ some cool animated ai backgrounds"; "Une vidéo promo avec nos couleurs de marque et des fonds animés générés par IA". With no approved profile, choose `quick-win-promo-plates`; with one and a request for brand colours, `quick-win-promo-plates-ds`. Generation is paid beyond tokens and capped per run, so state it in the lead-in before the card and point to the estimate for the amount. It needs a video-generation provider and a plan that includes generation.

## Narration on a plain code promo

Goals: "60 second promo for our app with a narrator, landscape"; "Explainer for our Blazor admin tool, about 90 seconds, landscape, calm voiceover"; "Preciso de um vídeo de onboarding mostrando o passo a passo do app". Choose the plain code promo plus a Voiceover step. Alternates that are also right: the quick vertical reel when the goal is short and punchy, the launch trailer when it is dramatic. Brief: voice narrated, tone Calm, stylePreset corporate-explainer for an explainer, format Landscape. Without the patch the film is mute.

## The brand profile is missing, a draft or unapproved

Goals: "Haz el video con nuestro design system" with no profile; "Faz um vídeo do nosso app com o perfil de marca" with a draft. The profile promo stops at its first step when nothing is approved. If a draft exists, offer approval, or fall back to the plain code promo, which ignores the brand. If no profile exists at all, ask first whether to build it from the code and design files (`product-profile-from-code`), because building is a separate step the person must approve; add one question about length and shape. "Build our brand profile from the code and design files" is that recipe.

## The same video in another language

Goal: "Abbiamo già il video dell'app in inglese, rifallo in italiano." There is no localise recipe for a code video. Run the profile promo again with language it so the scenes, narration and captions are rewritten in Italian. The writing agents follow the brief language; if the project has no profile, run the plain code promo with the same brief. For footage in another language use the dub recipe.

## Cutting footage

Goals: "cut my interview down to the best 2 minutes"; "add name tags and the key points on screen, 6 clips from our panel"; "Aus den Interview-Clips ein 45-Sekunden-Testimonial mit Namenseinblendung". Plain cut: the highlight edit. Names and callouts: the edit with titles. A considered cut of precious footage ("take your time", a wedding): the edit decided by an editing team, with the plain edit as the cheaper valid alternative. One consistent colour look: the colour grade edit. Always set lengthSec when a length is stated and put names to show in the notes.

## Music, sound effects and photos

Goals: "Highlight edit of the event footage with the music track I uploaded under it, 90s"; "make the footage edit more upbeat, add some music"; "Subí mis whooshes y clics". Use the music or the sound effects edit. There is no built-in music or sound library: if the files are missing, ask for the upload before running. Titles plus music in one film: the music edit plus the titles step, because the uploaded track is the harder requirement. A slideshow of photos: stills become clips, so the music edit with images and the track works.

## Narration, dub, pickups and captions on footage

Goals: "Coupe mes rushs puis ajoute une voix off et des sous-titres, 1 minute 30": narration edit. A demo of screen recordings with captions: narration edit, or the narrated story when the story is written first. "Dub my product walkthrough into Spanish with subtitles": the dub, language es, sourceLanguage en. "Fix the bits where I stumble using my own voice": pickups, which need a consented project voice from the Voices tab. Dubbing into a language the voice may not speak: warn and check. Colour plus a voice-over: the colour grade edit plus the narration steps.

## AI-generated shots

Goals: "I'd like a short AI-made opening shot and a closing card"; "need an ai generated shot of a city at night for my intro"; "Voglio delle inquadrature create con l'AI". Choose the b-roll edit. Generation is paid and capped: disclose before the card. It needs a video-generation provider and a plan with generation; report either gap before running. A quality check on every shot: the dailies edit.

## Hiding an object and the green screen

Goals: "the license plates in my dashcam video need to be hidden the whole time"; "Floute le visage de la personne à gauche dans toute la vidéo": the region effect recipe. The person must enable sending the clip to the tracking service, a separate permission that is off; the assistant proposes it only through an approval card, never silently. The tracking is billed, so state it and point to the estimate. "Put the screen recording of our dashboard on the laptop in my office clip": the green-screen insert; no model is used.

## Honest declines

Say in one sentence what ReelBolt does not do, then offer the nearest real option; never improvise a graph. A talking avatar presenter or lip sync: only where avatars are enabled (ListProjectAvatars answers), Talking avatar (budget) makes a project avatar say a short script of 5 to 15 seconds and Spokesperson clip shows an avatar without exact words; the person picks the avatar in the Avatar field of the app page and switches on Allow sending your media to the video service on the video step. A longer presenter video, or avatars not enabled: not available; offer the narrated story or a code promo with a voiceover. An Instagram carousel: ReelBolt makes video, not multi-image posts; offer a vertical reel. A cover image: no recipe makes one; the picture editor in the project does. A landscape version of a finished video: no recipe reformats a finished film; format is chosen per run, so re-run the original recipe with the new format in the brief.

## Subtitles only

Goal: "just add subtitles to my finished video, nothing else". Choose the subtitles recipe (`video-subtitles`): it listens to the video, burns the words in and also writes SRT and VTT files, with no cut, no new voice and no editor agent. For subtitles in another language ("subtitles in Spanish", "Untertitel auf Englisch"), choose `video-subtitles-translated`, which adds one translation step and keeps the original timing. Brief: sourceLanguage for the spoken language, and for the translated one language as the subtitle language. Both need a speech-to-text provider; the translated one also needs a chat AI provider. Do not add a captions switch to a cut recipe: captions there come from narration. The run page has no download button for the SRT and VTT files yet; say they are listed in the compile step result.

## Mixing code and footage

Goal: "Mix real screens from our code with my demo footage in one trailer." No recipe takes both source code and clips, so this is a custom graph: say why no recipe fits, check every step exists in the step catalog, validate the steps (and fix any error reported) before proposing it. The narrated story takes clips and may also name code files, but it does not draw screens from code; offer it as the simpler alternative.

## When to ask first

Ask only for a required field that neither the message nor the project data supplies, at most two questions. "make me a video" and "Need something for the launch": ask who it is for and how long, and whether there is code or footage. "Fammi un video per Instagram": the shape is known (Vertical, Italian), so ask what source there is and how long. "The same ad for Instagram, YouTube and LinkedIn": ask the message and length, and confirm three runs because a run has one format. Never ask what the project data answers, and never ask about fields with sensible defaults.
