import { useT } from '@/i18n';

/** The display name of a workspace role. The server sends the English role id; unknown ids show as sent. */
export function useRoleLabel(): (role: string | null | undefined) => string {
  const t = useT('org.roles');
  return (role) => {
    switch (role) {
      case 'Owner':
        return t('Owner');
      case 'Admin':
        return t('Admin');
      case 'Member':
        return t('Member');
      case 'Billing':
        return t('Billing');
      default:
        return role ?? '';
    }
  };
}
