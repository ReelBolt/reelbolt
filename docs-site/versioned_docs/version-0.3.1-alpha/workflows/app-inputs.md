---
title: App fields
sidebar_position: 1.5
description: The fields an app asks for before it runs (a prompt, a shape, a switch, a model), how to fill them, how to set up an app's own fields, and how to change one field of a finished run.
---

# App fields

Some apps ask for a few things of their own before they run: what the video should show, a shape, a length, a
**Lite mode (cheaper)** switch, or which AI model makes the clip. These are the app's **fields**. They appear on the
app's page, above **Run**, next to the usual choices such as language and notes.

Fields are always available. The Run box in the editor shows an app's fields in place of the free **Your brief** box;
an app without fields of its own keeps the usual choices and the brief.

## Filling in the fields

Each field has a name, sometimes a help line, and often an example in grey inside the empty box. The example is only
a hint: it is never sent.

- **Text.** Type in your own words. A long text shows how many characters are left near the limit.
- **Number** and **Length.** Pick one of the offered values, or type a number inside the limits.
- **Choice.** Pick one option. Some options have a short line under them saying what they do.
- **Switch.** On or off. The words under the switch say what each position does. One switch can change several
  things at once: **Captions** on a footage edit, for example, has your clips transcribed and the words burned in.
- **Shape.** Vertical, square, landscape or portrait, whichever the app offers.
- **Model.** See [Choosing a model](#choosing-a-model) below.

A field marked as required must be filled before the app can run. If you press **Run** without it, the page takes you
to the field and says what is missing. Some fields only show up after another choice: for example **What they say**
appears only when **Lite mode (cheaper)** is on.

An empty field that is not required keeps the app's own setting.

## More options

An app shows the fields most people change. The others sit under **More options**, closed until you open it; the
button says how many there are. The page remembers whether you left it open for that app.

## Choosing a model

A model field lets you pick the AI model a step uses, for example the model that makes a generated clip or the one
that writes the script.

- The recommended model is listed first and is used when you pick nothing.
- Each model shows its price in words compared with the others, such as **Cheapest** or **About 2x the price**.
  ReelBolt never shows a model's price in credits here; the total for the run is shown above **Run**.
- A model that cannot be used for this app is greyed out and says why, for example "Not available to your workspace"
  or "Cannot make this kind of clip".
- A model your plan does not include is greyed out too and says which part of the plan it needs, for example
  "Your plan does not include video generation" or "Your plan does not include avatars". If you send such a model
  anyway, for example from the assistant, the run does not start and the field says the same.
- On a phone, the models open in a panel from one button.

## The price follows your choices

The price above **Run** is worked out from what you filled in. Changing a model, a length, a shape, a number or a
switch updates it after a moment; typing text does not change it.

The credits set aside when the run starts follow the model you chose as well. A step that has run before in your
workspace sets aside what it usually costs, adjusted to the chosen model's price, so a cheaper model sets aside
less and a dearer one more. A voice model you choose is priced as that model, not the usual one.

A switch such as **Lite mode (cheaper)** can send the app down a different, cheaper path. The price and the checks
before the run then cover only the path you chose.

## Seeing what a run used

Each run in the app's history shows its first fields in a short line, for example *Lite mode (cheaper) · "Welcome to
Caffè Lume!"*. Open a run to see every field it used, with the names the app had at the time and a model by its name.

## Changing one field of a finished run

When a run is finished, **Change one thing and re-run** lets you change one or more fields, and the run's settings the
page shows (language, length, narration, your files, the notes), and make only the parts of the video that use them
again. Everything else is reused for free. See
[Changing an app's fields](request-changes.md#changing-an-apps-fields).

A field that picks another path (such as **Lite mode (cheaper)**) cannot be changed this way: run the app again
instead.

## Setting up an app's own fields

If you can edit the workflow, you can choose which settings people fill in on the app's page:

1. Open the workflow and switch to **Advanced**.
2. Press **App inputs** to see the app's page and a live preview of it.
3. Add a field with **Add a field**, or open a step and use **App fields** to turn one of its settings (a video
   prompt, a length, a model and so on) into a field.
4. Check **What changes when you save**, under the list: it says in plain words which fields are added, changed or
   removed and which settings start or stop using a field.
5. Press **Save Changes**.

Drag a field by its handle to move it, or use the arrows. A short text can become a long text (and back), and a number a
length (and back), from **Kind of field** in its **Edit**; for any other kind, add a new field and use it where the old
one was used.

A few more things the **App fields** of a step can do:

- **A switch or a choice for a setting of another kind.** Pick a switch or a choice for the setting, then, under each
  answer, what the step uses: for example an **Ending fade** switch that writes 800 ms when on and 0 when off. Leave an
  answer on **Keep the step's own** to change nothing for it. One switch can fill several settings, even on different
  steps (a **Captions** switch can ask the analysis for a transcript and turn captions on in the edit).
- **One item of a list.** A setting inside a list, such as the effect used on the first object to hide, has one row
  per item, so a choice can pick **Blur** or **Pixelate** for that item.
- **Words spoken exactly.** On a narration step whose lines are written in the step, **Words to speak** makes a text
  field whose words are spoken as typed. Each paragraph (separated by a blank line) is one spoken line, in order.
- **The picture of a talking avatar.** On a talking-avatar clip, **Picture model** lets people pick the model that
  makes the still picture the clip animates.

The full guide, including models, **Show only when**, the message for the AI steps and a cheaper path with a switch,
is in [Setting up an app's page](templates.md#setting-up-an-apps-page).

You can also ask the assistant, for example "let people choose the shape and type what happens in the video" or "add
a cheaper mode to this app". It shows you the fields it will add before anything is saved.

## Recipes with fields

Several recipes come with their own fields, such as **Call to action**, **What to hide** or **Caption look**. The
list is in [Recipe fields](../recipes/run-settings.md#recipe-fields).

A recipe whose page has a model field shows **Model choices** on its card in the gallery. Open it to see the models
each field offers before you create the app: the recommended one first, the price in words, and why a model cannot
be chosen. The assistant can tell you the same about a recipe.
