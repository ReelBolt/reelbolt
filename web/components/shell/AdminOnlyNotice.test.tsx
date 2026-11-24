// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';

const replace = vi.fn();
let search = '';
vi.mock('next/navigation', () => ({
  useRouter: () => ({ replace }),
  usePathname: () => '/dashboard',
  useSearchParams: () => new URLSearchParams(search),
}));
const show = vi.fn();
vi.mock('@mantine/notifications', () => ({ notifications: { show: (o: unknown) => show(o) } }));

import { AdminOnlyNotice } from './AdminOnlyNotice';

describe('AdminOnlyNotice', () => {
  beforeEach(() => {
    show.mockReset();
    replace.mockReset();
  });
  afterEach(cleanup);

  it('shows the notice once and strips the flag', () => {
    search = 'notice=admin-only&x=1';
    render(<AdminOnlyNotice />);
    expect(show).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(show.mock.calls[0][0])).toContain('platform admins');
    expect(replace).toHaveBeenCalledWith('/dashboard?x=1');
  });

  it('does nothing without the flag', () => {
    search = '';
    render(<AdminOnlyNotice />);
    expect(show).not.toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
  });
});
