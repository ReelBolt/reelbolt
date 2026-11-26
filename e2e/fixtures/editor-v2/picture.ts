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

/**
 * Opens one of the picture editor's tools: a tab on a desktop or tablet, the dock button that opens the bottom
 * sheet on a phone (the phone has no tab strip: `picture-dock-<name>` buttons with aria-pressed).
 */
export const isNarrow = (page: Page): boolean => (page.viewportSize()?.width ?? 1280) < 600;

export async function openPanel(page: Page, name: 'Layers' | 'Text' | 'AI' | 'Canvas'): Promise<void> {
  const dock = page.getByTestId(`picture-dock-${name.toLowerCase()}`);
  // The dock is drawn a beat after the stage on a phone: wait for it instead of falling back to a tab that never exists there.
  if (isNarrow(page)) await dock.waitFor({ state: 'visible', timeout: 15_000 });
  // A dialog left open (the AI tool picker after "Change tool") covers the dock on a phone: close it first.
  if (isNarrow(page) && (await page.locator('.mantine-Modal-content').first().isVisible().catch(() => false))) {
    await page.keyboard.press('Escape');
    await expect(page.locator('.mantine-Modal-content')).toHaveCount(0);
  }
  if (await dock.isVisible().catch(() => false)) {
    if ((await dock.getAttribute('aria-pressed')) !== 'true') await dock.tap();
    await expect(dock).toHaveAttribute('aria-pressed', 'true');
    return;
  }
  await page.getByRole('tab', { name, exact: true }).click();
}

/** Opens the AI tab's tool picker and picks a tool by its title (e.g. "Cut out a subject"). */
export async function pickAiTool(page: Page, title: string): Promise<void> {
  await openPanel(page, 'AI');
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

/** The picture tool button in the Tools toolbar (the phone's dock also has buttons named Text and AI). */
export const toolButton = (page: Page, name: string) => page.getByRole('toolbar', { name: 'Tools' }).getByRole('button', { name, exact: true });

/** Expects one tool panel to be the open one: the selected tab, or the pressed dock button on a phone. */
export async function expectPanelOn(page: Page, name: 'Layers' | 'Text' | 'AI' | 'Canvas'): Promise<void> {
  if (isNarrow(page)) await expect(page.getByTestId(`picture-dock-${name.toLowerCase()}`)).toHaveAttribute('aria-pressed', 'true');
  else await expect(page.getByRole('tab', { name, exact: true })).toHaveAttribute('aria-selected', 'true');
}

/**
 * Where a tool's Run button and the sentences about it are: the tool's own panel on a desktop, the sheet's pinned footer on
 * a phone (PinnedRun portals them out of the panel so Run stays on screen next to the brush and the prompt).
 */
export const runArea = (page: Page, panel: Locator): Locator => (isNarrow(page) ? page.getByTestId('picture-sheet') : panel);

/** Phone: the Text sheet keeps weight, wrapping and the other detail fields behind its "More" button; opens it (a no-op elsewhere). */
export async function textMore(page: Page): Promise<void> {
  if (!isNarrow(page)) return;
  const more = page.getByRole('button', { name: 'More', exact: true });
  await more.waitFor({ state: 'visible', timeout: 10_000 });
  if ((await more.getAttribute('aria-expanded')) !== 'true') await more.tap();
}

/**
 * One layer action: the row's Move and Delete buttons on a desktop or tablet, the row's "..." menu on a phone
 * (menu items `layer-menu-forward|back|delete-<id>`). `open` only opens the menu, for checks on a disabled item.
 */
export async function layerAction(page: Page, layer: { id: string; label: string }, action: 'forward' | 'back' | 'delete', open = false): Promise<Locator> {
  if (isNarrow(page)) {
    await page.getByTestId(`layer-menu-${layer.id}`).tap();
    const item = page.getByTestId(action === 'delete' ? `layer-menu-delete-${layer.id}` : `layer-menu-${action}-${layer.id}`);
    await expect(item).toBeVisible();
    if (!open) await item.tap();
    return item;
  }
  const button = page.getByRole('button', { name: action === 'delete' ? `Delete ${layer.label}` : `Move ${layer.label} ${action}` });
  if (!open) await button.click();
  return button;
}
