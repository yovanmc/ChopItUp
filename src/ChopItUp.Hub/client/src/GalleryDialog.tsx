import { useEffect, useState } from 'react';
import { describeError, listRoomFiles } from './api';
import { isVideo, routeFor } from './images';
import RoomImage from './RoomImage';
import { exactTime } from './time';
import type { Room, RoomFile, RoomFileListing } from './types';

/** Tiles shown before "Show more". Each tile loads its file at full size, so the page stays short. */
export const GALLERY_PAGE = 60;

interface Props {
  room: Room;
  onClose: () => void;
  onOpen: (ref: string) => void;
}

/** Every image and video in the room's artifacts folder, newest first. Read once on open; the
 *  request is dropped when the dialog closes. */
export default function GalleryDialog({ room, onClose, onOpen }: Props) {
  const [listing, setListing] = useState<RoomFileListing | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    listRoomFiles(room.id, abort.signal).then(
      (loaded) => { if (!abort.signal.aborted) setListing(loaded); },
      (failure: unknown) => { if (!abort.signal.aborted) setError(describeError(failure)); },
    );
    return () => abort.abort();
  }, [room.id]);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="overlay" role="presentation" onMouseDown={onClose}>
      <div
        className="dialog gallery-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="gallery-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <header className="dialog-head">
          <h2 id="gallery-title">Gallery of {room.name}</h2>
          <button type="button" className="quiet close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>
        {error && <p className="dialog-error">{error}</p>}
        {!listing && !error && <p className="dialog-note">Loading…</p>}
        {listing && <GalleryGrid roomId={room.id} listing={listing} onOpen={onOpen} />}
      </div>
    </div>
  );
}

interface GridProps {
  roomId: string;
  listing: RoomFileListing;
  onOpen: (ref: string) => void;
}

export function GalleryGrid({ roomId, listing, onOpen }: GridProps) {
  const [shown, setShown] = useState(GALLERY_PAGE);
  const { files } = listing;
  if (files.length === 0) {
    return <p className="dialog-note quiet-note">No images or videos yet. Renders a spawn saves to this room's artifacts folder appear here.</p>;
  }
  const more = files.length - shown;
  return (
    <>
      <p className="dialog-note">
        {files.length === 1 ? '1 file' : `${files.length} files`}, newest first
        {listing.omitted > 0 && `, and ${listing.omitted} older ones not listed`}
        {listing.unreadable > 0 && `. ${listing.unreadable === 1 ? 'One folder' : `${listing.unreadable} folders`} could not be read`}.
      </p>
      <ul className="gallery-grid" aria-label="Room files">
        {files.slice(0, shown).map((file) => (
          <li key={file.ref}>
            <Tile roomId={roomId} file={file} onOpen={onOpen} />
          </li>
        ))}
      </ul>
      {more > 0 && (
        <footer className="dialog-actions">
          <button type="button" className="quiet" onClick={() => setShown((count) => count + GALLERY_PAGE)}>
            Show more ({more})
          </button>
        </footer>
      )}
    </>
  );
}

function Tile({ roomId, file, onOpen }: { roomId: string; file: RoomFile; onOpen: (ref: string) => void }) {
  const video = isVideo(file.ref);
  const src = routeFor(file.ref, roomId);
  return (
    <button type="button" className="gallery-tile" onClick={() => onOpen(file.ref)} title={file.name}>
      <span className="gallery-thumb">
        {video || src === null ? (
          <span className="gallery-play" aria-hidden="true">▶</span>
        ) : (
          <RoomImage src={src} name={file.name} />
        )}
      </span>
      <span className="gallery-name">{file.name}</span>
      <span className="gallery-time">{exactTime(file.modified)}</span>
    </button>
  );
}
