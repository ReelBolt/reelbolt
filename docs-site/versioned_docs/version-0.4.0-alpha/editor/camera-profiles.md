---
title: Flat footage from your camera
sidebar_position: 9
description: How the editor turns flat, grey-looking log footage from a camera into normal colours, how it is applied automatically, how to turn it off, and how to use the viewing LUT.
---

# Flat footage from your camera

Many cameras can record "log" footage. It looks flat and grey on purpose, because it keeps more detail in the bright and dark parts of the picture. Apple Log on an iPhone 15 Pro, S-Log3 on a Sony, V-Log on a Panasonic and the log modes of Canon, DJI, ARRI, Fujifilm, Nikon and RED are examples.

The editor can convert that footage to normal colours at the very start, so your cuts, grade and export all start from a picture with normal contrast and colour.

## How it is applied

- **Automatically, when it is clear.** When the editor is sure about a clip, it converts the clip when you add it. The clip shows the badge "Converted from {camera profile} automatically". Click **Turn off** to undo it.
- **As a suggestion, when it is a guess.** For most cameras the editor only suggests. You see "Looks like {camera profile}. Convert to standard colours?" Click **Convert** to accept or **Not this one** to dismiss.
- **By hand.** Select a clip and pick a **Camera profile** in its settings. Pick **None** to turn it off. **Apply to all clips from this file** sets the same profile on every clip cut from the same file.

Footage that is already HDR (HLG or PQ) is never converted as log footage. If a clip has both, the HDR handling is used and the camera profile is ignored. Plain standard footage is left alone.

The conversion is part of your film: it is in the preview and in your export.

It is in the picture of your export of the finished film; exports that carry only the edit (EDL, SRT, VTT, FCPXML) do not include it, while an OTIO export keeps it.

## The viewing LUT

A viewing LUT only changes what you see in the viewer. It is useful when you want to judge flat footage with a look, without changing the film.

- Open the viewing LUT menu in the viewer and choose **Off**, one of your project's LUT files, or **Camera profile as view**.
- While it is on, the picture shows the badge "Viewing LUT on. Preview only, not in your export."
- It is never part of your export, a still, a review link or a hand-off. It is remembered on this device for this timeline only.
- It is not available on a phone.
- The scopes and reference stills always read the clean picture, without the viewing LUT.

## LUT files you can use

A LUT file is a `.cube` file in your project. The same files work as a viewing LUT and in the **LUT** tab of the colour tools. ReelBolt accepts three kinds:

- A **3D** `.cube` file, with 2 to 65 points per side.
- A **1D** `.cube` file, with 2 to 65,536 entries. If it has an input range, ReelBolt follows it.
- A **1D and 3D table in one file**, as DaVinci Resolve writes it: a 1D table first, then a 3D table, each with its own input range.

A file can be up to 16 MiB. ReelBolt checks the file when you pick it, and again when a preview or an export starts. If the file is not valid or uses a feature ReelBolt does not support, it is refused and the message gives the reason.

There is one approximation. If a file holds both a 1D and a 3D table and you set the intensity below 100%, ReelBolt combines them into a single 3D table of 65 points, so the result can differ very slightly from the full file. At 100% the two tables are used as they are.

## Limits

- **Not yet checked on every camera.** The profiles are built from the camera makers' published curves, but they have not yet been checked on real footage from each camera. Colours can be slightly off. The note is shown next to the profile.
- **The DJI D-Log M profile is approximate.** DJI does not publish its curve, so the profile stands in with D-Log.
- **The preview is very close, not identical.** The preview can differ from the export by tiny colour differences.
- **Timeline thumbnails stay flat.** The strip of thumbnails on the timeline is made from the original picture, so it can look grey even when the viewer looks right.
- **Detection is careful.** If your camera is not recognised, pick the profile by hand.

If a clip still looks too dark or too bright after the conversion, see [Fix colour and exposure](./fix-colour-and-exposure.md).
