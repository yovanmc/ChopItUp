import { useLayoutEffect, useRef, useState } from 'react';

const MAX_HEIGHT_PX = 200;

interface Props {
  roomName: string;
  disabled: boolean;
  onSend: (body: string) => Promise<void>;
}

export default function Composer({ roomName, disabled, onSend }: Props) {
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
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

  async function send() {
    const body = draft.trim();
    if (!body || sending || disabled) return;
    setSending(true);
    try {
      await onSend(body);
      setDraft('');
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
      <textarea
        ref={box}
        rows={1}
        value={draft}
        disabled={disabled}
        placeholder={disabled ? 'Pick a room first' : `Message ${roomName}…`}
        aria-label={`Message ${roomName}`}
        onChange={(event) => setDraft(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' && !event.shiftKey) {
            event.preventDefault();
            void send();
          }
        }}
      />
      <div className="composer-side">
        <button type="submit" className="send" disabled={disabled || sending || draft.trim().length === 0}>
          {sending ? 'Sending…' : 'Send'}
        </button>
        <span className="hint">Enter sends · Shift+Enter newline</span>
      </div>
    </form>
  );
}
