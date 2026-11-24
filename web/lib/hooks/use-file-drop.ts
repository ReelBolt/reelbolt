'use client';

import { useEffect, useRef, useState } from 'react';
import { collectDroppedFiles, hasFiles, type PendingUpload } from '../files/drop';

/**
 * Makes the whole window a drop target for files from the OS. Only drags that carry files count,
 * so dragging a text selection or one of the app's own file rows never lights it up.
 */
export function useWindowFileDrop(onFiles: (files: PendingUpload[]) => void, enabled = true) {
  const [dragging, setDragging] = useState(false);
  const depth = useRef(0);
  const cb = useRef(onFiles);
  useEffect(() => {
    cb.current = onFiles;
  });

  useEffect(() => {
    if (!enabled) return;
    const enter = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      depth.current += 1;
      setDragging(true);
    };
    const over = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      e.preventDefault(); // required, or the browser navigates to the dropped file
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
    };
    const leave = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      depth.current = Math.max(0, depth.current - 1);
      if (depth.current === 0) setDragging(false);
    };
    const drop = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      e.preventDefault();
      depth.current = 0;
      setDragging(false);
      if (!e.dataTransfer) return;
      void collectDroppedFiles(e.dataTransfer).then((files) => {
        if (files.length > 0) cb.current(files);
      });
    };
    document.addEventListener('dragenter', enter);
    document.addEventListener('dragover', over);
    document.addEventListener('dragleave', leave);
    document.addEventListener('drop', drop);
    return () => {
      document.removeEventListener('dragenter', enter);
      document.removeEventListener('dragover', over);
      document.removeEventListener('dragleave', leave);
      document.removeEventListener('drop', drop);
    };
  }, [enabled]);

  return dragging;
}
