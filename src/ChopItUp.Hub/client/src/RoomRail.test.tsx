import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, test } from 'vitest';
import RoomRail from './RoomRail';
import type { Room } from './types';

/** Row 28, AC5's second half. `markRead` is a background write, so its refusal is silent by design —
 *  which leaves the owner looking at unread badges that never clear, with nothing on screen saying
 *  why. This is the line that says why, and the affordance that gets him to the paste field: without
 *  it the suite would go green while the product quietly told him the wrong number.
 *
 *  Static markup through `react-dom/server`, like `ExchangeBar.test.tsx` and `RunBar.test.tsx`: this
 *  client has no jsdom, and presence plus wording is the whole of what is claimed here. */
const ROOMS: Room[] = [
  {
    id: 'lab',
    name: 'Lab',
    directory: null,
    createdAt: '2026-03-01T09:00:00.0000000+00:00',
    lastActivityAt: '2026-03-01T09:30:00.0000000+00:00',
    lastMessageId: 8,
    messageCount: 8,
    unread: 3,
    archivedAt: null,
  },
];

const render = (unreadBlocked: boolean) =>
  renderToStaticMarkup(
    <RoomRail
      rooms={ROOMS}
      activeRoomId={null}
      liveness="live"
      showArchived={false}
      unreadBlocked={unreadBlocked}
      onSelect={() => undefined}
      onNewRoom={() => undefined}
      onToggleArchived={() => undefined}
      onFixUnread={() => undefined}
    />,
  );

describe('RoomRail', () => {
  test('a rail whose read cursor is refused says the counts are stuck and offers the fix', () => {
    const markup = render(true);

    expect(markup).toContain('rail-unread-blocked');
    expect(markup).toContain('Unread counts are stuck until you paste the owner token');
  });

  /** The line has to be reachable, not just readable: the paste field lives on a skill card that may
   *  not be on screen and behind a refused write the owner may not have attempted yet, so the rail's
   *  own line is a control. A sentence naming a remedy with no way to reach it is the defect. */
  test('the line is something to press, not a dead-end sentence', () => {
    expect(render(true)).toContain(
      '<button type="button" class="quiet rail-unread-blocked">Unread counts are stuck until you paste the owner token.</button>',
    );
  });

  test('a rail that has not been refused anything shows no such line', () => {
    const markup = render(false);

    expect(markup).not.toContain('rail-unread-blocked');
    expect(markup).not.toContain('owner token');
  });

  test('the room list itself is unchanged either way', () => {
    for (const blocked of [true, false]) {
      const markup = render(blocked);
      expect(markup).toContain('<span class="room-name">Lab</span>');
      expect(markup).toContain('aria-label="3 unread"');
    }
  });
});
