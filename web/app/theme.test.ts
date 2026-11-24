import { describe, expect, it } from 'vitest';
import { EDITOR_BOTTOM_CHROME_PX, theme } from './theme';

describe('popover defaults', () => {
  it('uses the fixed strategy and leaves Mantine padding alone outside the editor', () => {
    const props = (theme.components?.Popover as { defaultProps: Record<string, unknown> }).defaultProps;
    expect(props.floatingStrategy).toBe('fixed');
    expect(props.middlewares).toBeUndefined();
  });

  it('reserves the bottom chrome strip for the editor only', () => {
    expect(EDITOR_BOTTOM_CHROME_PX).toBe(96);
  });
});
