import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DeployAIApiClient } from './client.js';
import type { DeployAIConfig } from '../config.js';

function createConfig(): DeployAIConfig {
  const dir = mkdtempSync(join(tmpdir(), 'deployai-mcp-inventory-'));
  return {
    apiUrl: 'http://localhost:5000',
    accessToken: 'access',
    refreshToken: 'refresh',
    tokenStorePath: join(dir, 'tokens.json'),
    streamTimeoutMs: 5000,
  };
}

/**
 * The inventory tool is a thin adapter: one authenticated GET on DeployAI, never a call to
 * Coolify itself. That is the MCP's "no provider bypass" rule — the Coolify token stays in
 * DeployAI's store, and the request carries DeployAI's own bearer, not Coolify's.
 */
describe('DeployAIApiClient.getCoolifyInventory', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('reads the inventory through DeployAI with DeployAI credentials', async () => {
    const client = new DeployAIApiClient(createConfig());
    await client.initialize();

    const inventory = {
      projects: [
        {
          uuid: 'proj-1',
          name: 'nabdah',
          environmentsInconclusive: false,
          environments: [
            {
              uuid: 'env-1',
              name: 'production',
              applications: [
                {
                  uuid: 'app-1',
                  name: 'reel-hub',
                  buildPack: 'dockercompose',
                  status: 'running:unknown',
                  gitRepository: 'https://github.com/acme/reelhub',
                  gitBranch: 'main',
                  domains: ['https://reelhub.example.com'],
                },
              ],
              databases: [],
            },
          ],
        },
      ],
      unplacedApplications: [],
      unplacedDatabases: [],
    };
    const fetchMock = vi.mocked(fetch);
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(inventory), { status: 200, headers: { 'Content-Type': 'application/json' } })
    );

    const result = await client.getCoolifyInventory('cred-123');

    expect(result).toEqual(inventory);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://localhost:5000/api/credentials/cred-123/coolify/inventory');
    expect(init.method ?? 'GET').toBe('GET');
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer access');
    expect(url).not.toContain('coolify.');
  });
});
