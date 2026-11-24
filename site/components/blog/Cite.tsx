import { Fragment } from 'react';

/**
 * Source markers for one claim, e.g. `<Cite n={[1, 2]} />` renders a superscript `1,2` whose parts
 * link to the matching `#source-n` entries in SourceList. Consecutive markers share one `<sup>` so
 * they read as a list, not as the number 12. Styling lives in Prose.
 */
export function Cite({ n }: { n: readonly number[] }) {
  return (
    <sup>
      {n.map((index, position) => (
        <Fragment key={index}>
          {position > 0 && ','}
          <a href={`#source-${index}`}>{index}</a>
        </Fragment>
      ))}
    </sup>
  );
}
