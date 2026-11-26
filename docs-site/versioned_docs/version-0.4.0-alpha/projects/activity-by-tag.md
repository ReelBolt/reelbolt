---
title: Activity by tag
sidebar_position: 5
description: See how many projects, runs and credits each tag used over 7, 30 or 90 days, and how to read the totals.
---
# Activity by tag

If you tag projects with customer names (see [Organizing projects](./organizing-projects.md)), **Activity by tag** shows what each customer used. Open it from the link on the projects page, or go to `/app/tags`.

## Read the table

Pick a time window: **7 days**, **30 days** or **90 days**. Each tag gets a row with:

| Column | Meaning |
|---|---|
| **Projects** | Projects that carry the tag |
| **Runs** | Workflow runs started in the window in those projects |
| **Passed** / **Failed** | How those runs ended |
| **Credits** | Credits those projects used in the window |

A run belongs to the window by the time it started. Runs from every kind of workflow are counted.

Below the tags you see a **Workspace total** row and an **Untagged** row for projects with no tag.

## Why the tags can add up to more than the total

A project with several tags is counted under each of them. If a project has the tags `acme` and `spring`, its runs show in both rows, so the tag rows can add up to more than the **Workspace total**. Use the workspace total for the real figure.

## One tag in detail

Click a tag to see its projects with their runs and credits, and its 50 most recent runs. Click **Open run** to go to a run.

## When credits are missing

If your installation has billing turned off, the **Credits** column is hidden and you still see projects and runs.

## Who can see it

Anyone in the workspace who can see usage can open this page. It shows credits, never money amounts.
