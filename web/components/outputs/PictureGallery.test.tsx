// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PictureGallery, type GalleryPicture } from './PictureGallery';

const pics: GalleryPicture[] = [
  { id: 'a', src: '/o/a/download', downloadUrl: '/o/a/download?saveAs=a.png', fileName: 'a.png', caption: 'Red', thumbSrc: '/o/a/download?w=480', largeSrc: '/o/a/download?w=1920' },
  { id: 'b', src: '/o/b/download', downloadUrl: '/o/b/download?saveAs=b.png', fileName: 'b.png' },
  { id: 'c', src: '/o/c/download', downloadUrl: '/o/c/download?saveAs=c.png', fileName: 'c.png' },
];

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(cleanup);

const ui = () => render(<MantineProvider><PictureGallery pictures={pics} /></MantineProvider>);

describe('PictureGallery', () => {
  it('uses thumbSrc in the grid and the large src in the modal', async () => {
    ui();
    const grid = screen.getByAltText('Red') as HTMLImageElement;
    expect(grid.getAttribute('src')).toBe('/o/a/download?w=480');
    expect((screen.getByAltText('Picture 2') as HTMLImageElement).getAttribute('src')).toBe('/o/b/download');
    fireEvent.click(screen.getByLabelText('Open Red full size'));
    await screen.findByText('Red (1/3)');
    const srcs = screen.getAllByAltText('Red').map((i) => i.getAttribute('src'));
    expect(srcs).toContain('/o/a/download?w=1920');
  });

  it('renders every picture and no draft or final label', () => {
    ui();
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
    expect(screen.getByText('3 pictures')).toBeTruthy();
    expect(screen.queryByText(/draft|final/i)).toBeNull();
  });

  it('links a download per picture and offers download all', () => {
    ui();
    const link = screen.getByLabelText('Download Red') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/o/a/download?saveAs=a.png');
    expect(screen.getByLabelText('Download Picture 2')).toBeTruthy();
    expect(screen.getByText('Download all')).toBeTruthy();
  });

  it('opens full size with previous and next', async () => {
    ui();
    fireEvent.click(screen.getByLabelText('Open Red full size'));
    expect(await screen.findByText('Red (1/3)')).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Next picture'));
    expect(await screen.findByText('Picture 2 (2/3)')).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Previous picture'));
    expect(await screen.findByText('Red (1/3)')).toBeTruthy();
  });
});
