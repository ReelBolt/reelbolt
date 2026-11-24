---
title: Cut to the music
sidebar_position: 9
description: How to find the beat of a music clip, check and fix it, and move, split or fit your clips to it in the editor.
---

# Cut to the music

**Cut to the music** makes your cuts land on the beat. Pick the music, let the editor find the beat, then choose what to do: move your cuts onto it, split clips on it, add markers to it, or lay clips out to it.

It is in the timeline toolbar, the music icon beside **Remove silence**. On a phone, open **Captions** in the bottom bar and tap **Cut to the music**.

## Pick the music

Choose a clip with sound in **Music**. The editor picks one for you: a clip you selected, or the first clip with sound on the timeline.

A clip that was made by a step has no file of its own, so the editor cannot find its beat. Use a clip from your media instead.

## Find the beat

Press **Find the beat**. This is free and reads only the music file. The editor shows **Waiting to start…**, then **Listening for the beat…**.

When it is ready, a line shows the tempo ("120 BPM, medium") and how many cut points the music has. Two warnings can appear:

- **The beat is not clear.** The beat is hard to hear in places. You can still go on, but check the cut points before you apply.
- **The beat changes during the track.** The tempo moves during the track. Check that the cuts land where you want them.

If the beat is not found, the editor says so and offers **Try again**.

## Check and fix the beat

- **Tempo**: **Half**, **As found** or **Double**. If the track sounds half or twice as fast as the line says, the editor offers **Use half tempo** or **Use double tempo**.
- **Nudge (10 ms steps)**: **Earlier** and **Later** move every beat a little, up to half a second each way. Use it when the cuts land just before or just after the beat.
- **Bar starts on beat**: tells the editor which beat is the first of a bar. Pick **Beat 1**, **Beat 2**, **Beat 3** or **Beat 4**. Change it when the cuts are on the right beats but every bar does not start where you expect.
- **Cut on**: **Every beat**, **Every two beats**, **Every bar**, **Every two bars** or **Every four bars**. This is the spacing of the cut points.

The line under the controls says what will change before anything is applied.

## Choose what to do

### Snap cuts

**Snap cuts** moves cuts that are close to a beat onto it. Choose **Tracks to snap** and **Snap within (frames)**; the line under it says about how many seconds that is. A cut with no room to move, or one outside the music, is left alone and counted as skipped.

### Split every

**Split every** cuts the clips you choose at every cut point. Choose **Clips to split** and **Shortest piece (frames)**; no piece shorter than that is made.

### Beat markers

**Beat markers** adds a marker at every cut point in the music. **Most markers** sets the limit, 500 by default. Bar markers are orange and beat markers grey, so you can tell them apart on the timeline.

### Fit clips to the music

**Fit clips to the music** lays the clips of one track end to end on the beat, like a montage. Choose:

- **Track to fit**.
- **Bars per clip**: **Auto** fits each clip to the last cut point inside its length, or the next cut point when none fits, or pick 1, 2 or 4 bars.
- **Start at**: **The music start** or **The first downbeat**.
- **Put the strongest clip on the drop**: when the editor has measured movement in your clips and found a drop in the music, this puts the liveliest clip at the drop and fills the run before it. If the liveliest clip is the first one, the other clips go before the drop first (as many as end on or before it), the last of them is stretched to end exactly on the drop if its file is long enough, then the liveliest clip starts on the drop and the rest follow. If the clips before the drop cannot reach it, a gap is left and counted as skipped. Otherwise the clips keep their order.
- **Stop at the music end**: clips that would start after the music ends are taken off the timeline. Leave it off to keep them.

A clip is only lengthened as far as its own file allows. A clip that cannot reach the chosen point is lengthened as far as its file allows and counted as skipped. A clip whose linked sound or picture is on a locked track is left alone, together with its partner, and the other clips are placed around it. Clips that cannot be placed within the music are taken off the timeline.

## Hear it, apply it, undo it

The line under the controls says what will change. Press **Play the result** to watch the cut in the viewer first; nothing is kept yet, and **Apply** or **Cancel** sits above the picture. Press **Apply** to keep the change. The whole change is one step, so one **Undo** brings everything back.

## See the beats on the timeline

Once the beat is ready, **Show beats** appears in the timeline toolbar. It draws a line at each bar and a small tick at each beat. While the lines are shown, a cut you drag snaps to them, like it snaps to other cuts; snapping must be on. Press **Hide beats** to take them away. The lines are only a guide and are never saved in your timeline.
