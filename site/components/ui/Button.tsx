import { Link } from '@/i18n/link';
import type { ReactNode } from 'react';
import { buttonClass, type ButtonSize, type ButtonVariant } from './button-styles';

interface ButtonProps {
  href: string;
  variant: ButtonVariant;
  size?: ButtonSize;
  block?: boolean;
  skew?: boolean;
  className?: string;
  /**
   * Optional click handler, forwarded to whichever element is rendered. Only a client call site
   * can pass one (a server component cannot); `PrimaryCta` uses it to fire the CTA's GA event.
   */
  onClick?: () => void;
  children: ReactNode;
}

export function Button({ href, variant, size, block, skew, className, onClick, children }: ButtonProps) {
  // `buttonClass` already drops its own falsy parts; the array join reproduces the previous
  // template-literal spacing exactly, including the trailing space when `className` is absent.
  const classes = [buttonClass({ variant, size, block, skew }), className ?? ''].join(' ');
  const content = skew ? <span className="inline-block skew-x-[5deg]">{children}</span> : children;

  // /app/* is a different Next.js app (the dashboard) reached only via nginx in production;
  // next/link would try a client-side navigation within this app's own router and 404.
  // http(s):// and mailto: are also always real page loads, never client-side routes.
  if (href.startsWith('/app') || href.startsWith('http') || href.startsWith('mailto:')) {
    return (
      <a href={href} className={classes} onClick={onClick}>
        {content}
      </a>
    );
  }

  return (
    <Link href={href} className={classes} onClick={onClick}>
      {content}
    </Link>
  );
}
