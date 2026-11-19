import { Marked } from 'marked';
import DOMPurify from 'dompurify';
import { escapeHtml, routeFor } from './images';
import { hostOf, mentionPattern } from './participants';

/** Rendering is pure in the body text and the room, so the result is cached: an incoming message must
 *  not make the whole thread re-parse its markdown. Cleared wholesale rather than evicted one at a
 *  time — this is a chat client, not a cache benchmark. */
const cache = new Map<string, string>();
const CACHE_LIMIT = 1000;

/** Markup a body never needs, each a way to load something from another host or to fake the app's
 *  own controls. The shell's CSP blocks the loads too; this keeps them out of the DOM at all. */
const FORBID_TAGS = [
  'style', 'form', 'input', 'button', 'textarea', 'select', 'svg', 'math', 'picture', 'source',
  'video', 'audio', 'track', 'iframe', 'object', 'embed', 'link', 'meta', 'base',
];
const FORBID_ATTR = ['style', 'background', 'srcset', 'poster', 'action', 'formaction'];

/** The room whose files the body being sanitised may show. Set only for one `sanitize` call. */
let sanitizingFor: string | undefined;

/** The one place an image gets a `src`: from its `data-ref`, through `routeFor`, for the room being
 *  rendered. An image with no ref, or one `routeFor` refuses, becomes its alt text. */
DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.nodeName !== 'IMG') return;
  const image = node as HTMLImageElement;
  const ref = image.getAttribute('data-ref');
  const url = ref !== null && sanitizingFor !== undefined ? routeFor(ref, sanitizingFor) : null;
  image.removeAttribute('src');
  if (url === null) {
    image.replaceWith(document.createTextNode(image.getAttribute('alt') ?? ''));
    return;
  }
  image.setAttribute('src', url);
  image.setAttribute('loading', 'lazy');
  image.setAttribute('decoding', 'async');
});

/** An image becomes `<img data-ref alt>` with no `src`, so the sanitiser hook above is the only thing
 *  that can point it anywhere. An href that is not a room file stays as its alt text. */
const markdown = new Marked({
  async: false,
  gfm: true,
  breaks: true,
  renderer: {
    image({ href, text }) {
      if (sanitizingFor === undefined || routeFor(href, sanitizingFor) === null) return escapeHtml(text);
      return `<img data-ref="${escapeHtml(href)}" alt="${escapeHtml(text)}">`;
    },
  },
});

/** Markdown to sanitised HTML, with @mentions decorated. Bodies are written by models and by pasted
 *  transcripts, so the output goes through DOMPurify before it ever reaches innerHTML. With a
 *  `roomId`, images naming that room's files are shown; without one, no image is. */
export function renderBody(body: string, roomId?: string): string {
  const key = `${roomId ?? ''}\u0000${body}`;
  const cached = cache.get(key);
  if (cached !== undefined) return cached;

  let fragment: DocumentFragment;
  sanitizingFor = roomId;
  try {
    const raw = markdown.parse(body) as string;
    fragment = DOMPurify.sanitize(raw, { RETURN_DOM_FRAGMENT: true, FORBID_TAGS, FORBID_ATTR });
  } finally {
    sanitizingFor = undefined;
  }
  decorateMentions(fragment);
  const host = document.createElement('div');
  host.appendChild(fragment);

  if (cache.size >= CACHE_LIMIT) cache.clear();
  cache.set(key, host.innerHTML);
  return host.innerHTML;
}

/** Walks text nodes rather than running a regex over the HTML string, so a mention can never be
 *  matched inside a tag or an attribute. Code spans and blocks are left alone: `@someone` inside a
 *  snippet is code, not an address. */
function decorateMentions(root: DocumentFragment): void {
  const pattern = mentionPattern();
  if (!pattern) return;

  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const targets: Text[] = [];
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const text = node as Text;
    pattern.lastIndex = 0;
    if (!pattern.test(text.data)) continue;
    if (text.parentElement?.closest('code, pre, a')) continue;
    targets.push(text);
  }

  for (const text of targets) {
    const replacement = document.createDocumentFragment();
    let cursor = 0;
    pattern.lastIndex = 0;
    for (let match = pattern.exec(text.data); match !== null; match = pattern.exec(text.data)) {
      if (match.index > cursor) {
        replacement.appendChild(document.createTextNode(text.data.slice(cursor, match.index)));
      }
      const span = document.createElement('span');
      span.className = 'mention';
      span.dataset['host'] = hostOf(match[1]!);
      span.textContent = match[0];
      replacement.appendChild(span);
      cursor = match.index + match[0].length;
    }
    if (cursor < text.data.length) {
      replacement.appendChild(document.createTextNode(text.data.slice(cursor)));
    }
    text.replaceWith(replacement);
  }
}
