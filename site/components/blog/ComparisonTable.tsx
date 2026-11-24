import type { ReactNode } from 'react';

/** A plain string, or rich text (for a `[n]` source marker) with an optional note under it. */
export type ComparisonCell = string | { text: ReactNode; note?: string };

export interface ComparisonRow {
  label: string;
  cells: ComparisonCell[];
}

export interface ComparisonTableProps {
  /** Visible caption and the accessible name of the scrolling region. */
  caption: string;
  /** Column headings after the row-label column. */
  columns: string[];
  rows: ComparisonRow[];
}

const FOCUS = 'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong';

function cellParts(cell: ComparisonCell): { text: ReactNode; note?: string } {
  return typeof cell === 'string' ? { text: cell } : cell;
}

/**
 * A comparison grid. The caption sits above the grid, outside any scroller, so it never clips.
 *
 * Two-column tables (label + 2) have no minimum width. From `sm` up they are a table; below `sm`
 * each row is stacked as a definition list, so the substance is readable on a 360px phone with no
 * sideways scrolling.
 *
 * Wider tables keep their natural width and scroll inside a focusable region. The first column
 * stays put, a fade on the right edge says there is more, and from `lg` the table breaks out of
 * the prose measure to `max-w-5xl`, centred.
 */
export function ComparisonTable({ caption, columns, rows }: ComparisonTableProps) {
  const wide = columns.length > 2;

  const grid = (
    <table className={`w-full border-collapse text-left text-sm ${wide ? 'min-w-[40rem]' : ''}`}>
      <thead>
        <tr>
          <th scope="col" className="sticky left-0 z-10 border-b border-line-strong bg-paper px-3 py-2 font-semibold text-ink">
            <span className="sr-only">Item</span>
          </th>
          {columns.map((column) => (
            <th
              key={column}
              scope="col"
              className="border-b border-line-strong px-3 py-2 font-display font-bold uppercase tracking-[-0.01em] text-ink"
            >
              {column}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.label}>
            <th
              scope="row"
              className="sticky left-0 z-10 border-b border-line bg-paper px-3 py-2 align-top font-semibold text-ink"
            >
              {row.label}
            </th>
            {row.cells.map((cell, index) => {
              const { text, note } = cellParts(cell);
              return (
                <td key={`${row.label}-${index}`} className="border-b border-line px-3 py-2 align-top text-ink-muted">
                  {text}
                  {note && <span className="mt-1 block text-xs text-ink-faint">{note}</span>}
                </td>
              );
            })}
          </tr>
        ))}
      </tbody>
    </table>
  );

  if (!wide) {
    return (
      <div className="my-8">
        <p className="mb-3 text-left font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted">{caption}</p>
        <dl className="sm:hidden">
          {rows.map((row) => (
            <div key={row.label} className="border-b border-line py-3">
              <dt className="font-semibold text-ink">{row.label}</dt>
              {row.cells.map((cell, index) => {
                const { text, note } = cellParts(cell);
                return (
                  <dd key={`${row.label}-${index}`} className="mt-2 text-ink-muted">
                    <span className="block font-display text-xs font-bold uppercase tracking-[-0.01em] text-ink">
                      {columns[index]}
                    </span>
                    {text}
                    {note && <span className="mt-1 block text-xs text-ink-faint">{note}</span>}
                  </dd>
                );
              })}
            </div>
          ))}
        </dl>
        <div role="region" aria-label={caption} tabIndex={0} className={`hidden sm:block ${FOCUS}`}>
          {grid}
        </div>
      </div>
    );
  }

  return (
    <div className="my-8 lg:relative lg:left-1/2 lg:w-[min(64rem,calc(100vw-3rem))] lg:-translate-x-1/2">
      <p className="mb-3 px-4 text-left font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted sm:px-0">
        {caption}
      </p>
      <div className="relative -mx-4 sm:mx-0">
        <div role="region" aria-label={caption} tabIndex={0} className={`overflow-x-auto px-4 sm:px-0 ${FOCUS}`}>
          {grid}
        </div>
        <div
          aria-hidden="true"
          className="pointer-events-none absolute inset-y-0 right-0 w-10 bg-linear-to-l from-paper to-transparent lg:hidden"
        />
      </div>
    </div>
  );
}
