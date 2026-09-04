import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { MENTIONABLE } from './participants';

const MENTION = new RegExp(`@(${MENTIONABLE.join('|')})\\b`, 'gi');

/** Rendering is pure in the body text, so the result is cached: an incoming message must not make
 *  the whole thread re-parse its markdown. Cleared wholesale rather than evicted one at a time —
 *  this is a chat client, not a cache benchmark. */
const cache = new Map<string, string>();
const CACHE_LIMIT = 1000;

/** Markdown to sanitised HTML, with @mentions decorated. Bodies are written by models and by pasted
 *  transcripts, so the output goes through DOMPurify before it ever reaches innerHTML. */
export function renderBody(body: string): string {
  const cached = cache.get(body);
  if (cached !== undefined) return cached;

  const raw = marked.parse(body, { async: false, gfm: true, breaks: true });
  const fragment = DOMPurify.sanitize(raw, { RETURN_DOM_FRAGMENT: true });
  decorateMentions(fragment);
  const host = document.createElement('div');
  host.appendChild(fragment);

  if (cache.size >= CACHE_LIMIT) cache.clear();
  cache.set(body, host.innerHTML);
  return host.innerHTML;
}

/** Walks text nodes rather than running a regex over the HTML string, so a mention can never be
 *  matched inside a tag or an attribute. Code spans and blocks are left alone: `@claude` inside a
 *  snippet is code, not an address. */
function decorateMentions(root: DocumentFragment): void {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const targets: Text[] = [];
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const text = node as Text;
    MENTION.lastIndex = 0;
    if (!MENTION.test(text.data)) continue;
    if (text.parentElement?.closest('code, pre, a')) continue;
    targets.push(text);
  }

  for (const text of targets) {
    const replacement = document.createDocumentFragment();
    let cursor = 0;
    MENTION.lastIndex = 0;
    for (let match = MENTION.exec(text.data); match !== null; match = MENTION.exec(text.data)) {
      if (match.index > cursor) {
        replacement.appendChild(document.createTextNode(text.data.slice(cursor, match.index)));
      }
      const span = document.createElement('span');
      span.className = 'mention';
      span.dataset['who'] = match[1]!.toLowerCase();
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
