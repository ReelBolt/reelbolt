import { test, expect } from '../fixtures/auth';

test.describe('smoke', () => {
  test('user can log in and reach dashboard', async ({ authenticatedPage }) => {
    // Navigate to the dashboard
    await authenticatedPage.goto('/app/dashboard');
    await authenticatedPage.waitForLoadState('networkidle');

    // Assert we're on the dashboard
    expect(authenticatedPage.url()).toContain('/app/dashboard');
  });
});
