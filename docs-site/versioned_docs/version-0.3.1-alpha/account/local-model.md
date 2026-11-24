---
title: Use a model on your own computer
sidebar_position: 11
description: How to let your workspace use an AI model that runs on your own computer, how to switch sharing on in the desktop app, how to add it in Workspace settings, and what to check when it does not answer.
---

# Use a model on your own computer

If you run an AI model on your own computer, your workspace can use it for the steps that write or read text and look at images. The model keeps running on your computer. ReelBolt only passes requests to it through the ReelBolt desktop app.

## What it does

You run the model yourself, with a program such as Ollama, LM Studio, llama.cpp, vLLM, or any other server that works the same way. When a step in your workspace uses it, ReelBolt sends the request to the desktop app on your computer, and the app hands it to your model. The answer comes back the same way.

- It does not use credits. You pay for your own computer, not ReelBolt.
- It works for chat and for steps that look at images, if your model supports images.
- If the model is an embedding model, it can also be used to index your project files for search. After you switch to it, reindex your project files so the search uses the new model.
- The desktop app starts the connection itself. You do not open any port on your network.

## Before you start

- The ReelBolt desktop app is installed and paired with your workspace. See [Desktop runner](./desktop-runner.md).
- Your model program is running on the same computer.
- You are an owner or admin of the workspace.

## Switch sharing on in the desktop app

1. Open the ReelBolt desktop app and go to the **Local model** screen.
2. Under **Where your model is**, pick the server the app found, or type its address. The usual address for Ollama is `http://127.0.0.1:11434`.
3. Tick the box to confirm you have read the note about prompts. The switch stays off until you do. Then turn on **Share my local model with my workspace**.
4. Check that your model names appear under **Models found**.

Sharing is off until you turn it on. By default the app only talks to a model on the same computer. If your model runs on another computer on your network, tick **My model runs on another computer on my network** and enter its address.

## Add it in Workspace settings

1. In ReelBolt, open **Workspace → Providers**.
2. Add a provider and choose **Model on my computer (desktop app)**.
3. Choose the computer from the list and enter the name of the model.
4. Save, then use the provider's test button to check it answers.

You do not enter a key. Then choose the model in the steps or defaults where you want it used.

## What to know before you share

- While sharing is on, prompts from members of your workspace, for the steps that use this model, go to your computer. Anyone who can use your computer can read them.
- The computer must be on, the desktop app must be running and connected, and your model program must be running.
- If the app is paused, sharing is off, or the computer is offline, the step fails with a message that says your local model is offline. ReelBolt does not switch to another model.
- ReelBolt does not control how fast the model is or how good its answers are. That depends on your model and your computer.

## Troubleshooting

- **"Your local model is offline".** Open the desktop app and check that it is connected and that sharing is on, then run the step again.
- **"Could not reach your local model server".** Start your model program, and check the address on the **Local model** screen.
- **"Busy".** Your model is already handling the most requests the app allows at once. Try again in a moment.
- **"Did not answer in time".** The model may still be loading, or the request may be too large for it. Try again, or use a smaller input. A slow model may need longer for its first answer, so wait for the step to finish before you try again.
- **The computer is missing from the list.** Check that the desktop app is paired with this workspace and that sharing is on, then reload the page.
- **The model list is out of date.** After you add or remove a model, reconnect the desktop app.
