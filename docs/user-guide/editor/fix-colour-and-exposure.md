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

## Corrections: name, order and solo

A clip's colour is built from **corrections**, listed on the left of the **Colour** page. They are applied from top to bottom, so the order changes the result. A clip can hold up to eight.

- **Rename:** double-click a name (or select it and click **Rename**), type, and press Enter. Escape keeps the old name.
- **Reorder:** drag a correction to a new place, or select it and click **Move up** or **Move down**. With the keyboard, go to the grip at the start of the row, press Space to pick the correction up, use the arrow keys to move it, and press Space to drop it. Escape cancels.
- **Switch on or off:** use the switch at the start of the row.
- **Duplicate** makes a copy right after the selected correction. **Reset** returns it to no change and keeps its name.
- **Solo (S):** shows the picture with only that correction, so you can see what it does on its own. A line reading **Solo:** and the correction's name appears above the scopes and above the grade tools while it is on. Click **Leave solo**, click **S** again or press Escape to end it. Solo also ends when you select another clip or leave the **Colour** page.

Solo is only a way of looking. It is never saved and it never changes your export.

Each of the other changes is one undo step.

## The grade chain

Above the list, **Grade chain** draws the same corrections as a row of numbered boxes, from **In** on the left to **Out** on the right. If the clip is in a colour group, the group's corrections come first, because they are applied first.

- Click a box to select that correction. It is the same selection as in the list.
- A box with a dashed outline is a correction that is switched off.
- The soloed correction carries an **S**.
- Small symbols in a box show what the correction holds: primaries, curves, a LUT, a qualifier or a window.
- With the keyboard, use the arrow keys to move along the chain and Enter to go to the grade tools.

Click the arrow next to **Grade chain** to hide it. The editor remembers that on this device.

## Scopes

The scopes sit under the viewer on the **Colour** page. Use the three layout buttons to show **one scope**, **two scopes side by side** or **all four** (waveform, RGB parade, vectorscope and histogram). With one scope you pick which one; with two you pick the left and the right.

Drag the top edge of the scopes to make them taller or shorter (on a tablet, use the plus and minus buttons), or click the arrow next to **Scopes** to fold them away. The layout and the height are remembered on this device.

## Before and after

Three controls sit above the grade tools on every tab:

- **Bypass grade** shows the picture without any colour change. Click it again to bring the grade back.
- **Split before / after** shows the ungraded picture on one side and the graded one on the other. Drag **Wipe** to move the line. **Close split** ends it.
- **Reference still** lets you compare with a frame you kept. Click **Grab still** to keep the current frame, then pick it in the list and drag **Wipe**.

None of these change your film or your export.

## What the viewer shows

The viewer draws lift, gamma, gain and offset the way the export does. Curves, LUTs and secondaries (the colour and brightness ranges that limit a correction to part of the picture) show live as you change them and match the export. While a LUT file is still loading, the viewer shows the grade without it and the badge above the viewer says so. The **Colour** badge on the clip in the timeline shows the same.
