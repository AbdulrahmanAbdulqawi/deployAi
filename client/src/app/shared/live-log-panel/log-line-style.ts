export type LogLineClass = 'error' | 'warn' | 'success' | 'stage' | 'muted' | 'default';

const STAGE_PREFIX = /^\[(?:stage|build)/i;
const ERROR_PATTERN = /\b(?:error|failed|fatal)\b|not found/i;
const WARN_PATTERN = /\b(?:warn|warning)\b/i;
const SUCCESS_PATTERN = /\b(?:success|complete|completed|published|live)\b/i;

export function classifyLogLine(line: string): LogLineClass {
  const trimmed = line.trim();
  if (!trimmed) {
    return 'muted';
  }

  if (STAGE_PREFIX.test(trimmed)) {
    return 'stage';
  }

  if (ERROR_PATTERN.test(trimmed)) {
    return 'error';
  }

  if (WARN_PATTERN.test(trimmed)) {
    return 'warn';
  }

  if (SUCCESS_PATTERN.test(trimmed)) {
    return 'success';
  }

  return 'default';
}

export function formatLogTimestamp(loggedAt?: string): string | null {
  if (!loggedAt) {
    return null;
  }

  const date = new Date(loggedAt);
  if (Number.isNaN(date.getTime())) {
    return null;
  }

  return date.toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false
  });
}

export function formatLinesForCopy(lines: { line: string; loggedAt?: string }[]): string {
  return lines
    .map(entry => {
      const timestamp = formatLogTimestamp(entry.loggedAt);
      return timestamp ? `${timestamp}  ${entry.line}` : entry.line;
    })
    .join('\n');
}
