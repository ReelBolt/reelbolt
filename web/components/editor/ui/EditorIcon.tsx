'use client';

import React from 'react';
import Link from 'next/link';
import { ActionIcon, Tooltip, type ActionIconProps, type MantineColor } from '@mantine/core';
import classes from './ui.module.css';

/** A Tabler-style icon component. */
export type EditorIconComponent = React.ComponentType<{ size?: number | string; stroke?: number | string; 'aria-hidden'?: boolean | 'true' | 'false' }>;

/** Icon sizes in px (the glyph; the hit area comes from the density tokens). */
export const EDITOR_ICON_SIZE = { sm: 14, md: 16, lg: 20 } as const;
export const EDITOR_ICON_STROKE = 1.7;
export type EditorIconSize = keyof typeof EDITOR_ICON_SIZE;

interface BaseProps {
  icon?: EditorIconComponent;
  size?: EditorIconSize;
  color?: MantineColor;
}

export interface EditorIconGlyphProps extends BaseProps {
  icon: EditorIconComponent;
  /** Omit for a decorative glyph (hidden from assistive technology). */
  title?: string;
  label?: undefined;
}

type NativeButton = Omit<React.ComponentPropsWithoutRef<'button'>, keyof ActionIconProps | 'color' | 'children' | 'aria-label'>;

export interface EditorIconButtonProps extends BaseProps, Omit<ActionIconProps, 'size' | 'color' | 'children'>, NativeButton {
  /** The accessible name. Required for a button: an icon alone says nothing to a screen reader. */
  label: string;
  /** A visible hint on hover or focus (defaults to the label); `false` for none. */
  tooltip?: string | false;
  /** A short text (such as "+V") shown instead of the icon; `label` is still the accessible name. */
  children?: React.ReactNode;
  /** A 24 px hit area (28 on a coarse pointer) for controls inside a fixed-height row such as a track header. */
  dense?: boolean;
  loading?: boolean;
  /** Makes the button a link to this path (client-side navigation). */
  href?: string;
  'data-testid'?: string;
}

export type EditorIconProps = EditorIconGlyphProps | EditorIconButtonProps;

const isButton = (p: EditorIconProps): p is EditorIconButtonProps => typeof p.label === 'string';

/**
 * The editor's one icon wrapper.
 *
 *   <EditorIcon icon={IconTrash} />                              decorative glyph (aria-hidden)
 *   <EditorIcon icon={IconTrash} title="Deleted" />              meaningful glyph (role=img, named)
 *   <EditorIcon icon={IconTrash} label="Delete clip" onClick />  icon button (named, focusable, tooltip)
 *
 * Glyph size comes from the size token (sm 14, md 16, lg 20) and a single stroke weight; the button's hit area comes from
 * the density tokens (`--rb-icon-h`: 28 compact, 32 comfortable, 44 on a coarse pointer), so no call site picks a
 * pixel size and no icon button is smaller than the 24 px WCAG minimum.
 */
export function EditorIcon(props: EditorIconProps): React.ReactElement {
  if (!isButton(props)) {
    const px = EDITOR_ICON_SIZE[props.size ?? 'md'];
    const { icon: Icon, title, color } = props;
    const glyph = <Icon size={px} stroke={EDITOR_ICON_STROKE} aria-hidden={title ? undefined : true} />;
    return title ? (
      <span role="img" aria-label={title} className={classes.glyph} data-color={color}>
        {glyph}
      </span>
    ) : (
      <span aria-hidden="true" className={classes.glyph} data-color={color}>
        {glyph}
      </span>
    );
  }
  const { icon: Icon, size, label, tooltip, color = 'gray', variant = 'subtle', className, href, type = 'button', dense, children, ...rest } = props;
  const px = EDITOR_ICON_SIZE[size ?? 'md'];
  const cls = [classes.iconButton, dense ? classes.dense : '', className].filter(Boolean).join(' ');
  const glyph = children ?? (Icon ? <Icon size={px} stroke={EDITOR_ICON_STROKE} aria-hidden /> : null);
  const button = href ? (
    <ActionIcon component={Link} href={href} variant={variant} color={color} aria-label={label} className={cls} data-testid={rest['data-testid']}>
      {glyph}
    </ActionIcon>
  ) : (
    <ActionIcon type={type} variant={variant} color={color} aria-label={label} className={cls} {...rest}>
      {glyph}
    </ActionIcon>
  );
  if (tooltip === false || rest.disabled) return button;
  return (
    <Tooltip label={tooltip ?? label} openDelay={600} withinPortal>
      {button}
    </Tooltip>
  );
}
