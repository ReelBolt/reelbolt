---
title: Make a thumbnail
sidebar_position: 2
description: How to open a picture in the thumbnail editor, move around the canvas, cut out subjects, add text, use AI tools, have an AI designer draw a graphic, let the assistant make changes, and export for YouTube, Photoshop, GIMP or Krita.
---

# Make a thumbnail

The thumbnail editor is a layered picture editor in your browser. You open one of your project's pictures, build it up from layers (the original, cut-out people or objects, text, other images, AI results), and export the finished picture as a PNG, a JPEG or another format, or as a Photoshop file that keeps every layer. Your original picture is never changed: every edit is a layer on top of it.

The picture editor is part of the editor's **early preview**, so its page shows an **Early preview** label. Click or tap it to read what that means: the editor is still being finished; your work is saved, but some tools may change.

## A picture editor and a video editor are not the same thing

ReelBolt has two editors, and they work on different material.

- The **picture editor** (this page, sometimes called the thumbnail editor) works on **one picture**: a
  stack of layers with a canvas, the original image, cut-out people or objects, text, AI results. You
  reach it by opening a picture, and its address contains that picture's id.
- The **video editor** works on a **timeline**: clips, sound and titles assembled into a film, usually
  from what a run produced. You reach it from a render or a project's editor, never from a picture. The
  video editor is available as an Early preview.

The **Editor page** is neither of them: it is the list of what you can open, with **Videos**
and **Pictures** tabs. Its rows open one editor or the other, and nothing is
being edited while you are only looking at the list.

The assistant can change a picture for you (layers, text, canvas, AI retouching) but it does not edit a
timeline: ask it to explain an edit or to change the workflow that produced it instead.

## Open a picture in the editor

You can reach the thumbnail editor from two places.

- **From the Editor page.** Open **Editor** and choose the **Pictures** tab. Every picture you have started in the project is listed there with its layer count; click one to continue it. To start a new one, pick a shape under **New picture** (Square, Post 4:5, Story 9:16, Landscape 16:9 or Custom), give it a name and press **New picture** for a blank canvas. To start from a photo, pick one of your images and press **Open in the picture editor**, or press **Upload a photo** to add one from your phone or computer. Above six pictures a search box appears.
- **From your project's files.** Open the project, go to the **Files** tab, open the menu next to a picture and choose **Edit as picture**.

Each picture shows its latest export as its small image (or the photo it came from). Next to each picture is a **⋮** menu with **Rename**, **Start a new one from this photo** and **Delete picture**. Deleting asks you to confirm and removes the picture and its layers; the photo it was made from, and any export you saved, stay in your project's files. The picture's name is the title of its editor page.

The first time you open an image, ReelBolt starts a new layered picture from it. After that, opening the same image takes you back to that picture instead of making a second one. Only pictures can be opened this way: a video, or a Photoshop or OpenRaster file you exported earlier, does not offer it.

A new picture opens at your photo's own size and shape, so all of the photo is in it, not a crop. A photo whose long side is more than 4096 pixels is scaled down to 4096 with its shape kept.

If you later delete the photo in your project's **Files**, the delete dialog tells you how many pictures use it. Each of them keeps its own copy, so they still open and export.

Keeping those copies uses storage. If your plan doesn't have enough left, the photo is not deleted and Files tells you why. Delete some pictures or free up space, then try again.

## Find your way around the thumbnail editor

The canvas is in the middle. On the left are five tools: **Select and move**, **Move around the canvas**, **Text**, **Cut out a subject** and **Brush mask**. On the right are four tabs: **Layers**, **Text**, **AI** and **Canvas**. Above the canvas, **Undo** and **Redo** step back and forward through your edits, a label tells you whether your changes are saved, and **Export** saves or downloads the finished picture.

On a phone the tools sit in a row above the canvas and the tabs move under it.

Your changes are saved for you a moment after you stop editing. You do not press a save button. If a save fails, a message appears with a **Try again** button, and the editor warns you before you leave the page with unsaved changes. Going to another page, or closing the tab, right after an edit still saves it.

If someone else changes the same picture at the same time, the editor tells you, reloads the latest version, and drops the edits that could not be applied. It never silently overwrites their work.

## On a phone

On a phone the picture stays at the top and the panels open from the bottom of the screen. Tap Layers, Text, AI or Canvas under the picture to open one; drag the bar at its top (or tap it) to make it taller or shorter, and tap the cross or use the back gesture to close it. The picture stays visible above the panel, so you see each change as you make it.

Each layer in the list has an eye, a lock and a name, plus a menu with Bring forward, Send back, Bring to front, Send to back and Delete layer. Under Position and size, the four arrow buttons nudge the selected layer by 1 pixel, or by 10 when you choose 10 px. Pinching with two fingers zooms the picture and never paints, erases or adds text.

## Change the shape, add your own image, export small

- **Shape.** On the **Canvas** tab, pick another shape (for example 4:5 for an Instagram post). The editor asks: **Fit what's there** scales and centres everything on the picture into the new shape, and one **Undo** puts it all back; **Keep positions** only changes the frame. **Original size** goes back to your photo's own size.
- **Upload an image.** On the **Layers** tab, press **Upload an image** to add a picture from your device. It is saved in your project's files and placed on the picture as a layer. If the upload fails, the editor says so in one sentence.
- **Export.** **JPEG**, **PNG** and **WebP** are listed first; **AVIF**, **Photoshop** and **OpenRaster** are under **More formats** (in those layered files, text becomes flattened pixels). Before you download, the dialog shows about how big the file will be, and it updates as you move the quality slider. Press **Under 2 MB** and it picks the highest quality that fits.
- **What AI tools cost.** Every AI tool shows **about N credits** before you run it, and **Used N credits** afterwards. If you don't have enough credits the tool is switched off with an **Add credits** link. If no AI service is set up, you are told to ask your workspace admin, or, if you are the admin, given a **Set up picture tools** link.

## Move around the canvas and zoom

The thumbnail editor's canvas has no edges. Your picture sits in a frame in the middle of it, and anything you drag past the frame stays visible beside it, a little darker. Only what is inside the frame is in a PNG, JPEG, WebP or AVIF export.

- **Move around:** hold **Space** and drag, drag with the middle mouse button, choose the **Move around the canvas** tool, or drag with two fingers on a touch screen.
- **Zoom:** turn the mouse wheel, pinch on a trackpad or touch screen, or use the **−** and **+** buttons in the corner of the canvas.
- **See the whole picture:** press the fit button in the corner of the canvas. The percentage between the zoom buttons shows the picture at its actual size.

When the canvas has keyboard focus, **+** and **−** zoom, **0** fits the whole picture and **1** shows it at actual size.

## Move, resize and rotate layers

Choose **Select and move** and click a layer on the canvas. A frame with handles appears.

- Drag the layer to move it. Hold **Shift** to keep the move straight across or straight up and down.
- Drag a corner handle to resize it. A layer always keeps its proportions.
- Drag the round handle above the layer to rotate it. Hold **Shift** to turn in steps of 15 degrees.
- Use the arrow keys to nudge the selected layer by one pixel, or hold **Shift** for ten.

A locked layer can be selected but not moved. Unlock it from the layers panel first.

## Manage layers

Open the **Layers** tab. The front-most layer is at the top.

- Drag a layer up or down to change its order, or use the small arrows beside it.
- The eye hides or shows a layer. A hidden layer is kept and does not appear in a flat export.
- The padlock locks a layer so it cannot be moved by accident.
- Select a layer to change its name, its opacity, how it blends with the layers under it (**Blend**), its role, and to flip it horizontally or vertically.
- The bin deletes a layer for good. Deleting cannot be undone, so the editor asks first and then clears the undo history.

To add another picture from the project as a new layer, for example a logo, pick it under **Add an image from this project** and press **Add to the picture**. A picture larger than the canvas is fitted inside it and centred, so you see all of it; drag a corner handle to make it bigger. On a touch screen the project's images are a list: tap one, then press **Add to the picture**.

On a touch screen the buttons in each layer row and the zoom buttons are larger, so a finger hits the right one.

## Add and style text

Open the **Text** tab and press **Add text**, or choose the **Text** tool and click on the canvas where the text should go. Select a text layer to change:

- the words, including line breaks, and **ALL CAPITALS**;
- the font and weight, from a short list that includes the ReelBolt fonts Chakra Petch and JetBrains Mono (a font is loaded the first time it is needed);
- the size, colour, alignment, line height and letter spacing;
- an optional outline and an optional shadow;
- an optional width at which lines wrap.

Text is drawn by your browser, so it stays sharp at any size. The editor tells you in plain words if a value is out of range, for example a font size above 600, and does not send an invalid value to be saved.

## Choose an AI tool

Open the **AI** tab and press **Choose an AI tool**. A list opens with every tool and one sentence about what it does; on a phone it fills the screen. Pick one, and the tab shows only that tool's controls. **Change tool** opens the list again.

- **Cut out a subject** makes a person or thing its own layer.
- **Remove background** cuts the main subject out with soft edges.
- **New background** keeps the subject and replaces everything behind it.
- **Erase** and **Fill** work on an area you paint with the brush.
- **Edit with words** makes a change you describe, such as "make the shirt red".
- **Expand** grows the picture past its edges to a new shape, such as a 9:16 reel cover or a square post; see [Expand a picture to a shape](#expand-a-picture-to-a-shape).
- **Upscale** makes the picture sharper and twice as large, twice as wide and twice as tall. It works on pictures up to 5040 × 2160 pixels; a bigger picture is refused with a message, and nothing is charged.
- **Relight** changes the lighting.
- **Create a picture** makes a new picture from a description and adds it behind your other layers.
- **Design a graphic** has an AI designer draw a title, badge, price tag, arrow or shape for you, with a see-through background. See [Design a graphic](#design-a-graphic).

Select the picture layer to work on first; everything except **Create a picture** works on the selected layer. Every AI tool is a paid call and uses credits; see [Plans and credits](../account/plans-and-credits.md). After a run, the tool shows which AI service answered.

If your workspace has no AI service set up for a tool, the tool says so and stays switched off, and the list marks it as not set up. Each tool can use its own AI service, so one tool can be set up while another is not. **Cut out a subject** uses the same service as **Remove background**, so it tells you up front when that one is not set up. An administrator can add them under **Admin, Inference providers**; see [Picture editor tools](../admin/inference-providers.md#picture-editor-tools-one-capability-per-tool).

**Cut-outs stay cut out.** When you use **Erase**, **Fill**, **Edit with words**, **Relight** or **Upscale** on a layer with a see-through background (a cut-out, or a PNG with transparent parts), the result keeps the same see-through areas, even though most AI services answer with a solid picture. **Fill** also keeps whatever you painted into a see-through area, so you can add a hat above a cut-out head. **New background** and **Expand** fill the background on purpose, so they do not. If the AI returns a picture of a different shape, the transparency cannot be lined up with it; the editor tells you so, and the result is added as it came.

If an AI service refuses or fails, the message says which tool did not work, that nothing was added to your picture, and what the service said. When the service failed or ran out of time, the editor also reloads the picture, in case a result arrived late.

What you type into a tool stays there while you look at another tab.

## Expand a picture to a shape

**Expand** makes a picture bigger and lets the AI continue the scene into the new space. It is how a landscape photo fills a tall 9:16 reel cover, or a portrait fills a 16:9 thumbnail.

1. Select the picture layer and choose **Expand**.
2. Keep **Fit a shape** and pick the shape. The first choice is your design's own shape when the picture does not have it yet, so for a 9:16 cover it is already 9:16. The panel says where the picture grows, for example "Adds picture above and below".
3. If the AI service takes a description, you can say what should appear, such as "more of the same street". Some services do not take one; the panel then shows no description field and says so.
4. Press **Expand the picture**. The result is a new layer placed so that the original part sits exactly where your picture was.

To grow only some sides, choose **Pixels per side** and type how many pixels of the picture to add on each side. Each service adds only so much to one side; the panel shows that limit and keeps the numbers within it. For a bigger change, use **Fit a shape**.

**Large photos.** A photo straight from a phone can be larger than some AI services accept. The editor always sends the full picture when the service accepts it. When it does not, the editor sends the picture at the size it appears in your design, and tells you so with a short note after the run, for example "This picture was larger than the AI service accepts, so it used the picture at the size it appears in your design (1247×831)." The result still lands in the right place and at the right size.

## Watch an AI tool work on the canvas

While an AI tool works, the part of the picture it is changing goes soft and shimmers on the canvas, right where it is: the area you painted for **Erase** and **Fill**, the selected layer for most tools, and the whole frame for **Create a picture**. When the result arrives, the changed part comes out of the blur into focus in under a second.

If your device is set to reduce motion, the area is shown blurred without the shimmer, and the result appears straight away. An AI edit can take a minute; if it takes longer than about a minute and a half, it stops with a message and you can try again. Every result is added as a **new layer** above the one you started from, so your original stays untouched and you can hide or delete the result.

## Cut out a subject

Select the picture layer to cut from, then choose the **Cut out a subject** tool on the left, or pick **Cut out a subject** under **Choose an AI tool**. Any layer with pixels works: the original, a picture you added, an AI result such as one made with **Create a picture**, or an earlier cut-out. With no layer selected, the original picture is used. The panel says which layer you are cutting from; marks you made are cleared when you select a different layer, because they belong to that picture.

You can say which subject you mean in any of these ways, and combine them:

- **Click** on it.
- **Leave something out**: choose **Leave this out** above the marks list, then click or tap it (with a keyboard, Shift-click does the same). Choose **Cut this out** to go back to marking the subject.
- **Drag a box** around it.
- Or **type its name**, for example "the person on the left".

Turn on **Give me up to 4 options** if you want several candidate cut-outs, each added as its own layer. Press **Cut out**. Each cut-out becomes a new layer with a transparent background, placed directly above the layer it was cut from, at the same position and size, so you can move it separately.

Cutting out a subject sends the picture to an AI provider and uses credits. Every press is a paid call.

## Design a graphic

**Design a graphic** is for the parts of a thumbnail that are designed rather than photographed: a bold title treatment, a "NEW" badge, a price tag, an arrow pointing at something, a big stylised number, a block of shapes behind some text. An AI designer draws it for you and adds it as a new layer with a see-through background, so it sits on top of your picture and you can move and resize it like any other layer.

1. Open the **AI** tab, press **Choose an AI tool** and pick **Design a graphic**.
2. Describe what you want, with the exact words it should show, for example: *a yellow sticker badge that says "NEW" in heavy black letters, slightly tilted*. Mention your brand colours or a style (minimal, bold) if you have one.
3. Choose where it goes (**Centre**, **Top**, **Bottom right** and so on). Leave the size on **Fit the picture** to let the designer place it on the whole picture, or choose **Custom** to give it a box of a set size.
4. Check the cost line, then press **Design it**.

Designing takes a few minutes, because the designer draws the graphic, checks it and only then hands it over. You can keep editing meanwhile: the place where the graphic will land shimmers on the canvas, the panel shows what the designer is doing, and the new layer appears there when it is ready. The words appear exactly as you wrote them.

**Brand fonts.** A designed graphic uses a font from the set installed in ReelBolt. If your brand uses a font that is not installed, the designer matches it to the closest installed font. Your brand's fonts are listed in its brand profile; see [Brand and product profile](../design-system.md).

**Change a graphic later.** Select a graphic the designer made and choose **Change this graphic** (in the panel, or next to the layer in the **Layers** tab). Say what should change, for example *make it bolder* or *use our brand blue*. The designer starts from its own design rather than from scratch, and the new version lands where you placed the old one; the old one is hidden, not deleted, so you can bring it back.

Each design is paid and uses credits, like a short workflow run; the panel shows the estimate before you start, and if a design fails your credits are returned. If designing graphics is not available in your workspace, the panel says so before you type anything: either the installation cannot design yet, or no chat AI provider is set up for the designer (an administrator adds one under AI providers).

## Ask the assistant to edit your picture

The assistant can work on the picture you have open. Open it with the assistant button in the header and ask in your own words, for example:

- "Make the title bigger and move it to the top left."
- "Hide the logo and change the background to dark blue."
- "Remove the car on the right." (the assistant marks the area to erase)
- "Design a red SALE badge for the top right corner."

The assistant first looks at your picture's layers, then shows a card with a concrete change: which layers, where, with which words. **Nothing changes until you press Allow** (unless you have switched **Ask before changes** off; see [the assistant](../assistant/index.md)). For a paid AI tool or a designed graphic the card shows the estimated cost and its button reads **Allow and use credits**. Once you allow it, the change is made and the editor shows it straight away: the changed layer is selected and comes into focus, and a message says what the assistant did. A layer the assistant added can be removed with **Undo**. The assistant can only change pictures in projects you can edit, and it writes words that appear in your picture exactly as you gave them.

## Paint a mask to erase or fill

Select a picture layer, then choose the **Brush mask** tool on the left, or pick **Erase** or **Fill** under **Choose an AI tool**. Paint over the part you want to change; the painted area shows in red. Choose **Rub out** to take area back out of the mask, and use the slider for the brush size. **Clear** removes the whole mask.

**Erase** removes the painted area and fills the gap to match. **Fill** puts what you describe there instead. The mask is only used for the next Erase or Fill, and is cleared when it succeeds.

## Hide parts of a layer with a mask

A mask hides parts of a layer without touching its pixels. You can add one to any picture layer (the original, a cut-out, an AI picture or an image you uploaded). Text layers have no pixels, so they have no mask.

1. In the **Layers** list, open the mask button on the layer (on a phone, the three-dot menu) and choose **Add mask**.
2. The mask panel opens. Choose **Hide** to paint parts away or **Show** to bring them back. Hold Alt for one stroke to switch. Set the brush size and how soft the edge is.
3. Paint on the picture. **Undo stroke** and **Redo stroke** step through your strokes (Ctrl+Z does the same while you paint).
4. Press **Done** to save the mask. If saving fails, your painting stays open and the panel says why; press **Try again**. **Cancel** throws the painting away, after asking.

A layer with a mask shows a small picture of it in its row. Press it to keep painting. The layer menu also has **Invert mask** (swap hidden and shown), **Delete mask** (the layer comes back whole) and **Show mask only** (look at the mask as black and white). Exports, copies of the picture and the layered files all use the mask; the original pixels are never changed.

## Set the canvas size and background

Open the **Canvas** tab.

- **Shape** sets the proportions: 16:9 for YouTube, 9:16 for Shorts and Reels, 1:1 square, 4:5 for Instagram, 4:3, 3:2, 2:3 and 21:9.
- **Size** sets how many pixels the picture has, named by its short side: **720p**, **1080p**, **1440p** or **4K**. At 16:9, 1080p is 1920 × 1080 and 4K is 3840 × 2160.
- **Original size** sets the canvas to your photo's own size (long side at most 4096 pixels) and puts the whole photo back in the frame. It is one step, so **Undo** takes it back.
- **Exact size** takes any width and height from 16 to 8192 pixels. Press **Use** to apply it; the swap button turns a landscape size into a portrait one.

Your layers keep their positions when the size changes, so a bigger canvas leaves empty room instead of stretching your picture, and a smaller one leaves part of a layer beside the frame. A very large picture may not open on some phones; the tab tells you when you pass that size.

Turn off **Transparent** to choose a colour behind all the layers. Leave it on to export a PNG with a see-through background.

## Export your thumbnail

Press **Export** above the canvas and choose a file type:

- **PNG**: best quality, keeps see-through areas. Good for YouTube and for editing later.
- **JPEG**: a small file every site accepts. See-through areas become your background colour, or white.
- **WebP**: smaller than PNG at the same look, and keeps see-through areas.
- **AVIF**: the smallest files. It is only offered when your browser can make it.
- **Photoshop (PSD)**: opens in Photoshop and Photopea with every layer kept, named, in order, with its opacity, blend and visibility.
- **OpenRaster (ORA)**: opens in GIMP and Krita with every layer kept.

For JPEG, WebP and AVIF you can also set the **Quality**. Give the file a name, then press **Save to project** to keep it in your project's files, in the **thumbnails** folder, or **Download** to save it on your computer. The message in the dialog tells you the saved name and size. If a layer is still loading, the buttons wait and tell you which one. If a layer's image is gone (its file was deleted), the canvas shows a crossed-out box where it was, its row in **Layers** says **Image missing**, and the export leaves it out rather than waiting; the Export dialog names the layer it leaves out. If a layer's image could not be loaded for another reason (a dropped connection, a server error), the editor tries again a few times; if it still cannot load it, Export refuses and names the layer, so the file is never made without it. Check your connection, then press **Try again**.

PNG, JPEG, WebP and AVIF contain only what is inside the frame. The layered files also keep the parts of a layer that reach past the frame, up to half the frame's width or height on each side, so you can move them back in later. Text becomes a picture layer in a layered file, named after the text; it is not editable type there. A file saved to the project can be up to 200 MB; use **Download** for anything larger.

Exporting is not an AI call and uses no credits.


## Placing, lining up, cropping and merging layers

In the **Layers** tab, under the list:

- **Position and size**: type Left, Top, Width and Height in pixels and press Enter. Width and height keep the picture in proportion.
- **Line up**: turn on **Choose several layers**, tick the layers, then pick an align button (left, centre, right, top, middle, bottom) either **to the picture** or **to each other**. With three or more layers, **Spread out** makes the gaps equal.
- **Crop**: opens the picture with a box. Drag the corners and edges, or type the numbers, then press **Crop**. The part outside the box is hidden, not thrown away: open Crop again to widen it, or press **Remove crop**. Cut-out, brush and AI tools work on the whole picture, so remove the crop before using them.
- **Merge into one layer**: tick two or more layers and press it.
- **Delete key** removes the selected layer; **Undo** brings it back. Undoing something you just added makes it disappear from the layers list too.

Crop changes the same layer. Merge makes one new layer and puts the originals away. **Undo** reverses either one, in one step.

## After an AI tool runs

A bar tells you what changed and how many credits it cost. Use **Compare with before**, **Undo**, or **Try again**. When a cut-out finds several options, the first is shown: use the arrows to look at the others, then **Keep this one** or **Keep all as layers**. If a tool takes a long time (up to 90 seconds) and the connection drops, the editor checks again and shows the result if it finished; you are charged once.

In the **Pictures** list, the menu on each picture has **Duplicate**.
