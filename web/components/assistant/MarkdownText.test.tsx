import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ReactMarkdown from 'react-markdown';
import { markdownComponents } from './MarkdownText';

// WEB-1: no markdown image ever loads. A remote one is a zero-click request to a host injected text
// chose; a same-origin one (e.g. an OAuth authorize URL) is a zero-click request with the user's cookies.
describe('assistant markdown img override', () => {
  const Img = markdownComponents.img;

  it('renders a remote image as a plain link with the alt text, never an <img>', () => {
    const { container } = render(<Img src="https://evil.example/p.png?d=secret" alt="chart" />);
    expect(container.querySelector('img')).toBeNull();
    const link = container.querySelector('a');
    expect(link?.getAttribute('href')).toBe('https://evil.example/p.png?d=secret');
    expect(link?.getAttribute('target')).toBe('_blank');
    expect(link?.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link?.textContent).toBe('chart');
  });

  it('falls back to the URL as link text when there is no alt', () => {
    const { container } = render(<Img src="https://evil.example/p.png" />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('a')?.textContent).toBe('https://evil.example/p.png');
  });

  it('renders a protocol-relative source as text only (no img, no link)', () => {
    const { container } = render(<Img src="//evil.example/p.png" alt="x" />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('a')).toBeNull();
    expect(container.textContent).toBe('x');
  });

  it.each([
    '/app/api/v1/files/1/preview.png',
    'preview.png',
    '../images/x.png',
    'https://reelbolt.example/app/x.png',
    'data:image/png;base64,AAAA',
    'blob:https://reelbolt.example/1b2c',
  ])('never renders an <img> for %s', (src) => {
    const { container } = render(<Img src={src} alt="preview" />);
    expect(container.querySelector('img')).toBeNull();
    expect(container.textContent).toBe('preview');
  });

  it('renders a same-origin authorize URL in markdown as no <img>', () => {
    const md = '![x](/oauth/authorize?client_id=a&redirect_uri=b&response_type=token&state=c)';
    const { container } = render(<ReactMarkdown components={{ img: Img }}>{md}</ReactMarkdown>);
    expect(container.querySelector('img')).toBeNull();
    expect(container.textContent).toBe('x');
  });

  it('does not leak the hast node onto the DOM', () => {
    const { container } = render(<Img src="https://evil.example/x.png" alt="x" node={{ type: 'element' }} />);
    expect(container.querySelector('a')?.hasAttribute('node')).toBe(false);
  });
});
