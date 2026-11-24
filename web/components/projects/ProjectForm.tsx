'use client';
/* eslint-disable i18next/no-literal-string -- the remaining literals are ids, enum keys and t.rich tag handlers, not user-visible text */

import { useEffect, useState } from 'react';
import { Modal, TextInput, Textarea, Button, Stack, Text, Select, MultiSelect, Radio, Switch } from '@mantine/core';
import { useIsPhoneClass } from '@/lib/hooks/use-device-class';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { createProject, updateProject } from '@/lib/api/projects';
import { plainErrorMessage } from '@/lib/api/network-error';
import type { Project } from '@/lib/types/project';
import { MAX_TAGS_PER_PROJECT } from '@/lib/types/project-organization';
import { useProjectFolders } from '@/lib/hooks/use-project-folders';
import { useProjectTags } from '@/lib/hooks/use-project-tags';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import { defaultStartFrom, starterTemplateKeyFor, type StartFrom } from '@/lib/onboarding/welcome';
import { useT } from '@/i18n';

interface ProjectFormProps {
  opened: boolean;
  onClose: () => void;
  /** Receives the saved project so a create flow can carry on to the next step (uploading files). */
  onSuccess: (project?: Project) => void;
  project?: Project;
  /** Pre-fills the folder when creating while a folder is selected on the projects page. */
  defaultFolderId?: string | null;
}

export function ProjectForm({ opened, onClose, onSuccess, project, defaultFolderId }: ProjectFormProps) {
  const t = useT('projects.form');
  const to = useT('projects.organization.form');
  const { data: folders } = useProjectFolders();
  const { data: tags } = useProjectTags();
  const [loading, setLoading] = useState(false);
  const isEdit = !!project;
  // What the new project starts from decides its first app: an edit of the person's clips, or a
  // promo made from an app's code. It follows what the person said in the welcome questions until
  // they choose here; without the choice every project got the promo-from-code app, whoever made it.
  const { profile } = useOnboarding();
  const [chosenStart, setChosenStart] = useState<StartFrom | null>(null);
  const startFrom: StartFrom = chosenStart ?? defaultStartFrom(profile);
  const isPhone = useIsPhoneClass();

  const form = useForm({
    initialValues: {
      name: project?.name || '',
      description: project?.description || '',
      folderId: '' as string,
      tagIds: [] as string[],
      analyzeOnUpload: project?.analyzeOnUpload ?? false,
    },
    validate: {
      name: (v) => (!v.trim() ? t('nameRequired') : v.length > 200 ? t('nameTooLong') : null),
    },
  });

  // The folder is chosen by the page when the form opens; re-apply it each time the dialog opens.
  useEffect(() => {
    if (opened && !isEdit) form.setFieldValue('folderId', defaultFolderId ?? '');
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only on open
  }, [opened, defaultFolderId]);

  // The project may have loaded, or changed, after this form was mounted: show its stored choice each time it opens.
  useEffect(() => {
    if (opened && isEdit) form.setFieldValue('analyzeOnUpload', project?.analyzeOnUpload ?? false);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only on open
  }, [opened, project?.analyzeOnUpload]);

  const handleSubmit = form.onSubmit(async (values) => {
    setLoading(true);
    try {
      const payload = { name: values.name.trim(), description: values.description.trim() };
      const saved = isEdit ? await updateProject(project.id, { ...payload, analyzeOnUpload: values.analyzeOnUpload }) : await createProject({
        ...payload,
        starterTemplateKey: starterTemplateKeyFor(startFrom),
        ...(values.tagIds.length ? { tagIds: values.tagIds } : {}),
        ...(values.folderId ? { folderId: values.folderId } : {}),
      });
      notifications.show({
        title: isEdit ? t('updatedTitle') : t('createdTitle'),
        message: isEdit ? t('updatedMessage') : t('createdMessage', { start: startFrom }),
        color: 'green',
      });
      form.reset();
      setChosenStart(null);
      onSuccess(saved);
      onClose();
    } catch (err: unknown) {
      notifications.show({
        title: isEdit ? t('saveFailed') : t('createFailed'),
        message: plainErrorMessage(err),
        color: 'red',
      });
    } finally {
      setLoading(false);
    }
  });

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      closeButtonProps={{ size: 44 }}
      title={isEdit ? t('editTitle') : t('newTitle')}
      centered={!isPhone}
      fullScreen={!!isPhone}
      radius="lg"
    >
      <form onSubmit={handleSubmit} noValidate>
        <Stack>
          {!isEdit && (
            <Text size="sm" c="dimmed">
              {t('intro')}
            </Text>
          )}
          <TextInput
            label={t('name')}
            placeholder={t('namePlaceholder')}
            data-autofocus
            withAsterisk
            size="md"
            {...form.getInputProps('name')}
          />
          <Textarea
            label={t('description')}
            description={t('descriptionHint')}
            placeholder={t('descriptionPlaceholder')}
            rows={3}
            size="md"
            {...form.getInputProps('description')}
          />
          {!isEdit && (
            <Radio.Group
              label={t('startFrom')}
              description={t('startFromHint')}
              value={startFrom}
              onChange={(value) => setChosenStart(value === 'code' ? 'code' : 'footage')}
              data-testid="project-start-from"
            >
              <Stack gap="xs" mt="xs">
                <Radio value="footage" label={t('startFootage')} styles={{ body: { alignItems: 'center', minHeight: 44 } }} />
                <Radio value="code" label={t('startCode')} styles={{ body: { alignItems: 'center', minHeight: 44 } }} />
              </Stack>
            </Radio.Group>
          )}
          {!isEdit && (
            <>
              <Select
                label={to('folder')}
                size="md"
                clearable
                placeholder={to('folderNone')}
                data={(folders ?? []).map((f) => ({ value: f.id, label: f.name }))}
                value={form.values.folderId || null}
                onChange={(v) => form.setFieldValue('folderId', v ?? '')}
              />
              <MultiSelect
                label={to('tags')}
                description={to('tagsHint')}
                size="md"
                placeholder={to('tagsPlaceholder')}
                maxValues={MAX_TAGS_PER_PROJECT}
                data={(tags ?? []).map((x) => ({ value: x.id, label: x.name }))}
                value={form.values.tagIds}
                onChange={(v) => form.setFieldValue('tagIds', v)}
              />
            </>
          )}
          {isEdit && (
            <Switch
              label={t('analyzeOnUpload')}
              description={t('analyzeOnUploadHint')}
              size="md"
              {...form.getInputProps('analyzeOnUpload', { type: 'checkbox' })}
            />
          )}
          <Button type="submit" loading={loading} fullWidth size="md">
            {isEdit ? t('saveChanges') : t('create')}
          </Button>
        </Stack>
      </form>
    </Modal>
  );
}
