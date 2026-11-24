---
title: Decision models
sidebar_position: 4
description: A plain-language guide to ReelBolt's optional decision models - the Off, Shadow and Gate modes, what they cost and risk, and how to read the Decision Calibration page.
---

# Decision models

A **decision model** is a small, fast AI service that answers simple multiple-choice questions, such as "which colour look fits this footage: warm, cool or filmic?". Some workflow decisions are simple enough that such a model could answer them instead of a full agent, which would be quicker and cheaper. ReelBolt's decision models let you **test that idea safely first** and only then, if the numbers support it, let the fast model take over some of those decisions.

This feature is optional, advanced and **switched off by default everywhere**. Most installations never need it.

## The three decision model modes: Off, Shadow and Gate

| Mode | What happens | Changes your videos? |
|---|---|---|
| `Off` | Nothing. No decision model is asked anything. This is the default. | No |
| `Shadow` | The agent makes the decision as usual. The decision model is asked the same questions on the side, and ReelBolt records whether it agreed and how confident it was. | No, purely an observation |
| `Gate` | The decision model is asked first. If it is confident enough on **every** question for that step, its answer is used and the agent is skipped. If it is unsure about even one question, the whole step goes to the agent as normal. | Yes, when the model is confident |

The usual path is: run in `Shadow` for a while, look at the results on the calibration page, and only switch to `Gate` for the decisions where the model has proven reliable.

A decision model can never cause a step to fail. If it is not configured, is slow (it gets a few seconds by default), or answers nonsense, ReelBolt simply falls back to the agent.

## Where ReelBolt can use a decision model

Decision models are only used at a few specific points:

- the **Colorist** agent (choosing a colour-grade look),
- the **MusicSupervisor** agent (choosing music intensity, ducking and fit),
- the **colour grade room**, to decide whether the room needs to meet at all,
- a **Conditional** step set to decide its branch with a decision model,
- a **ReviewLoop** step's optional early stop.

Everything else in ReelBolt is unaffected.

## Turning decision models on in ReelBolt

Two things are needed:

1. **A `Decision` provider.** An administrator adds an inference provider with the **Decision (Jev / logprob)** capability and makes it the default. Suitable kinds are TypeSafe, a self-hosted decision server (`OpenJev`), Azure OpenAI, OpenAI-compatible services and DeepSeek. Anthropic and Google Gemini cannot be used. See [Inference providers](./inference-providers.md).
2. **A mode.** For the Colorist and MusicSupervisor agents, the mode is set by whoever runs the installation, in its settings (`COLORIST_DECISION_GATE_MODE` and `MUSIC_SUPERVISOR_DECISION_GATE_MODE`, each `Off`, `Shadow` or `Gate`). The other three uses are switched on in the settings of the individual workflow step. Neither the dashboard nor the assistant can change these modes.

**Privacy note.** When a decision model is a cloud service, the questions and some context about the step are sent to that service, in addition to the main AI service the agents already use. In `Shadow` mode that context can include transcript text and on-screen text from your footage. A self-hosted decision server keeps this on your own network. Bear this in mind before turning it on.

## Reading the Decision Calibration page in ReelBolt

Administrators can open **Admin → Calibration** (`/app/admin/calibration`), the **Decision Calibration** page. It is read-only: it shows what the decision models have done so far and what would happen at different settings. It changes nothing. Open **How this page works** at the top for a built-in explanation.

The key ideas:

- **Observations**: how many decisions were recorded.
- **Agreement**: how often the decision model gave the same answer as the agent.
- **Confidence**: how sure the model said it was. A trustworthy model agrees more often when it says it is more confident; the reliability chart shows whether that holds.
- **Accept-at** and **Minimum margin** (the two sliders): what "confident enough" means. A decision is accepted only if the model's top answer is at least Accept-at sure and beats the runner-up by at least the minimum margin. The defaults are 0.85 and 0.25. Move the sliders to see how many decisions the model would have handled alone at other settings.
- **Would handle alone** / **Would escalate**: the share of steps the model would have taken over, or sent back to the agent, at the slider settings.
- **Tokens saved**: an **estimate** of the AI usage that would have been saved at the slider settings. It is a projection, never a measurement.

Each agent gets a card with an advisory badge: **Not enough data** (fewer than 30 observations), **Keep Shadow**, or **Safe to try Gate at X**. These are suggestions based on simple rules, not guarantees, and none of the default thresholds has been tuned for your footage. Use **Ask assistant** for a plain-language recommendation; the assistant can read this report but cannot change any setting.

If the page says **No decision observations recorded**, no workflow has run with `Shadow` or `Gate` switched on yet.
