---
title: Managing users
sidebar_position: 1
description: How ReelBolt administrators create accounts, hand out temporary passwords, reset passwords, grant admin rights and delete users.
---

# Managing users

This page covers the **Users** screen (**Platform → Users** in the left menu, at `/app/admin/users`), which belongs to the platform administrator — the person who runs the whole ReelBolt installation. It is about *accounts*: creating them, resetting their passwords and deleting them.

It is not where you add someone to a workspace. Adding a colleague to your projects is a workspace action, on **Workspace → Members**, and it is described in [Workspaces and teams](../workspaces.md). The two are deliberately separate, and so are the rights behind them: platform administrator rights never give access to another person's workspace, and being a workspace Owner or Admin never makes you a platform administrator.

## The first ReelBolt administrator account

When ReelBolt starts for the very first time with no accounts at all, it creates one platform administrator account automatically. Its email and password come from the installation's settings (`ADMIN_EMAIL` and `ADMIN_PASSWORD`; the email defaults to `admin@reelbolt.local`). If no password was set, ReelBolt makes up a random one and prints it once in the server log of the account service, so whoever installed ReelBolt can find it there.

This first account must change its password on first sign-in, like every new account.

## Creating a ReelBolt user account

This screen is the **self-hosted** way accounts appear. In a self-hosted ReelBolt it is the only way, because self-serve sign-up is switched off and the sign-up page is not reachable. (A cloud ReelBolt can switch self-serve sign-up on, in which case people register themselves from the sign-up page — see [Getting started](../getting-started.md); every account created that way starts with its own personal workspace and no workspace invitations.)

1. Go to **Platform → Users** and click **New User**.
2. Fill in **Email** and **Display Name** (both required).
3. Switch on **Administrator** only if this person should run the installation: manage users, the installation's own AI services and the other **Platform** pages. It is not needed to join a workspace.
4. Click **Create**.

A new account is given its own empty **personal workspace** straight away, so the person can start work on their own without anyone inviting them. Inviting them into a shared workspace is a separate step — see [Workspaces and teams](../workspaces.md).

A **User Created** box then shows a 16-character **temporary password**, with a **Copy Password** button. If the installation has email (SMTP) set up, the same password is also emailed to the new user in a "Welcome to ReelBolt" message. If email is not set up, this box is the only place the password appears, so copy it and pass it on before closing the box.

The user signs in with the temporary password and is immediately made to choose a new one (at least 8 characters). Until they do, the users list shows a **Must change password** badge next to them.

## Resetting a ReelBolt user's password

1. Open the user (click them in the list) and click **Edit**.
2. Switch on **Reset password (generates new temporary password)**.
3. Click **Update**.

ReelBolt gives the user a new temporary password and requires them to change it at their next sign-in. **The new password is sent by email only**; unlike account creation, it is not shown on screen. This means a reset only works for you if the installation has email set up. If it does not, the reset still takes effect but nobody can see the new password, and the user is locked out until email is configured. In that situation ask whoever runs the installation to configure email (the `SMTP_*` settings) before resetting passwords.

## Granting or removing administrator rights in ReelBolt

Open the user, click **Edit**, switch **Administrator** on or off and click **Update**. The users list shows an **Admin** or **User** badge for each person.

Platform administrators can:

- manage users on this screen,
- add and change the installation's own AI service connections (see [Inference providers](./inference-providers.md)),
- choose which AI service a particular agent uses across the whole installation,
- see the **Platform** pages in the left menu (**Overview**, **Workflow Service**, **Inference**, **Billing**, **Calibration**, **Skills**, **Users**),
- use the platform-wide abilities of the [assistant](../assistant/index.md).

**Administrator rights are not workspace rights.** A platform administrator manages accounts and the installation's AI services; they get no view of any workspace's projects, files, workflows or renders through the dashboard, and they cannot open, join or change a workspace they are not a member of. Run a workflow in a workspace and that workspace's rules apply to you there, exactly as they do to everyone else in it.

A change of rights applies within a minute or so. ReelBolt re-reads each person's platform rights and workspace role from its own records as requests arrive, rather than trusting what was written into their sign-in, so a promotion or demotion does not wait for them to sign in again.

## Deleting a ReelBolt user

Open the user and click **Delete**, or use the delete action in the users list, then confirm in the **Delete User** box. Deletion is permanent, and it takes a great deal with it. **Every project that person owned is deleted**, wherever it lived — including projects inside a shared team workspace — together with those projects' files, workflows and renders. Their **personal workspace** is deleted too. Custom agents they created inside their personal workspace go with it; custom agents they created in a shared workspace are kept but no longer belong to anyone. The shared workspace itself, and its other members' work, are not deleted. Because this reaches into team workspaces, remove the person from those workspaces first if their projects there must survive — deleting the account is not reversible.

If you only want to stop someone from signing in temporarily, consider resetting their password instead of deleting them.

## Managing ReelBolt users through the assistant

The [assistant](../assistant/index.md) cannot create, change or delete users, or reset passwords. Use the **Users** screen for all account management.
