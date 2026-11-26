import { expect, test, type Page } from '@playwright/test';

/**
 * T-013 signup polish (A01 A03 A08). Route-stubbed: no account is created and no email is sent.
 */

async function stubSignup(page: Page, status: number, headers: Record<string, string> = {}) {
  await page.route('**/api/v1/auth/signup', (route) =>
    route.fulfill({
      status,
      headers: { 'content-type': 'application/json', ...headers },
      body: JSON.stringify(
        status === 202
          ? { message: 'If this address can be used, an email is on its way.' }
          : { message: 'too many sign-ups from this network; try again later' },
      ),
    }),
  );
  await page.route('**/api/v1/auth/resend-verification', (route) =>
    route.fulfill({ status: 202, contentType: 'application/json', body: '{"message":"ok"}' }),
  );
}

async function fillAndSubmit(page: Page) {
  await page.goto('/signup');
  await page.getByLabel('Email').fill('ada@example.com');
  await page.getByLabel('Password', { exact: true }).fill('correct horse battery');
  await page.getByRole('checkbox', { name: /accept the/i }).check();
  await page.getByRole('button', { name: 'Create account' }).click();
}

test('A03: signup success shows a Check your email panel with resend, change address and sign in', async ({ page }) => {
  await stubSignup(page, 202);
  await fillAndSubmit(page);
  const panel = page.getByTestId('check-email-panel');
  await expect(panel).toContainText('Check your email');
  await expect(panel).toContainText('ada@example.com');
  await expect(panel).toContainText(/spam/i);
  await expect(panel.getByRole('link', { name: 'Sign in' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create account' })).toHaveCount(0);
  await panel.getByRole('button', { name: 'Resend email' }).click();
  await expect(panel.getByRole('button', { name: 'Resend email' })).toBeDisabled();
  await panel.getByRole('button', { name: 'Change address' }).click();
  await expect(page.getByLabel('Email')).toHaveValue('ada@example.com');
});

test('A01: a 429 shows friendly copy with the retry time and a support contact', async ({ page }) => {
  await stubSignup(page, 429, { 'retry-after': '3600' });
  await fillAndSubmit(page);
  await expect(page.getByText(/try again in about 1 hour/)).toBeVisible();
  await expect(page.getByText(/support@reelbolt\.ai/)).toBeVisible();
});

test('A08: the alpha banner follows the page content and does not push the form down', async ({ page }) => {
  await page.addInitScript(() => window.localStorage.clear());
  await page.goto('/signup');
  const form = page.locator('form').first();
  const banner = page.getByTestId('alpha-banner');
  await expect(form).toBeVisible();
  await expect(banner).toBeVisible();
  const [formBox, bannerBox] = [await form.boundingBox(), await banner.boundingBox()];
  expect(formBox!.y).toBeLessThan(bannerBox!.y);
});

test('A03: the verify link shows an email confirmed toast', async ({ page }) => {
  await page.route('**/api/v1/auth/verify-email', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{"message":"ok"}' }),
  );
  await page.goto('/verify-email?token=stub-token');
  await expect(page.getByText('Email confirmed').first()).toBeVisible();
});

test('A17: the billing page offers no marketing opt-in checkbox', async ({ page }) => {
  await page.goto('/settings/billing');
  await expect(page.getByTestId('marketing-optin-checkbox')).toHaveCount(0);
});

// A confirmation link opened after its day (or a second time) used to be a dead end: the only way
// on was to sign in with a password typed once. The page now sends a new link from where the
// person is. Route-stubbed: the token is refused and no email is sent.
test('an expired confirmation link offers a new one on the same page', async ({ page }) => {
  await page.route('**/api/v1/auth/verify-email', (route) =>
    route.fulfill({
      status: 400,
      contentType: 'application/json',
      body: JSON.stringify({ message: 'invalid or expired token' }),
    }),
  );
  let resent: unknown = null;
  await page.route('**/api/v1/auth/resend-verification', async (route) => {
    resent = route.request().postDataJSON();
    await route.fulfill({ status: 202, contentType: 'application/json', body: '{"message":"ok"}' });
  });

  await page.goto('/verify-email?token=expired-token');
  await expect(page.getByText(/This verification link cannot be used/)).toBeVisible();
  const resend = page.getByTestId('verify-resend');
  await expect(resend).toContainText('Need a new link?');
  await resend.getByLabel('Email').fill('ada@example.com');
  await resend.getByRole('button', { name: 'Send me a new link' }).click();
  // The same sentence whether or not anything was sent: no account-existence signal.
  await expect(resend.getByText(/If that address is waiting to be confirmed, a new link is on its way/)).toBeVisible();
  expect(resent).toEqual({ email: 'ada@example.com' });
  await expect(page.getByRole('link', { name: 'Go to sign in' })).toBeVisible();
});
