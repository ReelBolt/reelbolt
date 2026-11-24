---
title: Avatars
sidebar_position: 6
description: An avatar is a consented likeness of a real person, made from their pictures, short videos and speech. What it needs, the consent you give, how long it is kept, and how to withdraw or delete it.
---
# Avatars

An **avatar** is a likeness of a real person, made from a few of their pictures, short videos and recordings of their voice, kept in one project. It works like a cloned voice: the person must agree, you record that agreement in ReelBolt, and you can withdraw it or delete the avatar at any time.

Avatars are a new feature that is being rolled out, and they may not be enabled in your workspace yet. Making video clips that show the person is being tested first and may come to your workspace later than the Avatars tab; the avatars you prepare now work with it.

## What you need

- **The person's agreement.** Either you are the person, or you have their written permission. The person must be 18 or older. Avatars of children are not allowed, and neither are avatars of someone who did not agree or that impersonate a public figure.
- **At least one clear picture of the person.** A front-facing face, good light, one person in the picture, no sunglasses. JPEG, PNG or WebP, up to 30 MB, at least 256 pixels on each side.
- **Optionally, short videos and recordings.** A video of the person speaking to the camera (MP4 or MOV, 2 to 15 seconds, up to 50 MB) and short recordings of their voice (WAV or MP3, 2 to 15 seconds, up to 15 MB).

An avatar holds at most 8 pictures, 3 videos and 3 recordings, and a project holds at most 20 avatars. The files are ordinary files of the project: upload them first, then choose them when you make the avatar. Files that are still being checked after upload cannot be used until the check has finished.

## Making an avatar

Open the project and go to the **Avatars** tab, next to **Voices**. Choose the files, then say who the person is: their name, and whether this is you or someone who gave you written permission (you can attach the signed permission as a file of the project). Tick that the person is 18 or older, read the consent statement in full and tick it, then choose how long to keep the avatar (up to two years, one year by default). You can also link one of the project's voices to the avatar.

The consent statement is shown in your language. By ticking it you confirm that the person agreed to their face, body and voice being used to make AI videos in this project, and to these files being sent to the AI services the project uses for that purpose.

## Making a clip with an avatar

When it is available in your workspace, a **Generate Video** step can show one of the project's avatars. The **Spokesperson clip** and **Talking avatar (budget)** workflow templates are ready-made starts. You choose the avatar in one of two places:

- **On the app page, when you run.** An app made from either template has an **Avatar** field among its choices. It lists the project's avatars with their status (for example **Ready**); one that cannot be used is greyed out, with the reason. Pick one, then press **Run**. If you leave it on **None**, the clip shows no one from your **Avatars** tab.
- **In the step itself.** Open **Edit steps (advanced)**, open the **Generate Video** step and find its **Avatar** section. In **Avatar to show**, pick the avatar, or **None** to use the one chosen on the app page when the run starts. Under **References to send** you can untick kinds of file you do not want sent (at least one picture must stay ticked). **Speech the person says** takes the words from an earlier **Voiceover** step or from an audio file of the project; only a model that makes the person talk (as in **Talking avatar (budget)**) uses it, and any other model refuses the step.

An avatar works only on a step whose **What to generate** is **Inline prompt**, and never together with a first or last frame: in those cases **Avatar to show** is greyed out and says why. These fields appear only when avatars are enabled in your workspace.

Describe in the prompt what the person does. A **Spokesperson clip** is not guaranteed to say exact words, even with voice recordings in the avatar: to make the person say a script, use **Talking avatar (budget)**.

Two permissions are needed, and neither replaces the other:

- the person's consent, which you recorded when you made the avatar;
- the step's switch **Allow sending your media to the video service**, just below the **Avatar** section of the **Generate Video** step, which you switch on yourself. The app page warns you when it is off. The assistant can never switch it on for you.

What happens when the step runs:

- The avatar's pictures, short videos and voice recordings are sent to the video provider with every clip, so the provider can keep the person consistent. A clip cannot use an avatar and a first or last frame at the same time.
- Sending pictures and video costs extra on some models, on top of the clip itself: the cost estimate includes it.
- The avatar is checked again just before each clip is made. If its consent is withdrawn, its keep-until date passes or its files change while the workflow runs, the next clip is not made and you are not charged for it; the step stops and says why. A clip that was already being made finishes and is charged.
- Every clip of a person is marked as AI-generated in the video file itself, and also shows a small **AI-generated** label in the picture unless you chose, for that avatar, to disclose it another way.
- A clip made with an avatar is only reused (instead of being made again) while the avatar is unchanged. After you withdraw consent or change the avatar's files, earlier clips are never reused.

## A talking clip on a budget

The **Talking avatar (budget)** workflow template makes a cheaper clip of the person saying a few sentences, in their own voice. It writes the words from your brief, speaks them in the voice linked to the avatar, makes one still picture of the person from the avatar's pictures, and then animates that picture so the person says the words.

- The avatar needs a **linked voice** (or choose a voice for the run). Link one when you make or edit the avatar.
- The clip lasts as long as what the person says: at least 5 and at most 15 seconds. Ask for 10 to 15 seconds in your brief.
- Up to three of the avatar's pictures are sent to the picture service to make the still, and the narration and the still are sent to the video service. Switch on **Allow sending your media to the video service** in the last step yourself (**Edit steps (advanced)**, the **Generate Video** step); the assistant never does.
- The still is kept with the avatar, is reused for the next clips while the avatar is unchanged, and is deleted with the avatar. It never appears among the project's files.
- The cost is one still picture plus the seconds of video; the estimate shows it before you run.

## One recipe: Avatar clip

Where recipe fields are enabled in your workspace, **Spokesperson clip** and **Talking avatar (budget)** are one recipe,
**Avatar clip**. Pick the avatar and the **Shape**, then choose with the **Lite mode (cheaper)** switch:

- **Off:** write **What they do** and pick a **Length** of 6 or 10 seconds. This is the spokesperson clip.
- **On:** write **What they say**. The words are spoken exactly as you wrote them, in the avatar's linked voice, and
  animated on one still picture, as in the budget clip above. Each paragraph (separated by a blank line) is one spoken
  line. Only the chosen path runs and is priced.

The models are under **More options**; the recommended one is already chosen. In Lite mode you can also pick the
**Picture model**, the model that makes the still picture of the person. Allow sending media on the clip step of
the path you use. Workflows you already made from the two old templates keep working unchanged.

## When an avatar cannot be used

The **Avatars** tab shows whether each avatar is ready. An avatar cannot be used when:

- its consent was withdrawn;
- its keep-until date has passed;
- one of its pictures, videos or recordings was deleted, replaced or changed after you added it (choose the files again to fix it);
- one of its files was put on hold by a safety check;
- it has no usable picture of the person.

A linked voice that can no longer be used does not stop the avatar; only the voice is affected.

## Withdrawing consent and deleting

**Withdraw consent** makes the avatar unusable at once, everywhere in the project. A withdrawn avatar is deleted automatically after 30 days. **Delete** removes the avatar, its files and the still pictures made from it straight away, and tells you what was removed. When the keep-until date passes, the avatar is deleted automatically; to keep it longer, confirm the consent again before that date.

Videos you already made with an avatar stay your files; delete them yourself if the person asks you to.

In the project's **Files**, a file an avatar uses is marked **Avatar:** followed by the avatar's name. Such a file cannot be deleted on its own: remove it from the avatar (**Change files**) or delete the avatar first. You get an email a week before an avatar you made reaches its keep-until date; you can turn that email off in **Settings > Notifications**.

## What the assistant can do

The assistant can list a project's avatars, tell you which ones can be used and, if one cannot, why. It cannot make, change, withdraw or delete an avatar: consent is only ever given by you, in the **Avatars** tab. If avatars are not enabled in your workspace, the assistant says so.

When the assistant runs or changes a workflow that shows an avatar, the confirmation card names the avatar and says that the person's pictures, videos and voice recordings are sent to the video provider. It never switches on a step's permission to send media.
