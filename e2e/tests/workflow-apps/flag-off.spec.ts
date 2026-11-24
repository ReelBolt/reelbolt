import { expect, test } from '@playwright/test';
import { PROJECT_ID, mockApi, signIn } from './fixtures';

// Runs with WA_FLAG=off (WORKFLOW_APPS_ENABLED=false): today's Workflows tab, unchanged.
test('flag off: the tab is "Workflows" and renders the plain workflow list', async ({ page, baseURL }) => {
  await signIn(page, baseURL!);
  await mockApi(page);
  await page.goto(`/app/projects/${PROJECT_ID}?tab=workflows`);
  await expect(page.getByRole('tab', { name: /^Workflows/ })).toBeVisible();
  await expect(page.getByRole('tab', { name: /^Apps/ })).toHaveCount(0);
  // Selectors from ProjectWorkflowsTab: the count line, the New workflow button, one row per workflow.
  await expect(page.getByText('2 workflows')).toBeVisible();
  await expect(page.getByRole('link', { name: 'New workflow' })).toBeVisible();
  await expect(page.getByRole('link', { name: /Launch teaser/ })).toHaveAttribute('href', new RegExp(`/projects/${PROJECT_ID}/workflows/wf-a$`));
  await expect(page.getByRole('link', { name: /Footage recap/ })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Your apps' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'All apps' })).toHaveCount(0);
});
