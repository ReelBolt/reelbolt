'use client';

import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { displayNamer } from '@/lib/avatars/variants';
import type { ProjectAvatar } from '@/lib/types/avatars';

type NameOf = (avatar: Pick<ProjectAvatar, 'id' | 'name' | 'variantOfAvatarId' | 'variantLabel'>) => string;

const ownName: NameOf = (avatar) => avatar.name;
const AvatarNamesContext = createContext<NameOf>(ownName);

/**
 * Names the avatars of one project for everything inside: a variant reads `Car · Red` (its original's name and
 * its own label), any other avatar its own name. Cards and dialogs ask `useAvatarName()` instead of reading
 * `avatar.name`, so a variant is named the same way wherever it shows.
 */
export function AvatarNamesProvider({ avatars, children }: { avatars: readonly ProjectAvatar[] | null | undefined; children: ReactNode }) {
  const nameOf = useMemo<NameOf>(() => displayNamer(avatars), [avatars]);
  return <AvatarNamesContext.Provider value={nameOf}>{children}</AvatarNamesContext.Provider>;
}

/** The name to show for an avatar. Outside a provider it is the avatar's own name. */
export function useAvatarName(): NameOf {
  return useContext(AvatarNamesContext);
}
