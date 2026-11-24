'use client';

import { useState } from 'react';
import { Group, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { IconBox, IconUserCircle } from '@tabler/icons-react';
import { CheckedImage } from '@/components/outputs/CheckedVideo';
import { projectFileUrl } from '@/lib/api/files';
import { isObjectAvatar, primaryPictureFileId } from '@/lib/avatars/model';
import {
  VARIANT_DESCRIPTION_MAX, VARIANT_LABEL_MAX, VARIANT_NAME_SEPARATOR, variantDescriptionProblem, variantLabelProblem,
} from '@/lib/avatars/variants';
import type { ProjectAvatar, VariantCreateFields } from '@/lib/types/avatars';
import { useT } from '@/i18n';
import type { VariantTarget } from './variant-target';

/** The original a variant is made of, fixed and shown at the top of the form: its picture and its name. */
export function VariantOriginalLine({ projectId, original }: { projectId: string; original: ProjectAvatar }) {
  const t = useT('avatars.variants.fields');
  const pictureId = primaryPictureFileId(original);
  const isObject = isObjectAvatar(original);
  return (
    <Group gap="sm" wrap="nowrap" data-testid="variant-original">
      <div style={{ width: 44, height: 44, flex: '0 0 44px', borderRadius: 8, overflow: 'hidden', background: 'var(--mantine-color-default-hover)', display: 'grid', placeItems: 'center' }}>
        {pictureId
          ? <CheckedImage src={projectFileUrl(projectId, pictureId)} alt="" loading="lazy" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
          : isObject ? <IconBox size={26} stroke={1.3} aria-hidden /> : <IconUserCircle size={26} stroke={1.3} aria-hidden />}
      </div>
      <Stack gap={0} style={{ minWidth: 0 }}>
        <Text size="sm" c="dimmed">{t('originalLabel')}</Text>
        <Text size="sm" fw={650} truncate>{original.name}</Text>
      </Stack>
    </Group>
  );
}

export interface VariantText {
  label: string;
  description: string;
  setLabel: (label: string) => void;
  setDescription: (description: string) => void;
  /** True when both texts can be sent as they are. */
  ok: boolean;
  /** The three fields a create request carries for a variant, trimmed. */
  fields: VariantCreateFields;
}

/**
 * The label and the description of a new variant, with the checks the server makes too. A form that is not
 * making a variant passes null: the texts are then never shown or sent, and `ok` is false.
 */
export function useVariantText(target: VariantTarget | null): VariantText {
  const [label, setLabel] = useState('');
  const [description, setDescription] = useState('');
  return {
    label,
    description,
    setLabel,
    setDescription,
    ok: target !== null && variantLabelProblem(label, target.takenLabels) === null && variantDescriptionProblem(description) === null,
    fields: { variantOfAvatarId: target?.original.id ?? '', variantLabel: label.trim(), variantDescription: description.trim() },
  };
}

/**
 * The two texts every variant has: a short label ("Red"), shown next to the original's name, and what sets it
 * apart from the original ("matte red paint, black rims"). A problem is said under its field once something was
 * typed there, so an untouched form does not open full of errors.
 */
export function VariantFields({ target, text }: { target: VariantTarget; text: VariantText }) {
  const t = useT('avatars.variants.fields');
  const tProb = useT('avatars.variants.problems');
  const labelProblem = variantLabelProblem(text.label, target.takenLabels);
  const descriptionProblem = variantDescriptionProblem(text.description);
  const shownAs = `${target.original.name}${VARIANT_NAME_SEPARATOR}${text.label.trim() || t('labelPlaceholder')}`;
  return (
    <>
      <TextInput
        label={t('label')} description={t('labelHint', { name: shownAs })} placeholder={t('labelPlaceholder')} required
        maxLength={VARIANT_LABEL_MAX} value={text.label} onChange={(e) => text.setLabel(e.currentTarget.value)}
        error={text.label && labelProblem && labelProblem !== 'required' ? tProb(`label.${labelProblem}`, { max: VARIANT_LABEL_MAX }) : undefined}
        data-testid="variant-label"
      />
      <Textarea
        label={t('description')} description={t('descriptionHint')} placeholder={t('descriptionPlaceholder')} required
        autosize minRows={2} maxRows={5} maxLength={VARIANT_DESCRIPTION_MAX}
        value={text.description} onChange={(e) => text.setDescription(e.currentTarget.value)}
        error={text.description && descriptionProblem === 'tooLong' ? tProb('description.tooLong', { max: VARIANT_DESCRIPTION_MAX }) : undefined}
        data-testid="variant-description"
      />
    </>
  );
}
