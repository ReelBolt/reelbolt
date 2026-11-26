---
title: Privacy and crash reports
sidebar_position: 6
description: What the ReelBolt Runner sends when you opt in to crash reports, and how to see it before you decide.
---

# Privacy and crash reports

**Nothing is sent unless you turn crash reports on.** The Runner asks once. A "no" is remembered.

## What a report contains

A report is built from a fixed list of fields:

- your operating system and CPU type;
- the Runner version and the Go version it was built with;
- whether Docker or Podman is used, and in which mode;
- the kind of event (a crash, or a job the Runner refused to start), the type of job, a failure class and code, and the stage it reached;
- for a crash, the type of error and the names of the Runner's own functions, source files and line numbers where it happened;
- a random identifier for the report and the time it was made.

It never contains your files or folders, a project, a workspace, your name, your device name or the text of an error message. There is no field in the report that could hold them. Like any internet request, the server that receives a report can see the address it was sent from; ReelBolt keeps only a log line and a counter, not the report.

## See it first

Open **Settings → Privacy** in the Runner and choose **See what is sent**. It shows the exact report, built from your own computer, before anything is sent. You can turn reports off again at any time in the same place.

## Diagnostics export

If you contact support, the Runner can write a diagnostics file on your computer. Nothing uploads it: it is saved in a fixed folder (a `ReelBolt/diagnostics` folder inside your user cache folder), and you decide whether to attach it. Its log text has paths, names and addresses removed, but read it before you share it.

Work you run on your computer is described on the [Desktop runner](../account/desktop-runner.md) page, including what leaves the machine.
