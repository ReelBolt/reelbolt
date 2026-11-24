---
title: Writing a brief
kb:
  kind: how-to
---

# Writing a brief

Internal background for the assistant, with a customer-readable twin in the user guide. How to fill the run brief from what a person said. Every section stands alone.

## What the brief is

The brief is the set of choices made when a run starts. Every field is optional; an absent field means no preference. The fields are language, sourceLanguage, lengthSec, format, resolution, stylePreset (or a saved style, or a one-off style), tone, designSystem, voice, sourceFiles and notes. Anything a field can hold goes in the field. Nothing that changes a run lives only in the workflow name or in prose. The same brief shape is used by the run page, the assistant and external tools, and one validator checks all of them before any credit is held.

## Language and sourceLanguage

language is what the viewer reads or hears: narration, on-screen text, captions and thumbnail text. Set it to the language the person wrote in unless they ask for another one. sourceLanguage is the language spoken in the footage, used for transcription. They differ for a dub and for translated subtitles: language is the target and sourceLanguage is the original (for subtitles, sourceLanguage is the transcription language and language is the translation target). Examples: "Ci serve un video in italiano" gives language it. "Quiero el video en español" gives language es. "Mach das Video auf Deutsch" gives language de. "Fais la vidéo en français" gives language fr. "Dub my English walkthrough into Spanish" gives language es and sourceLanguage en. A regional tag such as es-MX is accepted; the region is kept for choosing voices. Language is never detected from the message by the platform. When it is not set, the run falls back to the language of the chosen style, then to the locale of the approved design system, so a brief language beats a style language, which beats the design locale. Set language whenever the person wrote in, or asked for, a language.

## Length, shape and resolution

lengthSec is a target in whole seconds from 5 to 600; reviewers check it within a tolerance, so it is not a guarantee. Words such as "a minute and a half" become 90. format is one of Original, Vertical, Square, Landscape or Portrait, also accepted as the ratios 9:16, 1:1, 16:9 and 4:5. A reel, short, story or TikTok is Vertical; LinkedIn feed is Square; a YouTube video is Landscape. One run has one format, so "the same ad for Instagram, YouTube and LinkedIn" is three runs. resolution is Auto, 720p, 1080p or 2160p and is checked against the plan before the run. Do not set a resolution the person did not ask for.

## Style, tone and design system

stylePreset names a built-in style such as fast-social-reel, corporate-explainer or cinematic; choose at most one of preset, saved style and one-off style. A preset suggests a shape and a tone, but the person decides. tone is one of Calm, Upbeat, Energetic, Serious, Playful, Inspirational and overrides the preset's tone. designSystem is project (default: build in the project's approved design) or none (deliberately unbranded). Do not invent a stylePreset, a tone or a length the person did not state or clearly imply; an invented value counts as a mistake.

## Voice and source files

voice has a mode, none or narrated, and may name a project voice or a library voice (only one). Set narrated when the person asks for a voice, narration or voice-over; set none when they say no voice or silent. A voice named in the brief is checked for consent. sourceFiles lists the run's media by role: video, code, music, sfx, image, document or insertContent (the one picture or video that goes on a green screen), each role once, files that belong to the project. Roles come from the file type: archives and source files count as code, audio counts as both music and sound effects. Name files by their names to the person, never by id.

## Precedence

Highest wins, field by field. An explicit brief field beats the free notes; the notes beat the style's axes; the design system's voice and tokens come next as defaults; then the step settings the workflow author chose; then the agent's default. For deterministic steps, a run field overrides the step's own setting for the property it is bound to, never the other way round. So when a person's message and a field disagree, the field is what runs, and the assistant is responsible for not creating the conflict.

## What the notes are for

notes carry what no field can: who the film is for, the message, what to show or keep, the names to put on screen, the mood of generated shots, product terms to leave untranslated. Write them without restating the fields: do not repeat the length or the language there. Keep them short and concrete. Example: for a finance audience, show the expense report flow, end with the sign-up screen. A changelog or other text the film should cover goes into the notes, since the code recipes do not read document files; attach it as a document file only for a workflow that has a step reading documents.

## A complete example

Request: "Ci serve un video esplicativo di circa 60 secondi sul portale note spese, in italiano, 16:9, con una voce che racconta. Usa il nostro design system." The brief: language it, format Landscape, lengthSec 60, designSystem project, voice narrated, sourceFiles code; notes: video esplicativo del portale note spese. Request: "make me a video" gives no brief fields to fill: ask who it is for and how long, in at most two questions. Request: "Fammi un video per Instagram" gives language it and format Vertical, then ask whether there is code or footage and how long.
