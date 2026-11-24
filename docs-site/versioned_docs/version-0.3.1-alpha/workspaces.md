---
title: Workspaces and teams
sidebar_position: 4
description: What a ReelBolt workspace is, the difference between your personal workspace and a team workspace, the four member roles, and how to invite people, switch workspace, change roles or leave.
---

# Workspaces and teams

A **workspace** in ReelBolt is the shared container that owns everything you do: your projects, their files, your workflows, your renders, your custom agents, your AI connections — and your billing. Everyone who signs in to ReelBolt works inside exactly one workspace at a time, and the workspace menu at the top left always tells you which one that is.

This page explains the two kinds of workspace, the four roles a member can hold, and the everyday jobs: inviting someone, switching workspace, changing a role, and leaving.

## The two kinds of workspace

There are two kinds, and the difference is who is in them.

- **Your personal workspace** is given to you when your account is created. It is yours alone — exactly one member, you, as its **Owner**. It cannot be shared, invited into or left, and it cannot be deleted. In the workspace menu it is always shown as **Personal**, whatever it is called internally. If you never need to work with anyone else, this is the only workspace you ever use.
- **A team workspace** is one you create for other people. It can hold any number of members, each with one of the four roles below, and it is the only kind of workspace that can be shared, left or deleted.

In a cloud installation you get a personal workspace the moment you sign up; in a self-hosted installation your administrator gives you an account and that account starts with a personal workspace too. Either way, the personal workspace is the default home.

## Creating a team workspace

Any signed-in person can create one:

1. Open **Workspace → Settings** in the left menu.
2. Click **New team workspace**.
3. Type a **Workspace name**, for example "Acme Team".
4. Click **Create workspace**. ReelBolt creates it, makes you its **Owner**, and switches you straight into it.

You can belong to as many team workspaces as you like — one for each client, say — and switch between them at any time.

If you only want to keep one client's projects apart from another's, you do not need a workspace for each. Put a tag with the client's name on each project instead (see [Organizing projects](./projects/organizing-projects.md)).

## The four member roles

Every member of a team workspace holds exactly one role. The role decides what that person can do, and it is set per workspace: being an Owner of one workspace gives you nothing in another.

| Role | In plain words |
|---|---|
| **Owner** | Everything, including renaming and deleting the workspace, buying the plan and credits, and managing billing. Every team workspace always keeps at least one Owner. |
| **Admin** | Runs the workspace day to day: members, invitations, the workspace's own AI connections, pairing machines and runners, and deleting projects. Cannot delete the workspace, change who owns it, or buy the plan. |
| **Member** | Does the work: creates projects, uploads files, builds and runs workflows, uses the assistant. Cannot manage people or AI connections. |
| **Billing** | Sees the billing pages only — the plan, credits and purchases. Cannot open projects, files, agents or AI connections at all, and cannot even see the member list. |

Two rules surprise people, so they are worth stating plainly:

- **An Admin can never create an Owner, remove an Owner or demote an Owner.** Only an Owner can. The last Owner of a workspace cannot be removed or demoted by anyone: ReelBolt refuses with **"the last owner cannot be removed or demoted"**.
- **Platform administration is not a workspace role, and a workspace role is not platform administration.** The person who runs the whole ReelBolt installation sees the **Platform** menu, but that does not give them access to your workspace's projects — a platform administrator who is only a Member of a workspace can do no more there than any other Member. Conversely, being an Owner of your workspace does not make you a platform administrator. See [Managing users](./admin/users.md).

## Buying the plan and billing

A workspace's plan and credits are managed on **Settings → Plan and billing** in the left menu, and the same page shows the credit balance and what is being spent. Only the **Owner** and members with the **Billing** role can change anything there; everyone else sees the page read-only. In a team workspace the **Owner** and **Billing** members buy the plan, change seats and open the billing portal; a **Member** or **Admin** cannot. See [Plans and credits](./account/plans-and-credits.md) for what the plans are and how credits work.

## Inviting people to a workspace

Only an **Owner** or an **Admin** can invite someone.

1. Switch into the workspace you want to invite them to.
2. Open **Workspace → Members**.
3. Under **Invite someone**, type the person's **Email**, choose a **Role**, and click **Send invite**.

ReelBolt emails that address a link that stays valid for **7 days**. If the installation has no email set up, the invitation is still created but the email is not sent, and the page says so — in that case configure email (the `SMTP_*` settings), then revoke the invitation and send it again. Inviting somebody who is already a member is refused. Under **Pending invitations** on the same page you can see every invitation that has not been used yet, and **Revoke** any of them; a revoked link stops working immediately.

What happens when the link is opened depends on whether that person already has an account:

- **They already have a ReelBolt account.** The link asks them to accept, and accepting puts them in the workspace and switches them into it right away.
- **They do not have an account yet.** The link takes them to a sign-up page that creates their account from the invited email address. Because the invitation proves the address belongs to them, they do not have to confirm it by email afterwards.

An Admin cannot hand out the **Owner** role and cannot invite someone as an Owner; only an Owner can do either. An invitation is addressed to one email address, and only an account signing up with that exact address can use it.

## Switching between workspaces

Use the workspace menu at the top left. It lists your **Personal** workspace first, then every team workspace you belong to, and selecting one switches you into it. ReelBolt reloads the dashboard for the new workspace, and any live run you were watching reconnects under the new workspace, so you are never looking at one workspace's data while the menu says another.

You can only switch to a workspace you are a member of. A workspace you are not a member of is treated as if it did not exist.

One browser session carries one active workspace at a time. To work in two workspaces side by side, use a second browser profile and sign in again there. Anything that signs in outside the browser — a script, or [Claude Code connected to ReelBolt](./assistant/claude-code.md) — is handed a credential for one workspace and keeps it, so switch workspace before starting it if you want it to work somewhere else.

## Changing a member's role, and removing a member

On **Workspace → Members**, an Owner or Admin can change anyone's **Role** from the list and can remove a member. A Member can open the same page and sees who is in the workspace, read-only; a Billing user cannot see the member list at all.

A change applies **immediately** — ReelBolt re-reads a member's role from its own records on every request rather than trusting the role written into their sign-in, so a demotion or removal takes effect on the next click, not on the next sign-in. An Owner's own screen refreshes itself when they change their own role.

Removing someone from a workspace takes away their access to that workspace's projects, files, workflows, renders and AI connections. It does **not** delete their account, and it does not delete their personal workspace.

## Leaving a workspace

You can take yourself out of any team workspace from **Workspace → Members**, by removing yourself the same way an Owner removes someone else (the confirmation box reads **Leave workspace?**). A **Member** can do this even though they cannot manage anyone else's membership, so you never need to ask an Owner to let you go. ReelBolt then moves you to your personal workspace.

Leaving is refused if you are the workspace's **last Owner** — promote someone else first, or delete the workspace instead. A personal workspace cannot be left at all.

## Renaming and deleting a workspace

Open **Workspace → Settings**, which always acts on the workspace you are currently in.

- **Rename.** An Owner or an Admin can change the workspace's name. Everyone else sees the name read-only. Team workspaces show this name everywhere; a personal workspace is always shown as **Personal**, so the rename field is not offered for it.
- **Delete.** Only an **Owner** can delete a workspace, and only a team workspace. Deletion takes effect immediately for everyone: the workspace disappears from each member's workspace menu, its projects can no longer be opened, and access to it does not come back. It cannot be undone from the dashboard.

## Workspace AI connections (bring your own)

By default every workspace uses the **platform-managed** AI connections the ReelBolt installation was set up with. A team workspace's Owners and Admins can connect the workspace's **own** AI services instead, on **Workspace → Providers**:

- A workspace connection is used only inside that workspace. Nobody else's work changes, and the installation's own connections stay available to you.
- If the workspace makes one of its own connections the default, that connection is preferred over the installation's default **for this workspace only**.
- The form offers fewer options than the platform page, and ReelBolt enforces this: a workspace connection cannot use the shared environment setting for its key (`env:`), cannot use a personal Claude subscription credential, and must point at a public address.
- Adding, changing or testing a workspace connection needs the workspace's plan to include bringing your own provider. If it does not, the form says so and explains how to upgrade; nothing breaks and nothing is charged. Seeing the list, and deleting a connection, always work — so a workspace that has lost the feature can still tidy up.
- A **Member** cannot open the **Providers** page, but can still **choose** among the connections offered in a workflow step's settings, because that is a read-only choice.

Platform-managed connections and workspace connections are described together in [Inference providers](./admin/inference-providers.md).

## How the assistant answers about workspaces

The [assistant](./assistant/index.md) works inside the workspace you are currently in. It lists that workspace's custom agents and — for an Owner or Admin — can assign skills to them. It cannot create, rename or delete workspaces, invite people, change roles or remove members: those actions live on the **Workspace** pages and nowhere else.

The assistant's provider tools act on the **platform-managed** connections only, so an Owner or Admin asking it to change AI connections can never touch the workspace's own ones.

## Next steps

- Learn what a plan is and how credits are spent and topped up in [Plans and credits](./account/plans-and-credits.md).
- Connect the workspace's own AI services in [Bring your own provider](./account/bring-your-own-provider.md).
- Pair a machine you own to render on in [Desktop runner](./account/desktop-runner.md).
