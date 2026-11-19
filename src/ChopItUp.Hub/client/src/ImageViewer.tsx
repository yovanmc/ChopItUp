import { useCallback, useEffect, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { fileName, IMAGE_RETRY_MS, isVideo, routeFor } from './images';

interface Props {
  roomId: string;
  fileRef: string;
  onClose: () => void;
}

const MARGIN = 48;
const MIN_SCALE = 0.05;
const MAX_SCALE = 16;
const WHEEL_STEP = 1.25;

interface View {
  /** null is "fitted to the window". */
  scale: number | null;
  /** Offset of the image centre from the stage centre, in screen pixels. */
  x: number;
  y: number;
}

const FITTED: View = { scale: null, x: 0, y: 0 };

/** One room image over the whole window: fitted at first, zoomed about the pointer with the wheel,
 *  panned by dragging, toggled between fit and 100% by a double-click. A video plays in place
 *  instead. Escape, Close and a click on the backdrop each close it, and only it. */
export default function ImageViewer({ roomId, fileRef, onClose }: Props) {
  const src = routeFor(fileRef, roomId);
  const name = fileName(fileRef);
  const video = isVideo(fileRef);
  const stage = useRef<HTMLDivElement>(null);
  const [natural, setNatural] = useState<{ w: number; h: number } | null>(null);
  const [box, setBox] = useState({ w: 0, h: 0 });
  const [view, setView] = useState<View>(FITTED);
  const [attempt, setAttempt] = useState(0);
  const [failed, setFailed] = useState(false);
  const drag = useRef<{ x: number; y: number } | null>(null);

  // Capture phase and stopped, so a dialog under the viewer (the gallery) does not close with it.
  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key !== 'Escape') return;
      event.stopPropagation();
      onClose();
    }
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  useEffect(() => {
    const element = stage.current;
    if (!element) return;
    const measure = () => setBox({ w: element.clientWidth, h: element.clientHeight });
    measure();
    window.addEventListener('resize', measure);
    return () => window.removeEventListener('resize', measure);
  }, []);

  const fit = natural && box.w > 0
    ? Math.min((box.w - MARGIN * 2) / natural.w, (box.h - MARGIN * 2) / natural.h, 1)
    : 1;
  const scale = view.scale ?? fit;

  /** Scales by `factor` keeping the image point under (px, py), stage-centre coordinates, still. */
  const zoomAbout = useCallback(
    (factor: number, px: number, py: number) => {
      setView((current) => {
        const from = current.scale ?? fit;
        const to = Math.min(MAX_SCALE, Math.max(MIN_SCALE, from * factor));
        const ratio = to / from;
        return { scale: to, x: px - (px - current.x) * ratio, y: py - (py - current.y) * ratio };
      });
    },
    [fit],
  );

  const pointer = useCallback((clientX: number, clientY: number) => {
    const rect = stage.current!.getBoundingClientRect();
    return { px: clientX - rect.left - rect.width / 2, py: clientY - rect.top - rect.height / 2 };
  }, []);

  // A native listener, not React's: React's wheel listener is passive, so it cannot stop Ctrl+wheel
  // from zooming the whole page.
  useEffect(() => {
    const element = stage.current;
    if (!element || video) return;
    function onWheel(event: WheelEvent) {
      event.preventDefault();
      if (event.deltaY === 0) return;
      const { px, py } = pointer(event.clientX, event.clientY);
      zoomAbout(event.deltaY < 0 ? WHEEL_STEP : 1 / WHEEL_STEP, px, py);
    }
    element.addEventListener('wheel', onWheel, { passive: false });
    return () => element.removeEventListener('wheel', onWheel);
  }, [video, pointer, zoomAbout]);

  useEffect(() => {
    function onMove(event: MouseEvent) {
      const last = drag.current;
      if (!last) return;
      drag.current = { x: event.clientX, y: event.clientY };
      setView((current) => ({
        scale: current.scale ?? fit,
        x: current.x + event.clientX - last.x,
        y: current.y + event.clientY - last.y,
      }));
    }
    function onUp() {
      drag.current = null;
    }
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    return () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
  }, [fit]);

  function onDoubleClick(event: ReactMouseEvent) {
    if (view.scale === null || Math.abs(view.scale - fit) < 0.001) {
      const { px, py } = pointer(event.clientX, event.clientY);
      zoomAbout(1 / scale, px, py);
    } else {
      setView(FITTED);
    }
  }

  function onError() {
    if (attempt === 0) window.setTimeout(() => setAttempt(1), IMAGE_RETRY_MS);
    else setFailed(true);
  }

  const shown = src === null ? null : attempt === 0 ? src : `${src}&retry=1`;
  const level = view.scale === null ? `Fit · ${Math.round(fit * 100)}%` : `${Math.round(scale * 100)}%`;

  return (
    <div
      className="viewer"
      role="dialog"
      aria-modal="true"
      aria-label="Image viewer"
      onMouseDown={(event) => {
        event.stopPropagation();
        if (event.target === event.currentTarget || event.target === stage.current) onClose();
      }}
    >
      <div className="viewer-stage" ref={stage}>
        {shown === null || failed ? (
          <p className="image-missing">{name} · not available</p>
        ) : video ? (
          <video className="viewer-video" src={shown} controls autoPlay onError={onError} />
        ) : (
          <img
            className="viewer-image"
            src={shown}
            alt={name}
            draggable={false}
            style={{
              width: natural ? natural.w * scale : undefined,
              height: natural ? natural.h * scale : undefined,
              transform: `translate(-50%, -50%) translate(${view.x}px, ${view.y}px)`,
              visibility: natural ? 'visible' : 'hidden',
            }}
            onLoad={(event) => setNatural({ w: event.currentTarget.naturalWidth, h: event.currentTarget.naturalHeight })}
            onError={onError}
            onMouseDown={(event) => {
              event.preventDefault();
              drag.current = { x: event.clientX, y: event.clientY };
            }}
            onDoubleClick={onDoubleClick}
          />
        )}
      </div>
      <div className="viewer-controls">
        {!video && <span className="viewer-level" aria-live="polite">{level}</span>}
        {src !== null && (
          <a className="viewer-button" href={src} target="_blank" rel="noopener noreferrer">
            Open original
          </a>
        )}
        <button type="button" className="viewer-button" onClick={onClose} aria-label="Close viewer">
          Close
        </button>
      </div>
      <p className="viewer-caption" title={fileRef}>
        {name}
      </p>
    </div>
  );
}
