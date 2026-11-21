/** The room a link names with `?room=<id>`. The desktop shell opens such a link when the owner clicks
 * a run notification. */
export function roomFromSearch(search: string): string | null {
  const id = new URLSearchParams(search).get('room');
  return id ? id : null;
}

/** The room to show once the list loads: the open (or linked) room while it is still listed, otherwise
 * (archived and hidden, gone, or never there) the first one. */
export function keepOrFirst(current: string | null, loaded: readonly { id: string }[]): string | null {
  return current !== null && loaded.some((room) => room.id === current) ? current : (loaded[0]?.id ?? null);
}
