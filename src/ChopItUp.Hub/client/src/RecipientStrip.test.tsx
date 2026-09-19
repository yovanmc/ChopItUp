import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import RecipientStrip from './RecipientStrip';
import { setRoster } from './participants';

/** Row 43 AC6. Static markup like the rest of this client's component tests: there is no DOM library
 *  here, so the strip is a pure component over the draft and every state is a string of markup. The
 *  chip texts and the status role asserted below are also what the UIA gate queries by name, so a
 *  rewording here is a rewording of the gate. */
setRoster([
  { id: 'opus', displayName: 'Opus', kind: 'model', host: 'claude', model: 'opus' },
  { id: 'gpt-5.6-sol', displayName: 'GPT-5.6 Sol', kind: 'model', host: 'codex', model: 'gpt-5.6-sol' },
  { id: 'claude', displayName: 'Claude', kind: 'model', host: 'claude', model: null },
  { id: 'hub', displayName: 'Hub', kind: 'system', host: 'hub', model: null },
]);

const render = (draft: string) => renderToStaticMarkup(<RecipientStrip draft={draft} />);

describe('RecipientStrip', () => {
  test('leading spawnable ids get one chip each in the host accent and a sends-to preview', () => {
    const markup = render('@opus @gpt-5.6-sol go');
    expect(markup).toContain('role="status"');
    expect(markup).toContain('aria-label="Recipients"');
    expect(markup).toContain('role="list"');
    expect(markup.match(/class="recipient-chip"/g)?.length).toBe(2);
    expect(markup).toContain('data-host="claude"');
    expect(markup).toContain('data-host="codex"');
    expect(markup).toContain('>Opus</li>');
    expect(markup).toContain('>GPT-5.6 Sol</li>');
    expect(markup).toContain('Sends to Opus and GPT-5.6 Sol.');
  });

  test('an app-backed row is a passive chip and the preview says nothing is spawned', () => {
    const markup = render('@claude look');
    expect(markup).toContain('class="recipient-chip passive"');
    expect(markup).toContain('Claude · not spawned');
    expect(markup).toContain('Claude reads this from its own app; the hub spawns nothing.');
    expect(markup).not.toContain('Sends to');
  });

  test('a leading word nobody owns gets its own chip and the rest still send', () => {
    const markup = render('@nobody @opus hi');
    expect(markup).toContain('class="recipient-chip unknown"');
    expect(markup).toContain('@nobody · no such participant');
    expect(markup).toContain('Sends to Opus. @nobody matches nobody.');
  });

  test('an id inside the text is named as a reference and addresses nobody', () => {
    const markup = render('please ask @opus');
    expect(markup).not.toContain('recipient-chip');
    expect(markup).toContain('Nobody is addressed. @opus is inside the text, so it is a reference.');
  });

  test('a leading @hub draft is unknown, not addressed: kind system is never a recipient', () => {
    const markup = render('@hub hi');
    expect(markup).not.toContain('class="recipient-chip"');
    expect(markup).toContain('class="recipient-chip unknown"');
    expect(markup).toContain('@hub · no such participant');
    expect(markup).toContain('@hub matches nobody.');
  });

  test('a word still being typed is not flagged until a separator follows it', () => {
    expect(render('@nob')).toBe('');
    expect(render('@nob ')).toContain('@nob · no such participant');
  });

  test('an empty or ordinary draft renders nothing', () => {
    expect(render('')).toBe('');
    expect(render('hello')).toBe('');
  });

  /** Row 44, AC5's second half: the token is part of the leading run the reader already walks, so the
   *  strip says what it will do to the exchange in the same breath as who it reaches. */
  test('a turns token adds a turns chip', () => {
    const markup = render('turns: 3 @opus go');

    expect(markup).toContain('class="recipient-chip turns"');
    expect(markup).toContain('>3 turns</li>');
    expect(markup).toContain('Sends to Opus. Sets the exchange to 3 turns.');
  });

  test('an out-of-range turns token warns', () => {
    const markup = render('turns: 99 @opus go');

    expect(markup).toContain('turns: 99 is out of range (1 to 16); the default 8 applies');
    expect(markup).not.toContain('class="recipient-chip turns"');
    expect(markup).not.toContain('Sets the exchange to');
    expect(markup).toContain('Sends to Opus.');
  });

  test('turns with no recipient still shows the chip and no sends-to line', () => {
    const markup = render('turns: 5');

    expect(markup).toContain('>5 turns</li>');
    expect(markup).not.toContain('Sends to');
    expect(markup).not.toContain('Sets the exchange to');
  });

  test('a turns token after prose adds nothing', () => {
    expect(render('how many turns: 3 did we burn?')).toBe('');
  });
});
