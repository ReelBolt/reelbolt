---
title: Layer one video over another
sidebar_position: 12
description: How to put one video on top of another as a picture-in-picture with Place over, how to blend a clip with the picture below it, and how titles follow the order of your tracks.
---

# Layer one video over another

You can stack videos: a clip on an upper track is drawn over the clips below it. This page shows how to put a small video in a corner, how to blend two clips, and how titles fit in the same order.

## Put a small video in the corner

Use **Place over**. It puts the clip at the playhead, on the first track above the pictures already there, small, in the bottom-right corner.

1. Select a clip in the **Media** pool, or open the add-media sheet on a phone.
2. Move the playhead to where the clip should start.
3. Choose **Place over** in the pool item's menu or the placement buttons, or press `Ctrl/Cmd+Alt+P`.

If no track above is free, the editor adds one. The new clip is selected, so you can drag it to another corner or resize it right away. See [Move and resize on the picture](./move-and-resize-on-the-picture.md).

## When a clip covers the one below

If you drop a clip onto an upper track over a clip on a lower track, it is placed full size and hides the one below. The editor says **This picture covers the one below.** Select **Make it picture-in-picture** to shrink it into the corner. One **Undo** takes that back.

## Blend a clip with the picture below

Select a clip on an upper track and open its properties. Next to **Opacity** you find **Blend**. The choices change how the clip mixes with what is under it:

| Choice | What it does |
| --- | --- |
| **Normal** | The clip covers the picture below. |
| **Multiply** | Darkens. White stays clear, black stays black. |
| **Screen** | Lightens. Black stays clear, white stays white. |
| **Overlay** | Deepens the dark and light parts of the picture below. |
| **Soft light** | A gentle version of Overlay. |
| **Hard light** | A strong version of Overlay. |
| **Darken** | Keeps the darker of the two at every point. |
| **Lighten** | Keeps the lighter of the two at every point. |
| **Add** | Adds the light of the two. |
| **Difference** | Shows how much the two differ. |

A blend only works on a clip on a picture track. The lowest clip in the stack blends over black. The viewer and the finished video use the same formulas. Where a clip is partly see-through, a colour can differ by one level between the two.

## Titles follow the track order

A title, a caption or a colour card is drawn at the place of its own track. A title on a lower track sits under a video on an upper track, the same as in the finished video. To keep a title on top, put it on a track above the video.

## On a phone

The add-media sheet has **Place over**, and the clip's sheet has the **Blend** choice.
