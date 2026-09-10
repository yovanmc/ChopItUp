import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test, vi } from 'vitest';
import { TokenGate } from './App';

/** Row 28, AC5's first half. A deliberate action the hub refused for want of a credential has to say
 *  what did not happen and give the owner somewhere to put the token — "Send" that silently ate the
 *  message is the failure this row is closing, not a smaller version of it.
 *
 *  `renderToStaticMarkup` for the same reason as the other component tests here: no jsdom. What that
 *  cannot prove is the wiring in `App` that decides to show this — which write raised it, and that a
 *  refused `markRead` never does. `api.test.ts` covers the value those branches key off.
 *
 *  `./markdown` is stubbed because importing `App` reaches `SkillPanel` and so DOMPurify, which needs
 *  a real DOM — the same stub `MemoryPanel.test.tsx` and `SkillPanel.test.tsx` use, and nothing here
 *  renders a message body. */
vi.mock('./markdown', () => ({ renderBody: (body: string) => `<p>${body}</p>` }));

const NOTICE = 'Your message was not posted. The hub needs the owner token before it will accept a write from this browser.';

const render = () =>
  renderToStaticMarkup(<TokenGate notice={NOTICE} onToken={() => undefined} onDismiss={() => undefined} />);

describe('TokenGate', () => {
  test('it says what did not happen, in the words the caller handed it', () => {
    expect(render()).toContain('Your message was not posted.');
  });

  test('it is announced, because it appears in answer to something the owner just pressed', () => {
    expect(render()).toContain('role="alert"');
  });

  test('it offers somewhere to paste the token, masked and never autofilled', () => {
    const markup = render();

    expect(markup).toContain('type="password"');
    // Case-insensitive: `react-dom/server` writes the JSX prop name through as `autoComplete`, which
    // the browser reads as the attribute either way. What is asserted is the value, not the casing.
    expect(markup).toMatch(/autocomplete="off"/i);
    expect(markup).toContain('Paste the owner token');
  });

  /** The skill card's field owns `owner-token`; two live fields with one id is a label that points at
   *  the wrong box, and both can be on screen at once. */
  test('its field does not collide with the skill card\'s field id', () => {
    const markup = render();

    expect(markup).toContain('id="write-token"');
    expect(markup).not.toContain('id="owner-token"');
  });

  test('the owner can put it away without pasting anything', () => {
    expect(render()).toContain('Not now');
  });
});
