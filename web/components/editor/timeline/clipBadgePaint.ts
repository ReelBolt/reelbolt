import { BADGE_GLYPHS, layoutBadges, type BadgeRect, type ClipBadgePaint } from '@/lib/editing/display/clip-badges';
import type { PaintContext, PaintTheme } from './paint';

/** The rects of a clip's badge row (clip-local geometry shared with the hit test). */
export function badgeRectsFor(badges: readonly ClipBadgePaint[] | undefined, x0: number, w: number, bodyY: number, bodyH: number): BadgeRect[] {
  return badges && badges.length > 0 ? layoutBadges(badges.map((b) => b.kind), x0, w, bodyY, bodyH) : [];
}

/**
 * Paints the badge row: a dark chip with a vector glyph (hollow and dim when the adjustment is switched off) and,
 * when the preview is not faithful for that feature, a corner dot (amber approximate, red render-required).
 */
export function paintClipBadges(ctx: PaintContext, rects: readonly BadgeRect[], badges: readonly ClipBadgePaint[], theme: PaintTheme): void {
  for (const r of rects) {
    ctx.save();
    ctx.fillStyle = theme.chip;
    ctx.globalAlpha = 0.85;
    ctx.fillRect(r.x, r.y, r.w, r.h);
    ctx.globalAlpha = 1;
    ctx.strokeStyle = theme.itemText;
    ctx.fillStyle = theme.itemText;
    ctx.lineWidth = 1;
    if (r.kind === 'more') {
      ctx.font = '12px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(`+${r.count ?? 0}`, r.x + r.w / 2, r.y + 1);
      ctx.restore();
      continue;
    }
    const badge = badges.find((b) => b.kind === r.kind);
    if (badge && !badge.enabled) {
      ctx.globalAlpha = 0.55;
      ctx.strokeRect(r.x + 0.5, r.y + 0.5, r.w - 1, r.h - 1);
    }
    // A 10 x 10 glyph centred in the chip.
    const gx = r.x + (r.w - 10) / 2;
    const gy = r.y + (r.h - 10) / 2;
    ctx.beginPath();
    for (const line of BADGE_GLYPHS[r.kind]) {
      for (let i = 0; i + 1 < line.length; i += 2) {
        const px = gx + line[i]!;
        const py = gy + line[i + 1]!;
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      }
    }
    ctx.stroke();
    ctx.globalAlpha = 1;
    if (badge && badge.parity !== 'faithful') {
      ctx.fillStyle = badge.parity === 'renderRequired' ? theme.renderRequired : theme.approximate;
      ctx.fillRect(r.x + r.w - 5, r.y + 1, 4, 4);
    }
    ctx.restore();
  }
}
