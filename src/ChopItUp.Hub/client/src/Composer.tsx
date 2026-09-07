import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import * as api from './api';
import type { Skill } from './types';

const MAX_HEIGHT_PX = 200;

/** The only draft shape that offers the menu: a slash at the very start of an otherwise-empty draft,
 *  followed by the characters a skill name may contain (`SkillStore.NamePattern`). It stops matching
 *  the moment a space or a second line arrives, which is what closes the menu after a selection —
 *  the draft becomes `/name ` and the owner is writing the ask, not the command. */
const COMMAND_DRAFT = /^\/[a-z0-9-]*$/;

interface Props {
  roomName: string;
  disabled: boolean;
  onSend: (body: string) => Promise<void>;
}

export default function Composer({ roomName, disabled, onSend }: Props) {
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [skills, setSkills] = useState<Skill[]>([]);
  const [highlight, setHighlight] = useState(0);
  /** Escape's memory. Cleared as soon as the draft leaves the command shape, so dismissing the menu
   *  once does not make the slash key feel dead for the rest of the session — it only means "not for
   *  this word". A dismissed menu leaves the draft alone: `/` then Escape then Enter sends `/`. */
  const [dismissed, setDismissed] = useState(false);
  const box = useRef<HTMLTextAreaElement>(null);

  useLayoutEffect(() => {
    const element = box.current;
    if (!element) return;
    element.style.height = 'auto';
    // scrollHeight measures content + padding, but the box is border-box, so assigning it verbatim
    // leaves the content two pixels short of what it needs and the textarea grows a permanent native
    // scrollbar even while empty. Add the borders back.
    const style = getComputedStyle(element);
    const borders = parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    element.style.height = `${Math.min(element.scrollHeight + borders, MAX_HEIGHT_PX)}px`;
  }, [draft]);

  // Once, on mount, like App.tsx's roster fetch. A failure is swallowed on purpose: the store is a
  // convenience, and a composer that refused to type because `/api/skills` is unreachable would be a
  // worse bug than the missing menu. An empty list simply never opens one.
  useEffect(() => {
    const abort = new AbortController();
    api
      .listSkills(abort.signal)
      .then(setSkills)
      .catch(() => {
        /* no menu, no error UI — the composer keeps working */
      });
    return () => abort.abort();
  }, []);

  const matches = useMemo(() => {
    if (!COMMAND_DRAFT.test(draft)) return [];
    const typed = draft.slice(1);
    return skills.filter((skill) => skill.name.startsWith(typed));
  }, [draft, skills]);

  const menuOpen = !disabled && !dismissed && matches.length > 0;
  // Clamped rather than reset by an effect: the list shrinks as the owner types, and a highlight
  // pointing past its end would render nothing selected and select nothing on Enter.
  const active = menuOpen ? Math.min(highlight, matches.length - 1) : -1;

  function edit(next: string) {
    setDraft(next);
    setHighlight(0);
    if (!COMMAND_DRAFT.test(next)) setDismissed(false);
  }

  /** Leaves the draft as the invocation plus one space, which is both what the hub parses and where
   *  the rest of the message goes. The space also drops the draft out of `COMMAND_DRAFT`, so the menu
   *  closes without needing to be told to. */
  function choose(skill: Skill) {
    setDraft(`/${skill.name} `);
    setHighlight(0);
    setDismissed(false);
    box.current?.focus();
  }

  async function send() {
    const body = draft.trim();
    if (!body || sending || disabled) return;
    setSending(true);
    try {
      await onSend(body);
      setDraft('');
      setHighlight(0);
      setDismissed(false);
      box.current?.focus();
    } finally {
      setSending(false);
    }
  }

  return (
    <form
      className="composer"
      onSubmit={(event) => {
        event.preventDefault();
        void send();
      }}
    >
      <div className="composer-field">
        {menuOpen && (
          <ul className="skill-menu" role="listbox" id="skill-menu" aria-label="Installed skills">
            {matches.map((skill, index) => (
              <li key={skill.name}>
                <button
                  type="button"
                  id={`skill-option-${skill.name}`}
                  role="option"
                  aria-selected={index === active}
                  className={`skill-option${index === active ? ' active' : ''}`}
                  // The textarea must keep focus: a click lands on mouseup, and the blur in between
                  // would move the caret out of the box the selection is about to write into.
                  onMouseDown={(event) => event.preventDefault()}
                  onMouseEnter={() => setHighlight(index)}
                  onClick={() => choose(skill)}
                >
                  <span className="skill-name">/{skill.name}</span>
                  <span className="skill-desc">{skill.description}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
        <textarea
          ref={box}
          rows={1}
          value={draft}
          disabled={disabled}
          placeholder={disabled ? 'Pick a room first' : `Message ${roomName}…`}
          aria-label={`Message ${roomName}`}
          role="combobox"
          aria-expanded={menuOpen}
          aria-controls="skill-menu"
          aria-autocomplete="list"
          aria-activedescendant={active >= 0 ? `skill-option-${matches[active]!.name}` : undefined}
          onChange={(event) => edit(event.target.value)}
          onKeyDown={(event) => {
            // The menu owns these four keys only while it is open. With it closed the composer is
            // exactly what it was: Enter sends, Tab moves focus, Escape does nothing, and a draft of
            // a bare `/` goes to the room as ordinary text.
            if (menuOpen) {
              if (event.key === 'ArrowDown') {
                event.preventDefault();
                setHighlight((active + 1) % matches.length);
                return;
              }
              if (event.key === 'ArrowUp') {
                event.preventDefault();
                setHighlight((active - 1 + matches.length) % matches.length);
                return;
              }
              if (event.key === 'Enter' || event.key === 'Tab') {
                event.preventDefault();
                choose(matches[active]!);
                return;
              }
              if (event.key === 'Escape') {
                event.preventDefault();
                setDismissed(true);
                return;
              }
            }
            if (event.key === 'Enter' && !event.shiftKey) {
              event.preventDefault();
              void send();
            }
          }}
        />
      </div>
      <div className="composer-side">
        <button type="submit" className="send" disabled={disabled || sending || draft.trim().length === 0}>
          {sending ? 'Sending…' : 'Send'}
        </button>
        <span className="hint">Enter sends · Shift+Enter newline · / for skills</span>
      </div>
    </form>
  );
}
