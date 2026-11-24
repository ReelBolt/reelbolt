# Enhancement backlog

Internal, unpublished. Candidate enhancements derived from limits previously shown on audience pages
(`/for/<slug>`), removed 2026-11-13. The owner picks what to build. Wording below is the English text
the pages carried. Audiences: creators (C), small business (SB), educators (E), nonprofits (N),
software teams (ST), agencies (A).

## shortVideos
- Cannot: "It is built for short videos, about 90 seconds or less."
- Affected: C, SB, E, N, ST, A (all).
- Candidate: raise the supported runtime (longer runs, chaptered output) beyond about 90 seconds.

## oneLongTake
- Cannot: "Several different shots make a real edit. One long take can only be trimmed."
- Affected: C, SB, E, N, A.
- Candidate: cut a single long take into several scenes (auto-segment, reframe, b-roll cutaways).

## noPromptOnly
- Cannot: "On the free trial, no recipe turns only a text prompt or only photos into a video. You need a few clips."
- Affected: SB, N.
- Candidate: a free-trial recipe that builds a video from a prompt or from photos alone.

## noPresenter
- Cannot: "It does not make long presenter or avatar videos."
- Affected: E, ST.
- Candidate: presenter/avatar video recipe with lip-synced narration.

## noCarousel
- Cannot: "It does not make image carousels."
- Affected: SB.
- Candidate: carousel/still-sequence output for social platforms.

## noReshape
- Cannot: "It cannot yet re-cut a finished video into another shape. You choose the shape before the run."
- Affected: C, ST, A.
- Candidate: re-cut a finished video to another aspect ratio or length without a full re-run.

## noMusicLibrary
- Cannot: "There is no built-in music library. Music comes from a file you upload, or from music generation on a paid plan."
- Affected: C.
- Candidate: licensed built-in music library available on the free trial.

## noLipSync
- Cannot: "A dub does not change mouth movement on camera."
- Affected: E.
- Candidate: lip-sync for dubbed audio.

## codePromoCost
- Cannot: "A promo from code is the most expensive job here. A large one can use more credits than the trial and first-video credits together; you see the estimate before you start."
- Affected: ST.
- Candidate: cheaper code-promo path (smaller default scope, cost cap) so a large repo fits the trial.

## handoffPreview
- Cannot: "Hand-off packages are an early preview. We have not yet opened one in the major editing apps ourselves, so try it on a small project first."
- Affected: A.
- Candidate: verify hand-off packages in the major editing apps and add a compatibility test matrix.

## apiAlpha
- Cannot: "The API is in alpha and may change."
- Affected: A.
- Candidate: stabilise and version the public API.

## trialExport
- Cannot: "Trial exports are {resolution} with a watermark."
- Affected: all six.
- Candidate: review the trial export quality and watermark policy (a conversion decision, not a defect).

## alpha
- Cannot: "ReelBolt is in public alpha. Expect rough edges."
- Affected: all six.
- Candidate: exit public alpha once the above are settled.
