declare const form: { getInputProps(k: string): object };
declare const myform: { getInputProps(k: string): object };
declare function getInputPropsX(k: string): object;
declare const t: ((k: string) => string) & { rich(k: string): string };
declare const tx: (k: string) => string;
declare const f: { date(d: Date, style: string): string };
declare function toast(s: string): string;
declare function top(s: string): string;
declare function trim(s: string): string;
declare const ui: { title(s: string): string };
export function Probe({ d }: { d: Date }) {
  const a = <i title={toast('Hello there')} />;
  const b = <i title={top('Hello there')} />;
  const c = <input placeholder={trim('Hello there')} />;
  const e = <img alt={ui.title('Hello there')} />;
  const g = <i title={String(getInputPropsX('Hello there'))} />;
  const h = <i title={String(myform.getInputProps('Hello there'))} />;
  const ok = <img title={t('files.title')} alt={tx('files.label')} aria-label={f.date(d, 'short')} {...form.getInputProps('email')} />;
  return <div>{a}{b}{c}{e}{g}{h}{ok}</div>;
}
