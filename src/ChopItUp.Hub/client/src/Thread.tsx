import { memo, useEffect, useRef } from 'react';
import { renderBody } from './markdown';
import { accentClass, badgeFor, displayName, isHuman } from './participants';
import { clockTime, dayLabel, exactTime, isSameDay, minutesBetween } from './time';
import type { Message } from './types';

/** A run breaks after this long even when the same participant is still talking. */
const RUN_GAP_MINUTES = 8;
/** How close to the bottom still counts as "following the conversation". */
const PIN_SLACK_PX = 96;

interface Props {
  messages: Message[];
  loading: boolean;
}

export default function Thread({ messages, loading }: Props) {
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);

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
}

/** Memoised on a stable message object plus two booleans, so appending one message renders exactly
 *  one new row: nothing above it changes props. */
const MessageRow = memo(function MessageRow({ message, startsRun, dayBreak }: RowProps) {
  const mine = isHuman(message.authorId);
  return (
    <>
      {dayBreak && (
        <div className="day-break">
          <span>{dayLabel(message.createdAt)}</span>
        </div>
      )}
      <article
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
          {startsRun && (
            <div className="row-meta">
              <span className="author">{displayName(message.authorId)}</span>
              <time className="stamp" dateTime={message.createdAt} title={exactTime(message.createdAt)}>
                {clockTime(message.createdAt)}
              </time>
            </div>
          )}
          <MessageBody body={message.body} />
        </div>
      </article>
    </>
  );
});

/** Markdown is rendered and sanitised once per distinct body (see markdown.ts's cache) and this
 *  component never re-renders for an unchanged body. */
const MessageBody = memo(function MessageBody({ body }: { body: string }) {
  return <div className="body" dangerouslySetInnerHTML={{ __html: renderBody(body) }} />;
});
