---
title: Clips with your avatars and objects
sidebar_position: 14
description: How to make a new video clip that shows up to three of your project's people and objects from the editor's media pool, what each choice in the dialog does, what it costs, and what happens while the clip is made.
---

# Clips with your avatars and objects

The editor can make a new video clip that shows people and objects from your project's [Avatars](../projects/avatars.md) tab, for example "Anna picks up the bottle and smiles at the camera". The clip is made by an AI video service and added to your project's media, so you can put it on the timeline like any other clip. It is not part of a workflow.

You find it in the **Avatars** tab of the media pool, beside **Media** and **Sound effects**. On a phone, open the add-media sheet and choose **Avatars**. The tab is there only when avatars or objects are enabled in your workspace.

## What the tab shows

The tab lists the project's **People** and **Objects**, each with whether it is ready. One that cannot be used right now is shown with the reason; fix it in the project's **Avatars** tab (the link **Add or manage them in the Avatars tab** takes you there). If the project has none yet, the tab says so.

## Make a clip

Choose **Generate clip** to open **Generate a clip**:

- **Who or what is in the clip.** Up to three of the project's people and objects, at most one of them a person.
- **What happens in the clip.** Describe the scene and what each one does, in up to 2,000 characters.
- **What they say (audio file).** Only when you chose exactly one person: pick a recording from the project's files, and the person says it. The clip is then as long as the recording. Leave it on **Nothing, no speech** for a clip without speech.
- **Model.** **The default model** of your workspace, or another video model your workspace has set up.
- **Length (seconds)**, **Size** and **Shape.** The shape starts as the one closest to your timeline's picture.
- **How many versions.** One or two clips of the same request, to choose from.

The dialog shows the price before you start, for example "About 12 credits". When **Includes one still picture made first** is shown, the model starts the clip from one picture of your people and objects in the scene, which is made first and is part of the price. Press **Generate clip** to start.

## Sending their files to the AI service

To show them, their files are sent to the AI services the project uses: their pictures, and depending on the service also their short videos and a person's voice recordings. The first time a project needs this, the editor asks: tick that you agree and choose **Agree and continue**. You are asked once per project, and you can withdraw it in the project's **Avatars** tab. See [Sending media to AI services](../projects/avatars.md#sending-media-to-ai-services).

## While the clip is made

The clip is made in the background, so you can keep editing. It is listed under **Clips being made** with its state: **Waiting**, **Making the clip**, **Done**, **Did not work** or **Stopped**. **Stop** stops a clip that is still being made; only what the AI service already made is charged. **Clear** removes a finished entry from the list.

A finished clip is a new video file of the project and appears in the **Media** tab. A clip that shows a person is marked as AI-generated in the video file, and also shows a small **AI-generated** label in the picture unless that person's avatar asks for another way to disclose it. A clip that shows only objects is marked in the file, with no label.

Each person and object is checked again just before the clip is made. If one stops being ready meanwhile (for example, its consent is withdrawn or one of its files changes), the clip is not made and you are not charged for it.

## When a clip cannot be made

The dialog says why, and nothing is charged:

- **The model cannot show pictures.** Some video models make a clip from words only. The dialog names a model of the same family that can, with a button to use it when your workspace has that model.
- **Your plan does not include it.** Clips like these need a plan that includes making videos with AI, plus avatars of people or objects for what you chose. **See plans** shows your options.
- **Not enough credits.** **Add credits** takes you to billing.
- **Clips that show a person are not available in your workspace yet.** A workspace can have clips of objects before clips of people; a clip with objects only can still be made.
- **One of your choices cannot be used right now.** Check it in the project's **Avatars** tab.
- **Too many clips in a short time.** Wait a little and try again.

The assistant can make the same kind of clip for you, by the names of your people and objects; see [What the assistant can do](../projects/avatars.md#what-the-assistant-can-do).
