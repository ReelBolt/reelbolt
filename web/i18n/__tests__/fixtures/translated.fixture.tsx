declare const notifications: { show(o: { title?: string; message?: string; color?: string }): void };
declare const t: (k: string) => string;
export function Good() {
  notifications.show({ title: t('saved'), color: 'green' });
  return (
    <div className="x" data-testid="ok">
      {t('hello')}
      <input placeholder={t('search')} aria-hidden="true" />
      <span>·</span>
    </div>
  );
}
