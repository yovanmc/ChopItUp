const clock = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });
const day = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
const exact = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' });

export function clockTime(iso: string): string {
  return clock.format(new Date(iso));
}

export function dayLabel(iso: string): string {
  return day.format(new Date(iso));
}

/** Title text: the hub's stamp in full, for when "9:14 AM" is not enough. */
export function exactTime(iso: string): string {
  return exact.format(new Date(iso));
}

export function isSameDay(a: string, b: string): boolean {
  return new Date(a).toDateString() === new Date(b).toDateString();
}

export function minutesBetween(a: string, b: string): number {
  return Math.abs(new Date(b).getTime() - new Date(a).getTime()) / 60000;
}
