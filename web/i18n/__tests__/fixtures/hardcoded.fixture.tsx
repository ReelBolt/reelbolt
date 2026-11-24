declare const notifications: { show(o: { title?: string; message?: string; color?: string }): void };
export function Bad() {
  notifications.show({ title: 'Saved', color: 'green' });
  notifications.show({ message: `Could not save` });
  return (
    <div className="x" data-testid="ok">
      Hello there
      <input placeholder="Search files" />
    </div>
  );
}
