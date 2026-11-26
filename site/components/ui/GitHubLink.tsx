import { useT } from '@/i18n';
import { buttonClass, type ButtonSize } from './button-styles';
import { REPO_URL } from '@/lib/site-config';

interface GitHubLinkProps {
  /** Where the button sits, for the tests and for the styling of the surrounding row. */
  size?: ButtonSize;
  block?: boolean;
  className?: string;
  onClick?: () => void;
}

/**
 * The visible GitHub button: the mark and the word "GitHub", an outline button like the site's
 * other secondary buttons. It always leaves the site, so it is a plain <a> that opens in a new tab
 * and tells screen readers so. Words come from `layout.github` in both catalogs.
 */
export function GitHubLink({ size = 'md', block, className, onClick }: GitHubLinkProps) {
  const t = useT('layout.github');
  const classes = [buttonClass({ variant: 'outline', size, block }), 'gap-2', className ?? '']
    .filter(Boolean)
    .join(' ');
  return (
    <a
      href={REPO_URL}
      target="_blank"
      rel="noopener"
      aria-label={t('aria')}
      className={classes}
      onClick={onClick}
    >
      <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true" focusable="false">
        <path d="M12 .5a11.5 11.5 0 0 0-3.64 22.41c.58.1.79-.25.79-.56v-2c-3.2.7-3.88-1.37-3.88-1.37-.52-1.33-1.28-1.69-1.28-1.69-1.04-.71.08-.7.08-.7 1.15.08 1.76 1.19 1.76 1.19 1.03 1.76 2.7 1.25 3.36.96.1-.75.4-1.25.73-1.54-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.29 1.19-3.1-.12-.29-.52-1.47.11-3.06 0 0 .97-.31 3.17 1.18a11 11 0 0 1 5.77 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.77.11 3.06.74.81 1.19 1.84 1.19 3.1 0 4.42-2.69 5.39-5.25 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56A11.5 11.5 0 0 0 12 .5z" />
      </svg>
      <span>{t('label')}</span>
    </a>
  );
}
