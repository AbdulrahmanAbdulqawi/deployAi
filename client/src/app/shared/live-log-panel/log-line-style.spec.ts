import { classifyLogLine, formatLinesForCopy, formatLogTimestamp } from './log-line-style';

describe('classifyLogLine', () => {
  it('classifies docker stage lines', () => {
    expect(classifyLogLine('[build 3/10] COPY DeployAI.Api/DeployAI.Api.csproj DeployAI.Api/')).toBe('stage');
    expect(classifyLogLine('[stage-1 2/3] WORKDIR /app')).toBe('stage');
  });

  it('classifies error lines', () => {
    expect(classifyLogLine('Build Failed: failed to solve')).toBe('error');
    expect(classifyLogLine('DeployAI.Providers.csproj: not found')).toBe('error');
  });

  it('classifies warning lines', () => {
    expect(classifyLogLine('warning: deprecated API')).toBe('warn');
  });

  it('classifies success lines', () => {
    expect(classifyLogLine('Build complete')).toBe('success');
    expect(classifyLogLine('Deployment published successfully')).toBe('success');
  });

  it('returns default for normal output', () => {
    expect(classifyLogLine('RUN dotnet restore DeployAI.Api/DeployAI.Api.csproj')).toBe('default');
  });

  it('returns muted for empty lines', () => {
    expect(classifyLogLine('   ')).toBe('muted');
  });
});

describe('formatLogTimestamp', () => {
  it('formats valid timestamps', () => {
    const formatted = formatLogTimestamp('2024-01-01T14:32:01.000Z');
    expect(formatted).toMatch(/^\d{2}:\d{2}:\d{2}$/);
  });

  it('returns null for missing or invalid timestamps', () => {
    expect(formatLogTimestamp()).toBeNull();
    expect(formatLogTimestamp('not-a-date')).toBeNull();
  });
});

describe('formatLinesForCopy', () => {
  it('joins lines with optional timestamps', () => {
    const text = formatLinesForCopy([
      { line: 'Building...', loggedAt: '2024-01-01T14:32:01.000Z' },
      { line: 'Done' }
    ]);

    expect(text).toContain('Building...');
    expect(text).toContain('Done');
    expect(text.split('\n').length).toBe(2);
  });
});
