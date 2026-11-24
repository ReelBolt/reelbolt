'use client';

import { use } from 'react';
import { ProviderDetail } from '@/components/admin/ProviderDetail';

const BREADCRUMBS = [{ label: 'Inference Providers', href: '/admin/inference-providers' }];

/** One platform provider: its account, models, capabilities and their prices. */
export default function InferenceProviderDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  return (
    <ProviderDetail
      id={id}
      scope="platform"
      breadcrumbs={BREADCRUMBS}
      listHref="/admin/inference-providers"
    />
  );
}
