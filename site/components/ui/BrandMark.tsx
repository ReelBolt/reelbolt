// The ReelBolt app-icon mark (brand/app-icon.svg) as an inline SVG, coloured with palette tokens so
// a palette change stays a one-file edit. Geometry must match brand/app-icon.svg.
export function BrandMark({ size = 32, className = '' }: { size?: number; className?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 120 120"
      fill="none"
      aria-hidden="true"
      focusable="false"
      className={`shrink-0 ${className}`.trim()}
    >
      <rect width="120" height="120" rx="28" className="fill-accent-deep" />
      <path
        d="M36 96V24H62a21 21 0 0 1 0 42H36"
        className="stroke-accent-ink"
        strokeWidth="14"
        strokeLinejoin="round"
      />
      <path
        d="M68 12 L28 68 H54 L46 108 L92 48 H64 Z"
        className="fill-accent stroke-accent-deep"
        strokeWidth="5"
        strokeLinejoin="round"
        paintOrder="stroke"
        transform="translate(66 80) scale(-.68 .68) translate(-60 -60)"
      />
    </svg>
  );
}
