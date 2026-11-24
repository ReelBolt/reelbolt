// The ReelBolt app-icon mark (brand/app-icon.svg) as an inline SVG, so the dashboard header and
// the auth screens draw it from the design system instead of loading a raster or an <img>.
//
// Geometry must match brand/app-icon.svg exactly. The colour does NOT: the master's hexes
// (#2E1065 square / #C4B5FD R / #A855F7 bolt) are duplicated here as Mantine's violet ramp, so the
// mark follows app/theme.ts's `primaryColor: 'violet'` and stays legible in both colour schemes.
// See docs/brand.md, "Files that intentionally differ" — this is an inline copy, not a synced one.
//
// The ramp steps are chosen for contrast against each other, which is what keeps the R and the bolt
// readable as two shapes at header size:
//   light: square violet-1 / R violet-9    ~5.4:1
//   light: square violet-1 / bolt violet-6 ~3.7:1
//
// The colours follow the colour scheme and live in BrandMark.module.css, not here — the square
// used to be a single fixed violet-1, which is a *near-white* step, so on a dark header the mark
// rendered as a large pale block. See that file for what each scheme draws and why the dark one
// returns to the master's own colours.
import classes from './BrandMark.module.css';

export function BrandMark({ size = 28 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 120 120"
      fill="none"
      aria-hidden="true"
      focusable="false"
      style={{ flexShrink: 0 }}
    >
      <rect width="120" height="120" rx="28" className={classes.square} />
      <path
        d="M36 96V24H62a21 21 0 0 1 0 42H36"
        fill="none"
        className={classes.mark}
        strokeWidth="14"
        strokeLinejoin="round"
      />
      <path
        d="M68 12 L28 68 H54 L46 108 L92 48 H64 Z"
        className={classes.bolt}
        strokeWidth="5"
        strokeLinejoin="round"
        paintOrder="stroke"
        transform="translate(66 80) scale(-.68 .68) translate(-60 -60)"
      />
    </svg>
  );
}
