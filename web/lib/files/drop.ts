/** A dropped/selected file with the path it should land at relative to the project root. */
export interface PendingUpload {
  file: File;
  relativePath: string;
}

interface FsEntry {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
  fullPath: string;
  file?: (ok: (f: File) => void, err: (e: unknown) => void) => void;
  createReader?: () => { readEntries: (ok: (e: FsEntry[]) => void, err: (e: unknown) => void) => void };
}

async function readAll(entry: FsEntry): Promise<FsEntry[]> {
  const reader = entry.createReader!();
  const all: FsEntry[] = [];
  // readEntries returns at most ~100 items per call and signals the end with an empty batch.
  for (;;) {
    const batch = await new Promise<FsEntry[]>((ok, err) => reader.readEntries(ok, err));
    if (batch.length === 0) return all;
    all.push(...batch);
  }
}

async function walk(entry: FsEntry, out: PendingUpload[]): Promise<void> {
  if (entry.isFile && entry.file) {
    const file = await new Promise<File>((ok, err) => entry.file!(ok, err));
    out.push({ file, relativePath: entry.fullPath.replace(/^\/+/, '') });
  } else if (entry.isDirectory) {
    for (const child of await readAll(entry)) await walk(child, out);
  }
}

/** Files (and the contents of dropped folders) from a drop event, keeping folder structure. */
export async function collectDroppedFiles(dt: DataTransfer): Promise<PendingUpload[]> {
  const out: PendingUpload[] = [];
  const items = Array.from(dt.items ?? []);
  const entries: FsEntry[] = [];
  for (const i of items) {
    const entry = (i as unknown as { webkitGetAsEntry?: () => FsEntry | null }).webkitGetAsEntry?.();
    if (entry) entries.push(entry);
  }

  if (entries.length > 0) {
    for (const e of entries) await walk(e, out);
    return out;
  }
  return Array.from(dt.files ?? []).map((file) => ({ file, relativePath: file.name }));
}

export function fromFileList(list: FileList | null): PendingUpload[] {
  return Array.from(list ?? []).map((file) => ({
    file,
    relativePath: (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name,
  }));
}

export function hasFiles(e: DragEvent): boolean {
  return Array.from(e.dataTransfer?.types ?? []).includes('Files');
}
