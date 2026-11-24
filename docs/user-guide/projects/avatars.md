---
title: Avatars
sidebar_position: 6
description: An avatar is a consented likeness of a real person, or an object such as a product, made from their pictures and short videos. What each needs, the consent you give, variants, sending media to AI services, and how to withdraw or delete them.
---
# Avatars

An **avatar** is a likeness of a real person, made from a few of their pictures, short videos and recordings of their voice, kept in one project. It works like a cloned voice: the person must agree, you record that agreement in ReelBolt, and you can withdraw it or delete the avatar at any time.

The **Avatars** tab can also hold **objects**: a product, a vehicle, a building or anything else that is not a person, made from a few clear pictures of it. An object needs no one's consent; see [Objects](#objects).

Avatars are a new feature that is being rolled out, and they may not be enabled in your workspace yet. People and objects are switched on separately, so your workspace may have one and not the other. Making video clips that show the person is being tested first and may come to your workspace later than the Avatars tab; the avatars you prepare now work with it.

## What you need

- **The person's agreement.** Either you are the person, or you have their written permission. The person must be 18 or older. Avatars of children are not allowed, and neither are avatars of someone who did not agree or that impersonate a public figure.
- **At least one clear picture of the person.** A front-facing face, good light, one person in the picture, no sunglasses. JPEG, PNG or WebP, up to 30 MB, at least 256 pixels on each side.
- **Optionally, short videos and recordings.** A video of the person speaking to the camera (MP4 or MOV, 2 to 15 seconds, up to 50 MB) and short recordings of their voice (WAV or MP3, 2 to 15 seconds, up to 15 MB).

An avatar holds at most 8 pictures, 3 videos and 3 recordings, and a project holds at most 20 avatars. The files are ordinary files of the project: upload them first, then choose them when you make the avatar. Files that are still being checked after upload cannot be used until the check has finished.

## Making an avatar

Open the project and go to the **Avatars** tab, next to **Voices**. Choose the files, then say who the person is: their name, and whether this is you or someone who gave you written permission (you can attach the signed permission as a file of the project). Tick that the person is 18 or older, read the consent statement in full and tick it, then choose how long to keep the avatar (up to two years, one year by default). You can also link one of the project's voices to the avatar.

The consent statement is shown in your language. By ticking it you confirm that the person agreed to their face, body and voice being used to make AI videos and pictures in this project, and to these files being sent to the AI services the project uses for that purpose.

An avatar made before the statement covered pictures still works for videos. Its card says **Not yet confirmed for pictures**: to show that person in a picture too, choose **Confirm consent again** (or **Change files**) and confirm the current statement.

## Objects

An **object** is a product, a vehicle, a building, a place or anything else that is not a person. Add a few clear pictures of it, and you can show it in your pictures and videos. In the **Avatars** tab, switch to **Objects** and choose **New object**.

- **Files.** Up to 8 pictures and 3 short videos of the object, taken from the project's files. For each picture say what it shows: **Front view**, **Another angle** or **Close-up detail**. Clear, sharp pictures from several angles on a plain background work best. An object takes no voice recordings.
- **No people.** The object must be on its own. When your workspace can check pictures, each picture (and one frame of each video) is checked automatically, and a picture whose main subject is a person with a clearly visible face is refused with its name. Someone small, far away, blurred or turned away in the background does not count. If the check cannot be done right now, nothing is saved; try again in a few minutes.
- **The statement.** Instead of a person's consent you confirm a short statement: that the files show an object, a product or a place and no identifiable person, and that you have the right to use them to make AI videos and pictures in this project and to send them to the AI services it uses.
- **No keep-until date.** An object is kept until you delete it, and there is no consent to withdraw. Deleting an object removes the object itself and the pictures made from it for clips; its files stay in the project's files.
- **Plan.** Objects come with the Creator, Pro, Team and Enterprise plans. A project holds up to 20 objects, besides its 20 people.

## Variants

A **variant** is the same person or object with a difference: the same car in another colour, the same bottle with another label, the same presenter in another outfit. It sits under its original on the card, shows as, for example, **Car · Red**, and can be chosen wherever the original can. An avatar can have up to 10 variants. Choose **Add a variant** on the card:

- **From other files.** Choose the files that show the variant, give it a **Variant label** (a word or two, such as "Red") and say **What sets this variant apart** ("matte red paint, black rims"); the AI reads that description to show this variant. For a person these are new files of the person, so their consent is confirmed again; the person's name and whose likeness it is come from the original.
- **Make with AI** (objects only). Say **What to change**, tick the **Pictures to change** and, if you like, also remake the object's videos. The AI changes the object's own pictures the way you describe and saves the results as a new variant; the original stays as it is. Videos are made again from the changed picture, so their movement can differ from the original, and this costs more. The price is shown before you start, and the job runs in the background under **Being made**, where you can cancel it. If no picture could be changed, no variant is made and nothing is charged.

Deleting an original does not delete its variants: they stay in the project on their own.

## Sending media to AI services

To make a clip that shows your people or objects in the timeline editor, a picture of them in the picture editor, a variant with AI, or any of these with the assistant, their files are sent to the AI services the project uses: their pictures, and depending on the service also their short videos and a person's voice recordings (for a variant, the pictures and videos you tick). The first time a tool needs that, it asks: tick that you agree and choose **Agree and continue**. You are asked once per project.

The **Avatars** tab shows the choice under **Sending media to AI services**, with **Allow** and **Withdraw**. After you withdraw it, the next tool that needs it asks again; the assistant cannot give this agreement for you. It does not change workflow steps: each step keeps its own switch, and an app asks for its own permission when you start it (see [Allowing your media to be sent](../workflows/app-inputs.md#allowing-your-media-to-be-sent)).

## The workspace avatar library

People and objects you want in many projects can be kept once for the whole workspace, in the **Avatar library**. Import one into a project with **From the workspace library** in the **Avatars** tab; it stays linked to the library, which manages its name, files and consent. See [The workspace avatar library](avatar-library.md).

## Making a clip with an avatar

When it is available in your workspace, a **Generate Video** step can show one of the project's avatars. The **Spokesperson clip** and **Talking avatar (budget)** workflow templates are ready-made starts. You choose the avatar in one of two places:

- **On the app page, when you run.** An app made from either template has an **Avatar** field among its choices. It lists the project's avatars with their status (for example **Ready**); one that cannot be used is greyed out, with the reason. Pick one, then press **Run**. If you leave it on **None**, the clip shows no one from your **Avatars** tab.
- **In the step itself.** Open **Edit steps (advanced)**, open the **Generate Video** step and find its **Avatar** section. In **Avatar to show**, pick the avatar, or **None** to use the one chosen on the app page when the run starts. Under **References to send** you can untick kinds of file you do not want sent (at least one picture must stay ticked). **Speech the person says** takes the words from an earlier **Voiceover** step or from an audio file of the project; only a model that makes the person talk (as in **Talking avatar (budget)**) uses it, and any other model refuses the step.

An avatar works only on a step whose **What to generate** is **Inline prompt**, and never together with a first or last frame: in those cases **Avatar to show** is greyed out and says why. These fields appear only when avatars are enabled in your workspace.

Objects are listed in the same places, below the people, and a variant appears with its label (**Car · Red**). A clip that shows only objects is marked as AI-generated in the video file, with no label on the picture. **Avatar to show** picks one person or one object. To show up to three in one clip (at most one of them a person), use **Generate clip** in the timeline editor (see [Clips with your avatars and objects](../editor/avatar-clips.md)), or ask the assistant.

Describe in the prompt what the person does. A **Spokesperson clip** is not guaranteed to say exact words, even with voice recordings in the avatar: to make the person say a script, use **Talking avatar (budget)**.

Two permissions are needed, and neither replaces the other:

- the person's consent, which you recorded when you made the avatar;
- your permission to send that person's media, which you give for each run with the switch **Allow sending your media to AI services** in the **Permission** block above **Run** on the app page. (The step has its own saved switch too, **Allow sending your media to the video service**, just below the **Avatar** section of the **Generate Video** step: switch that on only if you want every run to be allowed.) In the assistant chat a run that sends an avatar's media always asks you on its card first. You can also ask the assistant to switch the step's saved switch on; it shows a card that names the step first.

What happens when the step runs:

- How the avatar reaches the video provider depends on the model. Some models take the avatar's files with every clip (its pictures, and on some services also its short videos and voice recordings), so the provider can keep the person or the object consistent. Other models start the clip from one still picture: ReelBolt first makes a picture of the avatar in the scene you describe, from its own pictures, and the clip starts from that picture. A clip cannot use an avatar and a first or last frame at the same time.
- A model that cannot take pictures at all cannot show an avatar. The step is refused before anything is charged, and the message names a model of the same family that can.
- Sending pictures and video costs extra on some models, and the still picture costs a little too, on top of the clip itself: the cost estimate includes both.
- The avatar is checked again just before each clip is made. If its consent is withdrawn, its keep-until date passes or its files change while the workflow runs, the next clip is not made and you are not charged for it; the step stops and says why. A clip that was already being made finishes and is charged.
- Every clip of a person is marked as AI-generated in the video file itself, and also shows a small **AI-generated** label in the picture unless you chose, for that avatar, to disclose it another way.
- A clip made with an avatar is only reused (instead of being made again) while the avatar is unchanged. After you withdraw consent or change the avatar's files, earlier clips are never reused.

## Avatars and objects in planned shots

Some workflows plan their own shots and then make one clip per shot, for example the extra AI shots of a footage edit. The AI that plans the shots can also show your project's people and objects where they fit your brief: up to three in a shot, at most one of them a person, and only ones that are ready. It does this only when the run may send your media: switch on **Allow sending your media to AI services** in the **Permission** block above **Run** (or the step's own switch). With sending media off, planned shots show no one from your **Avatars** tab.

A planned clip that shows them is made like any clip with an avatar (see above), and its cost estimate already includes the still picture such a shot may need. When a workflow compares several takes of each shot (a **Dailies Room** step), the AI judges also check that each person and object looks like itself in the takes they pick.

## A talking clip on a budget

The **Talking avatar (budget)** workflow template makes a cheaper clip of the person saying a few sentences, in their own voice. It writes the words from your brief, speaks them in the voice linked to the avatar, makes one still picture of the person from the avatar's pictures, and then animates that picture so the person says the words.

- The avatar needs a **linked voice** (or choose a voice for the run). Link one when you make or edit the avatar.
- The clip lasts as long as what the person says: at least 5 and at most 15 seconds. Ask for 10 to 15 seconds in your brief.
- Up to three of the avatar's pictures are sent to the picture service to make the still, and the narration and the still are sent to the video service. Switch on **Allow sending your media to AI services** in the **Permission** block above **Run** when you start the app; it applies to that run only.
- The still is kept with the avatar, is reused for the next clips while the avatar is unchanged, and is deleted with the avatar. It never appears among the project's files.
- The cost is one still picture plus the seconds of video; the estimate shows it before you run.
- A workspace admin must first add a lip-sync model: under **Inference providers**, add the fal.ai lip-sync model with the capability **Lip-sync** and make it the default (see [Inference providers](../admin/inference-providers.md)). A model added under **Video Generation** is not used for this. With none set up, the run does not start and says that no lip-sync model is set up.

## One recipe: Avatar clip

Where recipe fields are enabled in your workspace, **Spokesperson clip** and **Talking avatar (budget)** are one recipe,
**Avatar clip**. Pick the avatar and the **Shape**, then choose with the **Lite mode (cheaper)** switch:

- **Off:** write **What they do** and pick a **Length** of 6 or 10 seconds. This is the spokesperson clip.
- **On:** write **What they say**. The words are spoken exactly as you wrote them, in the avatar's linked voice, and
  animated on one still picture, as in the budget clip above. Each paragraph (separated by a blank line) is one spoken
  line. Only the chosen path runs and is priced.

The models are under **More options**; the recommended one is already chosen. In Lite mode you can also pick the
**Picture model**, the model that makes the still picture of the person. Allow sending your media in the **Permission** block
above **Run**. Workflows you already made from the two old templates keep working unchanged.

## Pictures with avatars and objects

In the picture editor, **Create a picture** and **Edit with words** can show up to three of the project's people and objects, at most one of them a person. The picture is made from their own pictures, and the new layer says **AI-generated** and who it shows. When you export a picture that shows a person whose avatar asks for a visible label, the file gets a small **AI-generated** label. See [Make a thumbnail](../editor/thumbnails.md#show-your-avatars-and-objects-in-a-picture).

## When an avatar cannot be used

The **Avatars** tab shows whether each avatar is ready. An avatar cannot be used when:

- its consent was withdrawn;
- its keep-until date has passed;
- one of its pictures, videos or recordings was deleted, replaced or changed after you added it (choose the files again to fix it);
- one of its files was put on hold by a safety check;
- it has no usable picture of the person.

An object cannot be used when one of its files was deleted, changed or put on hold, when it has no usable picture, or when its statement is not on record (**Not confirmed**: choose its files again and confirm it). A person whose consent was confirmed before it covered pictures can still be used in videos, but not in pictures until you confirm it again.

A linked voice that can no longer be used does not stop the avatar; only the voice is affected.

## Withdrawing consent and deleting

**Withdraw consent** makes the avatar unusable at once, everywhere in the project. A withdrawn avatar is deleted automatically after 30 days. **Delete** removes the avatar, its files and the still pictures made from it straight away, and tells you what was removed. When the keep-until date passes, the avatar is deleted automatically; to keep it longer, confirm the consent again before that date.

Videos you already made with an avatar stay your files; delete them yourself if the person asks you to.

An object has no consent to withdraw, so its card has no **Withdraw consent**; use **Delete** when you no longer want it. Deleting it keeps its files in the project's files. An avatar imported from the workspace library is withdrawn or deleted in the library; in the project you can only **Remove from this project**, which deletes the copies of its files made for this project.

In the project's **Files**, a file an avatar uses is marked **Avatar:** followed by the avatar's name. Such a file cannot be deleted on its own: remove it from the avatar (**Change files**) or delete the avatar first. You get an email a week before an avatar you made reaches its keep-until date; you can turn that email off in **Settings > Notifications**.

## What the assistant can do

The assistant can list a project's people and objects (with their variants), tell you which ones can be used and, if one cannot, why, whether a person may be shown in pictures, whether sending media to AI services is allowed in the project, which ones are still being made, and the latest clips made with them. It can also show you the workspace avatar library.

With your OK on its card, the assistant can also:

- make an object from pictures and short videos already in the project, or take an object out of a video;
- make a variant of an object, from other files or by having AI change its pictures;
- bring a person or an object from the workspace avatar library into the project;
- make a video clip that shows your people and objects.

Making an object, a variant or an import always shows a card first, even when the assistant is set to go ahead without asking. For an object the card shows the object statement, and your OK confirms it. The assistant never makes, confirms again, withdraws or deletes a person's avatar: a person's consent is only ever given by you, in the **Avatars** tab. It never deletes an object or a library item and never saves to the library. If avatars are not enabled in your workspace, the assistant says so.

In the picture editor the assistant can create or edit a picture with your people and objects, by name.

When sending media is not allowed in the project yet, the assistant can still ask to send it for one clip, variant, object or picture you asked for: its card says so, and only your OK on that card allows it, for that one action. It does not change the project's choice in the **Avatars** tab. A tool connected to ReelBolt from outside (for example Claude Code) can do this only if you switched on **Let connected tools (MCP) allow sending my media to AI services** in **Settings**.

When the assistant runs or changes a workflow that shows an avatar, the confirmation card names the avatar and says that the person's pictures, videos and voice recordings are sent to the video provider. It switches on a step's permission to send media only when you ask it to, and only after you approve a card that names each step and what it sends. That card is always asked, even when you let the assistant act without asking. A connected tool such as Claude Code never switches on a step's permission; it can allow media for a single run only, and only if you switched on **Let connected tools (MCP) allow sending my media to AI services** in **Settings**.
