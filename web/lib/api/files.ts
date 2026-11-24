import { handleUnauthorized, unauthorizedMessage } from '../auth/session-expiry';
import { apiFetch, ApiError, extractErrorList, extractErrorMessage } from './client';
import type {
  ProjectFile,
  ProjectFileContent,
  MoveProjectFilesRequest,
  RenameFolderRequest,
  ReindexProjectFilesRequest,
  ReindexProjectFilesResponse,
  FileSearchResponse,
} from '../types/project';

type FileWithRelativePath = File & { webkitRelativePath?: string };

/**
 * The permission slip `POST …/files/upload-url` returns. `url` is the presigned PUT — a bearer
 * capability, so it is held in memory for the length of one upload and never logged. `contentType`
 * is the exact value the PUT must send, because it is one of the headers the signature covers.
 *
 * A ticket is one of two modes (docs/design/local-folder-sync-and-storage.md Part 3): `singlePut`
 * carries `url` and no parts, `multipart` carries `uploadId`/`partSizeBytes`/`partCount` and `url` is
 * null — its part URLs are fetched in batches from `POST …/files/upload-parts`.
 */
export interface DirectUploadTicket {
  uploadToken: string;
  key: string;
  fileId: string;
  url: string | null;
  expiresAtUnix: number;
  contentType: string;
  /** The server's ceiling: 5 TiB (S3's own object limit). The plan's quota is the real limit. */
  maxBytes: number;
  /** `singlePut` when absent, which is what every pre-multipart server sent. */
  mode?: 'singlePut' | 'multipart';
  uploadId?: string | null;
  partSizeBytes?: number;
  partCount?: number;
  fileName?: string | null;
  relativePath?: string | null;
  sizeBytes?: number;
}

/** One part's presigned URL, from `POST …/files/upload-parts`. Part numbers are 1-based. */
export interface DirectUploadPartUrl {
  partNumber: number;
  url: string;
  expiresAtUnix: number;
}

/** One uploaded part: its number and the `ETag` storage answered the PUT with. */
export interface DirectUploadPart {
  partNumber: number;
  etag: string;
}

/** What to PUT, so the API's ticket fields round-trip unchanged. */
export interface DirectUploadOptions {
  relativePath?: string;
  /** Stops an in-flight upload and aborts its ticket; used by cancel and by unmount. */
  signal?: AbortSignal;
}

export function getProjectFiles(projectId: string): Promise<ProjectFile[]> {
  return apiFetch<ProjectFile[]>(`/api/v1/projects/${projectId}/files`);
}

/**
 * The storage PUT could not reach the object store at all: `xhr.status === 0` (a rejected CORS
 * preflight — the one thing a self-host install has to configure — or a dead network) with not one
 * byte of the body flushed. Nothing was written, so this is the one storage failure the multipart
 * route can safely be retried through.
 */
class StorageUnreachableError extends Error {}

/** 501 from `upload-url`: this deployment cannot presign, so the multipart route is the route. */
function isDirectUploadUnsupported(cause: unknown): boolean {
  return cause instanceof ApiError && cause.status === 501;
}

/**
 * Uploads one file, straight to object storage when the deployment supports it.
 *
 * <p>**The one rule for choosing a route.** The browser first asks the API for a presigned PUT
 * (`POST …/files/upload-url`) and PUTs the bytes to storage, so neither Cloudflare's 100 s response
 * budget nor nginx's `client_max_body_size` ever sees them. It falls back to the multipart
 * `POST …/files` **only** in the two cases where no bytes reached storage: the API answered 501
 * `DIRECT_UPLOAD_DISABLED` (nothing to fall back from), or the storage PUT failed at the network
 * level with zero bytes sent — a bucket with no CORS policy, which is exactly what a self-host
 * install ships with. Every other failure (403 from storage, a 400/409/413 from the API, an expired
 * ticket) is reported to the caller rather than silently re-sent, because re-uploading a file the
 * server has already refused would hide the reason.</p>
 */
export function uploadFileWithProgress(
  projectId: string,
  file: File,
  onProgress: (percent: number) => void,
  options?: DirectUploadOptions,
): Promise<ProjectFile> {
  return uploadFileDirect(projectId, file, onProgress, options).catch((cause: unknown) => {
    if (!isDirectUploadUnsupported(cause) && !(cause instanceof StorageUnreachableError)) throw cause;
    // The fallback is the multipart FORM route, so a cancelled upload never re-sends itself there.
    if (isAborted(options?.signal)) throw cause;
    return uploadFileMultipart(projectId, file, onProgress, options);
  });
}

/** {@link uploadFileWithProgress} without progress reporting (the assistant's attach control). */
export function uploadFile(projectId: string, file: File): Promise<ProjectFile> {
  return uploadFileWithProgress(projectId, file, () => {});
}

function relativePathFor(file: File, options?: DirectUploadOptions): string | undefined {
  return options?.relativePath || (file as FileWithRelativePath).webkitRelativePath || undefined;
}

/**
 * The storage PUT's Content-Type. A file the browser could not type is sent as
 * `application/octet-stream`, which is the same value the API resolves a MIME type from by
 * extension for a known media file (so a generic type never smuggles a video past the media rules).
 */
function claimContentType(file: File): string {
  return file.type || 'application/octet-stream';
}

/** Step 1: ask for permission and a place to PUT. */
export function requestDirectUpload(
  projectId: string,
  payload: { fileName: string; sizeBytes: number; contentType: string; relativePath?: string },
  signal?: AbortSignal,
): Promise<DirectUploadTicket> {
  return apiFetch<DirectUploadTicket>(`/api/v1/projects/${projectId}/files/upload-url`, {
    method: 'POST',
    body: JSON.stringify(payload),
    signal,
  });
}

/**
 * Step 2: PUT the bytes straight to storage. No credentials are sent — `withCredentials` stays
 * false — because the signed URL *is* the authorization, and the header must match the signed
 * Content-Type byte for byte or the store rejects the signature.
 */
export function putFileToStorage(
  ticket: DirectUploadTicket,
  file: File,
  onProgress: (percent: number) => void,
  signal?: AbortSignal,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', ticket.url ?? '');
    if (ticket.contentType) xhr.setRequestHeader('Content-Type', ticket.contentType);

    let bytesSent = false;
    xhr.upload.onprogress = (event) => {
      if (event.loaded > 0) bytesSent = true;
      if (event.lengthComputable) onProgress(Math.round((event.loaded / event.total) * 100));
    };

    const onAbort = () => {
      try { xhr.abort(); } catch { /* already finished */ }
      reject(cancelledError());
    };
    if (signal) {
      if (signal.aborted) { onAbort(); return; }
      signal.addEventListener('abort', onAbort, { once: true });
    }

    xhr.onload = () => {
      signal?.removeEventListener('abort', onAbort);
      if (xhr.status >= 200 && xhr.status < 300) {
        onProgress(100);
        resolve();
        return;
      }
      reject(new Error(`Storage refused the upload (HTTP ${xhr.status}).`));
    };
    xhr.onerror = () => {
      signal?.removeEventListener('abort', onAbort);
      reject(bytesSent
        ? new Error('The upload to storage was interrupted. Please try again.')
        : new StorageUnreachableError('The upload could not reach storage.'));
    };
    xhr.onabort = () => {
      signal?.removeEventListener('abort', onAbort);
      reject(cancelledError());
    };
    xhr.send(file);
  });
}

/**
 * Step 3: tell the API it is done and receive the recorded file. The body echoes the ticket's own
 * fields back (`VerifyEcho` on the server rebuilds the ticket from them and checks its MAC), plus —
 * for a multipart upload — its mode/uploadId/partSizeBytes/partCount and every part's ETag.
 */
export function completeDirectUpload(
  projectId: string,
  ticket: DirectUploadTicket,
  file: File,
  relativePath?: string,
  parts?: DirectUploadPart[],
): Promise<ProjectFile> {
  const multipart = ticket.mode === 'multipart';
  const sizeBytes = ticket.sizeBytes ?? file.size;
  return apiFetch<ProjectFile>(`/api/v1/projects/${projectId}/files/upload-complete`, {
    method: 'POST',
    body: JSON.stringify({
      uploadToken: ticket.uploadToken,
      key: ticket.key,
      fileId: ticket.fileId,
      fileName: ticket.fileName || file.name,
      sizeBytes,
      expiresAtUnix: ticket.expiresAtUnix,
      contentType: ticket.contentType,
      relativePath: relativePath ?? ticket.relativePath ?? undefined,
      ...(multipart
        ? {
            mode: 'multipart',
            uploadId: ticket.uploadId ?? null,
            partSizeBytes: ticket.partSizeBytes ?? 0,
            partCount: ticket.partCount ?? 0,
            parts: parts ?? [],
          }
        : {}),
    }),
  });
}

// ---------------------------------------------------------------------------------------------
// Multipart (docs/design/local-folder-sync-and-storage.md Part 3)
// ---------------------------------------------------------------------------------------------

/** At most this many part numbers are signed in one `upload-parts` call — the server's own cap. */
export const PART_URL_BATCH = 100;
/** At most this many parts are in flight at once. */
export const PART_CONCURRENCY = 3;
/** How many times one part's PUT is attempted before the upload fails. */
export const PART_ATTEMPTS = 3;

/** How many parts of `sizeBytes` a ticket has, preferring the server's own count when it gave one. */
export function partCountOf(ticket: DirectUploadTicket, sizeBytes: number): number {
  if (ticket.partCount && ticket.partCount > 0) return ticket.partCount;
  const partSize = ticket.partSizeBytes ?? 0;
  return partSize > 0 ? Math.max(1, Math.ceil(sizeBytes / partSize)) : 0;
}

/** A short cancellation-shaped error, so an abort reads as one everywhere. */
function cancelledError(): Error {
  const error = new Error('Upload cancelled.');
  error.name = 'AbortError';
  return error;
}

/** The byte range of one 1-based part, from the ticket's part size (the last part is shorter). */
export function partRange(partNumber: number, partSizeBytes: number, sizeBytes: number): { start: number; end: number } {
  const start = (partNumber - 1) * partSizeBytes;
  return { start, end: Math.min(sizeBytes, start + partSizeBytes) };
}

function shouldRetryPart(error: unknown): boolean {
  if (error instanceof StorageUnreachableError) return false;
  if (error instanceof Error && error.name === 'AbortError') return false;
  // A refusal from storage (403/404/…) will be refused again; a network drop or a 5xx may not.
  const status = /\(HTTP (\d+)\)/.exec(error instanceof Error ? error.message : '');
  if (status) return Number(status[1]) >= 500;
  return true;
}

/**
 * Runs at most `limit` of the thunks at once, in order, returning their results in order. Used for
 * the parts: the whole file is never in memory at once, and no more than three PUTs are open.
 */
async function mapWithConcurrency<T, R>(items: T[], limit: number, run: (item: T, index: number) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const worker = async () => {
    for (;;) {
      const index = next++;
      if (index >= items.length) return;
      results[index] = await run(items[index], index);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

/** Aggregated progress across every part: bytes flushed everywhere over the whole file, 1–99 mid-flight. */
function partProgressTracker(sizeBytes: number, onProgress: (percent: number) => void) {
  const loaded = new Map<number, number>();
  let reported = -1;
  return {
    update(partNumber: number, partLoaded: number) {
      loaded.set(partNumber, partLoaded);
      if (sizeBytes <= 0) return;
      let total = 0;
      for (const value of loaded.values()) total += value;
      const percent = Math.max(1, Math.min(99, Math.floor((total / sizeBytes) * 100)));
      if (percent !== reported) {
        reported = percent;
        onProgress(percent);
      }
    },
    /** A part's PUT failed and is about to be retried: its flushed bytes are gone, not progress. */
    reset(partNumber: number) {
      loaded.delete(partNumber);
    },
  };
}

/** Step 2 of a multipart upload: PUT one part and read the `ETag` storage answered with. */
function putPartToStorage(
  ticket: DirectUploadTicket,
  part: DirectUploadPartUrl,
  blob: Blob,
  onProgress: (loaded: number) => void,
  signal?: AbortSignal,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', part.url);
    // No Content-Type: the part URL is signed for the part upload alone, and the ETag is what
    // completion needs — the R2/nginx CORS policy exposes it (docs/direct-upload.md).

    let bytesSent = false;
    let lastLoaded = 0;
    xhr.upload.onprogress = (event) => {
      if (event.loaded > 0) bytesSent = true;
      if (event.lengthComputable) {
        lastLoaded = event.loaded;
        onProgress(event.loaded);
      }
    };

    const onAbort = () => {
      try { xhr.abort(); } catch { /* already finished */ }
      reject(cancelledError());
    };
    if (signal) {
      if (signal.aborted) { onAbort(); return; }
      signal.addEventListener('abort', onAbort, { once: true });
    }

    xhr.onload = () => {
      signal?.removeEventListener('abort', onAbort);
      if (xhr.status < 200 || xhr.status >= 300) {
        reject(new Error(`Storage refused part ${part.partNumber} (HTTP ${xhr.status}).`));
        return;
      }
      // Missing ETag means storage (or a proxy in front of it) strips the header; the part would be
      // unreportable, so it is treated as a failed attempt rather than silently completed with ''.
      const etag = xhr.getResponseHeader('ETag');
      if (!etag) {
        reject(new Error(`Storage did not report an ETag for part ${part.partNumber}.`));
        return;
      }
      if (!bytesSent) onProgress(lastLoaded);
      resolve(etag);
    };
    xhr.onerror = () => {
      signal?.removeEventListener('abort', onAbort);
      reject(bytesSent
        ? new Error(`The upload of part ${part.partNumber} was interrupted. Please try again.`)
        : new StorageUnreachableError('The upload could not reach storage.'));
    };
    xhr.onabort = () => {
      signal?.removeEventListener('abort', onAbort);
      reject(cancelledError());
    };
    xhr.send(blob);
  });
}

/**
 * Runs one part's PUT, retrying a failed attempt up to {@link PART_ATTEMPTS} times with exponential
 * backoff. A part storage itself refused, a cancellation and a storage that never answered are not
 * retried: the first two would fail identically, and the third is the multipart route's one fallback
 * signal (it becomes a `StorageUnreachableError`, which `uploadFileWithProgress` catches).
 */
async function putPartWithRetry(
  ticket: DirectUploadTicket,
  part: DirectUploadPartUrl,
  blob: Blob,
  onProgress: (loaded: number) => void,
  onReset: () => void,
  signal?: AbortSignal,
): Promise<string> {
  let lastError: unknown = new Error(`Part ${part.partNumber} could not be uploaded.`);
  let slept = 0;
  for (let attempt = 1; attempt <= PART_ATTEMPTS; attempt++) {
    try {
      return await putPartToStorage(ticket, part, blob, onProgress, signal);
    } catch (cause) {
      lastError = cause;
      if (attempt >= PART_ATTEMPTS || !shouldRetryPart(cause)) throw cause;
      onReset();
      // Exponential backoff, capped so a stalled upload cannot sit here for long in total.
      const delay = Math.min(1000, 250 * 2 ** (attempt - 1));
      if (slept + delay <= 2500) {
        slept += delay;
        await new Promise((resolve) => setTimeout(resolve, delay));
      }
    }
  }
  throw lastError;
}

/**
 * `POST …/files/upload-parts` — presigned PUT URLs for the given part numbers, in as few calls as the
 * server's 100-per-call cap allows.
 */
export async function requestUploadPartUrls(
  projectId: string,
  ticket: DirectUploadTicket,
  partNumbers: number[],
): Promise<DirectUploadPartUrl[]> {
  const batches: number[][] = [];
  for (let i = 0; i < partNumbers.length; i += PART_URL_BATCH) {
    batches.push(partNumbers.slice(i, i + PART_URL_BATCH));
  }
  const url = `/api/v1/projects/${projectId}/files/upload-parts`;
  const answers = await Promise.all(
    batches.map((batch) => apiFetch<DirectUploadPartUrl[]>(url, {
      method: 'POST',
      body: JSON.stringify({
        uploadToken: ticket.uploadToken,
        key: ticket.key,
        fileId: ticket.fileId,
        fileName: ticket.fileName ?? '',
        sizeBytes: ticket.sizeBytes ?? 0,
        expiresAtUnix: ticket.expiresAtUnix,
        contentType: ticket.contentType,
        relativePath: ticket.relativePath ?? null,
        mode: 'multipart',
        uploadId: ticket.uploadId ?? null,
        partSizeBytes: ticket.partSizeBytes ?? 0,
        partCount: ticket.partCount ?? 0,
        partNumbers: batch,
      }),
    })),
  );
  return answers.flat();
}

/**
 * `POST …/files/upload-abort` (`204`) — aborts a multipart upload, freeing the parts already stored.
 * Best effort: an abort that itself fails must never replace the failure the caller is reporting.
 */
export async function abortDirectUpload(projectId: string, ticket: DirectUploadTicket): Promise<void> {
  try {
    await apiFetch<void>(`/api/v1/projects/${projectId}/files/upload-abort`, {
      method: 'POST',
      body: JSON.stringify({
        uploadToken: ticket.uploadToken,
        key: ticket.key,
        fileId: ticket.fileId,
        fileName: ticket.fileName ?? '',
        sizeBytes: ticket.sizeBytes ?? 0,
        expiresAtUnix: ticket.expiresAtUnix,
        contentType: ticket.contentType,
        relativePath: ticket.relativePath ?? null,
        mode: ticket.mode ?? 'singlePut',
        uploadId: ticket.uploadId ?? null,
        partSizeBytes: ticket.partSizeBytes ?? 0,
        partCount: ticket.partCount ?? 0,
      }),
    });
  } catch {
    // Nothing to do: the parts are aborted by the bucket's own lifecycle rule when this fails.
  }
}

export function isAborted(signal?: AbortSignal): boolean {
  return Boolean(signal && 'aborted' in signal && signal.aborted);
}

/**
 * The multipart half of the direct route: ask for part URLs in batches of ≤ 100, PUT the parts 3 at a
 * time reading each one's `ETag`, aggregate their progress, then finish with the part list. Every
 * failure and every cancellation aborts the upload first, so no half-written multipart upload is left
 * behind (the bucket's lifecycle rule is the backstop, not the plan).
 *
 * The file's own bytes are sliced per part, so nothing larger than one part is ever handed to XHR.
 */
async function uploadFileDirectMultipart(
  projectId: string,
  file: File,
  ticket: DirectUploadTicket,
  onProgress: (percent: number) => void,
  relativePath: string | undefined,
  signal?: AbortSignal,
): Promise<ProjectFile> {
  const sizeBytes = ticket.sizeBytes && ticket.sizeBytes > 0 ? ticket.sizeBytes : file.size;
  const partSize = ticket.partSizeBytes ?? 0;
  const partCount = partCountOf(ticket, sizeBytes);
  if (!partSize || partCount <= 0) {
    throw new Error('The upload ticket did not say how to split this file.');
  }

  const numbers = Array.from({ length: partCount }, (_, i) => i + 1);
  const tracker = partProgressTracker(sizeBytes, onProgress);

  const finish = (parts: DirectUploadPart[]) =>
    completeDirectUpload(projectId, ticket, file, relativePath, parts)
      .then((recorded) => { onProgress(100); return recorded; });

  try {
    const urls = await requestUploadPartUrls(projectId, ticket, numbers);
    if (urls.length !== numbers.length) {
      throw new Error('Storage did not issue every part of this upload. Try again.');
    }
    const byNumber = new Map(urls.map((u) => [u.partNumber, u]));
    const ordered = numbers.map((number) => {
      const part = byNumber.get(number);
      if (!part) throw new Error(`Storage did not issue a URL for part ${number}.`);
      return part;
    });

    const etags = await mapWithConcurrency(ordered, PART_CONCURRENCY, async (part) => {
      const { start, end } = partRange(part.partNumber, partSize, sizeBytes);
      const blob = file.slice(start, end);
      const etag = await putPartWithRetry(
        ticket,
        part,
        blob,
        (loaded) => tracker.update(part.partNumber, loaded),
        () => tracker.reset(part.partNumber),
        signal,
      );
      tracker.update(part.partNumber, blob.size);
      return { partNumber: part.partNumber, etag };
    });

    return await finish(etags);
  } catch (cause) {
    await abortDirectUpload(projectId, ticket);
    throw cause;
  }
}

async function uploadFileDirect(
  projectId: string,
  file: File,
  onProgress: (percent: number) => void,
  options?: DirectUploadOptions,
): Promise<ProjectFile> {
  const relativePath = relativePathFor(file, options);
  const ticket = await requestDirectUpload(projectId, {
    fileName: file.name,
    sizeBytes: file.size,
    contentType: claimContentType(file),
    relativePath,
  }, options?.signal);
  if (ticket.mode === 'multipart') {
    return uploadFileDirectMultipart(projectId, file, ticket, onProgress, relativePath, options?.signal);
  }
  await putFileToStorage(ticket, file, onProgress, options?.signal);
  return completeDirectUpload(projectId, ticket, file, relativePath);
}

/**
 * The original route: the whole file through the API. Kept because a self-host install has no bucket
 * CORS policy until its owner sets one — and because it is what a non-browser client can use.
 * Capped by Cloudflare (100 MB) and nginx (`client_max_body_size`), so it is the fallback, never
 * the first choice.
 *
 * A refusal's JSON body is decoded in full, so a `STORAGE_QUOTA_EXCEEDED` here keeps its structured
 * `limit`/`current` figures (the code reads them) instead of collapsing to a bare sentence.
 */
function uploadFileMultipart(
  projectId: string,
  file: File,
  onProgress: (percent: number) => void,
  options?: DirectUploadOptions,
): Promise<ProjectFile> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', `/api/v1/projects/${projectId}/files`);
    const signal = options?.signal;

    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable) {
        const percent = Math.round((event.loaded / event.total) * 100);
        onProgress(percent);
      }
    };

    const onAbort = () => {
      try { xhr.abort(); } catch { /* already finished */ }
      reject(cancelledError());
    };
    if (signal) {
      if (signal.aborted) { onAbort(); return; }
      signal.addEventListener('abort', onAbort, { once: true });
    }

    xhr.onreadystatechange = () => {
      if (xhr.readyState === XMLHttpRequest.DONE) {
        signal?.removeEventListener('abort', onAbort);
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            resolve(JSON.parse(xhr.responseText));
          } catch (err) {
            reject(err);
          }
        } else {
          let body: unknown;
          try { body = JSON.parse(xhr.responseText); } catch { /* a proxy's HTML error page */ }
          handleUnauthorized(xhr.status);
          if (body) {
            reject(new ApiError(unauthorizedMessage(xhr.status) ?? extractErrorMessage(body) ?? 'Upload failed', xhr.status, extractErrorList(body), body));
          } else {
            reject(new ApiError('Upload failed', xhr.status, []));
          }
        }
      }
    };

    xhr.onerror = () => {
      signal?.removeEventListener('abort', onAbort);
      reject(new Error('Network error'));
    };
    xhr.onabort = () => {
      signal?.removeEventListener('abort', onAbort);
      reject(cancelledError());
    };

    const formData = new FormData();
    formData.append('file', file);
    const relativePath = relativePathFor(file, options);
    if (relativePath) {
      formData.append('relativePath', relativePath);
    }
    xhr.send(formData);
  });
}

export function deleteFile(projectId: string, fileId: string): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${projectId}/files/${fileId}`, {
    method: 'DELETE',
  });
}

/**
 * How many pictures (thumbnail editor documents) use a file, so the delete dialog can say they keep
 * their own copy. `GET …/files/{fileId}/picture-usage` answers `{ pictures: number }`. Every read of
 * this count goes through here, so a change to the route's shape is a change to this one function.
 */
export async function getFilePictureUsage(projectId: string, fileId: string): Promise<number> {
  const body = await apiFetch<{ pictures?: unknown }>(
    `/api/v1/projects/${projectId}/files/${fileId}/picture-usage`,
  );
  const n = body?.pictures;
  return typeof n === 'number' && Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}

/**
 * Renames one file in place (`PATCH …/files/{fileId}`, body `{ fileName }`). Only the display name
 * changes; the stored object, and everything that references it, stays put. The server answers 400
 * for an empty name, a name with `/` or `\\`, or one longer than 255 characters, and 409 when another
 * file in the same folder already has the name — both surface as an `ApiError` with that message.
 */
export function renameFile(projectId: string, fileId: string, fileName: string): Promise<ProjectFile> {
  return apiFetch<ProjectFile>(`/api/v1/projects/${projectId}/files/${fileId}`, {
    method: 'PATCH',
    body: JSON.stringify({ fileName }),
  });
}

export function getProjectFileContent(projectId: string, fileId: string): Promise<ProjectFileContent> {
  return apiFetch<ProjectFileContent>(`/api/v1/projects/${projectId}/files/${fileId}/content`);
}

export function updateProjectFileContent(
  projectId: string,
  fileId: string,
  content: string,
  contentType?: string,
): Promise<ProjectFile> {
  return apiFetch<ProjectFile>(`/api/v1/projects/${projectId}/files/${fileId}/content`, {
    method: 'PUT',
    body: JSON.stringify({ content, contentType }),
  });
}

export function moveProjectFiles(projectId: string, payload: MoveProjectFilesRequest): Promise<ProjectFile[]> {
  return apiFetch<ProjectFile[]>(`/api/v1/projects/${projectId}/files/move`, {
    method: 'POST',
    body: JSON.stringify(payload),
  });
}

export function createFolder(
  projectId: string,
  path: string,
): Promise<{ path: string; materialized: boolean; message: string }> {
  return apiFetch<{ path: string; materialized: boolean; message: string }>(`/api/v1/projects/${projectId}/files/folders`, {
    method: 'POST',
    body: JSON.stringify({ path }),
  });
}

export function renameFolder(
  projectId: string,
  payload: RenameFolderRequest,
): Promise<{ sourcePath: string; targetPath: string; movedFiles: number }> {
  return apiFetch<{ sourcePath: string; targetPath: string; movedFiles: number }>(`/api/v1/projects/${projectId}/files/folders`, {
    method: 'PATCH',
    body: JSON.stringify(payload),
  });
}

export function deleteFolder(
  projectId: string,
  path: string,
  recursive?: boolean,
): Promise<{ deletedFiles: number; path: string }> {
  return apiFetch<{ deletedFiles: number; path: string }>(`/api/v1/projects/${projectId}/files/folders`, {
    method: 'DELETE',
    body: JSON.stringify({ path, recursive }),
  });
}

export async function downloadFile(projectId: string, fileId: string): Promise<Blob> {
  const res = await fetch(`/api/v1/projects/${projectId}/files/${fileId}/download`);

  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error(body.message || 'Download failed');
  }

  return res.blob();
}

export function reindexProjectFile(projectId: string, fileId: string): Promise<{ fileId: string; status: string }> {
  return apiFetch<{ fileId: string; status: string }>(`/api/v1/projects/${projectId}/files/${fileId}/reindex`, {
    method: 'POST',
  });
}

export function reindexProjectFiles(projectId: string, payload?: ReindexProjectFilesRequest): Promise<ReindexProjectFilesResponse> {
  return apiFetch<ReindexProjectFilesResponse>(`/api/v1/projects/${projectId}/files/reindex`, {
    method: 'POST',
    body: JSON.stringify(payload ?? {}),
  });
}

/** Semantic (by meaning) search over indexed file contents. Throws when embeddings are unavailable. */
export function searchProjectFiles(projectId: string, query: string, limit = 8): Promise<FileSearchResponse> {
  return apiFetch<FileSearchResponse>(`/api/v1/projects/${projectId}/files/search`, {
    method: 'POST',
    body: JSON.stringify({ query, limit }),
  });
}

/** Inline, Range-aware stream for previews (the /download endpoint is an attachment without ranges). */
export function projectFileUrl(projectId: string, fileId: string): string {
  return `/api/v1/projects/${projectId}/files/${fileId}/media`;
}

/**
 * A resized WebP preview of an IMAGE file, `width` px wide (server-allowed: 160, 320, 480, 640, 960, 1280, 1920).
 * Images only; never use for video/audio, downloads or links.
 */
export function projectFilePreviewUrl(projectId: string, fileId: string, width: number): string {
  return `${projectFileUrl(projectId, fileId)}?w=${width}`;
}

export type ArchiveUnpackStatus = 'Pending' | 'Running' | 'Done' | 'Failed';

/** One row of `GET …/archive-unpacks`: a zip being (or having been) unpacked into a folder. */
export interface ArchiveUnpack {
  id: string;
  archiveName: string;
  status: ArchiveUnpackStatus;
  failureCode: string | null;
  filesCreated: number;
  bytesCreated: number;
  targetDirectory: string | null;
  skipped?: { counts?: Record<string, number>; samples?: { path: string; reason: string }[] } | null;
  createdAt: string;
  finishedAt: string | null;
}

/** `POST …/files/{fileId}/unpack`: unpack (or retry unpacking) a zip row. */
export function unpackArchive(projectId: string, fileId: string): Promise<ArchiveUnpack> {
  return apiFetch<ArchiveUnpack>(`/api/v1/projects/${projectId}/files/${fileId}/unpack`, { method: 'POST' });
}

/** The key of the bundled sample clip the first-film path adds (the API only accepts keys it ships). */
export const FIRST_FILM_SAMPLE_KEY = 'first-film';

/** Copies a bundled sample clip into the project; the answer has the same shape as an upload result. */
export function addSampleFile(projectId: string, sampleKey: string = FIRST_FILM_SAMPLE_KEY): Promise<ProjectFile> {
  return apiFetch<ProjectFile>(`/api/v1/projects/${projectId}/files/sample`, {
    method: 'POST',
    body: JSON.stringify({ sampleKey }),
  });
}
