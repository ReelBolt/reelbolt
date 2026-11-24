---
title: Recipes the assistant can choose from
kb:
  kind: recipe
---

# Recipes the assistant can choose from

Internal background for the assistant. Never quote this page to a customer; customer wording lives in the user guide recipe pages. One section per recipe in the workflow template catalog, in catalog order, with the real template key in the heading. Every section stands alone.

Rules that hold for every recipe: a recipe is picked by what the person wants and what the project has, never by its title alone. The brief carries language, length, shape, tone, voice and the files; anything else goes in the notes. No section states a price or a credit amount: the estimate for the workflow and brief gives the number, and the run card repeats it. Names, never ids, in anything shown to the person.

## Promo video from your code (`quick-win-promo`)

Goals in the person's words: "make a promo video of my app"; "30 s square video for LinkedIn showing what the app does, no voiceover"; "Une vidéo promo de notre application"; "Quiero un video promo de mi app".

Steps in order: five code analyzers (structure, UI dependencies, components, routes and API, style tokens), scene translation, animation strategy, Scriptwriter, Director, Author, then a review loop that reviews the Author (step 11 targets step 10).

Needs: The app's source code uploaded to the project (an archive or synced files). No voice provider, no design profile.

Put in the brief: language; lengthSec (15, 30 or 60 are the offered choices); format (Vertical, Square or Landscape); stylePreset; tone. Put who it is for and what to show in the notes.

Patches: Silent as shipped. When the person wants narration, the fix is to add a Voiceover step that reads the Scriptwriter step before the Director and to re-point the review loop to the Author. The on-screen text and captions of the scenes are written by the Scriptwriter either way.

Cost class: Uses credits (agent work only). The estimate gives the range.

Worked example 1: "Italian, 30 s square explainer for LinkedIn, no voice" gives the brief: language it; format Square; lengthSec 30; voice none; stylePreset corporate-explainer; notes: for finance managers, show the expense flow.

Worked example 2: "30 s release-notes video, the changelog is in the project" gives the brief: language en; lengthSec 30; voice none; sourceFiles code; notes: cover the highlights of the changelog (paste the relevant lines of the changelog into the notes).

Typical mistakes: Asking for a voice without adding the Voiceover step (the film comes out mute; the linter reports a silent output). Putting the language only in the notes so the film is English. Choosing this when an approved brand profile exists and the person wants brand colours: use the profile variant instead.

## Promo video from your code with AI backgrounds (`quick-win-promo-plates`)

Goals in the person's words: "vertical reel of our app with cool animated AI backgrounds"; "promo with generated moving backgrounds"; "video promo con fondos animados generados por IA".

Steps in order: the same eleven steps as the plain code promo, with a Generate background plates step (video generation) before the Author; the review loop is step 12 and targets step 11.

Needs: Source code in the project, a video-generation provider, and a plan that includes video generation.

Put in the brief: language; lengthSec; format; stylePreset; tone. Say the mood of the backgrounds in the notes.

Patches: Add narration the same way as for the plain code promo (a Voiceover step after the Scriptwriter). Nothing else is needed.

Cost class: Uses credits and AI shots. Generation is paid beyond tokens and capped per run; say so in the lead-in, before the card, and read the estimate for the amount.

Worked example 1: "Vertical reel with animated AI backgrounds, no voice" gives the brief: language en; format Vertical; voice none; stylePreset fast-social-reel; notes: abstract backgrounds, calm mood.

Worked example 2: "Promo with soft moving backgrounds, Spanish, 30 s" gives the brief: language es; lengthSec 30; notes: fondos abstractos suaves.

Typical mistakes: Not saying that generation is paid. Choosing it with no video-generation provider (the linter reports the missing provider). Asking for people or text in the backgrounds: the plate prompt forbids both.

## Promo video from your code (lighter) (`lean-context-promo`)

Goals in the person's words: "cheapest promo video, my codebase is huge"; "promo video for a large app that runs faster"; "versión más barata, mi código es enorme".

Steps in order: the five analyzers, an Extract step that reduces the component inventory to a lean view, then scene translation, animation strategy, Scriptwriter, Director, Author and the review loop (step 12 targets step 11). The later agents read less per step.

Needs: Source code in the project. No voice, no design profile.

Put in the brief: language; lengthSec; format; stylePreset; tone; notes with the audience and what to show.

Patches: Silent as shipped; add narration the same way as for the plain code promo.

Cost class: Uses credits, less than the plain code promo on a large project. The estimate shows the range.

Worked example 1: "Spanish promo, cheapest option, huge code base" gives the brief: language es; voice none; notes: versión ligera.

Worked example 2: "Fast reel of a monorepo app" gives the brief: language en; format Vertical; lengthSec 20; voice none.

Typical mistakes: Promising a fixed saving: the saving depends on the project. Choosing it for a small app where the plain code promo is just as cheap.

## Quick vertical reel (cinematic-feature-spotlight) (`cinematic-feature-spotlight`)

Goals in the person's words: "15 s vertical reel about our timeline feature, with a voice"; "short punchy feature spotlight for TikTok"; "un reel vertical de 15 segundos con voz"; "ein kurzer Hochformat-Clip mit Sprecher".

Steps in order: Code structure, component inventory, style tokens, scene translation, animation strategy, Scriptwriter (step 6), Voiceover reading the Scriptwriter (step 7), Director, Author, review loop (step 10 targets step 9).

Needs: Source code and a voice provider with a voice the project can use.

Put in the brief: language; lengthSec (short); format Vertical; tone; voice narrated. A reel, short, story or TikTok is Vertical.

Patches: Already narrated. Captions are not part of it as shipped.

Cost class: Uses credits and a voice. Narration is paid beyond tokens; read the estimate.

Worked example 1: "15 s vertical reel about the timeline, with a voice" gives the brief: language en; format Vertical; lengthSec 15; voice narrated; tone Energetic.

Worked example 2: "Reel de 20 s sobre la búsqueda, voz alegre" gives the brief: language es; format Vertical; lengthSec 20; voice narrated; tone Upbeat.

Typical mistakes: Choosing it for a calm 90 s explainer (it is short and punchy). Forgetting the language so the voice speaks English. Running with no voice provider connected.

## Narrated launch trailer (`story-arc-launch-trailer`)

Goals in the person's words: "a proper launch trailer, around 45 seconds, dramatic"; "trailer for our product launch"; "Trailer di lancio con voce narrante"; "Ein Launch-Trailer mit Erzähler".

Steps in order: structure map, routes and user journey, mood and tone tokens, scene translation, Scriptwriter (step 5), Voiceover (step 6), Director, animation strategy, Author, review loop (step 10 targets step 9).

Needs: Source code, a voice provider and a usable voice.

Put in the brief: language; lengthSec; format (usually Landscape); tone (Inspirational or Serious); voice narrated; notes with the launch message and audience.

Patches: Already narrated.

Cost class: Uses credits and a voice. Read the estimate; narration is paid beyond tokens.

Worked example 1: "Dramatic 45 s landscape launch trailer" gives the brief: language en; format Landscape; lengthSec 45; tone Inspirational; voice narrated.

Worked example 2: "Trailer di lancio, 60 secondi, voce narrante" gives the brief: language it; lengthSec 60; voice narrated; notes: lancio lunedì, rivolto ai responsabili HR.

Typical mistakes: Using it for a calm explainer. Starting a run before a voice provider exists. Leaving the message out of the notes so the story is generic.

## Highlight edit from footage (`video-derush-edit`)

Goals in the person's words: "cut my interview down to the best 2 minutes"; "remove the dead air and the false starts"; "monte mes rushs sans les temps morts"; "Schneide mein Interview auf die besten Stellen".

Steps in order: Analyze source video (with picture offers), the story editor decides which spans to keep, the picture planner chooses pictures and moments, a motion director that plans moves on the detected events, Compile edited video (plan), review loop (step 6 targets step 2), Render the final video.

Needs: Video clips in the project. Nothing else.

Put in the brief: sourceLanguage when the speech is not English; lengthSec; format; stylePreset. Say what to keep or drop in the notes.

Patches: None needed. Add music, graphics, colour, sound effects, narration or b-roll with the matching sibling recipe.

Cost class: Uses credits (the story editor and the reviewer). No paid voice or generation.

Worked example 1: "Interview cut to the best 2 minutes" gives the brief: lengthSec 120; sourceFiles video; notes: keep the answers about pricing.

Worked example 2: "Coupe ma présentation, 90 secondes" gives the brief: language fr; sourceLanguage fr; lengthSec 90.

Typical mistakes: Treating it as a reformat tool (it cuts, it does not resize a finished film). Forgetting to choose the clips. Asking for narration: it adds none.

## Testimonial reel from a talking-head clip and a screenshot (`testimonial-reel`)

Goals in the person's words: "show this testimonial screenshot while they talk"; "put my review on screen for a few seconds"; "a customer quote over the interview"; "montre l'avis client pendant la vidéo".

Steps in order: Analyze source video (with picture offers), the story editor decides which spans to keep, the picture planner chooses which picture and moment, a motion director that plans moves on the detected events, Compile edited video (plan), review loop (step 6 targets step 2), Render the final video.

Needs: A talking-head clip and at least one picture or video in the project. The picture slot is optional: with none, the planner chooses from the project's pictures and videos.

Put in the brief: sourceLanguage when the speech is not English; lengthSec; format; overlayCardAudio (let the agent choose, keep the film's sound, or mute it under a full-screen picture). Name the picture in the notes if you want a specific one, and say where it should appear.

Patches: None needed. Add music, narration, graphics or a colour grade with the matching sibling recipe.

Cost class: Uses credits (the story editor, the picture planner and the reviewer).

Worked example 1: "Customer review over the interview" gives the brief: lengthSec 45; sourceFiles video and overlay; overlayCardAudio keep; notes: show the review on screen around 0:12.

Worked example 2: "Logo in the corner while I explain the product" gives the brief: language en; sourceFiles video and overlay; overlayCardAudio agent; notes: small logo bottom right.

Typical mistakes: Putting a picture in the slot and expecting it at an exact second without saying so (the planner picks an offered moment). Choosing a screenshot for a corner inset it is too small to read from. Muting a card when the speaker's voice is the point.

## Footage edit with titles and callouts (`video-derush-edit-graphics`)

Goals in the person's words: "add name tags and the key points on screen"; "testimonial with the customer's name on screen"; "Aus den Interview-Clips ein Testimonial mit Namenseinblendung"; "ajoute des titres et des légendes".

Steps in order: Analyze source video (with overlay placements), story editor, motion graphics planner, a motion director that plans moves on the detected events, Compile with graphics, review loop (step 6 targets step 2).

Needs: Video clips in the project.

Put in the brief: lengthSec; format; stylePreset; sourceLanguage. List the names and key points to show in the notes.

Patches: None. The editing-team variant swaps the planner for a graphics room.

Cost class: Uses credits.

Worked example 1: "45 s customer testimonial with name and company" gives the brief: language de; lengthSec 45; notes: Name und Firma einblenden.

Worked example 2: "Name tags for six panel clips" gives the brief: language en; notes: speaker name tags and key-point callouts.

Typical mistakes: Leaving the names out of the notes so the planner invents none. Using it for burned captions: titles are not captions.

## Footage edit with background music (`video-derush-edit-music`)

Goals in the person's words: "highlight edit with the music track I uploaded under it"; "make the edit more upbeat, add some music"; "slideshow of my photos with calm music"; "pon música de fondo a mi montaje".

Steps in order: Analyze source video, story editor, music supervisor, a motion director that plans moves on the detected events, Compile with music, review loop (step 6 targets step 2).

Needs: Video clips and an uploaded music track. There is no built-in music library.

Put in the brief: lengthSec; tone; sourceFiles for video and music. Say where the music should be loud or quiet in the notes.

Patches: None.

Cost class: Uses credits. The track is a file you upload, so no music generation is bought.

Worked example 1: "90 s event highlight with my track under it" gives the brief: lengthSec 90; sourceFiles video and music.

Worked example 2: "Calm 40 s slideshow of 12 photos" gives the brief: lengthSec 40; tone Calm; sourceFiles image and music.

Typical mistakes: Running with no track uploaded: ask for the file first. Expecting generated music: use music generation steps in a custom graph only if a provider exists.

## Highlight edit, decided by an editing team (`video-derush-edit-room`)

Goals in the person's words: "make the best possible film out of my wedding footage, take your time"; "a more considered cut of long footage"; "le meilleur montage possible".

Steps in order: Analyze source video, an Edit room where several editors and a director settle one decision, a motion director that plans moves on the detected events, Compile, review loop (step 5 targets step 2).

Needs: Video clips and a plan that includes editing rooms.

Put in the brief: lengthSec; tone; stylePreset; sourceLanguage; notes saying quality matters more than speed.

Patches: None.

Cost class: Uses credits, more than the single-editor edit; slower. The estimate shows the range.

Worked example 1: "Wedding film, quality over speed" gives the brief: voice none; stylePreset cinematic; tone Calm; notes: quality over speed.

Worked example 2: "Documentary cut of long interviews" gives the brief: lengthSec 300; sourceLanguage en.

Typical mistakes: Choosing it for a quick social cut (the plain highlight edit is enough). Not saying it costs more and takes longer.

## Footage edit with titles, designed by a graphics team (`video-derush-edit-graphics-room`)

Goals in the person's words: "titles and callouts designed with care"; "graphics team for my panel recording".

Steps in order: Analyze source video, story editor, a Graphics room (several planners and a director), a motion director that plans moves on the detected events, Compile with graphics, review loop (step 6 targets step 2).

Needs: Video clips and a plan that includes rooms.

Put in the brief: lengthSec; format; stylePreset; the names and points to show in the notes.

Patches: None.

Cost class: Uses credits, more than the single-planner graphics edit.

Worked example 1: "Conference talk with speaker titles" gives the brief: lengthSec 120; notes: speaker names, three key points.

Worked example 2: "Product demo with callouts" gives the brief: format Landscape; notes: label the three main buttons.

Typical mistakes: Choosing it when the plain graphics recipe is enough. Leaving the content of the titles out of the notes.

## Footage edit with a colour grade (`video-derush-edit-grade-room`)

Goals in the person's words: "all my clips should look like a film, warm and consistent"; "Alle Clips sollen aussehen wie aus einem Film"; "dale un color uniforme a mi grabación".

Steps in order: Analyze source video, story editor, a Colour grade room (several colourists and a director), a motion director that plans moves on the detected events, Compile with grade, review loop (step 6 targets step 2).

Needs: Video clips and a plan that includes rooms.

Put in the brief: stylePreset (cinematic suits it); tone; notes describing the look (warm, cool, muted).

Patches: Narration is not included: add the narration and caption steps from the narration recipe after the cut.

Cost class: Uses credits.

Worked example 1: "Warm filmic look for all clips" gives the brief: language de; stylePreset cinematic; notes: einheitlicher warmer Look.

Worked example 2: "Uniform colour and a voice-over" gives the brief: language es; voice narrated; notes: color uniforme y voz en off.

Typical mistakes: Expecting narration (it has none, and the linter flags a silent film when the brief asks for a voice). Choosing it for a one-off colour fix on one clip.

## Footage edit with sound effects (`video-derush-edit-sfx`)

Goals in the person's words: "put my whooshes and clicks on the cuts"; "add sound effects to the key moments"; "Subí mis whooshes y clics, ponlos en los cortes".

Steps in order: Analyze source video, story editor, sound designer, a motion director that plans moves on the detected events, Compile with sound effects, review loop (step 6 targets step 2).

Needs: Video clips and sound effect files in the project: the person's own uploads, or sounds from the built-in sound effects library (find one with SearchSoundEffects and add it with ProposeAddSoundEffect/ApplyAddSoundEffect, or add the starter set; free, no credits).

Put in the brief: sourceFiles for video and sfx; lengthSec; notes on where effects should land.

Patches: None.

Cost class: Uses credits.

Worked example 1: "Whooshes on every cut" gives the brief: language es; sourceFiles video and sfx.

Worked example 2: "Clicks on the UI moments of a demo" gives the brief: language en; notes: click on every button press.

Typical mistakes: No effect files uploaded: ask for them. Expecting generated effects.

## Footage edit with AI-generated b-roll (`video-derush-edit-broll`)

Goals in the person's words: "a short AI-made opening shot and a closing card"; "an AI generated shot of a city at night for my intro"; "Voglio inquadrature create con l'AI"; "ajoute des plans générés par IA".

Steps in order: Analyze source video, story editor, shot director, Generate planned b-roll clips, a motion director that plans moves on the detected events, a dynamics planner that sees the generated clip ids and plans beat-aligned moves (a static shot against a generated one on the beat), Compile with generated clips, review loop (step 8 targets step 2).

Needs: Video clips, a video-generation provider and a plan that includes video generation.

Put in the brief: lengthSec; format; the extra shots described in the notes.

Patches: None. The dailies variant adds a second take and a panel.

Cost class: Uses credits and AI shots. Generation is paid beyond tokens and capped; disclose it before the card and read the estimate.

Worked example 1: "Opening shot and end card by AI" gives the brief: notes: opening shot of a sunrise over the office, closing card with the logo.

Worked example 2: "City at night for the intro" gives the brief: notes: night city shot for the intro; format Landscape.

Typical mistakes: Not disclosing paid generation. Running on a plan without generation or with no provider; both are reported before the run.

## Footage edit with narration and captions (`video-derush-edit-voiceover`)

Goals in the person's words: "cut my clips, then add a voice-over and subtitles, 90 seconds"; "demo of three screen recordings with captions explaining it"; "Coupe mes rushs puis ajoute une voix off et des sous-titres"; "Corta mi grabación y añade voz en off".

Steps in order: Analyze source video, story editor, narration writer, Voiceover (reads the narration, step 4), a motion director that plans moves on the detected events, Compile with narration and Karaoke-style captions, review loop (step 7 targets step 2).

Needs: Video clips and a voice provider with a voice the project can use.

Put in the brief: language (narration and captions); lengthSec; tone; voice narrated; sourceLanguage of the footage.

Patches: None. Captions are burned from the narration text, so they follow the language.

Cost class: Uses credits and a voice. Read the estimate.

Worked example 1: "Screen-recording demo, calm voice, 60 s" gives the brief: language en; lengthSec 60; tone Calm; voice narrated.

Worked example 2: "Rushs en voix off française, 90 s" gives the brief: language fr; lengthSec 90; voice narrated; sourceFiles video.

Typical mistakes: Picking it for footage with speech in another language to translate (use the dub recipe). Forgetting language so narration is English. Choosing it when the story should be written first (use the narrated story).

## Fix flubbed lines in a talking-head video (`video-derush-edit-pickups`)

Goals in the person's words: "fix the bits where I stumble using my own voice"; "re-record my mistakes in my own voice"; "corrige mis tropiezos con mi propia voz".

Steps in order: Analyze source video, story editor, pickup planner, Voiceover that re-records the pickups, a motion director that plans moves on the detected events, Compile with the pickups spliced in, review loop (step 7 targets step 2).

Needs: Talking-head footage with clear speech, a transcription provider, a voice provider and a project voice with consent on record (set up in the project's Voices tab).

Put in the brief: voice with the project voice; language; sourceLanguage.

Patches: None. Lips are not re-synced; say so.

Cost class: Uses credits and a voice.

Worked example 1: "Fix stumbles with my own voice" gives the brief: language en; voice narrated with the project voice.

Worked example 2: "Corrige las erratas de mi presentación" gives the brief: language es; sourceLanguage es.

Typical mistakes: No consented voice yet: send the person to the Voices tab first. No transcription provider. Promising lip sync.

## Dubbed video in another language (`video-derush-edit-dub`)

Goals in the person's words: "dub my product walkthrough into Spanish with subtitles"; "Quero dublar o meu vídeo para japonês"; "doppiare il mio video in inglese"; "synchronise mon film en allemand".

Steps in order: Analyze source video, story editor, narration writer, narration translator (into the brief language), Voiceover of the translation, a motion director that plans moves on the detected events, Compile the dubbed video (replaces the original dialogue and burns subtitles), review loop (step 8 targets step 2).

Needs: Footage with speech, a voice provider, and a voice that speaks the target language.

Put in the brief: language is the target; sourceLanguage is what the footage speaks; voice narrated.

Patches: None. Lips are not re-synced.

Cost class: Uses credits and a voice.

Worked example 1: "Dub the English walkthrough into Spanish" gives the brief: language es; sourceLanguage en; voice narrated.

Worked example 2: "Doppia in tedesco con sottotitoli" gives the brief: language de; sourceLanguage it; voice narrated.

Typical mistakes: Mixing up language and sourceLanguage. Not checking that the voice speaks the target (the linter warns when support is unknown or missing).

## Narrated product story (`narrated-story`)

Goals in the person's words: "tell a 45 second story about our product using these clips, vertical, with narration"; "story first, then the clips"; "Raconte l'histoire de notre produit avec ces clips".

Steps in order: Analyze your clips, narration writer writes the story (step 2), story editor chooses and orders clips for it, Voiceover (step 4), a motion director that plans moves on the detected events, Compile with narration and captions, review loop (step 7 targets step 2).

Needs: Video clips and a voice provider.

Put in the brief: language; lengthSec; format; tone; voice narrated; notes with the topic and audience.

Patches: None.

Cost class: Uses credits and a voice.

Worked example 1: "45 s vertical story about the product" gives the brief: language en; format Vertical; lengthSec 45; stylePreset fast-social-reel; voice narrated.

Worked example 2: "Storia di prodotto, 60 secondi" gives the brief: language it; lengthSec 60; voice narrated.

Typical mistakes: Choosing it when the footage already has the talk to keep (cut first with the narration edit). Leaving the topic out of the notes.

## Footage edit with AI b-roll, best take chosen by a panel (`video-derush-edit-dailies`)

Goals in the person's words: "generate the extra shots twice and keep the better one"; "AI b-roll with a quality check".

Steps in order: Analyze source video, story editor, shot director, Generate two takes per shot, Measure every generated take (analysis), Dailies room picks one take per shot, a motion director that plans moves on the detected events, a dynamics planner that sees the chosen takes and plans beat-aligned moves, Compile with the chosen takes, review loop (step 10 targets step 2).

Needs: Video clips, a video-generation provider and a plan with generation and rooms.

Put in the brief: lengthSec; format; the shots described in the notes.

Patches: None.

Cost class: Uses credits and AI shots, roughly twice the generation of the plain b-roll edit, capped. Disclose it and read the estimate.

Worked example 1: "Intro shot with a quality check" gives the brief: notes: sunrise opening shot, pick the best take.

Worked example 2: "End card, two takes" gives the brief: notes: closing card with the logo.

Typical mistakes: Offering it when the plain b-roll recipe is enough. Not saying the generation is doubled.

## Blur one object through the whole clip (`video-region-effect`)

Goals in the person's words: "the license plates in my dashcam video need to be hidden"; "Floute le visage de la personne à gauche"; "blur the screen of the speedometer"; "ocultar la matrícula en todo el vídeo".

Steps in order: Find the object you named (analysis), Blur it through the whole clip (compile). No editor agent.

Needs: One video clip, the object described in plain words, and the permission to send the clip to the tracking service. The person enables that permission (the allow-source-media-egress setting on the first step); it is off by default. The person switches it on in the app, or asks the in-app assistant to, which shows a card naming the step and what it sends; a connected tool never switches it on. An edit that does not touch that step keeps a permission already given.

Put in the brief: notes with the object; sourceFiles video. Language is not read; format, resolution and style reach only the compile.

Patches: None.

Cost class: Uses credits, because following the named object uses a billed tracking service (the plain analysis in other recipes is not). Tell the person it is billed and that the estimate shows the amount, and that the tracking permission is a separate switch.

Worked example 1: "Hide plates in a dashcam clip" gives the brief: notes: hide license plates throughout; sourceFiles video.

Worked example 2: "Flouter un visage" gives the brief: language fr; notes: flouter le visage de la personne à gauche.

Typical mistakes: Forgetting the permission (the run stops before anything leaves the server). Expecting a style to change anything.

## Build brand and product profile (`product-profile-from-code`)

Goals in the person's words: "build our brand profile from the code and design files"; "Costruisci il profilo del brand dal nostro codice"; "creer le profil de marque".

Steps in order: Read design files and code (free), checks whether the product profile or the design is missing, runs the code analyzers and the style extractor only for what is missing, then composes the profile.

Needs: Design files and/or source code in the project. A draft profile is refreshed, not duplicated.

Put in the brief: designSystem; sourceFiles code (design files are read from the project). Not a video; the result is a draft profile to approve.

Patches: None.

Cost class: Uses credits only for the gaps; files are read free first. The estimate comes before the run.

Worked example 1: "Build the profile from code and design files" gives the brief: designSystem none; sourceFiles code and document.

Worked example 2: "Aggiorna la bozza dal codice Blazor" gives the brief: language it; designSystem project; sourceFiles code.

Typical mistakes: Treating the draft as approved: a person must approve it before the profile promo runs. Running it again when an approved profile exists.

## Promo video from your profile (`quick-win-promo-ds`)

Goals in the person's words: "promo with our brand colours"; "Ci serve un video esplicativo di 60 secondi, in italiano, con il nostro design system"; "Mach ein Reel mit unserem Markenprofil"; "un vídeo con nuestro design system".

Steps in order: Require approved profile (stops if none), scene translation, animation strategy, Scriptwriter (step 4), Director (step 5), Author (step 6), review loop (step 7 targets step 6). It does not read the code.

Needs: An approved design and product profile in the project. No code needed at run time.

Put in the brief: language; lengthSec; format; stylePreset; tone; designSystem project (default). Notes say the audience.

Patches: Silent as shipped. For narration insert a Voiceover step that reads the Scriptwriter (step 4) before the Director and re-point the loop to the Author.

Cost class: Uses credits (agents). With a Voiceover step added it also uses a voice.

Worked example 1: "60 s Italian explainer, landscape, narrated, our design system" gives the brief: language it; format Landscape; lengthSec 60; designSystem project; voice narrated.

Worked example 2: "20 s vertical reel with our brand, no sound" gives the brief: language de; format Vertical; lengthSec 20; designSystem project; voice none.

Typical mistakes: No approved profile: the first step stops the run; offer approval or build the profile first. Asking for narration without adding the Voiceover step.

## Promo video from your profile with AI backgrounds (`quick-win-promo-plates-ds`)

Goals in the person's words: "brand-colour promo with AI generated animated backgrounds"; "Une vidéo promo avec nos couleurs et des fonds générés par IA".

Steps in order: Require approved profile, scene translation, animation strategy, Scriptwriter, Director, Generate background plates, Author, review loop (step 8 targets step 7).

Needs: An approved profile, a video-generation provider and a plan with generation.

Put in the brief: language; lengthSec; format; stylePreset; designSystem project; the mood in the notes.

Patches: Add narration as in the profile promo.

Cost class: Uses credits and AI shots. Disclose generation first; read the estimate.

Worked example 1: "French promo in brand colours with AI backgrounds" gives the brief: language fr; designSystem project; notes: fonds abstraits.

Worked example 2: "Square branded promo with soft plates" gives the brief: language en; format Square; designSystem project.

Typical mistakes: No approved profile or no provider. Not disclosing paid generation.

## Green-screen insert (`green-screen-insert`)

Goals in the person's words: "put the screen recording of our dashboard on the laptop in my office clip"; "replace the green screen with my demo"; "pon la grabación en la pantalla verde".

Steps in order: Find the green screen (analysis), Put the content on the screen (compile, keeps the clip whole).

Needs: A clip with a green screen and the content to insert (a project file or an earlier render).

Put in the brief: sourceFiles video; notes naming the content to insert. Language is not read.

Patches: None.

Cost class: No credits: no model is used; only processing time is charged.

Worked example 1: "Dashboard recording on the office laptop" gives the brief: sourceFiles video; notes: insert the dashboard demo.

Worked example 2: "Replace a green phone screen" gives the brief: notes: show the app recording on the phone.

Typical mistakes: Setting a language and expecting a change (nothing reads it). No content chosen for the insert.

## Spokesperson clip (`spokesperson-clip`)

Goals in the person's words: "make a short clip of me greeting our customers"; "a clip of our founder presenting the launch"; "un video del nostro portavoce che saluta".

Steps in order: Make the spokesperson clip (one generated clip that shows a project avatar).

Needs: A project avatar that is ready (ListProjectAvatars says which ones can be used), and the step's permission to send media to the provider, which the assistant turns on only when the customer asks, through an approval card that names the step and what it sends. The assistant never creates an avatar.

Put in the brief: avatar (the avatar's id, as the app gives it); notes describing what the person does. Language and length are not read. In the app the customer picks the person in the Avatar field of the app page, or in Avatar to show in the Avatar section of the Generate Video step (Edit steps (advanced)); the switch Allow sending your media to the video service sits just below that section.

Patches: None.

Cost class: Uses credits and AI shots. Each clip is paid generation, plus the provider's charge for the person's reference pictures and video; disclose it before the card and read the estimate.

Worked example 1: "A 6 second hello from our presenter" gives the brief: avatar; notes: the presenter greets the viewer in a bright studio.

Worked example 2: "Our founder announces the launch" gives the brief: avatar; notes: the founder smiles and says the product is live.

Typical mistakes: Expecting the clip to say exact words (reference audio is not known to drive speech yet; for exact spoken lines use Talking avatar (budget)). Asking the assistant to switch on sending media (the customer does it in the app). Using an avatar whose consent was withdrawn.

## Talking avatar (budget) (`talking-avatar-budget`)

Goals in the person's words: "a short clip of me saying a few words about our new menu"; "make our founder say thank you to our customers"; "un breve video del nostro portavoce che parla".

Steps in order: Write what the person says; Speak it in the avatar's voice; Make the talking clip (a still of the avatar made from its pictures, animated so the person says the narration).

Needs: A project avatar that is ready (ListProjectAvatars says which ones can be used) with a linked voice, and the last step's permission to send media, which the assistant turns on only when the customer asks, through an approval card that names the step and what it sends. A fal.ai lip-sync model (minimax/h3-max/lip-sync/image-to-video) set up under Lip-sync and marked as the default, and a fal.ai picture-edit model set up as the default. The assistant never creates an avatar.

Put in the brief: avatar (the avatar's id, as the app gives it); notes with what the person should say; lengthSec 10 to 15 (the clip is at most 15 seconds and at least 5). In the app the customer picks the person in the Avatar field of the app page and switches on Allow sending your media to the video service on the last step (Edit steps (advanced), Generate Video step).

Patches: None.

Cost class: Uses credits and AI shots. One still picture of the person plus the seconds of video the narration lasts; disclose it before the card and read the estimate. Cheaper than Spokesperson clip.

Worked example 1: "A 10 second thank-you from our founder" gives the brief: avatar; lengthSec: 10; notes: thanks the customers for the first year and invites them to the anniversary party.

Worked example 2: "Our presenter announces Saturday's opening" gives the brief: avatar; lengthSec: 12; notes: says the shop opens on Saturday at nine and everyone gets a free coffee.

Typical mistakes: A narration over 15 seconds (the clip step refuses it). An avatar with no linked voice (link one on the Avatars tab, or choose a voice in the brief). Asking the assistant to switch on sending media (the customer does it in the app).

## Avatar clip (`avatar-clip`)

Only where recipe fields (app inputs) are switched on; there it replaces Spokesperson clip and Talking avatar (budget), which are then hidden from the recipe list (workflows already made from them keep running unchanged).

Goals in the person's words: "a short clip of our spokesperson"; "make my avatar say a few words"; "un breve video del nostro portavoce".

Steps in order: Lite mode? (decides the path from the Lite mode switch); Lite path: Write what the person says, Speak it in the avatar's voice, Make the talking clip, Finish here; full path: Make the spokesperson clip. Only the chosen path runs and is priced.

Needs: the same as the two recipes it replaces, for the chosen path: a ready project avatar (with a linked voice for Lite mode), and the clip step's permission to send media, which the assistant turns on only when the customer asks, through an approval card that names the step and what it sends.

Fields on the app page: Avatar; Lite mode (cheaper) switch; What they do (full path, the video prompt); What they say (Lite path, written into the request word for word); Shape; Length (full path, 6 or 10 seconds); under More options, Video model (full path) and Lip-sync model (Lite path), the recommended model preselected.

Patches: None.

Cost class: Uses credits and AI shots. Lite mode is the cheaper path; disclose it and read the estimate, which follows the switch.

Worked example: "Our founder thanks the customers in her own voice" gives: Lite mode on; What they say: thank you for the first year, see you at the party.

Typical mistakes: Expecting exact words with Lite mode off (use Lite mode). Switching on media sending for the customer.

## Sound effects for your video (`video-sound-effects`)

Goals in the person's words: "add sound effects to my finished video, do not cut anything"; "put pops and whooshes on my edit"; "sound design for my reel"; "aggiungi effetti sonori al mio video"; "ponle efectos de sonido a mi vídeo".

Steps in order: Find the moments in the video (looks at the picture and lists what happens: cuts, things appearing, moving or flashing, text coming in; the video is chosen in this step), Choose a sound for each moment (the sound designer picks a sound from the built-in sound library, or from the project's own sound files, for the moments worth one), then Mix the sound effects in (the compile keeps the whole video, re-encodes, and puts each sound on its moment under the voice). Nothing is cut and no music is added.

Needs: One finished video in the project, and a chat AI provider for the sound designer. A vision AI provider is optional: with one, the first step also looks at some moments and notes what they show, so filmed footage gets no sound; without one the recipe still runs. No speech-to-text provider is needed. Sound files of the customer's own are optional: the built-in library is always offered.

Put in the brief: notes, when the customer says what the sounds should feel like ("soft and subtle", "only on the big moments"). Language, tone, length and style are not read.

Fields on the app page (More options): footageSounds (switch, off: no sounds on plain filmed footage), lookModel (the vision model that looks), mostLooks (number 0 to 40; empty: the video's length decides, 12 to 30; 0: no looks; else the sound designer may take 6 more).

Patches: None.

Cost class: Uses credits, for the sound designer step and for the looks at the picture. Finding the moments and mixing are priced for processing time. Read the estimate.

Worked example 1: "add sound effects to my finished video, do not cut anything" gives the brief: sourceFiles video.

Worked example 2: "Aggiungi effetti sonori al mio video, ma delicati" gives the brief: sourceFiles video; notes: soft and subtle sounds.

Typical mistakes: Choosing Footage edit with sound effects for a finished video (that recipe cuts the footage down first). Promising a sound on every caption word (caption words are never given a sound). Promising generated sounds (this recipe only uses the library and the project's files).

## Subtitles for your video (`video-subtitles`)

Goals in the person's words: "just add subtitles to my finished video, nothing else"; "burn the words into my video"; "I need an SRT file for my video"; "ajoute des sous-titres à ma vidéo"; "Untertitel für mein Video"; "ponle subtítulos a mi vídeo".

Steps in order: Listen to the video (transcription is required, the clip is chosen in this step), then Burn in the subtitles (the compile keeps the whole clip, re-encodes, and takes the caption text from the transcript). No agent and no editing. It also writes SRT and VTT files.

Needs: One finished video with speech in the project, and a speech-to-text provider. Without a provider the run stops at the first step and says so.

Put in the brief: sourceLanguage, the language spoken in the video, when detection might guess wrong. Language, tone and style are not read.

Patches: None.

Cost class: Uses credits, for the speech-to-text step (priced by the length of the audio; free only when the bundled local speech server is used). No other model is used; read the estimate.

Worked example 1: "just add subtitles to my finished video, nothing else" gives the brief: sourceLanguage en; sourceFiles video.

Worked example 2: "Ajoute des sous-titres à ma vidéo" gives the brief: sourceLanguage fr; sourceFiles video.

Typical mistakes: Choosing a cut recipe with a captions switch (captions there come from narration, not from the speech). No transcription provider. Promising a download button for the SRT and VTT files: the run page has none yet, the files are listed in the compile step result.

## Subtitles in another language (`video-subtitles-translated`)

Goals in the person's words: "subtitles for my video in Spanish"; "translate the subtitles into German, keep the timing"; "sottotitoli in inglese per il mio video"; "sous-titres en anglais".

Steps in order: Listen to the video (transcription required, long segments kept whole), Reduce the transcript to what is said (an Extract step), Translate what is said (the narration translator, reading only the previous step), Burn in the subtitles (the compile reads the translation at step 3). The timing is always the original's, one sentence for one sentence.

Needs: One finished video with speech, a speech-to-text provider and a chat AI provider for the translation.

Put in the brief: language is the subtitle (target) language; sourceLanguage is what is spoken. Both matter here.

Patches: None.

Cost class: Uses credits, for the speech-to-text step and the translation step. Read the estimate.

Worked example 1: "subtitles for my video in Spanish" gives the brief: language es; sourceLanguage en; sourceFiles video.

Worked example 2: "Sottotitoli in inglese per il mio video" gives the brief: language en; sourceLanguage it; sourceFiles video.

Typical mistakes: Swapping language and sourceLanguage. Using it for a spoken dub (that is the dub recipe). A missing chat provider or transcription provider.

## Product promo, your way (`product-promo-your-way`)

Goals in the person's words: "a promo video of my product from its code, with an optional voiceover"; "make a promo for my product and read it out loud".

Steps in order: the promo steps (analyse the code, scenes, strategy, script), then Spoken voiceover? (decides from the Spoken voiceover switch); if on, Record the narration; then Build shot direction, Assemble and render master composition, and Review quality and compliance. Only the chosen path runs and is priced.

Needs: a code archive or repository in the project, and a voice provider when the voiceover switch is on.

Fields on the app page: What should it show? (notes); Who it is for; Call to action; Language; Length; Shape; Spoken voiceover switch; under More options, Narrator voice, Tone, Voice model (when the voiceover is on), Script model and Video builder model.

Patches: None.

Cost class: Uses credits and a voice. Disclose it and read the estimate, which follows the voiceover switch.

Worked example: "A 30 second promo for our booking app for hairdressers" gives: Who it is for: hairdressers; Call to action: Book a demo; Length: 30; Spoken voiceover on.

Typical mistakes: Switching the voiceover on without a voice provider. Leaving Who it is for empty.

## Avatar explainer from a topic (`avatar-explainer`)

Goals in the person's words: "a real person from our avatars explains a topic"; "an avatar video from my own words".

Steps in order: Own words? (decides from the I will write the words switch); topic path: Write what the person says, Speak it in the avatar's voice, Make the talking clip, Finish here; own words path: Say the words in the avatar's voice, Make the talking clip from your words. Only the chosen path runs and is priced.

Needs: a ready project avatar with a linked voice, and the clip step's permission to send media, which the assistant turns on only when the customer asks, through an approval card that names the step and what it sends.

Fields on the app page: Avatar; I will write the words switch; Topic (when off); What they say (when on, spoken word for word); Tone; Language; Shape; under More options, Lip-sync model, Picture model and Script model.

Patches: None.

Cost class: Uses credits and AI shots. One still picture of the person plus the seconds of video the speech lasts (about 15 at most); the clip step has a spend cap.

Worked example: "Our chef explains why the menu is local" gives: Topic: why our autumn menu uses local ingredients; Tone: warm.

Typical mistakes: Words over 15 seconds. An avatar with no linked voice. Asking the assistant to switch on media sending.

## Music or sound effect (`music-or-sound-effect`)

Goals in the person's words: "make a music track for my video"; "a door creaking sound effect".

Steps in order: Music? (decides from the What to make choice); music path: Make the music, Finish here; sound effect path: Make the sound effect. Each take is saved as an audio file in the project.

Needs: a music or sound-effects provider in the workspace.

Fields on the app page: What to make (Music or Sound effect); Describe the music or Describe the sound (only the one that applies); Length (15 to 120 seconds for music, 1 to 8 for a sound); Takes (1 to 4); under More options, the Music model or Sound effect model.

Patches: None.

Cost class: Uses credits and AI shots. Every take is billed, and the step has a spend cap; more takes cost more.

Worked example: "Calm piano for a 30 second intro" gives: What to make: Music; Describe the music: calm solo piano, slow, warm; Length: 30.

Typical mistakes: Asking for a sound over 8 seconds here. Asking for many takes when one will do.

## Thumbnail and cover text (`thumbnail-and-cover-text`)

Goals in the person's words: "a thumbnail for my video with a title on it"; "pick a cover image from my video".

Steps in order: Look at the video (local analysis only), then Pick the frame myself? (decides from the Which frame choice); Choose the best frame, or Take the frame at that time. The picture is saved in the project.

Needs: one video in the project. Nothing is sent to a provider.

Fields on the app page: Your video; Which frame (Pick the best one or Use a time I choose); Time in the video (only for a chosen time); Cover text (optional).

Patches: None.

Cost class: No credits.

Worked example: "A cover for my intro video that says Our autumn menu" gives: Which frame: Pick the best one; Cover text: Our autumn menu.

Typical mistakes: A time beyond the length of the video. A cover text longer than 90 characters.

## Read my text aloud (`read-text-aloud`)

Goals in the person's words: "read this text out loud"; "turn my script into an audio file".

Steps in order: one step: Read the text aloud, which speaks the text word for word and saves the audio.

Needs: a speech-synthesis provider (voice provider) in the workspace.

Fields on the app page: Text to read; Voice; Language; under More options, Voice model.

Patches: None.

Cost class: Uses credits and a voice. Disclose it and read the estimate.

Worked example: "Read my welcome message in Italian" gives: Text to read: the message; Language: it.

Typical mistakes: A text with a paragraph longer than the voice step takes. Expecting a translation (the text is spoken as written).

## AI video clip from your idea (`ai-clip-from-idea`)

Goals in the person's words: "an AI video clip of a scene I describe"; "a short clip of steam over a coffee".

Steps in order: one step: Make the clip. The How much effort choice sets the length and the number of takes together: Draft (6 s, 1 take), Standard (10 s, 1 take), Best of two (10 s, 2 takes).

Needs: a video-generation provider in the workspace. No person from the project is shown.

Fields on the app page: Your idea; Mood and look (optional); How much effort; Shape; under More options, Video model.

Patches: None.

Cost class: Uses credits and AI shots. The step has a spend cap; Draft is the cheapest.

Worked example: "A calm shot of rain on a window" gives: Your idea: rain running down a window at night; Mood and look: calm, blue; How much effort: Draft.

Typical mistakes: Asking for people or text in the clip. Choosing Best of two when a draft will do.

## Edit my video (`edit-my-video`)

Goals in the person's words: "edit my footage"; "cut my screen recording down and add music and zooms"; "clean up this animation and add sound effects"; "add a voice-over and captions to my clips". This one app replaces every old footage edit; the old keys (the plain edit, testimonial reel, graphics, music, sfx, colour, b-roll, dailies, voice-over, narrated story, pickups and the edit rooms) are hidden aliases that open it with the matching field preset.

Steps in order: analysis (events and outline on), story editor, Lite mode check, then one check plus agent per extra the person switched on (sound effects, music, motion director, graphics, colour look, extra AI shots, voice-over, pickups), the compile, and a review loop that goes back to the story editor. A switched-off extra is not run, held or charged. Lite mode ends after one plain compile with no review.

Needs: footage in the project (video files). Music needs a music file in the project. Voice-over and pickups need a voice provider; extra AI shots need a video-generation provider and stay capped.

Fields: Your video; What is it (raw footage, finished animation, screen recording); Length; Shape; Sound effects; Captions; Lite mode; Notes; under More options: Music (off by default), Your music track, Zooms and camera moves (subtle by default), Speed ramps and Flashes (on by default), Graphics, Transitions, Colour look, Extra AI shots, Voice-over with its language and voice, Fix flubbed lines, Cut on the action (on by default), Style.

Patches: none needed; switch the extra on instead. The permission to send footage to a vendor is never an app field.

Cost class: derived from the steps; Uses credits, plus voice or AI shots when those extras exist.

Typical mistakes: Switching on speed ramps or flashes with zooms and camera moves off (they work together). Switching on Music with no music file in the project.

## Languages (`languages`)

Goals in the person's words: "add subtitles"; "subtitles in French"; "dub my video into Spanish"; "translate my video".

Steps in order: analysis with required transcription, then a branch: dub (story editor, narration writer, translator, voice, compile, review loop that targets the story editor), translated subtitles (extract, translator, compile) or plain subtitles (compile). Subtitles are burned in and come as SRT and VTT files.

Needs: a video with speech and a speech-to-text provider; a translation or dub also needs a chat provider and, for a dub, a voice provider.

Fields: Your video; Subtitles (off or burned in); Translate the subtitles; Target language; Dub; under More options: Dub voice, Spoken language, Notes.

Patches: none. The lips of a dub are not re-synced.

Cost class: derived; Uses credits (transcription, translation and, for a dub, the voice).

Typical mistakes: Asking for a target language without switching Translate or Dub on.

## Effects on a clip (`effects-on-a-clip`)

Goals in the person's words: "blur the number plate"; "put my screen recording on the green screen"; "make the camera follow the dancer".

Steps in order: three branches chosen by What to do. Blur: find the named object, hide it through the whole clip. Screen: find the green screen, put the content on it. Follow: find the named subject, follow it with the camera. Each ends the run.

Needs: a clip; for Screen, the picture or video that goes on it. Finding an object sends the clip to a tracking service: the permission is off and is given in the app on the first step, never through a field.

Fields: Your clip; What to do; What to hide and How to hide it (Blur); What to follow (Follow); What goes on the screen (Screen).

Patches: none.

Cost class: derived; Blur and Follow use credits for tracking, Screen uses processing time only.

Typical mistakes: Forgetting to allow sending the clip to the tracking service. Naming the object vaguely.
