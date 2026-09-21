import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { DeployAIApiClient } from './client.js';
import type { DeployAIConfig } from '../config.js';
import { DeployAIApiError } from '../errors.js';
import { formatToolError } from '../errors.js';

function createConfig(overrides: Partial<DeployAIConfig> = {}): DeployAIConfig {
  const dir = mkdtempSync(join(tmpdir(), 'deployai-mcp-test-'));
  return {
    apiUrl: 'http://localhost:5000',
    accessToken: 'old-access',
    refreshToken: 'old-refresh',
    tokenStorePath: join(dir, 'tokens.json'),
    streamTimeoutMs: 5000,
    ...overrides,
  };
}

describe('DeployAIApiClient', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('refreshes token on 401 and retries request', async () => {
    const client = new DeployAIApiClient(createConfig());
    await client.initialize();

    const fetchMock = vi.mocked(fetch);
    fetchMock
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            accessToken: 'new-access',
            refreshToken: 'new-refresh',
            expiresIn: 900,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } }
        )
      )
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ projects: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      );

    const result = await client.listProjects();
    expect(result.projects).toEqual([]);
    expect(fetchMock).toHaveBeenCalledTimes(3);

    const refreshCall = fetchMock.mock.calls[1];
    expect(refreshCall[0]).toBe('http://localhost:5000/api/auth/refresh');
  });

  it('maps API error body to DeployAIApiError', async () => {
    const client = new DeployAIApiClient(createConfig());
    await client.initialize();

    vi.mocked(fetch).mockResolvedValue(
      new Response(
        JSON.stringify({
          error: { code: 'provider_token_invalid', message: 'Reconnect Vercel.' },
        }),
        { status: 400, headers: { 'Content-Type': 'application/json' } }
      )
    );

    await expect(client.listProjects()).rejects.toMatchObject({
      code: 'provider_token_invalid',
      message: 'Reconnect Vercel.',
    });
  });

  it('throws not_authenticated when no token is set', async () => {
    const client = new DeployAIApiClient(
      createConfig({ accessToken: null, refreshToken: null })
    );
    await client.initialize();

    await expect(client.listProjects()).rejects.toMatchObject({
      code: 'not_authenticated',
    });
  });
});

describe('formatToolError', () => {
  it('formats DeployAIApiError as JSON tool result', () => {
    const result = formatToolError(new DeployAIApiError('fix_not_eligible', 'Not fixable.'));
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain('fix_not_eligible');
    expect(result.content[0].text).toContain('Not fixable.');
  });

  it('formats unknown errors with unexpected_error code', () => {
    const result = formatToolError(new Error('boom'));
    expect(result.isError).toBe(true);
    expect(result.content[0].text).toContain('unexpected_error');
    expect(result.content[0].text).toContain('boom');
  });
});
