const MIB = 1024 * 1024;

/** True for a name that ends in `.zip` (any case). The same test the unpack service applies to the file name. */
export function isZipName(name: string): boolean {
  return /\.zip$/i.test(name);
}

/** The MIME types the unpack service also treats as a zip (ArchiveUnpackPaths.ZipMimeTypes), compared lower-case. */
const ZIP_MIME_TYPES = new Set(['application/zip', 'application/x-zip-compressed', 'application/x-zip']);

/** True for a file the unpack service reads as a zip: a `.zip` name (any case) or a zip MIME type. */
export function isZipFile(file: { name: string; type?: string }): boolean {
  return isZipName(file.name) || (!!file.type && ZIP_MIME_TYPES.has(file.type.toLowerCase()));
}

/**
 * The zips among `files` that are bigger than the unpack limit. `maxMb` is what `GET /api/v1/storage/usage`
 * serves (`zipUnpackLimits.maxMb`); null or missing (an older server, or not loaded yet) means no warning.
 * The limit is on the size of the zip as uploaded, not on what is inside it.
 */
export function zipsOverLimit<T extends { name: string; size: number; type?: string }>(files: readonly T[], maxMb: number | null | undefined): T[] {
  if (!maxMb || maxMb <= 0) return [];
  return files.filter((f) => isZipFile(f) && f.size > maxMb * MIB);
}
