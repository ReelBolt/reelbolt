'use client';

import { forwardRef } from 'react';
import { PasswordInput as MantinePasswordInput, type PasswordInputProps } from '@mantine/core';
import classes from './PasswordInput.module.css';

/**
 * The app's password field: Mantine's, with the show/hide eye made usable. Mantine ships the eye
 * with tabindex=-1 (keyboard users cannot reveal what they typed) and a ~26px target; here it is a
 * normal tab stop (aria-pressed comes from Mantine), has the stable accessible name "Show password" with aria-pressed reporting the
 * state, and is 44px on touch pointers.
 */
export const PasswordInput = forwardRef<HTMLInputElement, PasswordInputProps>(function PasswordInput(
  { visibilityToggleButtonProps, visibilityToggleFocusable = true, classNames, ...rest },
  ref,
) {
  return (
    <MantinePasswordInput
      ref={ref}
      {...rest}
      visibilityToggleFocusable={visibilityToggleFocusable}
      classNames={{ ...(classNames as Record<string, string> | undefined), visibilityToggle: classes.toggle }}
      visibilityToggleButtonProps={{ 'aria-label': 'Show password', ...visibilityToggleButtonProps }}
    />
  );
});
