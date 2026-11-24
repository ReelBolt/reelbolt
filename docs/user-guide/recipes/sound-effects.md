---
title: Add sound effects to a finished video
sidebar_position: 10
description: Recipe for adding short sound effects to a finished video where things happen on screen, without cutting anything, using the built-in sound library.
---

# Add sound effects to a finished video

**Goal:** take a video that is already edited and get a new MP4 with short sound effects on the moments where
something happens on screen: a pop where something appears, a whoosh on a move or a scene change, a shutter on a
camera flash. Nothing is cut, and no music is added. The voice stays in front; the effects sit under it.

## Prerequisites

- **The video uploaded to the project** (Files tab).
- **A chat AI provider**, for the step that chooses the sounds.
- **A vision AI provider is optional and makes the result better.** With one, step 1 also looks at the picture at
  each moment and notes what it shows ("a price tag pops up", "a man walks past"), so sounds fit what happens and
  ordinary filmed footage gets none. Without one the app still runs.
- **Your own sound files are optional.** ReelBolt offers its built-in sound library (pops, clicks, whooshes, impacts,
  a camera shutter and more, free to use). If the project also has sound files of your own, they are offered too.

No speech-to-text provider is needed.

## The recipe

Add the **Sound effects for your video** app (`video-sound-effects`) from **New workflow**. It has three steps:

| # | Label | What it does |
|---|---|---|
| 1 | Find the moments in the video | Lists what happens in the picture: scene changes, things that appear, move or flash, text that comes in. Select your video as the source. Finding the moments uses no AI model. If your workspace has a vision model, it then looks at some of the moments to say what they show. |
| 2 | Choose a sound for each moment | The sound designer picks a fitting sound for the moments worth one. |
| 3 | Mix the sound effects in | Puts each sound on its moment and gives you the new video. |

In your brief you can say what the sounds should feel like, for example "soft and subtle" or "only on the big moments".

Under **More options** on the app page:

| Option | What it does |
|---|---|
| Sounds on filmed footage too | Off by default: sounds go on designed moments (graphics, text, transitions), not on plain cuts between filmed shots or on people moving. Turn it on to get sounds there as well. A camera flash and an impact always get one. |
| Model that looks at the picture | Which vision model looks at the moments. Leave it on the recommended one unless you have a reason. |
| Most looks at the picture | The most moments the model looks at in step 1 (0 to 40). Leave it empty and the length of the video decides: about one look per six seconds, between 12 and 30. Set it to 0 to switch the looks off: no picture is sent to the vision model. When looks are on, the sound designer in step 2 may ask for up to 6 more looks at single moments. Each look is one small call to the vision model; the estimate you see before the run includes them. |

## What you get

- The same video, same length and same picture, with the sound effects mixed in.
- The result of step 3 lists every sound and the moment it was put on, and says why a planned sound was left out
  when one was.

## What it will not do

- **It does not put a sound on every word of running captions.** Words of captions that pop in one after another are
  recognised and left alone.
- **It does not cut, trim or reorder your video.**
- **It does not make new sounds.** It chooses from the built-in library and from your own files.

## If something looks wrong

- **Too many or too few sounds.** Say so in the brief ("fewer sounds", "a sound on every scene change") and run again.
- **A sound is on the wrong kind of moment.** The moments are found from the picture alone, so a pop-in on top of busy
  footage can be read as a plain change. The sounds are mixed into the new video, so a single sound cannot be moved or
  removed afterwards. Run the app again and say in the brief what to change ("no sound on the logo at the start",
  "nothing on the filmed shots"), or switch **Sounds on filmed footage too** on or off under **More options**. Running
  again makes a new video from your original; nothing is lost.
- **You want to place every sound by hand.** Open your original video in the editor, show the
  [Events lane](../editor/events-lane.md) and use **Add sound here** on a moment. That places sound files of your own;
  the built-in library is used by this app only.
- **No sound at all.** The result of step 2 says why: for example the video has no clear moments (a plain talking
  video), or no chat AI provider is set up.
