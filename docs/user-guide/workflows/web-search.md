---
title: Web search for agents
sidebar_position: 10
description: Let some built-in agents look up current facts on the web. What it does, how an admin turns it on, what it costs and how to treat the results.
---

# Web search for agents

Some built-in agents can search the web while they work, for a recent fact, a statistic, or a name or
date they need to check. The agent writes a short search query, gets back a few results (title, link and
a short snippet), and uses them as reference.

## Turning it on

Web search is off until an admin adds a provider for it.

1. Open **Admin**, then **Inference providers**, and add a provider with the **Web search** capability.
2. Choose DeepSeek, Google Gemini or OpenAI. For OpenAI, the endpoint must be `https://api.openai.com/v1`.
3. Make it the default for Web search.

Steps are in [Inference providers](../admin/inference-providers.md). If no provider is set, agents still
run: the search simply reports that it is unavailable and the agent carries on without it.

## Which agents use it

The Scriptwriter, the Director, the Shot Director and the Narration Writer. Custom agents do not get web
search. See [Agents](./agents.md).

## What it costs

Each search query is billed on its own, at the provider's price plus a markup, in the same credits as the
rest of the run. For OpenAI and Google, the model's own usage for the search is billed as well, where the model has a price. An agent
can make a limited number of searches per step and per run, so cost has a ceiling. A step that searched
is never stored in the cache, because the web changes; a step that could search but did not may be cached. If you use your own provider connection,
ReelBolt does not charge for the searches.

## Google Gemini is not available for shared use yet

Google's terms restrict how search results may be stored and shown. Until ReelBolt can show the required
Google search suggestions, results from a Google Gemini connection are refused and the agent carries on
without them. Use DeepSeek or OpenAI for web search today.

## How to treat the results

Search results come from third-party web pages and may be wrong or out of date. ReelBolt marks them as
untrusted, so an agent will not follow instructions found in a page. Links are shown so you can check a
source yourself; agents do not open them. The search query leaves ReelBolt and is sent to the provider you
chose, and it can contain words from your project. See [Your data](../your-data.md).
