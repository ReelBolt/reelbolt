---
title: Brand and product profile
sidebar_position: 10
description: What ReelBolt's brand and product profile is, how it is built from your design files, how changes are approved, and which parts use credits.
---

# Brand and product profile

A brand and product profile is ReelBolt's saved description of your product: its look and voice, and the way it is put together. ReelBolt uses the profile whenever it generates a video for the project, so the result follows your real colours, fonts and tone, and describes your real product. This page explains what the two profiles contain, how to build and import them, how changes are approved, how to see which version a video used, and what is free.

## What a brand and product profile is

A **brand profile** collects the things that make your product look and sound like itself: your colours, your fonts, the spacing you use between elements, and the voice you write in. A **product profile** collects the way your product is built: which framework it uses, which pages, screens and routes it has, which components it is made of, and which libraries it depends on.

ReelBolt keeps the two profiles for each project. Together they replace guesswork with a summary your team can read and correct, and every generated video can be checked against them.

## Building a brand and product profile

Open the project and go to the **Profile** tab, then choose **Build brand and product profile**. ReelBolt reads your design files first and works out as much as it can on its own, which costs nothing. Only the parts it cannot work out are filled in by an AI model, as a fallback. When the build finishes, choose **Save as drafts** to keep the result so you can review it; nothing is approved until you approve it. You can review the result, change it, and approve it before any video uses it.

Before you confirm, ReelBolt shows the estimated cost as a range, for example "between 12 and 80 credits". If the estimate is above the limit for one build, ReelBolt tells you so and points to the free route: import a design token file, a `DESIGN.md` file, a CSS file with your colours and fonts, or a zip exported from your design tool. Some video templates are written for a project that already has an approved profile; they stay switched off in the template list until the project has one.

## Shared brand library

A **shared brand library** is a set of brand values kept for the whole workspace. A project can inherit from it, so projects that do can work from one brand. An inheriting project starts from the shared values and can add its own values, or change inherited ones, for that project alone.

A value that is locked in the shared brand library wins over anything a project sets, so a project cannot change it. Owners and Admins of the workspace can change the shared brand library. Members can see it but not change it, and Billing members cannot open it.

Updating the shared brand library does not change a video that has already run. Each project still approves its own profile, and each run records the versions it used.

## Importing your design files

You can bring your existing design files into a project instead of typing everything by hand. ReelBolt accepts the formats your team already keeps:

- **Design tokens** — the DTCG JSON format, including files written for Style Dictionary, and Tokens Studio exports.
- **CSS** — custom properties, and theme blocks written for Tailwind v4.
- **`DESIGN.md`** — a Markdown file, optionally with a small front-matter block that lists your colours or typography.
- **Component catalogues** — shadcn registry files and a Storybook index.
- **Figma variables** — when your workspace has the Figma import switched on, you can point ReelBolt at a file and it reads the variables once.
- **A brand-guide PDF** — ReelBolt reads the text of the guide and pulls the colours and typography it describes.

You can also upload a zip archive of these files. Archives and PDFs are unpacked and read by a separate, isolated helper that never touches your projects directly, and ReelBolt puts strict limits on how large and how deeply nested they may be.

You can also bring in a zip saved by a design tool — a shared library or a starter project, for example — and ReelBolt picks out the colours, fonts and tokens it recognises. ReelBolt works out where each tool keeps its colours and fonts by inferring the layout of its exports. Those layouts are inferred, and they have not been checked against a real export from any of these tools. Design tools also change how they export over time. For a few tools — including Claude Design, v0, Lovable, Penpot and Sketch — ReelBolt **may not recognise every export**. When it meets something it cannot read, it skips that part and imports the rest rather than failing. A Tailwind configuration file inside a zip is not read; add it to the project as a file instead. After a zip import, ReelBolt lists the files it skipped and why (scripts and web pages are never run, files too large to read, and files with an unreadable or unsafe name), with a few example names. File names with accents and non-Latin letters (for example `café.css` or `日本語.json`) are fine and are read like any other; only names with invisible or text-direction control characters are skipped, and two names that are the same once accents are normalised (a precomposed and a decomposed `é`, or names differing only by letter case) count as one file, so the second is skipped.

Imported files are treated as reference data only: ReelBolt reads their contents, it does not run them, and it does not fetch anything from the internet on their behalf.

## Exporting your profile

You can download an approved brand profile to use outside ReelBolt. Choose **Export** and pick the format your team needs:

- **DTCG JSON**: design tokens in the DTCG format, for tools that read design tokens. They keep the values of your tokens; if you import that file again, the notes, locks, modes, voice and font entries it contains are not read back.
- **CSS variables**: a CSS file of custom properties.
- **Tailwind v4 theme**: a theme block for Tailwind v4.
- **DESIGN.md**: a Markdown document describing the brand.
- **JSON**: the whole approved profile as ReelBolt stores it.

A format keeps only what it can express, so an export may not contain every part of your profile. A CSS file does not carry shadows or full typography definitions, and a `DESIGN.md` document keeps the written parts of a brand rather than every technical detail. A product profile exports as JSON only. Exporting is free.

## Staying up to date

Once a profile is built or imported, ReelBolt keeps watching the files it came from. When those files change — because someone uploaded a new version, or a connected folder or repository synced — ReelBolt re-reads them and works out whether anything actually changed. It also checks on a schedule, so a change is noticed even when no sync reported it.

When a change is found, ReelBolt prepares an update for you to review rather than applying it. Nothing about your profile changes until a person approves it. If you have already turned down the same change recently, ReelBolt stays quiet instead of asking you again. Values you have locked are never overwritten by an update: the change shows up in the comparison, marked **Locked, kept as is**, and your locked value wins. You set locks on a draft; an approved version cannot be changed, so to lock a value, start from a draft.

## Approving changes

Every change to a profile is saved as a **draft**. A draft is never used by a video on its own: a person reviews it and approves it first. Before you approve a draft, ReelBolt shows a short summary of what it changes compared with the current version (how many values were added, changed or removed) and asks you to confirm, because approving replaces the version every new video uses. A proposed update shows you what changed, and it stays waiting until you approve or reject it — a proposal nobody answers for long enough simply expires. When you approve a draft it becomes the version ReelBolt uses, and the version it replaced is kept so you can see what changed and when.

## Asking the assistant

The platform assistant can answer questions about a profile and prepare a change for you: ask it what your brand colours are, or ask it to update a value, and it will show you the proposed change. It can read the current profile and raise a proposal, but it cannot approve a change to your profile on its own — that always takes a person. Approving needs your own click on the approval card in ReelBolt or on the profile page, including when an outside tool is connected to your account; rejecting a change can be done through the assistant.

## Seeing which version a video used

Each run records the profile versions it was built with, and the run page shows them as **"Used brand v7 / product v3"**. If you later change or rebuild a profile, older videos still show the versions they used, so you can always tell which description of your product a particular video followed.

## What is free and what uses credits

Reading and importing your design files, keeping watch for changes, comparing them, building the parts ReelBolt can work out on its own, and approving a profile are **free**. The AI fallback that fills in the gaps uses **credits**: before the run starts, ReelBolt shows you the estimated amount, and you confirm it before any credits are spent. A project whose design files already describe it well may need no fallback at all, and then building the profile costs nothing. The fallback only fills in the part that is missing: if your design files cover the look but not the product, only the product part uses credits. If the estimate is above the limit for a profile build, the build does not start and ReelBolt shows you the estimate instead.
