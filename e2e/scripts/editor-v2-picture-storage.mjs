#!/usr/bin/env node
// Applies the documented bucket CORS rule (docs/direct-upload.md "The bucket CORS policy": browser-presigned-get-put)
// to the isolated stack's Garage bucket, so a browser on the stack's origin may PUT to a presigned URL.
//
//   S3_ENDPOINT=http://localhost:19000 S3_ACCESS_KEY=... S3_SECRET_KEY=... S3_BUCKET=reelforge \
//   ALLOWED_ORIGIN=http://localhost:18080 node editor-v2-picture-storage.mjs [apply|show]
//
// Pure Node (SigV4 by hand): the e2e package has no AWS SDK and this adds none. Nothing here touches a deployed box.
import { createHash, createHmac } from 'node:crypto';

const { S3_ENDPOINT, S3_ACCESS_KEY, S3_SECRET_KEY, S3_BUCKET = 'reelforge', S3_REGION = 'garage', ALLOWED_ORIGIN } = process.env;
const mode = process.argv[2] ?? 'apply';
if (!S3_ENDPOINT || !S3_ACCESS_KEY || !S3_SECRET_KEY || (mode === 'apply' && !ALLOWED_ORIGIN)) {
  console.error('S3_ENDPOINT, S3_ACCESS_KEY, S3_SECRET_KEY (and ALLOWED_ORIGIN for apply) are required');
  process.exit(2);
}
const host = new URL(S3_ENDPOINT).host;
if (!/^(localhost|127\.0\.0\.1)(:\d+)?$/.test(host)) {
  console.error(`refusing to touch ${host}: this helper only configures the local isolated stack`);
  process.exit(2);
}

const sha256 = (data) => createHash('sha256').update(data).digest('hex');
const hmac = (key, data) => createHmac('sha256', key).update(data).digest();

async function signed(method, query, body = '') {
  const now = new Date();
  const amzDate = now.toISOString().replace(/[-:]|\.\d{3}/g, '');
  const day = amzDate.slice(0, 8);
  const payloadHash = sha256(body);
  const headers = { host, 'x-amz-content-sha256': payloadHash, 'x-amz-date': amzDate };
  if (body) headers['content-md5'] = createHash('md5').update(body).digest('base64');
  const names = Object.keys(headers).sort();
  const canonicalHeaders = names.map((n) => `${n}:${headers[n]}\n`).join('');
  const canonical = [method, `/${S3_BUCKET}`, query, canonicalHeaders, names.join(';'), payloadHash].join('\n');
  const scope = `${day}/${S3_REGION}/s3/aws4_request`;
  const toSign = ['AWS4-HMAC-SHA256', amzDate, scope, sha256(canonical)].join('\n');
  const kDate = hmac(`AWS4${S3_SECRET_KEY}`, day);
  const signature = createHmac('sha256', hmac(hmac(hmac(kDate, S3_REGION), 's3'), 'aws4_request')).update(toSign).digest('hex');
  headers.authorization = `AWS4-HMAC-SHA256 Credential=${S3_ACCESS_KEY}/${scope}, SignedHeaders=${names.join(';')}, Signature=${signature}`;
  const res = await fetch(`${S3_ENDPOINT}/${S3_BUCKET}?${query}`, { method, headers, body: body || undefined });
  return { status: res.status, text: await res.text() };
}

if (mode === 'show') {
  const r = await signed('GET', 'cors=');
  console.log(r.status, r.text);
  process.exit(r.status === 200 ? 0 : 1);
}

const xml = `<?xml version="1.0" encoding="UTF-8"?><CORSConfiguration><CORSRule>
<ID>browser-presigned-get-put</ID>
<AllowedOrigin>${ALLOWED_ORIGIN}</AllowedOrigin>
<AllowedMethod>GET</AllowedMethod><AllowedMethod>HEAD</AllowedMethod><AllowedMethod>PUT</AllowedMethod>
<AllowedHeader>content-type</AllowedHeader><AllowedHeader>content-length</AllowedHeader><AllowedHeader>range</AllowedHeader>
<AllowedHeader>x-amz-content-sha256</AllowedHeader><AllowedHeader>x-amz-date</AllowedHeader><AllowedHeader>authorization</AllowedHeader>
<ExposeHeader>ETag</ExposeHeader><ExposeHeader>Content-Length</ExposeHeader><ExposeHeader>Content-Range</ExposeHeader><ExposeHeader>Accept-Ranges</ExposeHeader>
<MaxAgeSeconds>3600</MaxAgeSeconds>
</CORSRule></CORSConfiguration>`;
const r = await signed('PUT', 'cors=', xml);
console.log(`PutBucketCors ${S3_BUCKET} -> ${r.status} ${r.text.slice(0, 300)}`);
process.exit(r.status === 200 ? 0 : 1);
