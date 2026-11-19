/** Which image hrefs in a message body the chat may load, and from where. Only files in the room's
 *  own folders, through the hub's room file route: a body is model or pasted text, so an image that
 *  loaded from anywhere else would tell that host the room was read. */

const VIDEO = /\.(mp4|webm)$/i;
const SCHEME = /^[a-z][a-z0-9+.-]*:/i;
const ARTIFACT = /^artifact:/i;
const DRIVE = /^[a-z]:/i;

/** The room file route URL for `href`, or null when the href is not a room file. A percent-encoded
 *  href (`my%20file.png`) is decoded first, so the hub sees the name on disk. */
export function routeFor(href: string, roomId: string): string | null {
  let decoded: string;
  try {
    decoded = decodeURI(href.trim());
  } catch {
    return null;
  }
  if (decoded === '' || roomId === '') return null;
  if (!ARTIFACT.test(decoded)) {
    if (SCHEME.test(decoded) || DRIVE.test(decoded)) return null;
    if (decoded.startsWith('/') || decoded.startsWith('\\')) return null;
  }
  return `/api/rooms/${encodeURIComponent(roomId)}/files?ref=${encodeURIComponent(decoded)}`;
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
