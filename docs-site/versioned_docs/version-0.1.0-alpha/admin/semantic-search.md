---
title: Semantic search
sidebar_position: 3
description: What an Embedding provider unlocks in ReelBolt - searching project files by meaning and the assistant's documentation search - and how to reindex after setting one up or switching models.
---

# Semantic search

**Semantic search** means searching by meaning instead of by exact words: a search for "login screen" can find a file that only ever says "sign-in page". ReelBolt uses it in two places, and both depend on one thing an administrator sets up: an **Embedding provider**.

## What an Embedding provider does in ReelBolt

An Embedding provider is an AI service that turns a piece of text into a list of numbers that captures its meaning, so that texts with similar meanings end up with similar numbers. ReelBolt stores these numbers for your project files and for this documentation, and compares them against your question to find the closest matches.

Without an Embedding provider, nothing breaks: file-name filtering, uploads, summaries and workflows all keep working. Only search-by-meaning is unavailable. To set one up, see "Setting up local search" in [Inference providers](./inference-providers.md), or connect an Azure OpenAI or OpenAI-compatible embedding model. Only those two kinds can serve the `Embedding` capability; Anthropic, Google Gemini and DeepSeek cannot.

## Searching inside project files in ReelBolt

On a project's **Files** tab, type at least three characters in the filter box. Besides filtering by file name, ReelBolt offers a button **Search inside files for "…"**. Click it to search the contents of the project's text files by meaning; matching passages appear under **Content matches**, and clicking one opens the file.

If you see **Content search is not available yet** ("Searching by meaning needs an embeddings provider and indexed files"), either no Embedding provider is set up or the project's files have not been indexed yet.

Agents use the same search while a workflow runs, so a working Embedding provider also helps code-analysis agents find the relevant parts of a large code base.

Only text files are searchable. Video and audio files are never indexed.

## How ReelBolt indexes project files

**Indexing** is the process of reading a file, splitting it into passages and storing their meaning so they can be searched. It happens automatically in the background every time you upload a text file. Each file has an index status, shown on its **Details** tab under **Search index**:

| Status | Meaning |
|---|---|
| `Pending` | Waiting to be indexed. |
| `Processing` | Being indexed now. |
| `Indexed` | Searchable. |
| `Failed` | Indexing did not work, for example because no Embedding provider existed when the file was uploaded. |
| `NotIndexed` | Not indexed on purpose (video and audio files). |

## Reindexing project files after adding an Embedding provider

Files uploaded before any Embedding provider existed end up `Failed` and are not searchable. After setting up a provider:

- **Whole project**: on the **Files** tab, open the **⋮** (More file actions) menu and choose **Reindex for search**, then **Queue reindex**. This queues up to 25 files that are not indexed yet (failed ones included); run it again for the next batch. The batch size keeps the AI service's rate limits calm.
- **One file**: open the file's **⋮** menu (or its **Details** tab) and choose **Reindex**.

## Reindexing after switching to a different embedding model

ReelBolt keeps a separate search index for each embedding model, because two models' numbers cannot be compared with each other. When you make a different Embedding provider the default (or change its model), the old index is left alone and simply stops being used, and the new index starts **empty**. Search results will be missing until files are reindexed with the new model.

Be aware that **Reindex for search** on the Files tab only picks up files that are not already `Indexed`. Files that were indexed with the old model still show `Indexed`, so after a model switch you need to either:

- use **Reindex** on each important file individually, or
- have someone with API access call the project reindex endpoint with `includeIndexed` set to `true` (`POST /api/v1/projects/{projectId}/files/reindex` with body `{"includeIndexed": true, "maxFiles": 200}`; at most 200 files per call).

The assistant's documentation index (next section) handles a model switch by itself.

## How the ReelBolt assistant searches this documentation

The [assistant](../assistant/index.md) answers how-to questions by searching ReelBolt's documentation, using the same default Embedding provider. It does this in two ways: it can search on purpose when it needs to look something up, and for every question you ask it automatically pulls in the few most relevant sections.

The documentation index builds itself in the background. It checks every few minutes (every 10 minutes by default) whether the index matches the current documentation and the current embedding model, and rebuilds it when either has changed. So after you add or switch an Embedding provider, documentation search starts working, or catches up with the new model, within a few minutes with no action from you. Until then, the assistant answers from what it already knows and from live platform data.
