import {
  durationMsFromTimestamps,
  formatDurationMs,
  formatDurationSeconds,
  formatElapsedMs
} from './duration';

describe('formatElapsedMs', () => {
  it('formats mm:ss with zero padding', () => {
    expect(formatElapsedMs(0)).toBe('00:00');
    expect(formatElapsedMs(65_000)).toBe('01:05');
    expect(formatElapsedMs(125_000)).toBe('02:05');
  });
});

describe('formatDurationMs', () => {
  it('formats short and long durations', () => {
    expect(formatDurationMs(45_000)).toBe('45s');
    expect(formatDurationMs(135_000)).toBe('2m 15s');
    expect(formatDurationMs(120_000)).toBe('2m');
  });
});

describe('formatDurationSeconds', () => {
  it('handles edge cases', () => {
    expect(formatDurationSeconds(0)).toBe('0s');
    expect(formatDurationSeconds(59)).toBe('59s');
    expect(formatDurationSeconds(61)).toBe('1m 1s');
    expect(formatDurationSeconds(120)).toBe('2m');
  });
});

describe('durationMsFromTimestamps', () => {
  it('returns null when timestamps are missing', () => {
    expect(durationMsFromTimestamps(undefined, '2024-01-01T00:01:00Z')).toBeNull();
    expect(durationMsFromTimestamps('2024-01-01T00:00:00Z', undefined)).toBeNull();
  });

  it('computes positive duration', () => {
    expect(durationMsFromTimestamps(
      '2024-01-01T00:00:00Z',
      '2024-01-01T00:02:15Z'
    )).toBe(135_000);
  });
});
