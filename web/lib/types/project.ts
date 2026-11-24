export interface Project {
  id: string;
  name: string;
  description: string;
  status: string;
  userId: string;
  createdAt: string;
  updatedAt: string;
  /** The folder the project is filed in; null/absent when unfiled. */
  folderId?: string | null;
  tags?: ProjectTagRef[];
  /** The project's choice to analyse each new video when it is uploaded. Off by default. */
  analyzeOnUpload?: boolean;
}

export interface ProjectTagRef {
  id: string;
  name: string;
  color: string;
}

export interface CreateProjectRequest {
  name: string;
  description: string;
  tagIds?: string[];
  folderId?: string | null;
  /**
   * The recipe the project starts with: a listed, creatable template key (the welcome flow passes
   * the one `PUT /api/v1/onboarding/profile` answered). With it the project gets that app and the
   * automatic code-promo starter is not created; an unknown key is a `400`. Omitted everywhere else.
   */
  starterTemplateKey?: string;
}

export interface UpdateProjectRequest {
  name?: string;
  description?: string;
  /** Left out, the stored choice is kept. */
  analyzeOnUpload?: boolean;
}

export interface ProjectFile {
  id: string;
  projectId: string;
  originalFileName: string;
  originalPath?: string; // may include folder segments when imported from directories
  directoryPath?: string;
  /** `github` for files synced from a repository. */
  sourceKind?: string | null;
  category: string; // userFiles | agentFiles | outputFiles
  mimeType: string;
  sizeBytes: number;
  storageKey: string;
  storageFileName?: string;
  agentSummary: string | null;
  // Mirrors the backend SummaryStatus enum (ReelBolt.Shared/Data/Models/Enums.cs) exactly —
  // 'Done', not 'Completed' (that value belongs to the separate StepStatus enum).
  summaryStatus: 'Pending' | 'Processing' | 'Done' | 'Failed';
  indexingStatus: 'NotIndexed' | 'Pending' | 'Processing' | 'Indexed' | 'Failed';
  indexedAt?: string;
  indexingError?: string | null;
  uploadedAt: string;
}

export interface ProjectFileContent {
  id: string;
  originalFileName: string;
  originalPath?: string;
  mimeType: string;
  content: string;
  uploadedAt: string;
}

export interface MoveProjectFilesRequest {
  fileIds: string[];
  targetDirectoryPath?: string;
  targetCategory?: 'userFiles' | 'agentFiles' | 'outputFiles';
}

export interface RenameFolderRequest {
  sourcePath: string;
  targetPath: string;
}

export interface DeleteFolderRequest {
  path: string;
  recursive?: boolean;
}

export interface ReindexProjectFilesRequest {
  maxFiles?: number;
  includeIndexed?: boolean;
}

export interface ReindexProjectFilesResponse {
  queuedFiles: number;
  eligibleFiles: number;
  hasMore: boolean;
}

export interface FileSearchHit {
  fileId: string;
  filePath: string;
  fileName: string;
  chunkIndex: number;
  totalChunks: number;
  language: string;
  content: string;
  score: number;
}

export interface FileSearchResponse {
  results: FileSearchHit[];
  /** True when embeddings are not configured or the index is not built yet. */
  indexNotReady: boolean;
}
