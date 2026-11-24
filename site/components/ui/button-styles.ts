export type ButtonVariant = 'accent' | 'outline' | 'onInk' | 'dark' | 'ghost';
export type ButtonSize = 'md' | 'lg';

interface ButtonClassOptions {
  variant: ButtonVariant;
  size?: ButtonSize;
  block?: boolean;
  skew?: boolean;
}

const BASE =
  'inline-flex items-center justify-center min-h-11 font-display font-bold rounded-[2px] transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong';

const SIZE_CLASS: Record<ButtonSize, string> = {
  md: 'px-5 py-2.5 text-sm',
  lg: 'px-7 py-3.5 text-base',
};

const VARIANT_CLASS: Record<ButtonVariant, string> = {
  accent: 'bg-accent text-paper hover:bg-accent-hover',
  dark: 'bg-ink text-paper hover:bg-ink-2',
  outline: 'border border-line-strong text-ink hover:bg-paper-2',
  // For a secondary button on a `bg-ink` band: `text-ink` would vanish into the band, so it takes
  // the band's inverse, `paper`.
  onInk: 'border border-paper text-paper hover:bg-paper/10',
  ghost: 'text-ink-muted hover:text-ink',
};

export function buttonClass({ variant, size = 'md', block, skew }: ButtonClassOptions): string {
  return [
    BASE,
    SIZE_CLASS[size],
    VARIANT_CLASS[variant],
    block ? 'w-full' : '',
    skew ? '-skew-x-[5deg]' : '',
  ]
    .filter(Boolean)
    .join(' ');
}
