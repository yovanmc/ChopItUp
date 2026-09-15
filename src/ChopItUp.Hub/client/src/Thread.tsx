import { memo, useEffect, useMemo, useRef } from 'react';
import { renderBody } from './markdown';
import { accentClass, badgeFor, displayName, isHuman, isSystem } from './participants';
import { replySnippet } from './reply';
import { clockTime, dayLabel, exactTime, isSameDay, minutesBetween } from './time';
import type { Message } from './types';

export { replySnippet };

/** A run breaks after this long even when the same participant is still talking. */
const RUN_GAP_MINUTES = 8;
/** How close to the bottom still counts as "following the conversation". */
const PIN_SLACK_PX = 96;
/** How long a message a quote jumped to stays highlighted. Matches `.row.flash` in styles.css. */
const FLASH_MS = 1200;

interface Props {
  messages: Message[];
  loading: boolean;
  onReply?: (message: Message) => void;
}

/** Brings the original of a reply into view and briefly highlights it. The class is toggled on the DOM
 *  node rather than through state so no row re-renders for it; removing it and forcing a reflow first
 *  restarts the fade when the same quote is clicked twice in a row. */
function jumpTo(messageId: number): void {
  const target = document.getElementById(`msg-${messageId}`);
  if (!target) return;
  target.scrollIntoView({ block: 'center' });
  target.classList.remove('flash');
  void target.offsetWidth;
  target.classList.add('flash');
  window.setTimeout(() => target.classList.remove('flash'), FLASH_MS);
}

export default function Thread({ messages, loading, onReply }: Props) {
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);
  const byId = useMemo(() => new Map(messages.map((message) => [message.id, message])), [messages]);

  useEffect(() => {
    const element = scroller.current;
    if (element && pinned.current) element.scrollTop = element.scrollHeight;
  }, [messages]);

  function onScroll() {
    const element = scroller.current;
    if (!element) return;
    pinned.current = element.scrollHeight - element.scrollTop - element.clientHeight < PIN_SLACK_PX;
  }

  return (
    <div className="thread" ref={scroller} onScroll={onScroll}>
      <div className="thread-inner">
        {loading && <p className="thread-note">Loading the room…</p>}
        {!loading && messages.length === 0 && (
          <p className="thread-note">Nothing here yet. Say something, or import a transcript.</p>
        )}
        {messages.map((message, index) => {
          const previous = index > 0 ? messages[index - 1] : undefined;
          const newDay = !previous || !isSameDay(previous.createdAt, message.createdAt);
          const startsRun =
            !previous ||
            newDay ||
            previous.authorId !== message.authorId ||
            minutesBetween(previous.createdAt, message.createdAt) > RUN_GAP_MINUTES;
          return (
            <MessageRow
              key={message.id}
              message={message}
              startsRun={startsRun}
              dayBreak={newDay && index > 0}
              original={message.replyToId == null ? undefined : byId.get(message.replyToId)}
              onReply={onReply}
            />
          );
        })}
      </div>
    </div>
  );
}

interface RowProps {
  message: Message;
  startsRun: boolean;
  dayBreak: boolean;
  /** The message this one replies to, when it is loaded. Resolved by the parent so the row never
   *  receives the whole list. */
  original: Message | undefined;
  onReply: ((message: Message) => void) | undefined;
}

/** Memoised on stable message objects, two booleans and a stable callback, so appending one message
 *  renders exactly one new row: nothing above it changes props. */
const MessageRow = memo(function MessageRow({ message, startsRun, dayBreak, original, onReply }: RowProps) {
  const system = isSystem(message.authorId);
  const mine = isHuman(message.authorId);
  return (
    <>
      {dayBreak && (
        <div className="day-break">
          <span>{dayLabel(message.createdAt)}</span>
        </div>
      )}
      {system ? (
        /* A hub note is the app talking about the room, so it gets no avatar and no accent — but it
           keeps the row grid, which lines its text up with every other row's body at both widths, and
           it keeps `starts-run`, so the spacing around it still reads as grouping. It always shows its
           label: these arrive one at a time, and a run of two would still want naming. */
        <article id={`msg-${message.id}`} className={`row system${startsRun ? ' starts-run' : ''}`}>
          <div className="row-gutter" />
          <div className="row-main">
            <div className="row-meta">
              <span className="system-label">{displayName(message.authorId)}</span>
              <time className="stamp" dateTime={message.createdAt} title={exactTime(message.createdAt)}>
                {clockTime(message.createdAt)}
              </time>
            </div>
            <MessageBody body={message.body} />
          </div>
        </article>
      ) : (
        <article
          id={`msg-${message.id}`}
          className={`row ${accentClass(message.authorId)}${startsRun ? ' starts-run' : ''}${mine ? ' mine' : ''}`}
        >
          <div className="row-gutter">
            {startsRun ? (
              <span className="avatar" aria-hidden="true">
                {badgeFor(message.authorId)}
              </span>
            ) : (
              <time className="hover-time" dateTime={message.createdAt} title={exactTime(message.createdAt)}>
                {clockTime(message.createdAt)}
              </time>
            )}
          </div>
          <div className="row-main">
            {/* First in the column and floated right, so it takes a slot at the end of the first line
                instead of covering text, and revealing it on hover moves nothing. */}
            {onReply && (
              <button
                type="button"
                className="reply-action"
                aria-label={`Reply to ${displayName(message.authorId)}`}
                onClick={() => onReply(message)}
              >
                Reply
              </button>
            )}
            {startsRun && (
              <div className="row-meta">
                <span className="author">{displayName(message.authorId)}</span>
                <time className="stamp" dateTime={message.createdAt} title={exactTime(message.createdAt)}>
                  {clockTime(message.createdAt)}
                </time>
              </div>
            )}
            {message.replyToId != null && <ReplyQuote replyToId={message.replyToId} original={original} />}
            <MessageBody body={message.body} />
          </div>
        </article>
      )}
    </>
  );
});

/** The one line above a reply's body that says what it answers. With the original loaded it names the
 *  author and a snippet; without it (an older page the thread has not loaded) only the id. Either way
 *  it is a button that jumps to the original when the original is on the page. */
function ReplyQuote({ replyToId, original }: { replyToId: number; original: Message | undefined }) {
  return (
    <button type="button" className="reply-quote" onClick={() => jumpTo(replyToId)}>
      <span className="reply-quote-mark" aria-hidden="true">
        ↪
      </span>
      {original ? (
        <span className="reply-quote-text">
          <span className={`reply-quote-author ${accentClass(original.authorId)}`}>{displayName(original.authorId)}</span>
          {`: ${replySnippet(original.body)}`}
        </span>
      ) : (
        <span className="reply-quote-text">{`#${replyToId}`}</span>
      )}
    </button>
  );
}

/** Markdown is rendered and sanitised once per distinct body (see markdown.ts's cache) and this
 *  component never re-renders for an unchanged body. */
const MessageBody = memo(function MessageBody({ body }: { body: string }) {
  return <div className="body" dangerouslySetInnerHTML={{ __html: renderBody(body) }} />;
});
