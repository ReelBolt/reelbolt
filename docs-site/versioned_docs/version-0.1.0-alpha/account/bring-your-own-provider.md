---
title: Bring your own provider
sidebar_position: 3
description: How a ReelBolt team workspace connects its own AI services, what the workspace page offers compared to the platform page, and the plan limit that controls it.
---

# Bring your own provider

By default, every ReelBolt workspace uses the **platform-managed** AI connections the installation was set up with. If you have your own AI service accounts — an OpenAI key, a model you host on your own GPU, a speech service — a team workspace can connect its **own** AI services and use them for the work inside that workspace only. This is called **bring your own provider** (BYO). This page is the workspace's point of view on those connections; the platform administrator's point of view is in [Inference providers](../admin/inference-providers.md).

## What a workspace's own connection is in ReelBolt

A **workspace connection** belongs to one team workspace. It is visible and usable only inside that workspace, it is paid for by that workspace's own account with the AI service, and nobody else's work is affected by it. When a workspace marks one of its own connections as the default for a capability, that connection wins for that workspace — the platform default still serves everyone else.

Because the work goes to your account, **workspace connections are charged 0 ReelBolt credits**: you pay your provider directly, and ReelBolt records the usage (so the Usage page stays complete) without charging your balance. The same is true of work on the workspace's own paired machine — see [Desktop runner](./desktop-runner.md).

## Who can use the workspace provider page in ReelBolt

The workspace's **Providers** page is at **Organization → Providers** in the left menu.

- **Owner and Admin** can add, edit, test and delete the workspace's own connections.
- **Member and Billing** cannot open the page at all. A Member can still **choose** among the connections a workflow step's settings offer, because that is a read-only choice.
- A connection a workspace makes is used only inside that workspace. The installation's platform-managed connections stay available to everyone, and a workspace connection never changes them.

## Adding a connection in a ReelBolt workspace

On **Organization → Providers**, click **New provider** and fill in the same form as the platform page (name, kind, capability, endpoint, model, display name, API key, timeout, set-as-default, enabled), then **Test connection** and **Create**. This adds your account with its first model.

Add each account once. To use more models from the same account, open the provider and click **Add model**; to give a model another job (for example `Vision` on a chat model), click **Add capability** on it. The key and address belong to the provider, so you never type the key again, and changing it applies to every model under it. Workspace providers have no prices: their work is charged 0 credits.

The form offers fewer options than the platform page, and ReelBolt enforces the difference no matter what the form shows:

- **No `env:` shortcut.** A workspace connection must carry its own key; it cannot be pointed at a key from the installation's environment.
- **No personal subscription mode.** The **Dev/OAuth (Personal use only)** switch the platform form offers for Anthropic is not available here — a personal Claude subscription is licensed for its owner's own use only, and a workspace serving other people must use a proper API key.
- **Public addresses only.** A workspace connection must use a public address. A self-hosted service on the installation's private network can only be a platform-managed connection.

The kind-and-capability matrix, the per-capability defaults, and the **Test connection** behaviour are exactly the same as on the platform page — see [Inference providers](../admin/inference-providers.md) for the full table and the test details.

## The plan limit that controls bring-your-own in ReelBolt

Adding, changing or testing a workspace connection requires the workspace's plan to include bringing your own provider.

- **If the plan includes it** (Creator, Pro, Team and Enterprise do; the free Trial does not), the page works normally.
- **If the plan does not include it**, adding, changing or testing a connection is refused and the form shows a plan limit with the upgrade path — it never fails obscurely, and nothing breaks and nothing is charged. **Seeing the list and deleting a connection always work**, so a workspace that has lost the feature can still clean up after itself. A connection that is dropped by the plan is simply not used until the plan allows it again.

Because a plan can withhold the feature, a workspace can also be pointed at a stronger model for one agent without touching the others: an Owner or Admin can pick any enabled chat connection the workspace can use — platform-managed or the workspace's own — for a built-in agent, and it applies to that workspace alone. See [Inference providers](../admin/inference-providers.md) for how that per-agent choice works.

## Choosing a connection for one step in ReelBolt

Inside a workflow step's settings, the connection a step uses is chosen from the ones the workspace can use, in the same order ReelBolt resolves them everywhere: a connection chosen specifically for that step, then the default connection for that capability (a workspace default wins inside that workspace), then the platform fallback. A Member can make this choice even though they cannot open the Providers page, because choosing a connection for a step is read-only.

## Related pages in ReelBolt

- [Inference providers](../admin/inference-providers.md) — the platform administrator's full reference for kinds, capabilities, keys and testing.
- [Plans and credits](./plans-and-credits.md) — which plans include bring-your-own, and why workspace work costs 0 credits.
- [Desktop runner](./desktop-runner.md) — pairing a machine you own for the rendering work.
- [Workspaces and teams](../workspaces.md) — the roles that can open this page.
