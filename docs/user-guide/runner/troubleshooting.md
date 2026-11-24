---
title: Runner troubleshooting
sidebar_position: 9
description: Fixes for a ReelBolt Runner that cannot pair, shows offline, has no Docker or Podman, sits behind a proxy, or has the wrong clock.
---

# Runner troubleshooting

Run `reelbolt-runner doctor` first. It checks the video tools and Docker or Podman, and says what is wrong.

## It cannot pair

- The code expires after a few minutes. Ask the Runner for a new code.
- Make sure you are signed in to ReelBolt and that you are an Owner or Admin of the workspace you picked.
- Check the computer's clock (see below).
- If the page does not open, copy the address the Runner prints into your browser.

## Pairing says ReelBolt is not ready for runners

If the Runner says ReelBolt approved the computer but is not ready for runners to connect, nothing is wrong on your side: ReelBolt has not finished setting up its runner connection. Contact support. The approval has already created the device in your workspace, so the first attempt leaves a device listed that never connects. Pair again once support confirms it is fixed (a new attempt registers as a new device), then remove the unused entry under **Runners** in ReelBolt.

## It shows as offline

- The Runner must be running. Start it again.
- Check your internet connection. The Runner only needs to make outgoing connections.
- A computer that was removed from the workspace must be paired again.

## Docker or Podman is missing

Without Docker (24 or newer) or Podman (5 or newer), the Runner still works but only takes the simpler video work (compiling and analysis). It cannot take video renders, and a workspace set to **Only my machines** will fail those steps with `NO_RUNNER_ONLINE`. Install one of them and restart the Runner. More in [Desktop runner](../account/desktop-runner.md).

## Behind a company proxy

The Runner reads the standard `HTTPS_PROXY` and `NO_PROXY` environment variables for its connection to ReelBolt. Set them for the account that runs the Runner. Your network must also allow `downloads.reelbolt.ai`, the only address the Runner downloads updates from.

## The clock is wrong

ReelBolt rejects requests from a computer whose clock is more than 120 seconds off. Turn on automatic time in your system settings, and restart the Runner.

## Still stuck

Export diagnostics from **Settings** in the Runner and attach them when you contact support. See [Privacy](privacy.md).
