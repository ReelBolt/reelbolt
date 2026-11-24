Recorded mock fixtures for the audio arm. Every JSON body is copied verbatim from
the audio vendor fact sheet, section C (C1-C6); message
texts the fact sheet marks illustrative stay illustrative. `silence.mp3` is the fact sheet's own recipe:
`ffmpeg -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -q:a 9 silence.mp3` (2.0 s). No fixture came from a
live call and none carries a key: the tests use the literal key `test`.

QA-P1-18: `fal-rate-limit-429.json`, `fal-validation-422.json`, `fal-payment-required-402.json` and
`fal-service-unavailable-503.json` are NOT verbatim — the fact sheet records no fal body for those
statuses, only the shape of the measured 401 (`{"detail": "<message>"}`, a flat string, which
`FalQueueClient.VendorMessage` reads). Their message texts are illustrative; their SHAPE is fal's own,
which is the point: the fal tests no longer replay an ElevenLabs nested `detail` object that fal's own
reader cannot parse.
