/** Mirrors the Inference API `ProjectPronunciationController` contract (/api/v1/projects/{id}/pronunciation). */
export interface PronunciationEntry {
  id: string;
  term: string;
  spelling: string;
  caseSensitive: boolean;
  updatedAt: string;
}

export interface PronunciationList {
  entries: PronunciationEntry[];
  max: number;
}

export interface PronunciationInput {
  term: string;
  spelling: string;
  caseSensitive: boolean;
}

export const DEFAULT_GLOSSARY_MAX = 200;
