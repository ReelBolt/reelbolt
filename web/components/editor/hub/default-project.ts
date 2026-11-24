import type { Project } from '@/lib/types/project';

/**
 * The project the hub opens on when the URL names none: the remembered one if it still exists,
 * otherwise the most recently updated (the one the user last worked in), never just the newest
 * created, which is often an empty test project.
 */
export function pickDefaultProject(projects: readonly Project[] | undefined, remembered: string | null): string | null {
  if (!projects?.length) return null;
  if (remembered && projects.some((p) => p.id === remembered)) return remembered;
  let best = projects[0];
  for (const p of projects) {
    if (Date.parse(p.updatedAt) > Date.parse(best.updatedAt)) best = p;
  }
  return best.id;
}
