# PDQ parity fixture

`bridge-1-original.jpg` is a photograph of a bridge. It is the regression vector for the PDQ perceptual hash
port that `PdqHasherTests` checks (`inference/tests/ReelBolt.WorkflowEngine.Tests/Csam/PdqHasherTests.cs`).
It is not abuse material and is not related to any person.

## Where it comes from

The file is a byte-identical copy of `pdq/data/reg-test-input/dih/bridge-1-original.jpg` in Meta's
ThreatExchange repository, <https://github.com/facebook/ThreatExchange> (the same bytes are also
`pdq/data/bridge-mods/aaa-orig.jpg`). Checked on 2026-10-25 against the repository at commit
`295fd16e53c553443d8cfcb83ddc2f1c1d826fbd`.

- SHA-256: `b5b0799616df52d475a3968dc7e54f1d0724c912244ffa6175bc786375dd7298` (pinned in `PdqHasherTests`)
- The official PDQ hash that Meta documents for this image is the one `PdqHasherTests` compares against.

## Licence

The ThreatExchange repository, including its `pdq/data` test images, is distributed under the
BSD licence below. Meta's licence file is at <https://github.com/facebook/ThreatExchange/blob/main/LICENSE>.
The image is used here only as a test input; ReelBolt's own AGPL-3.0 licence does not apply to it.

```
BSD License

For ThreatExchange software

Copyright (c) Meta Platforms, Inc. and affiliates.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

 * Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

 * Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

 * Neither the name Facebook nor the names of its contributors may be used to
   endorse or promote products derived from this software without specific
   prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Rules for this folder

Do not replace the image: the test pins its SHA-256, and a different picture would make the regression
vector meaningless. Do not add other images here without recording their source and licence in this file.
