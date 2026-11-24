---
title: Fix colour and exposure
sidebar_position: 10
description: How to use the round colour wheels and the Fix exposure button in the editor to change the colour of shadows, midtones and highlights and to brighten or darken a clip.
---

# Fix colour and exposure

The **Colour** page has two tools for changing how a clip looks: round colour wheels for the colour, and a **Fix exposure** button for a clip that is too dark or too bright. The wheels need a tablet or a larger screen. **Fix exposure** also works on a phone.

## Colour wheels

Open the **Colour** page and the **Primaries** tab. You see four wheels:

- **Lift (shadows)** changes the dark parts of the picture.
- **Gamma (midtones)** changes the middle tones, where most of the picture is.
- **Gain (highlights)** changes the bright parts.
- **Offset** shifts the whole picture.

On each wheel, drag the dot away from the centre to add colour. The direction is the colour and the distance is how much. Near the centre the dot moves in smaller steps, so small changes are easy. The slider under each wheel makes that part of the picture lighter or darker. The viewer and the scopes follow your drag live.

- **Clear the colour:** double-click the wheel, or click **Reset** for that wheel.
- **Nudge:** click a wheel and use the arrow keys. Hold Shift for bigger steps.
- **Snap to a main colour:** hold Shift while you drag to snap to red, yellow, green, cyan, blue or magenta.
- **Undo:** one drag is one undo step.

The exact numbers are still there under **Exact values** when you want to type them.

## Fix exposure

Select a clip on the **Colour** page and click **Fix exposure**. On a phone, open the **Look** sheet and tap it there.

The editor looks at the clip with your grade switched off. It checks the frame under the playhead and up to four more frames across the clip. It only looks at the clip's own picture, so black bars around it do not count. This takes a few seconds, and you can click **Cancel** while it measures.

It then adds one adjustment named **Auto fix** at the top of the clip's list. It changes brightness only, not colour. Click **Fix exposure again** and the same adjustment is replaced, not added again.

When it finishes it tells you what it did, for example "Brightened by 0.8 stops". It can also tell you:

- **This clip already looks fine.** Nothing was changed.
- **This clip is a single flat colour; nothing to fix.**
- **Could not read the picture.** Keep the clip on screen and try again.
- **This clip already has the most adjustments it can hold. Remove one first.** The editor never removes one of your adjustments for you.

If the clip is in a colour group that has a grade, the fix goes into the group and changes every clip in it. A note under the button says **Fixes the whole group**.

If you change the clip while it is measuring, you can choose **Apply anyway** or **Measure again**.

## Flat footage from a camera

If your footage is flat and grey because it is log footage, set the camera profile first, then use **Fix exposure**. The fix measures the clip after the conversion. See [Flat footage from your camera](./camera-profiles.md).

## What the viewer shows

The viewer draws lift, gamma, gain and offset the way the export does. Other tools, such as curves, LUTs and secondaries, can still look slightly different in the viewer than in the export, and the page says so where they do.
