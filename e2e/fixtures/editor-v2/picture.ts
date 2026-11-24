import { expect, type Locator, type Page } from '@playwright/test';
import type { EditorApi } from './api';

/**
 * Helpers for the picture editor rows (13-picture-editor, remediation-plan.md W0.8). A picture is an
 * image document: created through the REST API from a project photo, opened at
 * `/app/editor/thumbnails/{id}?project={projectId}`. No helper here runs an AI tool (D9: no paid calls).
 */

export type PictureLayer = {
  id: string;
  kind: string;
  label: string;
  zOrder: number;
  visible?: boolean;
  imageUrl: string;
  matteUrl: string | null;
};

export type Picture = { id: string; canvasWidth: number; canvasHeight: number; layers: PictureLayer[] };

export const picturesUrl = (projectId: string, id?: string): string =>
  `/api/v1/projects/${projectId}/image-documents${id ? `/${id}` : ''}`;

/** Creates a picture from a project photo (a fresh document every call). */
export async function createPicture(api: EditorApi, projectId: string, fileId: string, name: string): Promise<Picture> {
  const created = await api.json<{ id: string }>('POST', picturesUrl(projectId), { sourceProjectFileId: fileId, name }, [200, 201]);
  return getPicture(api, projectId, created.id);
}

export function getPicture(api: EditorApi, projectId: string, id: string): Promise<Picture> {
  return api.json<Picture>('GET', picturesUrl(projectId, id));
}

/** Opens a picture in the editor and waits for the stage and the save badge. Returns the stage. */
export async function openPicture(page: Page, projectId: string, id: string): Promise<Locator> {
  await page.goto(`/app/editor/thumbnails/${id}?project=${projectId}`);
  const stage = page.locator('[role=application][aria-label="Thumbnail canvas"]');
  await expect(stage, 'the picture opens').toBeVisible({ timeout: 45_000 });
  await expect(page.getByRole('status').filter({ hasText: /changes saved|unsaved|saving/i }).first()).toBeVisible({ timeout: 15_000 });
  return stage;
}

/** Opens the AI tab's tool picker and picks a tool by its title (e.g. "Cut out a subject"). */
export async function pickAiTool(page: Page, title: string): Promise<void> {
  await page.getByRole('tab', { name: 'AI' }).click();
  const change = page.getByRole('button', { name: 'Change tool' });
  if (await change.isVisible().catch(() => false)) await change.click();
  else await page.getByRole('button', { name: /Choose an AI tool/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Choose an AI tool' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: title, exact: true }).click();
  await expect(dialog).toBeHidden();
}

export type Box = { x: number; y: number; width: number; height: number };

export async function box(locator: Locator, what: string): Promise<Box> {
  const b = await locator.boundingBox();
  expect(b, `${what} is laid out`).toBeTruthy();
  return b!;
}

export function overlaps(a: Box, b: Box): boolean {
  return a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
}

export function inside(inner: Box, outer: Box, slack = 0.5): boolean {
  return (
    inner.x >= outer.x - slack &&
    inner.y >= outer.y - slack &&
    inner.x + inner.width <= outer.x + outer.width + slack &&
    inner.y + inner.height <= outer.y + outer.height + slack
  );
}
