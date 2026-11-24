# Editor on a phone: emulation spike

This note records what the phone editor was checked against in emulation, what the measurements say, and what emulation
cannot show. There is no device lab: every result here comes from Playwright's Chromium (Pixel 7 profile, real touch events
through the browser's debugging protocol) and WebKit (iPhone 14 profile, synthetic pointers). A phone gesture that passes
here has not been seen on a real iPhone. The phone editor therefore ships under the "Early preview" label, and the manual
checklist at the end is what a person with real devices runs before a release.

The measurements are made by `e2e/tests/editor-v2/30-phone/rm1-spike.spec.ts`. Each test writes its numbers into the test
annotations (`rm1:<item>`) and attaches a JSON file named `rm1-<item>.json`. The table below is filled from those files.
**Run on** 2026-10-26: production build of the Wave 3 branch, emulated Pixel 7 (Chromium) and iPhone 14 (WebKit). Items the spec does not measure are marked as such. "Go (emulation)" means measured clean in emulation only.

**To produce the numbers**, with the local stack up, run from `e2e/`:

```
npx playwright test -c playwright.editor-v2.config.ts --project=pixel7 --project=iphone14webkit tests/editor-v2/30-phone/rm1-spike.spec.ts
```

Each run also writes `/tmp/ev2-w3/rm1/<project>.json` (one entry per item, keyed by the item name; the same JSON is attached
to the test report as `rm1-<item>.json`). Copy the values from the file of the matching project into the "Measured" column,
one row per item, and change the decision only if a measurement fails its check (a Range answer other than 206, a viewer that
does not recover from a lost context, a canvas that does not allocate).

**Not measurable on WebKit emulation (D8).** The pointercancel counts (pan, pinch, trim) and the edge-swipe dead zone need
real multi-finger touch drags, which Playwright's WebKit cannot synthesize; those tests skip on `iphone14webkit` and are
measured on `pixel7` only. Their rows stay a device check.

## Go / no-go per gesture

"Go" means the gesture ships on by default. "Flag" means it ships behind a per-device switch until it has been seen on real
hardware. Every gesture also has a visible button, so a "no-go" never removes a feature, only the gesture.

| Gesture or capability | What is measured in emulation | Measured | Decision |
|---|---|---|---|
| Tap to select a clip | the tap selects and does not move the playhead | not measured by the spike spec (covered only by the general phone specs) | Needs device |
| One-finger pan of the strip | `touch-action: none` on the strip; no `pointercancel` during a pan | pixel7: strip `none`, canvas `none`, `overscroll-behavior` `contain`; 0 `pointercancel` in the pan. iphone14webkit: computed style `none`; pan count not measurable on emulation (D8) | Go (emulation) on Pixel 7; no-go until a real device for iOS |
| Pinch to zoom time | two-finger stream ends without `pointercancel`; the page itself does not zoom | pixel7: 0 `pointercancel` in the pinch. iphone14webkit: not measurable on emulation (D8) | Go (emulation) on Pixel 7; no-go until a real device for iOS |
| Trim-handle drag | the 44 px handle drag is stored and ends without `pointercancel` | pixel7: both handles `touch-action: none`; 0 `pointercancel` in the trim drag. iphone14webkit: handles `none`; drag count not measurable on emulation (D8) | Go (emulation) on Pixel 7 (buttons in the Trim sheet as well); no-go until a real device for iOS |
| Two-finger pan | pans without changing the selection | not measured by the spike spec | Needs device |
| Double tap to zoom to a clip | zooms to the clip | not measured by the spike spec | Needs device |
| Long press then drag to move a clip | the pickup works in emulation | not measured by the spike spec | Flag: off by default (`reelbolt:editor:touch-drag`), needs device; "Move earlier / later" and "Nudge" are the buttons |
| Edge of the screen | a drag starting in the outer 20 px is ignored by the strip | pixel7: the edge drag was ignored (timecode stayed `00:00:00:00`), but the control drag from the inner area also did not pan, so the check is weak. iphone14webkit: not measurable on emulation (D8) | Needs device (the system back swipe itself is a device check) |
| Media served in ranges | derivative content answers a `Range` request with 206 | pixel7 and iphone14webkit: status 206, `Accept-Ranges: bytes`, `Content-Range: bytes 0-1023/1807963`, 1024 bytes returned (7 derivatives found) | Go (emulation) |
| WebGL2 context loss and restore | `WEBGL_lose_context`: lost then restored events fire and the viewer keeps responding | pixel7 and iphone14webkit: extension present, lost and restored events fired, loss event cancelled, `isContextLost()` false afterwards, session `live` before and after, viewer visible, editor still responds, frame luma unchanged (57.8 to 58.0 on Pixel 7; 130.6 to 130.7 on iPhone 14) | Go (emulation); a memory-warning recovery on iOS stays a device check |
| Large canvas | a 16 megapixel canvas allocates and paints | pixel7: allocated and painted, 43 ms. iphone14webkit: allocated and painted, 352 ms | Go (emulation) (a picture editor job bigger than that is refused in plain words) |
| Sound after the first tap | an audio context resumes and an unmuted element plays after one tap | pixel7: context `running` before and after the first tap, unmuted play ok. iphone14webkit: context `suspended` until resumed, resumes to `running`, unmuted play ok (before and after the tap) | Go (emulation) (the first tap on Play starts the sound; the mute switch is a device check) |
| Recording | `MediaRecorder` exists and which formats it accepts | pixel7: present; accepts `audio/webm;codecs=opus`, `audio/webm`, `audio/mp4`; `getUserMedia` present. iphone14webkit: `MediaRecorder` absent in the emulated WebKit build (no formats); `getUserMedia` present | Go where present; the Audio sheet offers a voice-file upload where it is missing; iOS recording needs device |
| Soft keyboard | a sheet ends inside the visible area after the viewport shrinks by about 300 px | pixel7: visual viewport 839 to 539 px, sheet bottom 782 to 482, inside the visible area. iphone14webkit: 664 to 364 px, sheet bottom 607 to 364, inside the visible area (a resized window, not a real keyboard) | Go (emulation); the real iOS keyboard needs device |

## What emulation cannot show

This is the list that stays unverified until a person tries it on hardware:

- Real iOS touch handling: how Safari decides between scrolling, the edge back swipe and our pointer events.
- The silent (mute) switch and Bluetooth audio routing.
- Memory pressure: how a phone behaves when it is short of memory, including WebGL2 recovery after a system memory warning.
- Playing, proxying and rendering a 4K HLG clip from an iPhone (colour and speed).
- The real soft keyboard: in emulation a smaller window stands in for it, and iOS resizes only the visual viewport.
- Background and foreground handling over 60 seconds or more, including autosave in flight.
- Recording a voiceover with the real microphone on iOS and on Android.

## Manual real-device checklist

Run on an iPhone (current) with Safari, one older iPhone, and a mid-range Android phone with Chrome, once per release that
changes the phone editor:

- [ ] Strip pan, pinch and trim work without page scroll or back-swipe.
- [ ] Playback with sound after the first tap; the mute switch and Bluetooth behave.
- [ ] The soft keyboard never hides a focused field in a sheet.
- [ ] Background the tab for 60 s during autosave, return: no lost edit and no stuck chip.
- [ ] Upload a 4K HLG iPhone clip: poster, proxy, playback and render colours are right.
- [ ] WebGL2 recovers after a memory warning; a 10-minute session does not crash.
- [ ] Voiceover record on iOS (or the upload fallback) and on Android.
