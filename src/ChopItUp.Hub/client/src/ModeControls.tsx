import { useEffect, useState } from 'react';
import * as api from './api';
import type { Participant, Room, RoomModeSettings } from './types';

export default function ModeControls({ room, onSaved }: { room: Room; onSaved?: () => void }) {
  const [settings, setSettings] = useState<RoomModeSettings | null>(room.modeSettings ?? null);
  const [participants, setParticipants] = useState<Participant[]>([]);
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => { setSettings(room.modeSettings ?? null); setNotice(''); }, [room.id, room.modeSettings]);
  useEffect(() => {
    const abort = new AbortController();
    api.listParticipants(abort.signal).then(setParticipants).catch(() => undefined);
    return () => abort.abort();
  }, []);
  if (!settings) return <span>Room mode unavailable</span>;
  const options = participants.filter(p => p.kind === 'model' && p.model != null);
  async function save() {
    if (!settings) return;
    const savedRoom = room.id;
    setBusy(true);
    try { await api.setRoomMode(savedRoom, settings); setNotice('Mode saved'); onSaved?.(); }
    catch (error) { setNotice(api.describeError(error)); }
    finally { setBusy(false); }
  }
  return <details className="mode-controls">
    <summary>Mode: {room.modeSettings?.mode ?? 'unavailable'} · {room.modeSettings?.first} {room.modeSettings?.mode !== 'primary' && `→ ${room.modeSettings?.second}`} · {room.modeSettings?.mode === 'primary' ? 1 : room.modeSettings?.mode === 'relay' ? 2 : 3} turns</summary>
    <label>Room mode <select aria-label="Room mode" value={settings.mode} onChange={e => setSettings({ ...settings, mode: e.target.value as RoomModeSettings['mode'] })}>
      <option value="primary">Primary · 1 turn</option><option value="relay">Relay · 2 turns</option><option value="panel">Panel · 3 turns, including synthesis</option>
    </select></label>
    <label>First participant <select aria-label="First participant" value={settings.first} onChange={e => setSettings({ ...settings, first: e.target.value })}>
      {options.map(p => <option key={p.id} value={p.id}>{p.displayName}</option>)}
    </select></label>
    <label>Second participant <select aria-label="Second participant" value={settings.second ?? ''} onChange={e => setSettings({ ...settings, second: e.target.value || null })}>
      <option value="">None (primary only)</option>{options.map(p => <option key={p.id} value={p.id}>{p.displayName}</option>)}
    </select></label>
    <p>Panel gives independent answers, then a synthesis. It reviews a clean snapshot and makes no file edits. Money and token cost are unknown.</p>
    <button type="button" disabled={busy} onClick={() => void save()}>Save mode</button>
    <span role="status">{notice}</span>
  </details>;
}
