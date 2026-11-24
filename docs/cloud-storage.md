# Cloud storage (Cloudflare R2)

The cloud deployment keeps all media and artifacts in one Cloudflare R2 bucket in the EU
jurisdiction. Self-hosting keeps using the bundled Garage container; the application code is
identical for both and is configured only through the `MinIO__*` keys (the prefix is historical).
This page is the operator reference for the R2 side. The code is `ReelBolt.Shared/Storage`
(`S3ClientFactory`, `PresignedUrlService`); the runner side of presigned URLs is in
[`runner-protocol.md`](runner-protocol.md); the VM layout is in
an operator file.

## Settings for R2

These are the exact values the Inference API and the WorkflowEngine need. In the cloud compose
files they come from `/opt/reelbolt/.env`, so the variable names below are the `.env` names.

| `.env` variable | Application setting | Value for R2 |
|---|---|---|
| `MINIO_ENDPOINT` | `MinIO__Endpoint` | `https://<account_id>.eu.r2.cloudflarestorage.com`. The `eu` host is mandatory for a bucket created in the EU jurisdiction; the default host cannot see it. |
| `MINIO_PUBLIC_ENDPOINT` | `MinIO__PublicEndpoint` | The same URL. Presigned URLs are signed against it, and SigV4 signs the `Host` header, so it must be the host a runner or browser really connects to. Leaving it empty falls back to `MinIO__Endpoint`, which is the same host on R2, so empty also works there; set it explicitly anyway so a later move to a custom domain is a one-line change. |
| `MINIO_REGION` | `MinIO__Region` | `auto`. The cloud compose files default to it. R2 accepts `auto` (and `us-east-1`) as the signing region. |
| `MINIO_FORCE_PATH_STYLE` | `MinIO__ForcePathStyle` | `true`, which is also the code default. The cloud role files pass it through with a default of `true`. Path style (`https://<acct>.eu.r2.cloudflarestorage.com/<bucket>/<key>`) works on R2 and keeps the bucket out of the host name. |
| `MINIO_BUCKET` | `MinIO__BucketName` | The object-store bucket name, for example `reelbolt-staging-objects`. |
| `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` | `MinIO__AccessKey` / `MinIO__SecretKey` | An R2 API token's key pair (see "API tokens"). |

The AWS SDK settings that matter are already in `S3ClientFactory` and need no per-environment
configuration. See "Checksums and presigning" for why.

## API tokens

Create tokens by hand in the Cloudflare dashboard (R2, Manage API tokens). Use one token per
purpose, each scoped to a single bucket:

| Token | Permission | Used by | Where it lives |
|---|---|---|---|
| Application (control) | Object Read and Write, only the app bucket | Inference API (`MINIO_ACCESS_KEY`/`MINIO_SECRET_KEY`) | `.env` on the control VM |
| Application (render host) | Object Read and Write, only the app bucket | WorkflowEngine (`MINIO_ACCESS_KEY`/`MINIO_SECRET_KEY`) | `.env` on the engine VM. A token of its own so it can be revoked alone; it still reaches every object in the bucket (the operator runbook) |
| Smoke test | Object Read and Write, only the **staging** environment's bucket | The staging smoke checklist below, run from a laptop | Your password manager only; revoke or rotate after each use |

The application token is deliberately narrow: the running services can read and
write objects but cannot change bucket configuration, delete the bucket or read other buckets.
A runner never holds an R2 token at all; it only receives presigned URLs. Rotation of each token
is in [`secrets.md`](secrets.md).

## Bucket lifecycle and CORS

The lifecycle and CORS rules below apply to the app bucket.

| Rule | Effect |
|---|---|
| `expire-runner-staging` | Objects under `runner-staging/` are deleted one day after upload (`staging_expiry_days`). Promotion moves a verified output to its final key within minutes, so anything that old is an abandoned upload. |
| `abort-incomplete-multipart` | Incomplete multipart uploads anywhere in the bucket are aborted after one day (`abort_multipart_days`). R2's own default is seven days, and the uploaded parts are billed until then. |
| CORS `browser-presigned-get-put` | `GET`, `HEAD` and `PUT` from the origins in `cors_allowed_origins` (the env's app hostname). Exposed headers are `ETag`, `Content-Length`, `Content-Range` and `Accept-Ranges`; allowed request headers are `content-type`, `content-length`, `range`, `x-amz-content-sha256`, `x-amz-date` and `authorization`. The dashboard's browser uploads rely on this rule too — see [`direct-upload.md`](direct-upload.md) "The bucket CORS policy" for the values and for the `OPTIONS` preflight check to run before trusting an upload. |

Limits worth knowing:

- Lifecycle rules run asynchronously inside R2. Expiry is "about a day", not to the minute, and
  nothing in the application relies on a staging object disappearing on a deadline.
- R2 CORS is **per bucket, not per prefix**. It cannot be restricted to `runner-staging/`. What keeps a
  presigned URL on a staging key or inside the caller's own project is the Inference API that
  signs it (`PresignedUrlService` refuses any key outside `projects/{projectId}/` for GET and
  always writes PUT URLs to `runner-staging/{jobId}/{n}`), not the CORS rule.
- Desktop and cloud runners are not browsers, so CORS does not apply to them. The dashboard IS one,
  so a missing or non-matching CORS rule breaks its direct uploads at the preflight — the client then
  falls back to the multipart route, and nothing else about the app notices.
- A browser PUT is signed for an exact `Content-Type`, so it must send the value its ticket
  returned: the same rule the line above states for a runner's staging PUT.
- The per-plan retention of delivered objects (30 days by default) is not a bucket rule. The
  application deletes them (F10), because the period differs per plan.
- A PUT URL is signed with the `Content-Type` it was minted with. The uploader must send exactly
  that header or R2 answers 403 `SignatureDoesNotMatch`.
- R2 and Garage cannot sign a maximum object length into a presigned PUT. The platform checks the
  size with a HEAD request in `PromoteAsync` and deletes an oversize staging object.
- `PromoteAsync` uses a single `CopyObject`, which R2 limits to 5 GiB. The largest cap in the
  runner protocol is 4 GiB (compile output), so the cap, not a multipart copy, is what keeps
  promotion working.

## Checksums and presigning

AWS SDKs from early 2025 (.NET `AWSSDK.S3` 4.x, JavaScript v3.729.0 and later) send a CRC32
checksum on uploads by default (`WHEN_SUPPORTED`). R2 did not implement that and answered
`Header 'x-amz-checksum-algorithm' with value 'CRC32' not implemented`
([Cloudflare Community report](https://community.cloudflare.com/t/aws-sdk-client-s3-v3-729-0-breaks-uploadpart-and-putobject-r2-s3-api-compatibility/758637)).
Cloudflare's own SDK examples set `requestChecksumCalculation: "WHEN_REQUIRED"` and
`responseChecksumValidation: "WHEN_REQUIRED"` for exactly this reason
([R2 aws-sdk-js-v3 guide](https://developers.cloudflare.com/r2/examples/aws/aws-sdk-js-v3/)).
The same bug hits presigning: a presigned PUT has no body when it is signed, so the default mode
signs the CRC32 of an empty payload into the URL and the real upload then mismatches
([example report](https://github.com/m0t0r/recomencemos/issues/317)).

`S3ClientFactory.Create` already sets both flags to `WHEN_REQUIRED` on **every** client,
including the keyed `"presign"` client that mints URLs, for Garage's sake. That is the setting R2
needs too, so the cloud needs **no extra flag**. Presigned GET and PUT then carry only the
standard `X-Amz-*` query parameters (`X-Amz-Algorithm`, `X-Amz-Credential`, `X-Amz-Date`,
`X-Amz-Expires`, `X-Amz-SignedHeaders`, `X-Amz-Signature`) and no checksum parameter.

Two cautions for future changes:

- Do not remove the two `WHEN_REQUIRED` lines (necessary but not sufficient; see "Chunked uploads are disabled"), and do not build an `AmazonS3Client` anywhere else
  without going through `S3ClientFactory`. A client created with defaults works against Garage
  in tests and fails against R2.
- The SDK's version pin (`AWSSDK.S3` 4.0.103.4) is the version these flags were verified on in
  code. R2's checksum support has been moving; Cloudflare may eventually accept CRC32, but
  `WHEN_REQUIRED` remains correct either way. The **staging smoke checklist below is the real
  proof** and must be run once against a real R2 bucket before the first cloud deploy and after
  any `AWSSDK.S3` upgrade. It has not been run against a live bucket yet (no Cloudflare account
  exists at the time of writing).

## Chunked uploads are disabled

Every server-side upload from the Inference API and the WorkflowEngine (`PutObject`, `UploadPart`,
so also `TransferUtility` multipart) used to fail against R2 with:

```
Amazon.S3.AmazonS3Exception: STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented
```

**Cause.** `AWSSDK.S3` 4.x sends those requests as an aws-chunked streaming-signature body, and R2
does not implement it. `RequestChecksumCalculation.WHEN_REQUIRED` only removes the CRC trailer; it
does not turn the streaming signature off, and a `BeforeRequestEvent` hook is too late (the request
is already marshalled). Browser uploads were unaffected because they use presigned PUTs.

**Fix.** `S3ClientFactory` registers one process-wide `IRuntimePipelineCustomizer`
(`ReelBolt.S3.DisableChunkedUploads`) that adds a handler before the SDK's `Marshaller` and sets
`UseChunkEncoding = false` on every `PutObjectRequest` and `UploadPartRequest`. A request whose
`InputStream` is not seekable cannot be hashed up front, so over https it also gets
`DisablePayloadSigning = true` (`UNSIGNED-PAYLOAD`; TLS protects the body). Over plain http the SDK
refuses that flag, so such a request is left chunked, which Garage over http accepts. The change is
unconditional because Garage accepts plain single-shot SigV4 as well. A non-seekable upload must
carry `Headers.ContentLength`, as the SDK cannot determine it otherwise.
Tests: `S3ChunkedUploadCustomizerTests`. Re-run the smoke checklist after any `AWSSDK.S3` upgrade.

## Staging smoke checklist

Run this against the staging bucket with the smoke-test token. It
exercises every object-store path the product uses. Export `ENDPOINT`, `BUCKET`, `AWS_ACCESS_KEY_ID`,
`AWS_SECRET_ACCESS_KEY` and `AWS_DEFAULT_REGION=auto`. Use the AWS CLI v2 with
`aws configure set default.s3.addressing_style path` and, because the CLI also defaults to CRC32,
`aws configure set request_checksum_calculation when_required` and
`aws configure set response_checksum_validation when_required`.

1. **Upload.** `aws --endpoint-url "$ENDPOINT" s3 cp ./sample.mp4 s3://$BUCKET/projects/smoke/sample.mp4`
   succeeds, and `aws ... s3api head-object` reports the right `ContentLength`.
2. **Presigned GET.** `aws ... s3 presign s3://$BUCKET/projects/smoke/sample.mp4 --expires-in 900`,
   then `curl -sS -o /dev/null -w '%{http_code}\n' "<url>"` prints `200`. The URL names the
   `$ENDPOINT` host.
3. **Presigned PUT to staging.** Get a URL from the Inference API (or `aws s3 presign` with the
   `runner-staging/smoke/0` key), then
   `curl -sS -X PUT -H 'Content-Type: video/mp4' --data-binary @sample.mp4 "<url>"` returns 200.
   Repeat without the `Content-Type` header and confirm the failure is 403 (the signed header
   is enforced), then check the URL contains no `x-amz-checksum-*` parameter.
4. **Promote.** Through the platform (a runner job, or an integration call to
   `PresignedUrlService.PromoteAsync`), move `runner-staging/smoke/0` to
   `projects/smoke/promoted.mp4`. The staging key is gone, the final key has the same size, and
   an oversize cap deletes the staging object and raises `PresignedObjectTooLargeException`.
5. **Ranged streaming.** `curl -sS -H 'Range: bytes=0-1023' -D - -o /dev/null "<presigned GET>"`
   returns `206` with `Content-Range: bytes 0-1023/<size>`. Then open the object through the
   dashboard player or `GET /api/v1/projects/{id}/files/{fileId}/media` with a `Range` header
   (`RangedObjectStreamer`), seek mid-file and confirm playback continues without a full
   download. A browser seek from the app origin must also pass CORS: in devtools there is no
   CORS error on the ranged request.
6. **Multipart abort.** Start `aws s3api create-multipart-upload` on `projects/smoke/mp`, upload
   one part, and leave it. After the rule runs (about a day) `aws s3api list-multipart-uploads`
   no longer lists it. Optional on the first pass, but the only way to see the lifecycle rule work.
7. **Staging expiry.** The `runner-staging/smoke/0` object from step 3, if still present, is gone
   after about a day.
8. **Clean up.** Delete `projects/smoke/` and revoke or rotate the smoke-test token.

Record the date, the SDK version and the result in the staging status notes. If step 1 or 3
fails with `x-amz-checksum-algorithm ... not implemented`, a client was built without
`S3ClientFactory`; fix that, do not add a per-client workaround.
