import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import Composer from './Composer';

function render(draft: string): string {
  return renderToStaticMarkup(
    <Composer
      roomId="general"
      roomName="General"
      disabled={false}
      draft={draft}
      onDraftChange={() => undefined}
      replyTo={null}
      onCancelReply={() => undefined}
      onSend={async () => undefined}
    />,
  );
}

/** Attribute order is React's to choose, so read the whole opening tag rather than a fixed spelling. */
function sendButton(markup: string): string {
  return markup.match(/<button[^>]*class="send"[^>]*>/)![0];
}

describe('composer draft', () => {
  test('the box holds what the draft prop says, not a draft of its own', () => {
    expect(render('half a thought')).toContain('half a thought</textarea>');
  });

  test('Send waits for a server preview and remains disabled for a blank draft', () => {
    expect(sendButton(render('ready'))).toContain('disabled');
    expect(sendButton(render('   '))).toContain('disabled');
  });
});
