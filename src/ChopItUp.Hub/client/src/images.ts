/** Which image hrefs in a message body the chat may load, and from where. Only files in the room's
 *  own folders, through the hub's room file route: a body is model or pasted text, so an image that
 *  loaded from anywhere else would tell that host the room was read. */

const VIDEO = /\.(mp4|webm)$/i;
const SCHEME = /^[a-z][a-z0-9+.-]*:/i;
const ARTIFACT = /^artifact:/i;
const DRIVE = /^[a-z]:/i;

/** Whether `ref`, a name as it is on disk, can name a file in the room: an `artifact:` ref or a
 *  relative path. Never a URL, a rooted path or a drive path. */
function isRoomRef(ref: string): boolean {
  if (ref.trim() === '') return false;
  if (ARTIFACT.test(ref)) return true;
  return !SCHEME.test(ref) && !DRIVE.test(ref) && !ref.startsWith('/') && !ref.startsWith('\\');
}

/** The ref a markdown image href names, or null when it names no room file. A percent-encoded href
 *  (`my%20file.png`) is decoded, so the ref is the name on disk. */
export function refFromHref(href: string): string | null {
  let decoded: string;
  try {
    decoded = decodeURI(href.trim());
  } catch {
    return null;
  }
  return isRoomRef(decoded) ? decoded : null;
}

/** The room file route URL for `ref`, a name as it is on disk (never decoded again, so a file named
 *  `50%.png` stays that name), or null when it names no room file. */
export function routeFor(ref: string, roomId: string): string | null {
  if (roomId === '' || !isRoomRef(ref)) return null;
  return `/api/rooms/${encodeURIComponent(roomId)}/files?ref=${encodeURIComponent(ref)}`;
}

/** The last path segment of a ref, for captions and labels. */
export function fileName(ref: string): string {
  const path = ref.replace(ARTIFACT, '');
  return path.slice(Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\')) + 1) || path;
}

export function isVideo(ref: string): boolean {
  return VIDEO.test(ref);
}

/** For text placed inside a double-quoted attribute or between tags. */
export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
