---
title: Choosing models for your agents
sidebar_position: 7
description: How Owners and Admins pick which AI model each built-in agent runs on in a workspace, what the recommended model and relative cost mean, and how to reset a choice.
---

# Choosing models for your agents

Each built-in [agent](./agents.md) runs on an AI model. ReelBolt picks a **recommended model** for every agent, and an Owner or Admin of a workspace can choose a different one for that workspace. The choice applies to every workflow in the workspace that uses the agent.

## Who can change it

- **Owners and Admins** pick a model per agent for the workspace.
- **Members** cannot change the model. Ask an Owner or Admin of the workspace; the assistant can also tell you which model an agent uses.

## Pick a model for an agent

1. Open the **Agents** page and select the agent.
2. Open the model picker. The model marked **Recommended** is the one ReelBolt suggests for this agent, with a short reason why. For example, the recommendation for an agent can be DeepSeek V4.1 Flash or GPT-6 Luna.
3. Choose a model from the list.

The change applies from the agent's next step. A step that is already running finishes on the model it started with.

## Read the relative cost

Each option shows how it compares with the recommended model, for example "about 4 times pricier" or "about 3 times cheaper". These are comparisons for a typical amount of work, not prices. To see what a run costs, use the estimate before you run (see below).

## Your own connected models

If your workspace has connected its own models (see [Bring your own provider](../account/bring-your-own-provider.md)), they appear in the same list. They show no relative cost because you pay your provider directly for them.

## Reset to recommended

Choose **Reset to recommended** in the picker to remove the workspace choice. The agent goes back to the recommended model, and follows any later change to the recommendation.

## Estimates follow your choice

The cost estimate you see before a run uses the model chosen for each agent in the workflow. Change an agent's model and the estimate changes with it.

## Ask the assistant

The [assistant](../assistant/index.md) can tell you which model each agent uses. Changing a model is done in the agent's page for now.
