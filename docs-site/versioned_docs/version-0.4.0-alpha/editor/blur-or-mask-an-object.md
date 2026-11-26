---
title: Blur or mask an object
sidebar_position: 8
description: How to blur, pixelate, highlight or outline a named object in a clip, such as a license plate or a face, from the editor or by asking the assistant, what it costs, what permission it needs and what to do when it cannot find the object.
---

# Blur or mask an object

You type the name of an object in a clip, for example "license plate" or "face". ReelBolt finds it in the video and puts an effect on it only: blur it, pixelate it, highlight it, or the reverse and apply the effect to everything except it.

## Before you start

- **Your plan.** The feature is not on the trial plan. It is available on Creator and above.
- **A clip on a picture track.** The clip must come from a file in your project or from a finished result.
- **A one-time permission for the project.** To find the object, the video (only this video) is sent to an outside AI service. Nothing else is shared. The first time you use the feature in a project, the editor asks you to agree. You can withdraw the permission at any time.
- **Length.** The video can be up to 10 minutes long.

## Add an effect from the editor

1. Select a clip on a picture track.
2. Open the **Objects** section of the clip's properties. On a phone, open the **More** sheet for the clip and choose **Blur or mask an object**.
3. Under **What should it find?**, type the object. You can also tap a quick pick: license plate, face or logo.
4. Choose an **Effect**: Blur, Pixelate, Highlight, Spotlight, Colour pop or Outline.
5. Optionally turn on **Apply to everything except the object**, which keeps the object clear and applies the effect to the rest of the picture.
6. Set the **Strength** (and, if shown, **Feather**, which softens the edge of the effect).
7. Check the price line, then select **Find and apply**.
8. If the editor asks **Send this video to an outside AI service?**, read the text, tick **I agree to send this video to find objects** and select **Agree and continue**.

While the search runs, the section shows **Looking for ...**. You can select **Stop watching**; the search keeps going. When it finishes, the effect is added to the clip in one step, so one undo removes it. Each effect then appears in the list **Object effects on this clip**, where you can switch it off, change its strength or remove it.

## Pick the object on the picture

Instead of typing a name, you can point at the object.

1. In the **Objects** section, select **Pick on the picture**.
2. Move the video to a moment where the object is clearly visible.
3. Tap the object, or drag a box around it. With the keyboard, use the arrow keys to move the marker (1% of the picture per press), Enter to confirm with **Use this**, and Escape to cancel.
4. Choose the effect and select **Find and apply**.

The section shows **Object picked at** the time you chose. Select **Pick again** or **Clear** to change it, or **Find by name** to go back to typing. On a phone the same pick is in the clip's **More** sheet. The same pick works for following a subject.

If nothing is under your tap, the section says **We could not find an object where you tapped. Tap its centre, or draw a box.** Nothing is charged for that. After 5 taps in an hour that found nothing, you have to wait before you can tap again.

## What it costs

The section shows the price before you run anything, as **About N credits**. If the same object was already found in the same video, it shows **Already found in this video: free**, and running it again costs nothing. Check your plan page for what your plan includes; this page does not list prices.

If you do not have enough credits, the section says **You do not have enough credits to find this object.** If your plan does not include the feature it says **Your plan does not include finding objects.**

## The preview shows it

The viewer shows the effect on the object, so you can check it before you export. The badge above the viewer tells you how close the preview is.

- **Outline.** When the object's outline is ready, the viewer follows the exact outline, as the finished video does.
- **Box.** While the outline is still being prepared, the viewer shows the effect inside a box that follows the object, and the badge says **Outline preview still preparing, showing a box.** The box turns into the outline by itself when it is ready.
- **Dimmed picture.** If the object has not loaded yet, the viewer shows the clip without the effect, dimmed, and the badge says why.

On the timeline, a clip with an object effect shows an **Object effects** badge. While the search runs, the clip shows a strip with its progress. See [Pages and panels](./pages-and-panels.md#badges-on-the-timeline).

Render the video (see [Export your film](export-your-film.md)) for the final result.

## Limits

- Up to 4 object effects on one clip. At the limit the section says **A clip can have up to 4 object effects. Remove one first.**
- Videos up to 10 minutes. A longer one is refused with **This video is too long to search for an object.**
- If the object cannot be found, nothing in your clip is changed.

Credits are returned for a search that fails because of a problem on our side. They are not returned when you cancel it or when the request itself was wrong. A search that ran to the end and found nothing has used its credits.

## Ask the assistant

In the assistant you can write "Blur the license plate in my interview clip". The assistant finds the timeline by name and the video by its file name (for example "interview.mp4"), uses every clip of that video on the timeline, and shows a card with the object, the effect, the clips it applies to and the credits, or that it is free. Approve the card to start the search.

- The assistant never gives the video permission for you. If the project has not agreed yet, it tells you to open the clip in the editor, choose **Objects** and agree once.
- It does not wait for the result. After a few minutes the effect appears on the timeline in the editor. If the object cannot be found, nothing changes and the editor tells you.
- If the same object was found before, the effect is added straight away and for free.

## If something goes wrong

| What you see | What it means |
| --- | --- |
| We could not find that object in the video. Try describing it differently, or pick another clip. | The search finished without a match. Use other words, such as "number plate", or another clip. |
| We could not find (object) in this video. Try other words. | The same, shown in the editor with the name you typed. |
| The search could not start. Try again in a moment. | The search did not begin. Try again. |
| The price could not be read. Try again in a moment. | The price did not load. Try again. |
| Allow this project's video frames to be sent to our object-detection service. Open the clip in the editor, choose Objects and agree once. | The one-time permission is missing. Agree in the editor, then try again. |
| This project has not allowed its video frames to be analysed. Agree to it in the editor and try again. | You withdrew the permission. Agree again. Withdrawing it also stops any search that was still waiting. |
| Your plan does not include object detection. Upgrade to use it. | The feature is not on your plan. |
| There were not enough credits to analyse this video. | Add credits and try again. |
| This video is longer than object detection can handle. Use a shorter clip. | The video is over 10 minutes (counted at 30 frames per second: a faster video counts as longer). If we cannot read its length, you are shown the highest possible price and are charged at most that. |
| This video has too many frames for object detection. Use a shorter clip or a lower frame rate. | The video has too many frames to search. |
| We could not open that video. It may have been removed. | The file behind the clip is gone. |
| The object-detection service had a problem. Nothing was lost; try again in a few minutes. | A problem on our side or at the outside service. Try again later. |
| The object-detection service returned something we could not use. Try again in a few minutes. | Same as above. |
| Object detection is not available on this installation, so the video could not be analysed. | The feature is switched off where you run ReelBolt. |
| Object detection is not set up for this workspace. | The feature is not set up for your workspace. |
| The analysis was cancelled. | The search was stopped before it finished. |
| The search did not work. Try again. | Any other failure. Try again. |
| The permission could not be withdrawn. Try again in a moment. | The withdraw request failed. Try again. |
