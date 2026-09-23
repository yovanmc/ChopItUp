import { describe, expect, test } from 'vitest';
import fixture from '../../../../tests/mention-cases.json';
import { recipientsOf, setRoster } from './participants';
import type { Participant } from './types';

/** `recipientsOf` is the client twin of `Mentions.Leading` (Core). The fixture at the repo
 *  root is the contract between them — both readers run every case in it, so a grammar change that
 *  only one side takes shows up here rather than in the room. It is imported rather than read off
 *  disk because this client has no `@types/node`, and `tsc --noEmit` is part of the build. */
type Case = (typeof fixture.cases)[number];

/** Shapes mirror `ChopDb.SeedRoster`: the app-backed `claude`/`codex` rows are kind `model` with no
 *  model of their own, a spawn row carries one, and both human rows carry none. */
const SEED: Participant[] = [
  { id: 'owner', displayName: 'Owner', kind: 'human', host: 'human', model: null },
  { id: 'claude', displayName: 'Claude', kind: 'model', host: 'claude', model: null },
  { id: 'codex', displayName: 'Codex', kind: 'model', host: 'codex', model: null },
  { id: 'opus', displayName: 'Opus', kind: 'model', host: 'claude', model: 'opus' },
  { id: 'sonnet', displayName: 'Sonnet', kind: 'model', host: 'claude', model: 'sonnet' },
  { id: 'fable', displayName: 'Fable', kind: 'model', host: 'claude', model: 'fable' },
  { id: 'gpt-6-astra', displayName: 'GPT-6 Astra', kind: 'model', host: 'codex', model: 'gpt-6-astra' },
  { id: 'gpt-5.6-sol', displayName: 'GPT-5.6 Sol', kind: 'model', host: 'codex', model: 'gpt-5.6-sol' },
  { id: 'gpt-5.5', displayName: 'GPT-5.5', kind: 'model', host: 'codex', model: 'gpt-5.5' },
  { id: 'owner-remote', displayName: 'Owner (remote)', kind: 'human', host: 'human', model: null },
];

setRoster(
  fixture.roster.map((id) => {
    const p = SEED.find((s) => s.id === id);
    if (!p) throw new Error(`the fixture roster names ${id}, which this file does not model`);
    return p;
  }),
);

describe('recipientsOf', () => {
  test.each(fixture.cases)('$name', (c: Case) => {
    const start = performance.now();
    const read = recipientsOf(c.body);
    const elapsed = performance.now() - start;
    // The timing bound guards the catastrophic-backtracking canary only; on every other case it is a
    // measurement of the machine's load, which is not what this fixture is the contract for.
    if (c.name.startsWith('perf canary')) expect(elapsed).toBeLessThan(200);
    expect(read.recipients.map((p) => p.id)).toEqual(c.recipients);
    expect(read.unknown).toEqual(c.unknown);
    expect(read.references.map((p) => p.id)).toEqual(c.references);
    // Only the turns cases carry `turns`; a case without it asserts nothing here. A refused token
    // reports the value 0 on both sides, so the fixture's `value` (absent means 0) is asserted
    // whatever the token says.
    const turns = (c as { turns?: { token: string; value?: number } }).turns;
    if (turns === undefined) return;
    if (turns.token === 'none') expect(read.turns).toBeNull();
    else if (turns.token === 'out-of-range') expect(read.turns).toEqual({ turns: turns.value ?? 0, valid: false });
    else if (turns.token === 'valid') expect(read.turns).toEqual({ turns: turns.value, valid: true });
    else throw new Error(`Unknown turns token '${turns.token}' in case '${c.name}'`);
  });
});
