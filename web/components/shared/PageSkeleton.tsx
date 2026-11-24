import { Skeleton, Stack } from '@mantine/core';

/** Text-free placeholder for `loading.tsx`. */
export function PageSkeleton() {
  return (
    <Stack gap="md" p="md" data-testid="page-skeleton">
      <Skeleton height={28} width="30%" />
      <Skeleton height={16} />
      <Skeleton height={16} width="85%" />
      <Skeleton height={200} />
    </Stack>
  );
}
