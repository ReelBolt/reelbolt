'use client';

import { Suspense } from 'react';
import { EditorHub } from '@/components/editor/hub/EditorHub';

/**
 * `/app/editor`: the editor hub — videos and pictures for one project. The project and
 * the view ride in the query (`?project=…&view=pictures`), which is why the hub sits under Suspense.
 */
export default function EditorHubPage() {
  return (
    <Suspense fallback={null}>
      <EditorHub />
    </Suspense>
  );
}
