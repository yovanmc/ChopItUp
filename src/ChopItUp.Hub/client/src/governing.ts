export function governingCommand(body: string): { slot: 'objective' | 'correction'; text: string } | null {
  const match = /^\/(objective|correction)(?=[ \t\r\n]|$)/.exec(body);
  return match ? { slot: match[1] as 'objective' | 'correction', text: body.slice(match[0].length).trim() } : null;
}

export function messageBody(draft: string): string {
  const trimmed = draft.trim();
  // Indentation and leading blank lines must not turn an example into a command.
  return governingCommand(trimmed) && !governingCommand(draft) ? draft.trimEnd() : trimmed;
}
