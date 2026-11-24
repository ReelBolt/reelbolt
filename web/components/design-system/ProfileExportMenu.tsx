'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import { Button, Menu, Text } from '@mantine/core';
import {
  DESIGN_EXPORT_FORMAT_LABELS,
  DESIGN_EXPORT_FORMATS,
  designExportFileName,
  downloadBlob,
  exportDesignDocument,
  type DesignExportFormat,
} from '@/lib/api/design-system';
import type { DesignSystemKind } from '@/lib/types/design-system';
import { useT } from '@/i18n';

/**
 * SPEC §16.2: the profile export menu. The design member offers every exporter the route serves
 * (DTCG, CSS, Tailwind v4, DESIGN.md, JSON); the product map offers JSON only. Each choice fetches
 * the effective document as a Blob and saves it under the name the server uses.
 */
export function ProfileExportMenu({
  projectId,
  kind,
}: {
  projectId: string;
  kind: DesignSystemKind;
}) {
  const tr = useT('brandLibraries.profile.export');
  const tAll = useT();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const formats: DesignExportFormat[] = kind === 'product_map' ? ['json'] : [...DESIGN_EXPORT_FORMATS];

  async function download(format: DesignExportFormat) {
    setBusy(true);
    setError(null);
    try {
      const blob = await exportDesignDocument(projectId, kind, format);
      downloadBlob(blob, designExportFileName(kind, format));
    } catch (cause) {
      setError(describeError(tAll, cause, tr('failed')));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Menu withinPortal position="bottom-end">
        <Menu.Target>
          <Button
            size="xs"
            variant="default"
            loading={busy}
            aria-label={kind === 'design' ? tr('brandLabel') : tr('productLabel')}
            data-testid="profile-export-menu"
          >
            {tr('button')}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          {formats.map((format) => (
            <Menu.Item
              key={format}
              data-testid={`export-${format}`}
              onClick={() => void download(format)}
            >
              {DESIGN_EXPORT_FORMAT_LABELS[format]}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
      {error && (
        <Text size="xs" c="red" role="alert">
          {error}
        </Text>
      )}
    </>
  );
}
