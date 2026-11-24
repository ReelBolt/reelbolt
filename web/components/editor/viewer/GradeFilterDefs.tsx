import React from 'react';
import type { SvgGradeFilter } from '@/lib/editing/color/svg-grade';

/**
 * The Simple preview's grade as an SVG filter (see lib/editing/color/svg-grade.ts). It is mounted, not
 * `display: none` (browsers drop a filter whose SVG is not rendered), but takes no room. The `<video>`
 * wears it with `filter: url(#id)`.
 */
export function GradeFilterDefs({ filter }: { filter: SvgGradeFilter }) {
  const [r, g, b] = filter.tables;
  return (
    <svg width={0} height={0} aria-hidden focusable="false" style={{ position: 'absolute' }} data-testid="grade-filter-defs">
      <defs>
        <filter id={filter.id} colorInterpolationFilters="sRGB" x="0" y="0" width="100%" height="100%">
          <feComponentTransfer>
            <feFuncR type="table" tableValues={r.join(' ')} />
            <feFuncG type="table" tableValues={g.join(' ')} />
            <feFuncB type="table" tableValues={b.join(' ')} />
          </feComponentTransfer>
          <feColorMatrix type="matrix" values={filter.matrix.join(' ')} />
        </filter>
      </defs>
    </svg>
  );
}
