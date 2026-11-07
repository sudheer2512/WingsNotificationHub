import { parseDate, toLocalInput } from './ui';

describe('toLocalInput', () => {
  it('is empty for no value', () => {
    expect(toLocalInput(null)).toBe('');
    expect(toLocalInput('')).toBe('');
  });

  it('round-trips a stored UTC time through the form without moving it', () => {
    const stored = '2026-11-09T13:54:00'; // the API sends UTC without a zone
    const reSaved = new Date(toLocalInput(stored)).toISOString();
    expect(reSaved).toBe(parseDate(stored)!.toISOString());
  });
});
