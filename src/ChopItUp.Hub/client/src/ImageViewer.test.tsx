import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import ImageViewer from './ImageViewer';

const view = (fileRef: string) =>
  renderToStaticMarkup(<ImageViewer roomId="lab" fileRef={fileRef} onClose={() => undefined} />);

describe('image viewer', () => {
  test('an image opens as a labelled dialog with its name, the original and a close control', () => {
    const markup = view('artifact:renders/shot 1.png');
    expect(markup).toContain('role="dialog"');
    expect(markup).toContain('aria-label="Image viewer"');
    expect(markup).toContain('src="/api/rooms/lab/files?ref=artifact%3Arenders%2Fshot%201.png"');
    expect(markup).toContain('class="viewer-caption" title="artifact:renders/shot 1.png">shot 1.png</p>');
    expect(markup).toMatch(/<a class="viewer-button" href="[^"]+" target="_blank" rel="noopener noreferrer">Open original<\/a>/);
    expect(markup).toContain('aria-label="Close viewer"');
    expect(markup).toContain('Fit · ');
  });

  test('a video plays with controls and shows no zoom level', () => {
    const markup = view('artifact:clip.webm');
    expect(markup).toContain('<video class="viewer-video"');
    expect(markup).toContain('controls');
    expect(markup).not.toContain('viewer-level');
  });

  test('a ref the route would refuse shows the placeholder and no original link', () => {
    const markup = view('https://example.com/a.png');
    expect(markup).toContain('a.png · not available');
    expect(markup).not.toContain('Open original');
    expect(markup).not.toContain('<img');
  });
});
