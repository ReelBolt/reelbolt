---
title: Give ReelBolt your code
sidebar_position: 0
description: Three ways to give a "from your code" workflow your app's source, upload a zip, connect GitHub or sync a folder on your computer, and what to do when a zip cannot be unpacked.
---

# Give ReelBolt your code

A workflow such as "Promo video from your code" reads your app's source files to build the video. You can give it those files in three ways.

| Way | Best for |
|---|---|
| Upload a `.zip` | A one-off video from a copy of your code |
| [Connect GitHub](./github-repository.md) | Code that changes and lives on GitHub |
| [Sync a folder on your computer](./local-folder-sync.md) | Code that you keep editing locally |

## Upload a zip

1. Zip your source folder **without `node_modules`**. The zip is smaller, uploads faster and stays under the limits.
2. Open the project and click **Upload files**, or drag the zip onto the project page.
3. Wait a moment. ReelBolt unpacks the zip into a folder named after it. A zip called `my-app.zip` becomes the folder `my-app`. If that name is taken, the folder is `my-app (2)`.
4. Open the **Files** tab and check the folder.

When everything unpacked, the zip itself is removed, so you see your files once and they count once against your storage. If the unpacking failed, the zip stays and the Files tab shows the reason.

If the zip holds a single top-level folder, ReelBolt uses its contents directly instead of adding a second folder with the same name.

### What is left out

ReelBolt skips files a video never needs:

- Dependencies and version control: `node_modules`, `bower_components`, `.git`, `.svn`, `.hg`, `.venv`, `venv`, `.gradle`.
- Build output and caches: `dist`, `build`, `out`, `.next`, `.nuxt`, `.output`, `.svelte-kit`, `coverage`, `target`, `bin`, `obj`, `.turbo`, `.cache`, `.parcel-cache`, with `__pycache__` and `*.pyc` always skipped. A zip that holds only built files, and no source, keeps them.
- System and editor leftovers: `__MACOSX`, `.DS_Store`, `Thumbs.db`, `._*` files, `.idea`.

### Limits

| Limit | Value |
|---|---|
| Size of the zip | 64 MiB |
| Files kept | 2,000 |
| Total size after unpacking | 128 MiB |
| Size of one file | 32 MiB |

Files bigger than the per-file limit are skipped. A zip over any other limit is not unpacked. The unpacked files also need to fit in your plan's storage.

### If the zip does not unpack

The zip stays in the project and the Files tab shows why. Common reasons and fixes:

- **Too big or too many files.** Zip again without `node_modules`, build folders and large videos or images you do not need.
- **Not a valid zip, or password-protected.** Create the zip again without a password.
- **Not enough storage.** Free space or upgrade your plan, then retry.

To try again, open the **Files** tab and press **Retry** on the note that says the zip could not be unpacked. You can also delete the zip and upload a fixed one.

:::tip Before a paid run
A project whose only "code" is a zip that did not unpack is not ready. ReelBolt tells you before the run, and the workflow stops without charging you if it finds no readable source.
:::

## Connect GitHub or sync a folder

[GitHub](./github-repository.md), [cloud drive](./cloud-drive-sync.md) and [computer folder](./local-folder-sync.md) sync copy your files as they are. They never unpack a zip: a zip inside the synced folder stays one file and the workflow cannot read it. Keep your source as normal files in the synced folder, or upload the zip directly to the project.

## Next

- [Create a project and upload files](../basics/create-a-project-and-upload.md)
- [Make a promo video from your app's code](../recipes/promo-from-code.md)
