'use client';

import { Suspense, use } from 'react';
import { useSearchParams } from 'next/navigation';
import Link from 'next/link';
import { Alert, Anchor, Button, Center, Container, Group, Loader, Title } from '@mantine/core';
import { IconAlertCircle, IconChevronLeft } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { AssistantPageContext } from '@/components/assistant/AssistantPageContext';
import { ThumbnailEditor } from '@/components/editor/thumbnail/ThumbnailEditor';
import { EarlyPreviewLabel } from '@/components/editor/shell/EarlyPreviewLabel';
import { editorHubHref } from '@/lib/editing/open-timeline';
import { useImageDocuments } from '@/lib/hooks/use-image-documents';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { useT } from '@/i18n';

/**
 * `/app/editor/thumbnails/{documentId}?project={projectId}` — the layered thumbnail editor.
 *
 * The project is a query parameter because the document routes are project-scoped
 * (`/api/v1/projects/{projectId}/image-documents/{documentId}`) and a document id alone does not
 * name its project. Both ids are ownership-checked by the server, so a wrong or foreign pair is a
 * 404 shown as "could not be opened", never someone else's picture.
 */
function ThumbnailEditorRoute({ documentId }: { documentId: string }) {
  const t = useT('editor.pages.editorThumbnailsPage');
  const projectId = useSearchParams().get('project');
  const { data: pictures } = useImageDocuments(projectId);
  const pictureName = pictures?.find((d) => d.id === documentId)?.name?.trim() || null;
  const title = pictureName ?? t('pictureEditor');
  const phone = useDeviceClass() === 'phone';

  return (
    <Container fluid px="md" py={phone ? 'xs' : 'lg'}>
      {phone ? (
        // Phone: breadcrumbs, title and the label fold into one row so the canvas and the tool
        // dock sit above the fold.
        <Group gap="xs" wrap="nowrap" mb="xs" data-testid="picture-header-compact">
          <Anchor
            component={Link}
            href={editorHubHref(projectId, 'pictures')}
            aria-label={t('backToPictures')}
            style={{ display: 'inline-flex', alignItems: 'center', minHeight: 44, flexShrink: 0 }}
            size="sm"
          >
            <IconChevronLeft size={16} aria-hidden />
            {t('pictures')}
          </Anchor>
          <Title order={1} fz="sm" fw={600} style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {title}
          </Title>
          <EarlyPreviewLabel />
        </Group>
      ) : (
      <PageHeader
        title={title}
        breadcrumbs={[
          { label: t('editor'), href: editorHubHref(projectId) },
          { label: t('pictures2'), href: editorHubHref(projectId, 'pictures') },
          { label: title },
        ]}
      >
        <EarlyPreviewLabel />
      </PageHeader>
      )}
      {projectId ? (
        <>
          {/* The assistant's picture tools act on what this page tells them is open, so the page
              that knows the pair states it: the document from the path, the project from the query
              it already read through the router. */}
          <AssistantPageContext projectId={projectId} imageDocumentId={documentId} />
          <ThumbnailEditor projectId={projectId} documentId={documentId} />
        </>
      ) : (
        <Alert icon={<IconAlertCircle size={16} />} color="yellow" variant="light" title={t('openThisFromA')}>
          {t('openThisBody')}
          <div>
            <Button component={Link} href={editorHubHref(null, 'pictures')} mt="sm" size="xs" variant="default">
              {t('goToTheEditor')}
            </Button>
          </div>
        </Alert>
      )}
    </Container>
  );
}

export default function ThumbnailEditorPage({ params }: { params: Promise<{ documentId: string }> }) {
  const { documentId } = use(params);
  return (
    <Suspense
      fallback={
        <Center h={300}>
          <Loader />
        </Center>
      }
    >
      <ThumbnailEditorRoute documentId={documentId} />
    </Suspense>
  );
}
