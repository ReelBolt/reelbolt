# RNNoise model: sh.rnnn ("somnolent-hogwash", speech signal / recording noise)

Source: https://github.com/GregorR/rnnoise-models (somnolent-hogwash-2018-09-01/sh.rnnn),
for ffmpeg's `arnndn` filter.

Licence: the repository README states: "With the exception of the tools/ directory and this
file, none of this work is creative and thus none of it is subject to copyright." The model
file is therefore not under copyright and carries no licence restrictions, so bundling it is
permitted. The repository ships no separate LICENSE file; this notice records that statement
as obtained on 2026-11-02. The tools/ directory is NOT vendored.

Integrity: SHA-256 `70bb6685eb0c2a1d18e2918dca3fbfbd39317010b1802eb1b6ea73a92f3fdec0`, pinned in
`inference/tests/ReelBolt.WorkflowEngine.Tests/LocalCleanupModelTests.cs` and verified by
`LocalAudioCleaner` before the first `VoiceBasic` use (mismatch is a typed failure).
