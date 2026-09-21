import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import * as api from './api';
import { displayName } from './participants';
import RecipientStrip from './RecipientStrip';
import { messageBody } from './governing';
import { replySnippet } from './reply';
import type { DispatchPreview, Message, Skill } from './types';

const MAX_HEIGHT_PX = 200;

/** The only draft shape that offers the menu: a slash at the very start of an otherwise-empty draft,
 *  followed by the characters a skill name may contain (`SkillStore.NamePattern`). It stops matching
 *  the moment a space or a second line arrives, which is what closes the menu after a selection —
 *  the draft becomes `/name ` and the owner is writing the ask, not the command. */
const COMMAND_DRAFT = /^\/[a-z0-9-]*$/;

interface Props {
  /** The open room. Everything the composer holds that belongs to a room — the draft, the reply
   *  target, which sends are in flight — is keyed by this rather than reset when it changes. */
  roomId: string;
  roomName: string;
  disabled: boolean;
  /** What this room holds unsent. Owned by App so switching rooms and coming back finds it again. */
  draft: string;
  onDraftChange: (text: string) => void;
  /** The message the next post replies to, or null. Owned by App, which clears it after a successful
   *  send and keeps it after a failed one. */
  replyTo: Message | null;
  onCancelReply: () => void;
  onSend: (body: string, replyToId: number | null, admission?: { quote: string; clientKey: string }) => Promise<void>;
  previewEpoch?: string;
  onPreviewError?: (error: unknown) => void;
}

export default function Composer({ roomId, roomName, disabled, draft, onDraftChange, replyTo, onCancelReply, onSend, previewEpoch, onPreviewError }: Props) {
  const [preview, setPreview] = useState<{ identity: string; value: DispatchPreview } | null>(null);
  const [previewError, setPreviewError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const retry = useRef<{ identity: string; key: string } | null>(null);
  const identity = JSON.stringify([roomId, messageBody(draft), replyTo?.id ?? null]);
  useEffect(() => {
    const abort = new AbortController();
    let current = true;
    setPreview(null);
    setPreviewError('');
    if (!messageBody(draft) || disabled) return () => abort.abort();
    const timer = setTimeout(() => {
      api.dispatchPreview(roomId, messageBody(draft), replyTo?.id ?? null, abort.signal)
        .then(value => { if (current) setPreview({ identity, value }); })
        .catch(error => { if (current) { setPreviewError(api.describeError(error)); onPreviewError?.(error); } });
    }, 200);
    return () => { current = false; clearTimeout(timer); abort.abort(); };
  }, [identity, roomId, draft, replyTo?.id, disabled, previewEpoch, refresh, onPreviewError]);
  const ready = preview?.identity === identity && !preview.value.error;
  /** The rooms whose send is in flight. A set rather than a flag: a slow send in the room just left
   *  must not grey the Send button of the room now open. */
  const [sending, setSending] = useState<ReadonlySet<string>>(() => new Set());
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

  // Choosing Reply on a message puts the caret where the reply goes.
  useEffect(() => {
    if (replyTo) box.current?.focus();
  }, [replyTo]);

  // The menu's own state is about the word being typed here and now, so a room change starts it over
  // rather than carrying one room's highlight or its Escape into the next.
  useEffect(() => {
    setHighlight(0);
    setDismissed(false);
  }, [roomId]);

  const matches = useMemo(() => {
    if (!COMMAND_DRAFT.test(draft)) return [];
    const typed = draft.slice(1);
    return skills.filter((skill) => skill.name.startsWith(typed));
  }, [draft, skills]);

  const busy = sending.has(roomId);
  const menuOpen = !disabled && !dismissed && matches.length > 0;
  // Clamped rather than reset by an effect: the list shrinks as the owner types, and a highlight
  // pointing past its end would render nothing selected and select nothing on Enter.
  const active = menuOpen ? Math.min(highlight, matches.length - 1) : -1;

  function edit(next: string) {
    onDraftChange(next);
    setHighlight(0);
    if (!COMMAND_DRAFT.test(next)) setDismissed(false);
  }

  /** Leaves the draft as the invocation plus one space, which is both what the hub parses and where
   *  the rest of the message goes. The space also drops the draft out of `COMMAND_DRAFT`, so the menu
   *  closes without needing to be told to. */
  function choose(skill: Skill) {
    onDraftChange(`/${skill.name} `);
    setHighlight(0);
    setDismissed(false);
    box.current?.focus();
  }

  async function send() {
    // The room this message is for, read before the await: another room may be open by the time it
    // resolves, and what is released then is this room's send, not whatever is on screen.
    const room = roomId;
    const body = messageBody(draft);
    if (!body || sending.has(room) || disabled || !ready || !preview) return;
    if (retry.current?.identity !== identity) retry.current = { identity, key: crypto.randomUUID() };
    setSending((previous) => new Set(previous).add(room));
    try {
      // The draft is App's, cleared there on success so a failed send leaves the words to retry.
      await onSend(body, replyTo?.id ?? null, { quote: preview.value.quote, clientKey: retry.current.key });
      retry.current = null;
      setHighlight(0);
      setDismissed(false);
      box.current?.focus();
    } catch {
      // App retains the draft and reports the failure. A changed quote requires a new click.
      setRefresh(previous => previous + 1);
    } finally {
      setSending((previous) => {
        const next = new Set(previous);
        next.delete(room);
        return next;
      });
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
        {replyTo && (
          <div className="reply-chip">
            <span className="reply-chip-text">
              Replying to <span className="reply-chip-author">{displayName(replyTo.authorId)}</span>
              <span className="reply-chip-snippet">{replySnippet(replyTo.body)}</span>
            </span>
            <button
              type="button"
              className="reply-chip-cancel"
              aria-label="Cancel reply"
              // Same reason as the skill options: the caret stays in the box being written in.
              onMouseDown={(event) => event.preventDefault()}
              onClick={onCancelReply}
            >
              ×
            </button>
          </div>
        )}
        <RecipientStrip draft={draft} />
        <div className="composer-input">
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
              // exactly what it was: Enter sends, Tab moves focus, Escape cancels a reply (and does
              // nothing when there is none), and a draft of a bare `/` goes to the room as ordinary text.
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
              if (event.key === 'Escape' && replyTo) {
                event.preventDefault();
                onCancelReply();
                return;
              }
              if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                void send();
              }
            }}
          />
        </div>
      </div>
      <div className="composer-side">
        <div className="dispatch-preview" aria-live="polite">
          {preview?.identity === identity ? preview.value.error ?? (preview.value.turns != null
            ? `${preview.value.mode}: ${preview.value.participants.join(' → ')} · ${preview.value.turns} planned model turns · money/tokens unknown${preview.value.commit ? ` · snapshot ${preview.value.commit.slice(0, 12)}` : ''}`
            : 'Explicit recipients or command; existing dispatch rules apply.') : previewError || (draft.trim() ? 'Checking recipients and turn count…' : '')}
          {(previewError || preview?.value.error) && <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh preview</button>}
        </div>
        <button type="submit" className="send" disabled={disabled || busy || draft.trim().length === 0 || !ready}>
          {busy ? 'Sending…' : 'Send'}
        </button>
        <span className="hint">Enter sends · Shift+Enter newline · / for skills · /objective and /correction pin context</span>
      </div>
    </form>
  );
}
