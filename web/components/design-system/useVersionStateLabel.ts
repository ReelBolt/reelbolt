import { useT } from '@/i18n';

/** The display name of a design-system version state. The server sends the English state id. */
export function useVersionStateLabel(): (state: string) => string {
  const t = useT('brandLibraries.versionState');
  return (state) => {
    switch (state) {
      case 'Draft':
        return t('Draft');
      case 'Approved':
        return t('Approved');
      case 'Superseded':
        return t('Superseded');
      case 'Rejected':
        return t('Rejected');
      default:
        return state;
    }
  };
}
