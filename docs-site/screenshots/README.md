# Docs screenshots

One command regenerates every screenshot used by the "basics" guides in `docs/user-guide/basics/`:

```bash
cd docs-site
node screenshots/run.mjs                  # every shot in shots.json
node screenshots/run.mjs --only sign-up-and-first-look-02,pick-an-app-and-run-01
node screenshots/run.mjs --list        # ids, guide, viewport, route
node screenshots/run.mjs --check       # no browser: every shot has an image, every image has a shot, each <= 120 KB
```

Images land in `docs/user-guide/basics/img/<id>.webp`, and the ids must equal the `./img/<id>.webp` references in the
guides. A run twice in a row leaves every file byte-identical: the browser and the lossy encoder move single pixels
by a level or two between runs, so a new capture in which no pixel differs by more than 40 levels from the
committed image is discarded and the committed bytes are kept (`kept <id>.webp unchanged`). A real change (a word,
a colour, a shifted layout) is far above that and is written. `--force` rewrites everything.

## How it works

- It starts the **real** dashboard (`web/`, `next dev`) on a free port, drives it with Playwright's Chromium, and
  fakes **only** `/api/**` with fictional data (`fixtures/`). No account or server is touched, and every request to a host other than 127.0.0.1 is refused, so nothing reaches the internet.
- The company is "Northwind Studio"; people, projects and files are invented; the only addresses are `example.com`.
  Plans and prices are read from the canonical `plans.catalog.json`, never typed.
- Light theme, browser clock frozen at 2026-10-27 09:20 UTC, UTC time zone, animations off, videos parked on the
  same frame, the editor's Google Fonts request refused with all other outside requests (it raced the first canvas draw), Chromium without LCD text or hinting. The alpha banner is
  hidden unless a shot sets `"alpha": true`. The dev server runs in a whole-hour time zone chosen so its own clock
  reads about 08:00 (the dashboard greeting is rendered by the server).
- Viewports: `desktop` 1440x900 at 1x, `phone` 390x844 at 2x (touch). The dev server's own clock is set to about 08:00 by a time-zone trick because the dashboard greeting is rendered by the server.
- Logs go to the scratch directory: `SCREENSHOT_SCRATCH` if set, else `<repo>/../_scratch/screenshots` when
  `<repo>/../_scratch` exists, else a temporary directory that is deleted at the end. `TMPDIR` points there for
  the whole run. When the run ends (or is interrupted) it stops the dev server's whole process group, closes the
  browser, removes `web/.next` if it created it, and removes the temp folder it made inside the scratch directory (`tmp-XXXX`; nothing that was already there).
- Dependencies: no new ones. Playwright comes from `e2e/node_modules`, `sharp` (WebP encoding) and `next` from
  `web/node_modules`. In a git worktree without them, the first run hard-links `web/node_modules` from the main
  checkout (`cp -al`, no extra disk; the folder is git-ignored). Run `npm ci` in `e2e/` and `web/` of the main checkout once.

## Add a shot

1. Add an entry to `shots.json`:

   ```json
   {
     "id": "app-run-form",
     "guide": "pick-an-app-and-run",
     "route": "/app/projects/p-aurora/workflows/w-aurora",
     "viewport": "desktop",
     "alpha": false,
     "waitFor": ["text=What should the episode be about?"],
     "steps": [
       { "click": "role=button[name=\"Advanced\"]" },
       { "fill": "role=textbox[name=\"What should the episode be about?\"]", "value": "A 6 minute cut" },
       { "press": "Tab" },
       { "scroll": "text=Estimate" },
       { "waitFor": "text=Estimate" }
     ],
     "clip": null,
     "highlight": "role=button[name=\"Run\"]"
   }
   ```

   - `id`: lower-case letters, digits, dashes; the file is `<id>.webp`.
   - `guide`: the page slug that shows it (`/docs/basics/<slug>`), so a reader of `--list` can see where it is used.
   - `route`: a dashboard path starting with `/app`.
   - `viewport`: `desktop` or `phone`.
   - `waitFor`: selector or list of selectors that must be visible before the shot (Playwright selectors:
     `text=...`, `role=button[name="..."]`, CSS). The harness also waits for skeletons and loaders to go,
     fonts, and video thumbnails.
   - `steps`: run in order; each uses exactly one of `click`, `fill` (with `value`), `hover`, `press` (key, optional
     `on` selector), `scroll` (a selector, or `{ "y": 400 }`), `waitFor`, `wait` (milliseconds, last resort).
   - `clip`: `null` = the viewport; a selector = that element's box; `{ "x", "y", "width", "height" }` = a
     rectangle. Optional `"fullPage": true` (with `clip: null`) grows the viewport to the page, up to 2400 px.
   - `highlight` (optional): a selector, a rectangle `{x,y,width,height}` in viewport pixels (for things drawn on a
     canvas), or a list of them, ringed in the brand violet. It is drawn 6 px outside the box, so when you also
     clip to a selector, clip to a parent or a rectangle that leaves that room. Scroll first: it uses viewport coordinates.
   - Other optional keys: `auth: false` (signed-out page, e.g. sign-up), `scenario` (a variation of the fake data,
     read in `fixtures/respond.mjs`: `trial`, `ready`, `one-runner`, `two-projects`), `chat` (a canned assistant
     reply from `fixtures/chat.mjs`; `hold` ones stay running, which keeps a permission card waiting), `height`
     (viewport height), `alpha: true`, `storage` (localStorage entries, e.g. a preference set earlier). Extra steps: `clickAt: [x, y]`, `scrollTo: selector` (element top just
     under the header).
2. If the page needs API answers the fixtures lack, the run prints `Unmatched /api requests` at the end. Add
   them to `fixtures/respond.mjs` (data in `fixtures/data.mjs`). Keep everything fictional, numbers round.
3. `node screenshots/run.mjs --only <id>`, look at the image, then run `node screenshots/run.mjs` once without `--only`
   (its closing check fails if any shot has no image or any image has no shot) and commit the manifest and the images.

An image over 120 KB lowers its WebP quality step by step; if it still does not fit, the run fails: clip it, or
use a smaller viewport or a less busy page.

## Files

- `run.mjs` entry; `shots.json` the manifest.
- `lib/`: `env.mjs` (paths, dependency lookup, scratch), `server.mjs` (dev server start and teardown),
  `shoot.mjs` (one shot, fake API installer), `encode.mjs` (WebP), `manifest.mjs` (validation and `--check`).
- `fixtures/`: `data.mjs` (the fictional data), `respond.mjs` (the fake API), `media/` (four 2 second clips served
  for video thumbnails).
