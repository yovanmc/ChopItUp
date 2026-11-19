import { useState } from 'react';
import { IMAGE_RETRY_MS } from './images';

/** A room file image that tries once more a second after a failed load (a spawn may be mid-write),
 *  then says the file is not available instead of showing the browser's broken-image glyph. */
export default function RoomImage({ src, name }: { src: string; name: string }) {
  const [attempt, setAttempt] = useState(0);
  const [failed, setFailed] = useState(false);
  if (failed) return <span className="image-missing">{name} · not available</span>;
  return (
    <img
      src={attempt === 0 ? src : `${src}&retry=1`}
      alt={name}
      loading="lazy"
      decoding="async"
      onError={() => {
        if (attempt === 0) window.setTimeout(() => setAttempt(1), IMAGE_RETRY_MS);
        else setFailed(true);
      }}
    />
  );
}
