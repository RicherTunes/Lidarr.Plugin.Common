# DRM Conformance Fixtures

The Bento4 conformance tests embed their binary fixtures as base64 so CI does not need Bento4 installed.
They were ported from GitHub PRs `#609` and `#610`.

## Current Fixture Facts

The original PRs document that the media segment and init segment were produced with Bento4
`mp4encrypt --method MPEG-CENC`, validated with `mp4decrypt`/`mp4dump`, using:

- content key: `00112233445566778899aabbccddeeff`
- KID: `9eb4050de44b4802932e27d75083e266`
- scheme: `cenc`
- per-sample IV size: `16`

The original PRs did not record the exact Bento4 build/version or complete shell transcript. Do not
replace these fixtures unless the replacement PR records both.

## Embedded Fixture Hashes

These hashes are for the decoded bytes, not the base64 text:

| fixture | bytes | sha256 |
| --- | ---: | --- |
| `CencBento4ConformanceTests.SegmentBase64` | 1331 | `e9382ff6e831730f3d302cc9d68b6bcb62b93390664839e983cc8c0e0a4e0456` |
| `CencBento4ConformanceTests.ExpectedPlaintextBase64` | 934 | `0923ecd21a753e2c75bc42b01929516951b90b61c432fec9316694f0d36a3a2a` |
| `CencBento4InitTencConformanceTests.InitBase64` | 711 | `69eac2eca327d036e6999fb33b0159ad8f015664e75bbd1a63595efba989f0df` |

## Regeneration Requirements

Any future fixture-refresh PR must include:

- exact Bento4 version/build output
- exact `ffmpeg`, `mp4fragment`, `mp4encrypt`, `mp4decrypt`, and `mp4dump` commands
- SHA256 hashes for the decoded init segment, encrypted segment, and expected plaintext
- the test run proving the regenerated fixtures still pass without Bento4 installed
