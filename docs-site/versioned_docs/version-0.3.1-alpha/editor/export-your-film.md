---
title: Export your film
sidebar_position: 3
description: How to render your timeline into a film, watch it, download it, find it in your project files, share its link, delete it, and export only one clip.
---

# Export your film

When your edit is ready, open **Deliver** (the editor's **More > Export** menu opens it). The editor's **Early preview** label stays on this page like everywhere else in the editor.

## Your film comes first

The top of Deliver shows your **latest finished film**: a player, the file name, how long it is, how big the file is and how long ago it was made. Under the player:

- **Download** saves the film to your computer under its own name.
- **Open in project files** takes you to your project's Files page with the film selected.
- **Copy link** copies the address of the film in your project. People who have access to the project can open it. It is not a public link.
- **Delete** asks first, then removes the film from your project files. Your timeline is not changed.

Below the buttons, a line tells you whether the film has your latest edits: "Last render: your latest edit", or "Last render: before your latest edits (3 changes since)".

## Choose a format and press Render

- **Format** is the shape of the film: Full HD landscape, 4K landscape, Vertical for phones, Square, Portrait 4:5, Master (large, for editing elsewhere) or Audio only. If the shape does not match your timeline, you are told the picture will be cropped or get bars.
- **Quality** is Draft (fastest, smallest), Good, or Best (cleanest, slower, bigger file).
- **Only the marked in/out range** renders just the part between your in and out marks.
- **Advanced** holds loudness normalising and the codec and speed choices for people who need them.

**Render** saves your latest edits first, so the film always contains what is on screen. If the save does not work, Render stays off and says "Save first:" with the reason, and you can try saving again from there.

## Set the file size

Under **Size and quality**, **How to set the file size** has three choices:

- **Steady look** keeps the picture looking the same. The file size depends on your video.
- **Look first, with a size limit** keeps the look and the file close to or under the video bitrate you set. It is a target, so the file can run slightly over.
- **Aim for a size** aims for the video bitrate you set, so the file ends up close to the size shown under the setting.

Set **Video bitrate (Mbps)** from 0.2 to 100 Mbps and **Audio bitrate (kbps)** from 64 to 320. The size shown is always approximate ("About"), because the real file depends on your video. An audio-only film has no video bitrate.

**Suggested for** fills the settings for a destination. These are suggested starting points, not a promise that a site accepts the file. YouTube's 8 Mbps is its published recommendation for 1080p; Instagram Reels and TikTok publish no bitrate, so those values are our own suggestions.

| Destination | Format | File size setting | Video | Audio |
|---|---|---|---|---|
| YouTube | Full HD landscape | Aim for a size | 8 Mbps | 320 kbps |
| Instagram Reels | Vertical for phones | Look first, with a size limit | 5 Mbps | 128 kbps |
| TikTok | Vertical for phones | Look first, with a size limit | 6 Mbps | 128 kbps |
| Small for email or chat | Your current format | Aim for a size | 2.5 Mbps | 128 kbps |

The bitrate does not change the price: a render costs the same at 2 Mbps and at 100 Mbps.

## What it costs

If your plan uses credits, the line next to Render says "About 12 credits, about 3 min" before you start. If you do not have enough, it says how many more you need and offers **Add credits**. If credits are not part of your plan, nothing is shown.

## Export only one clip

Select a clip, open Deliver and press **Export only "clip name"**. One press sets the range to that clip and starts the render. While it works, the panel says "Rendering "clip name"". Your in and out marks stay set to the clip afterwards.

## For another editor

Under the export links, **For another editor** prepares one package for a professional editor: the cut, your video files under their real names, a list of what is inside and a short note on how to open it.

1. Choose the editor: Premiere Pro, DaVinci Resolve, Final Cut Pro, another editor, or **Cuts only (EDL)**. Premiere Pro and DaVinci Resolve can take either of two file types; the card picks the first.
2. Choose **Include the video files** or **List files only**. The card shows what the package holds, for example "12 files, 1.4 GB". When the files together are too big for one package, the first option is switched off, the card says why, and **List files only** is chosen for you. Copy the originals yourself then.
3. For **Cuts only (EDL)** with more than one video track, pick which video track to write.
4. Press **Download the package**. The file is saved by your browser. If something stops it (for example two packages are already being prepared), the card says so in words.

Hand-off packages carry an **Early preview** label. We have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut Pro, so treat them as a first version.

**How to open it** gives three lines per editor. We have not opened these packages in every editor, so the lines say "Should open in ..." and ask you to tell us if it does not.

On a phone the card only says that this is a desktop job; the export links above still work.

## Importing a timeline from another editor

On your timelines page, **Import** reads an OpenTimelineIO file. When it is done, a sheet says what happened: the files it matched (and how: same file as the export, matched by name, matched by name and length) and the clips whose media is **offline**, each with the reason:

- "No file with this name in the project"
- "Two files share this name; pick one"
- "The file's length differs from the clip"

Each offline clip is a gap of its own length with a red marker. **Relink** puts a project file there; for two files with the same name **Pick a file** lists them with their length and size. Relinking is one step you can undo. The switch **Import fails if anything is missing** (off by default) refuses the whole import instead. Relinking a whole folder at once is not available yet.

## The render list

Every render of this timeline is listed under the settings: Waiting, Rendering (with progress and a **Stop** button), Finished, Did not finish or Stopped. A render that did not finish says why in plain words and offers **Try again** (with the settings shown now) and **Remove**.

## On a phone

Tap **Export** in the top bar. The sheet opens full height: pick a format (Vertical for phones comes first), check the price line, and press **Render**, which stays at the bottom of the sheet so you never scroll for it. The range, the file name, how captions are delivered and the loudness choices are under **More options**.

While a film renders you can leave the page. When you come back, the list is refreshed. If a film finished while the sheet was closed, **Export** shows a green **Ready** mark and you get a message on screen. The finished film plays right there. **Download** saves it under its own name, and **Share film** hands the file to another app when your phone allows it (otherwise **Share link** shares the project link, and if your phone cannot share at all the button is not shown). We do not send an email when a film is done.

## Downloading from other pages

On your project's Renders tab and on a workflow step's result, **Download** saves the video as a file instead of opening it in the browser.
