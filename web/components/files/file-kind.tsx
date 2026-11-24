import { IconFile, IconFileMusic, IconFileText, IconPhoto, IconVideo } from '@tabler/icons-react';
import type { FileKind } from '@/lib/files/model';
import { msg, type Msg } from '@/i18n/msg';
import classes from './files.module.css';

const KIND_STYLE: Record<FileKind, { color: string; Icon: typeof IconFile }> = {
  video: { color: 'violet', Icon: IconVideo },
  audio: { color: 'teal', Icon: IconFileMusic },
  image: { color: 'blue', Icon: IconPhoto },
  text: { color: 'gray', Icon: IconFileText },
  other: { color: 'gray', Icon: IconFile },
};

export function KindIcon({ kind, size = 36 }: { kind: FileKind; size?: number }) {
  const { color, Icon } = KIND_STYLE[kind];
  return (
    <span
      className={classes.fileIcon}
      style={{
        width: size,
        height: size,
        color: `var(--mantine-color-${color}-light-color)`,
        background: `var(--mantine-color-${color}-light)`,
      }}
      aria-hidden
    >
      <Icon size={Math.round(size * 0.55)} stroke={1.6} />
    </span>
  );
}

export const KIND_LABEL: Record<FileKind | 'all', Msg> = {
  all: msg('files.kinds.all'),
  video: msg('files.kinds.video'),
  audio: msg('files.kinds.audio'),
  image: msg('files.kinds.image'),
  text: msg('files.kinds.text'),
  other: msg('files.kinds.other'),
};
