import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import { GALLERY_PAGE, GalleryGrid } from './GalleryDialog';
import type { RoomFile, RoomFileListing } from './types';

const file = (ref: string, name = ref.replace(/^artifact:(.*\/)?/, '')): RoomFile => ({
  ref,
  name,
  bytes: 10,
  modified: '2026-11-19T10:00:00Z',
});

const grid = (listing: RoomFileListing) =>
  renderToStaticMarkup(<GalleryGrid roomId="lab" listing={listing} onOpen={() => undefined} />);

describe('gallery grid', () => {
  test('tiles keep the listing order and label each file', () => {
    const markup = grid({ files: [file('artifact:b.png'), file('artifact:renders/a.webp')], omitted: 0, unreadable: 0 });
    expect(markup.indexOf('b.png')).toBeLessThan(markup.indexOf('a.webp'));
    expect(markup).toContain('src="/api/rooms/lab/files?ref=artifact%3Arenders%2Fa.webp"');
    expect(markup).toContain('title="a.webp"');
    expect(markup).toContain('2 files, newest first');
  });

  test('a video tile shows a play mark and loads no media', () => {
    const markup = grid({ files: [file('artifact:clip.mp4')], omitted: 0, unreadable: 0 });
    expect(markup).toContain('gallery-play');
    expect(markup).not.toContain('<img');
    expect(markup).not.toContain('<video');
  });

  test('only the first page renders, with a control for the rest', () => {
    const files = Array.from({ length: GALLERY_PAGE + 5 }, (_, i) => file(`artifact:f${i}.png`));
    const markup = grid({ files, omitted: 0, unreadable: 0 });
    expect(markup.match(/class="gallery-tile"/g)?.length).toBe(GALLERY_PAGE);
    expect(markup).toContain('Show more (5)');
  });

  test('the note counts files past the cap and folders that could not be read', () => {
    const markup = grid({ files: [file('artifact:a.png')], omitted: 3, unreadable: 1 });
    expect(markup).toContain('and 3 older ones not listed');
    expect(markup).toContain('One folder could not be read');
  });

  test('an empty folder says where files come from', () => {
    expect(grid({ files: [], omitted: 0, unreadable: 0 })).toContain('No images or videos yet');
  });
});
